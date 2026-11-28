using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using VoiceTyper.Asr.Hotword;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.Asr;

/// <summary>
/// 识别引擎的最小接口，供 <see cref="RecognitionBuffer"/> 依赖，便于用假实现测试滑窗逻辑
/// 而不必加载真实 ONNX 模型。
/// </summary>
internal interface ISenseVoiceRecognizing
{
    string Recognize(ReadOnlySpan<float> samples);

    /// <summary>终稿启用 LLM 校对时调用：除 1-best 文本外，同步提取局部声学低置信度备选（HINT-5）。</summary>
    RecognitionResult RecognizeWithHints(ReadOnlySpan<float> samples) =>
        new(Recognize(samples), Array.Empty<AcousticHint>());
}

/// <summary>
/// <see cref="AsrService"/> 持有的引擎抽象：识别 + 语言热更新 + 释放。生产实现是
/// <see cref="SenseVoiceEngine"/>；生命周期测试用假实现替身，避免加载真实 ONNX 模型。
/// 除 <see cref="Abort"/> 外，所有成员只应在 <see cref="AsrPump"/> 线程上调用。
/// </summary>
internal interface IAsrEngine : ISenseVoiceRecognizing, IDisposable
{
    void SetLanguage(AsrLanguage language);

    /// <summary>只在 <see cref="AsrPump"/> 线程上调用。编译在调用线程同步完成，返回报告（VOC-2）。
    /// 默认空实现：测试替身不需要。</summary>
    HotwordCompileReport? SetVocabulary(VocabularyConfig vocabulary) => null;

    /// <summary>最近一次 <see cref="ISenseVoiceRecognizing.Recognize"/> 的解码统计，只在 AsrPump 线程上读。</summary>
    DecodeStats LastDecodeStats => DecodeStats.Greedy;

    /// <summary>
    /// 请求中止正在进行的 <see cref="ISenseVoiceRecognizing.Recognize"/>（被中止的那次调用抛异常）。
    /// <b>唯一可以跨线程调用的成员</b>，用于松键时让终稿不必等一次已经没人要的预览。
    /// 终止标志一直保持到 <see cref="ResetAbort"/>，所以每个推理任务开始前必须先调用后者。
    /// 默认空实现：测试替身不需要。
    /// </summary>
    void Abort() { }

    /// <summary>清除 <see cref="Abort"/> 置位的终止标志。在推理线程上、新一轮推理开始前调用。</summary>
    void ResetAbort() { }
}

/// <summary>
/// SenseVoice-Small ONNX 推理封装：fbank → LFR/CMVN → ORT session → CTC 解码（无词汇时贪心，有词汇时带热词偏置的束搜索）。
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
internal sealed class SenseVoiceEngine : IAsrEngine
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
    private volatile bool _disposed;
    /// <summary>每次推理共用的运行选项；唯一用途是让 <see cref="Abort"/> 能从别的线程设置终止标志。
    /// 构造函数末尾才创建：前面的契约校验失败时没有原生资源需要释放。</summary>
    private readonly RunOptions _runOptions;

    /// <summary>词表前缀树与 token 类别：第一次 <see cref="SetVocabulary"/> 时懒构建，之后复用（与具体词汇无关）。</summary>
    private TokenTrie? _tokenTrie;
    private CtcPrefixBeamSearch.TokenClasses? _tokenClasses;
    /// <summary><c>&lt;asr_hints&gt;</c> 静态 token 属性表：首次提取备选时懒构建，之后复用（HINT-2）。</summary>
    private AcousticHintExtractor.TokenMetadata? _hintMetadata;
    /// <summary>当前词汇的上下文图；null = 走贪心解码，金标准与既有输出逐字节不变（VOC-D4）。</summary>
    private ContextGraph? _contextGraph;
    private CtcPrefixBeamSearch.Parameters? _beamParameters;

    public DecodeStats LastDecodeStats { get; private set; } = DecodeStats.Greedy;

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

        // 先解析小文件并完成维度/词表校验：这些步骤失败时还没有任何原生资源需要释放。
        var parsedCmvn = CmvnStats.Parse(bundle.CmvnPath);
        var parsedTokens = ModelLocator.LoadTokens(bundle.TokensPath);

        var expectedDim = bundle.LfrM * fbankOptions.NumMelBins;
        if (parsedCmvn.Means.Length != expectedDim || parsedCmvn.Vars.Length != expectedDim)
        {
            throw new ModelContractException(
                $"lfr_m({bundle.LfrM}) × n_mels({fbankOptions.NumMelBins}) = {expectedDim}，"
                + $"但 am.mvn 维度为 means={parsedCmvn.Means.Length} vars={parsedCmvn.Vars.Length}");
        }
        if (parsedTokens.Count <= 1)
        {
            throw new ModelContractException("tokens.json 词表为空或只有 1 个 token");
        }
        var frontend = new FbankFrontend(fbankOptions);

        // SessionOptions 在 InferenceSession 构造完成后不再被引用，用 using 确定释放。
        using var options = new SessionOptions
        {
            IntraOpNumThreads = threads > 0 ? threads : Math.Min(4, Environment.ProcessorCount),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
        };
        // 实测（macOS 侧）：这一行把常驻内存从 ~800MB 降到 ~510MB，30s 音频推理耗时几乎不变。
        // 详见 windows/DESIGN.md §4.2。Windows 上的实际数字需 P0 阶段用真机复测。
        options.AddSessionConfigEntry("session.disable_prepacking", "1");

        // session 所有权：构造成功并通过契约校验后才转交给字段；任何异常路径由 finally 释放。
        InferenceSession? session = null;
        try
        {
            session = new InferenceSession(bundle.OnnxFilePath, options);

            var actualInputNames = session.InputMetadata.Keys.ToHashSet();
            var actualOutputNames = session.OutputMetadata.Keys.ToHashSet();
            if (!ExpectedInputNames.All(actualInputNames.Contains) || !ExpectedOutputNames.All(actualOutputNames.Contains))
            {
                throw new ModelContractException(
                    $"模型 I/O 与 SenseVoice-Small 契约不符：期望输入 [{string.Join(", ", ExpectedInputNames)}] "
                    + $"输出 [{string.Join(", ", ExpectedOutputNames)}]，实际输入 [{string.Join(", ", actualInputNames)}] "
                    + $"输出 [{string.Join(", ", actualOutputNames)}]");
            }

            _session = session;
            session = null;
        }
        finally
        {
            session?.Dispose();
        }

        _runOptions = new RunOptions();
        _frontend = frontend;
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

    public HotwordCompileReport? SetVocabulary(VocabularyConfig vocabulary)
    {
        if (vocabulary.Entries.Count == 0)
        {
            _contextGraph = null;
            _beamParameters = null;
            return new HotwordCompileReport();
        }
        if (_tokenTrie is null)
        {
            _tokenTrie = new TokenTrie(_tokens);
            _tokenClasses = new CtcPrefixBeamSearch.TokenClasses(_tokens);
        }
        // 强度为「关」时也编译：报告照样说明哪些词条能参与偏置，只是不建图。
        var compiled = HotwordCompiler.Compile(vocabulary, _tokenTrie);
        var report = compiled.Report;
        if (CtcPrefixBeamSearch.Parameters.Preset(vocabulary.BiasValue) is { } parameters && compiled.Sequences.Count > 0)
        {
            _contextGraph = new ContextGraph(compiled.Sequences, parameters.PerTokenBias, parameters.MaxScoredDepth);
            _beamParameters = parameters;
        }
        else
        {
            _contextGraph = null;
            _beamParameters = null;
        }
        AppLog.Info("asr",
            $"词汇已编译：entries={report.Entries.Count} sequences={report.SequenceCount} "
            + $"biased={report.CountOf(HotwordCompileReport.Status.Biased)} too_short={report.CountOf(HotwordCompileReport.Status.TooShort)} "
            + $"unsupported={report.CountOf(HotwordCompileReport.Status.UnsupportedCharacters)} "
            + $"truncated={report.CountOf(HotwordCompileReport.Status.Truncated)} bias={vocabulary.BiasValue.ToYamlValue()} "
            + $"耗时={Stopwatch.GetElapsedTime(0, report.CompileTicks).TotalMilliseconds:F0}ms");
        return report;
    }

    public void Abort()
    {
        // 释放与中止可能交错（重载模型时旧引擎被释放）：此时推理早已结束，忽略即可。
        try { if (!_disposed) _runOptions.Terminate = true; }
        catch (ObjectDisposedException) { }
    }

    public void ResetAbort()
    {
        try { if (!_disposed) _runOptions.Terminate = false; }
        catch (ObjectDisposedException) { }
    }

    private delegate T DecodeAction<out T>(ReadOnlySpan<float> logits, int numFrames, int vocabSize);

    /// <param name="samples">16kHz / mono / float32，范围 [-1, 1]。</param>
    /// <returns>识别文本；样本不足一帧或识别为纯静音/纯标点时返回空字符串。</returns>
    public string Recognize(ReadOnlySpan<float> samples) =>
        RunInference(samples, "", Decode);

    /// <summary>终稿且启用 LLM 校对时调用：与 <see cref="Recognize"/> 共用同一推理主干，仅末步解码同步提取 <c>&lt;asr_hints&gt;</c>（HINT-5）。</summary>
    public RecognitionResult RecognizeWithHints(ReadOnlySpan<float> samples) =>
        RunInference(samples, new RecognitionResult("", Array.Empty<AcousticHint>()), DecodeWithHints);

    private T RunInference<T>(ReadOnlySpan<float> samples, T emptyValue, DecodeAction<T> decode)
    {
        // 提前返回（样本不足、静音）时不能留着上一次的统计。
        LastDecodeStats = new DecodeStats(
            _contextGraph is null ? DecodeStats.DecodeMode.Greedy : DecodeStats.DecodeMode.Beam, 0, 0);
        if (samples.Length < FbankFrontend.MinimumSamples) return emptyValue;

        var feats = _frontend.Compute(samples);
        if (feats.Length == 0) return emptyValue;
        var flatFeats = LfrCmvn.ApplyLfrCmvnFlat(feats, _lfrM, _lfrN, _cmvn, out int featsLen, out int dim);
        if (featsLen == 0) return emptyValue;

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

        using var results = _session.Run(inputs, new[] { "ctc_logits", "encoder_out_lens" }, _runOptions);
        var logitsResult = results.FirstOrDefault(r => r.Name == "ctc_logits");
        var lensResult = results.FirstOrDefault(r => r.Name == "encoder_out_lens");
        // 输出缺失是模型契约错误，不是合法静音——用 throw 让两者可区分（R2-09）。
        if (logitsResult is null || lensResult is null)
        {
            throw new ModelContractException("推理输出缺失 ctc_logits/encoder_out_lens");
        }

        var lensTensor = lensResult.AsTensor<int>();
        int validFrames = lensTensor[0];
        if (validFrames <= 0) return emptyValue;

        var logitsTensor = logitsResult.AsTensor<float>();
        var shape = logitsTensor.Dimensions;
        int vocabSize = shape[shape.Length - 1];
        if (vocabSize <= 0) return emptyValue;
        // 只有拿到推理输出的实际 shape 才能知道 vocabSize；此前不匹配时 CtcDecoder 会
        // 静默跳过越界 id，表现为"识别成功但缺字"，比报错更难排查（R2-09）。
        if (vocabSize != _tokens.Count)
        {
            throw new ModelContractException($"模型输出 vocab 维度({vocabSize}) 与词表大小({_tokens.Count}) 不一致");
        }

        int needed = validFrames * vocabSize;
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

        return decode(logitsSpan, validFrames, vocabSize);
    }

    private AcousticHintExtractor.TokenMetadata ResolvedHintMetadata() =>
        _hintMetadata ??= new AcousticHintExtractor.TokenMetadata(_tokens);

    /// <summary>终稿启用 LLM 校对时的解码与局部声学备选提取（HINT-1 / HINT-5 / HINT-7）。</summary>
    private RecognitionResult DecodeWithHints(ReadOnlySpan<float> logits, int numFrames, int vocabSize)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var meta = ResolvedHintMetadata();

        RecognitionResult FinishGreedy(ReadOnlySpan<float> data)
        {
            var (ids, aligned) = CtcDecoder.GreedyAlignedTokens(data, numFrames, vocabSize, _tokens);
            var raw = new System.Text.StringBuilder(ids.Length * 2);
            foreach (var id in ids)
            {
                if (id >= 0 && id < _tokens.Count) raw.Append(_tokens[id]);
            }
            var text = TextPostprocessor.Process(raw.ToString());
            var hints = string.IsNullOrEmpty(text)
                ? Array.Empty<AcousticHint>()
                : AcousticHintExtractor.Extract(data, numFrames, vocabSize, aligned, text, meta);
            LastDecodeStats = new DecodeStats(DecodeStats.DecodeMode.Greedy, 0, Stopwatch.GetTimestamp() - startedAt);
            return new RecognitionResult(text, hints);
        }

        if (_contextGraph is not { } graph || _beamParameters is not { } parameters || _tokenClasses is not { } classes)
        {
            return FinishGreedy(logits);
        }
        try
        {
            var result = CtcPrefixBeamSearch.Decode(logits, numFrames, vocabSize, graph, classes, parameters);
            var raw = new System.Text.StringBuilder(result.Tokens.Length * 2);
            foreach (var id in result.Tokens)
            {
                if (id >= 0 && id < _tokens.Count) raw.Append(_tokens[id]);
            }
            var text = TextPostprocessor.Process(raw.ToString());
            IReadOnlyList<AcousticHint> hints;
            if (string.IsNullOrEmpty(text))
            {
                hints = Array.Empty<AcousticHint>();
            }
            else if (result.HotwordHits > 0)
            {
                var aligned = AcousticHintExtractor.ForcedAlign(logits, numFrames, vocabSize, result.Tokens, _tokens, graph);
                if (aligned is not null)
                {
                    hints = AcousticHintExtractor.Extract(logits, numFrames, vocabSize, aligned, text, meta);
                }
                else
                {
                    AppLog.Warn("asr", "热词路径强制对齐异常，放弃本次声学备选提取");
                    hints = Array.Empty<AcousticHint>();
                }
            }
            else
            {
                var (_, aligned) = CtcDecoder.GreedyAlignedTokens(logits, numFrames, vocabSize, _tokens);
                hints = AcousticHintExtractor.Extract(logits, numFrames, vocabSize, aligned, text, meta);
            }
            LastDecodeStats = new DecodeStats(DecodeStats.DecodeMode.Beam, result.HotwordHits, Stopwatch.GetTimestamp() - startedAt);
            return new RecognitionResult(text, hints);
        }
        catch (CtcPrefixBeamSearch.DecodeException ex)
        {
            AppLog.Error("asr", "热词束搜索失败，回退贪心解码", ex);
            return FinishGreedy(logits);
        }
    }

    /// <summary>有可用热词时先运行束搜索，未采用热词时由解码器返回贪心 token。两条路径共用文本后处理。</summary>
    private string Decode(ReadOnlySpan<float> logits, int numFrames, int vocabSize)
    {
        var startedAt = Stopwatch.GetTimestamp();
        if (_contextGraph is not { } graph || _beamParameters is not { } parameters || _tokenClasses is not { } classes)
        {
            var greedy = CtcDecoder.Decode(logits, numFrames, vocabSize, _tokens);
            LastDecodeStats = new DecodeStats(DecodeStats.DecodeMode.Greedy, 0, Stopwatch.GetTimestamp() - startedAt);
            return greedy;
        }
        try
        {
            var result = CtcPrefixBeamSearch.Decode(logits, numFrames, vocabSize, graph, classes, parameters);
            var raw = new System.Text.StringBuilder();
            foreach (var id in result.Tokens)
            {
                if (id >= 0 && id < _tokens.Count) raw.Append(_tokens[id]);
            }
            LastDecodeStats = new DecodeStats(DecodeStats.DecodeMode.Beam, result.HotwordHits, Stopwatch.GetTimestamp() - startedAt);
            return TextPostprocessor.Process(raw.ToString());
        }
        catch (CtcPrefixBeamSearch.DecodeException ex)
        {
            // 束搜索的内部不一致不应让本次听写失败：记录后回退贪心结果（§5.8）。
            AppLog.Error("asr", "热词束搜索失败，回退贪心解码", ex);
            var greedy = CtcDecoder.Decode(logits, numFrames, vocabSize, _tokens);
            LastDecodeStats = new DecodeStats(DecodeStats.DecodeMode.Greedy, 0, Stopwatch.GetTimestamp() - startedAt);
            return greedy;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
        _runOptions.Dispose();
    }
}
