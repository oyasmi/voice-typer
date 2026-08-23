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

    /// <summary>模型契约的输入/输出名称（见类文档顶部的 I/O 表）。加载期一次性校验，
    /// 避免侧载模型 I/O 不齐时直到每次推理才失败（R2-09）。</summary>
    private static readonly string[] ExpectedInputNames = { "speech", "speech_lengths", "language", "textnorm" };
    private static readonly string[] ExpectedOutputNames = { "ctc_logits", "encoder_out_lens" };

    private readonly InferenceSession _session;
    private readonly FbankFrontend _frontend;
    private readonly CmvnStats _cmvn;
    private readonly IReadOnlyList<string> _tokens;
    private readonly int _lfrM;
    private readonly int _lfrN;
    private int _languageId;
    private bool _disposed;

    /// <summary>
    /// 侧载模型（<c>asr.model_dir</c>）的 config.yaml / am.mvn 相互独立，解析失败时各自静默
    /// 回落默认值，二者之间此前没有任何一致性校验：<c>lfr_m × n_mels</c> 与 am.mvn 实际维度
    /// 不匹配会在 <see cref="LfrCmvn.ApplyCmvn"/> 里数组越界，<c>lfr_n = 0</c> 会在
    /// <see cref="LfrCmvn.ApplyLfr"/> 里除零（F-08）。
    /// </summary>
    public sealed class ModelContractException : Exception
    {
        public ModelContractException(string message) : base(message) { }
    }

    public SenseVoiceEngine(ModelBundle bundle, AsrLanguage language, int threads)
    {
        var fbankOptions = bundle.FbankOptions;
        if (bundle.LfrM < 1 || bundle.LfrN < 1 || fbankOptions.NumMelBins <= 0
            || fbankOptions.FrameLengthMs <= 0 || fbankOptions.FrameShiftMs <= 0)
        {
            throw new ModelContractException(
                $"模型参数不自洽，请检查侧载模型的 config.yaml 与 am.mvn: "
                + $"lfr_m={bundle.LfrM} lfr_n={bundle.LfrN} n_mels={fbankOptions.NumMelBins} "
                + $"frame_length_ms={fbankOptions.FrameLengthMs} frame_shift_ms={fbankOptions.FrameShiftMs}");
        }
        // 采集链路固定 16kHz（AGENTS.md）；别的采样率的模型在这套前端下本来就不可用，
        // 用等值判断比"大于 0"更强，同时挡掉 fs<=0 导致 FbankFrontend 里的非正
        // frameLength 触发的数组分配异常。
        if (fbankOptions.SampleRate != AppConstants.TargetSampleRate)
        {
            throw new ModelContractException(
                $"模型参数不自洽，请检查侧载模型的 config.yaml: fs={fbankOptions.SampleRate}，"
                + $"但采集链路固定 {AppConstants.TargetSampleRate}");
        }

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

        var actualInputNames = _session.InputMetadata.Keys.ToHashSet();
        var actualOutputNames = _session.OutputMetadata.Keys.ToHashSet();
        if (!ExpectedInputNames.All(actualInputNames.Contains) || !ExpectedOutputNames.All(actualOutputNames.Contains))
        {
            _session.Dispose();
            throw new ModelContractException(
                $"模型 I/O 与 SenseVoice-Small 契约不符：期望输入 [{string.Join(", ", ExpectedInputNames)}] "
                + $"输出 [{string.Join(", ", ExpectedOutputNames)}]，实际输入 [{string.Join(", ", actualInputNames)}] "
                + $"输出 [{string.Join(", ", actualOutputNames)}]");
        }

        _frontend = new FbankFrontend(fbankOptions);
        var parsedCmvn = CmvnStats.Parse(bundle.CmvnPath);
        var parsedTokens = ModelLocator.LoadTokens(bundle.TokensPath);

        var expectedDim = bundle.LfrM * fbankOptions.NumMelBins;
        if (parsedCmvn.Means.Length != expectedDim || parsedCmvn.Vars.Length != expectedDim)
        {
            _session.Dispose();
            throw new ModelContractException(
                $"lfr_m({bundle.LfrM}) × n_mels({fbankOptions.NumMelBins}) = {expectedDim}，"
                + $"但 am.mvn 维度为 means={parsedCmvn.Means.Length} vars={parsedCmvn.Vars.Length}");
        }
        if (parsedTokens.Count <= 1)
        {
            _session.Dispose();
            throw new ModelContractException("tokens.json 词表为空或只有 1 个 token");
        }

        _cmvn = parsedCmvn;
        _tokens = parsedTokens;
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
        // 输出缺失是模型契约错误，不是合法静音——用 throw 让两者可区分（R2-09）。
        if (logitsResult is null || lensResult is null)
        {
            throw new ModelContractException("推理输出缺失 ctc_logits/encoder_out_lens");
        }

        var lensTensor = lensResult.AsTensor<int>();
        int validFrames = lensTensor[0];
        if (validFrames <= 0) return "";

        var logitsTensor = logitsResult.AsTensor<float>();
        var shape = logitsTensor.Dimensions;
        int vocabSize = shape[shape.Length - 1];
        if (vocabSize <= 0) return "";
        // 只有拿到推理输出的实际 shape 才能知道 vocabSize；此前不匹配时 CtcDecoder 会
        // 静默跳过越界 id，表现为"识别成功但缺字"，比报错更难排查（R2-09）。
        if (vocabSize != _tokens.Count)
        {
            throw new ModelContractException($"模型输出 vocab 维度({vocabSize}) 与词表大小({_tokens.Count}) 不一致");
        }

        int needed = validFrames * vocabSize;
        // ctc_logits 形状 [1, logits_length, vocabSize]，行主序展平，只需要前 validFrames 帧
        // （encoder_out_lens 给出的有效帧数）。DenseTensor<float>.Buffer 直接暴露底层内存，
        // 可以不经拷贝就切出所需前缀；只有在 ORT 返回了非 DenseTensor 的实现时才退化为
        // ToArray() 全量拷贝（F-07a——120s 音频场景下 ctc_logits 本身约 200MB，
        // 全量 ToArray() 后再切片等于白白多复制一份）。
        ReadOnlySpan<float> logitsSpan;
        if (logitsTensor is DenseTensor<float> dense)
        {
            logitsSpan = dense.Buffer.Span;
        }
        else
        {
            logitsSpan = logitsTensor.ToArray();
        }
        logitsSpan = logitsSpan[..Math.Min(needed, logitsSpan.Length)];

        return CtcDecoder.Decode(logitsSpan, validFrames, vocabSize, _tokens);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
