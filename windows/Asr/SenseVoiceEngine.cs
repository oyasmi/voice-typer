using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using VoiceTyper.Core;

namespace VoiceTyper.Asr;

/// <summary>
/// 识别引擎的最小接口，供 <see cref="RecognitionBuffer"/> 依赖，便于用假实现测试滑窗逻辑
/// 而不必加载真实 ONNX 模型。
/// </summary>
internal interface ISenseVoiceRecognizing
{
    string Recognize(ReadOnlySpan<float> samples);
}

/// <summary>
/// SenseVoice-Small ONNX 推理封装：fbank → LFR/CMVN → ORT session → CTC 贪心解码。
/// 对外只有一个方法 <see cref="Recognize"/>，非线程安全——只应在 <see cref="AsrPump"/> 的
/// 专用线程上使用（与服务端单 worker executor 的约束一致：ONNX session 与前端状态都有可变缓冲）。
///
/// 模型 I/O 契约（与 macOS 侧一致，已实测确认，见 windows/DESIGN.md §4.2）：
/// <code>
/// IN   speech           float32  [1, feats_length, 560]
/// IN   speech_lengths   int32    [1]
/// IN   language         int32    [1]   auto=0 zh=3 en=4 yue=7 ja=11 ko=12
/// IN   textnorm         int32    [1]   withitn=14  woitn=15
/// OUT  ctc_logits       float32  [1, logits_length, 25055]
/// OUT  encoder_out_lens int32    [1]
/// </code>
/// </summary>
internal sealed class SenseVoiceEngine : ISenseVoiceRecognizing, IDisposable
{
    /// <summary>withitn：SenseVoice 自带 ITN（"六十四兆"→"64兆"），本项目不暴露 woitn 选项。</summary>
    private const int TextNormWithItn = 14;

    private readonly InferenceSession _session;
    private readonly FbankFrontend _frontend;
    private readonly CmvnStats _cmvn;
    private readonly IReadOnlyList<string> _tokens;
    private readonly int _lfrM;
    private readonly int _lfrN;
    private int _languageId;
    private bool _disposed;

    public SenseVoiceEngine(ModelBundle bundle, AsrLanguage language, int threads)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = threads > 0 ? threads : Math.Min(4, Environment.ProcessorCount),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
        };
        // 实测（macOS 侧）：这一行把常驻内存从 ~800MB 降到 ~510MB，30s 音频推理耗时几乎不变。
        // 详见 windows/DESIGN.md §4.2。Windows 上的实际数字需 P0 阶段用真机复测。
        options.AddSessionConfigEntry("session.disable_prepacking", "1");

        _session = new InferenceSession(bundle.OnnxFilePath, options);
        _frontend = new FbankFrontend(bundle.FbankOptions);
        _cmvn = CmvnStats.Parse(bundle.CmvnPath);
        _tokens = ModelLocator.LoadTokens(bundle.TokensPath);
        _lfrM = bundle.LfrM;
        _lfrN = bundle.LfrN;
        _languageId = language.TokenId();
    }

    public void SetLanguage(AsrLanguage language)
    {
        _languageId = language.TokenId();
    }

    /// <param name="samples">16kHz / mono / float32，范围 [-1, 1]。</param>
    /// <returns>识别文本；样本不足一帧或识别为纯静音/纯标点时返回空字符串。</returns>
    public string Recognize(ReadOnlySpan<float> samples)
    {
        if (samples.Length < FbankFrontend.MinimumSamples) return "";

        var feats = _frontend.Compute(samples);
        if (feats.Length == 0) return "";
        var lfr = LfrCmvn.ApplyLfr(feats, _lfrM, _lfrN);
        var normalized = LfrCmvn.ApplyCmvn(lfr, _cmvn);
        if (normalized.Length == 0) return "";

        int featsLen = normalized.Length;
        int dim = normalized[0].Length;
        var flatFeats = new float[featsLen * dim];
        for (int t = 0; t < featsLen; t++)
        {
            Array.Copy(normalized[t], 0, flatFeats, t * dim, dim);
        }

        var speechTensor = new DenseTensor<float>(flatFeats, new[] { 1, featsLen, dim });
        var lengthTensor = new DenseTensor<int>(new[] { featsLen }, new[] { 1 });
        var languageTensor = new DenseTensor<int>(new[] { _languageId }, new[] { 1 });
        var textnormTensor = new DenseTensor<int>(new[] { TextNormWithItn }, new[] { 1 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("speech", speechTensor),
            NamedOnnxValue.CreateFromTensor("speech_lengths", lengthTensor),
            NamedOnnxValue.CreateFromTensor("language", languageTensor),
            NamedOnnxValue.CreateFromTensor("textnorm", textnormTensor),
        };

        using var results = _session.Run(inputs, new[] { "ctc_logits", "encoder_out_lens" });
        var logitsResult = results.FirstOrDefault(r => r.Name == "ctc_logits");
        var lensResult = results.FirstOrDefault(r => r.Name == "encoder_out_lens");
        if (logitsResult is null || lensResult is null) return "";

        var lensTensor = lensResult.AsTensor<int>();
        int validFrames = lensTensor[0];
        if (validFrames <= 0) return "";

        var logitsTensor = logitsResult.AsTensor<float>();
        var shape = logitsTensor.Dimensions;
        int vocabSize = shape[shape.Length - 1];
        if (vocabSize <= 0) return "";

        int needed = validFrames * vocabSize;
        // ctc_logits 形状 [1, logits_length, vocabSize]；ToArray() 按行主序展平，
        // 只取前 validFrames 帧（encoder_out_lens 给出的有效帧数）。
        var fullLogits = logitsTensor.ToArray();
        var logitsArray = needed >= fullLogits.Length ? fullLogits : fullLogits[..needed];

        return CtcDecoder.Decode(logitsArray, validFrames, vocabSize, _tokens);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
