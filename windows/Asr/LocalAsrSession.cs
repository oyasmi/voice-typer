using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using VoiceTyper.Core;
using VoiceTyper.Llm;
using VoiceTyper.Support;

namespace VoiceTyper.Asr;

/// <summary>
/// 单次录音会话的本地识别接缝层，实现 <see cref="IDictationSession"/> 契约：
/// <see cref="Core.VoiceTyperController"/> 送入音频、请求收尾，并通过回调接收预览、最终文本与错误。
/// C# 直译自 <c>macos/Sources/VoiceTyper/ASR/LocalASRSession.swift</c>。
///
/// 职责映射（对照服务端 <c>app.StreamRecognizeHandler</c>）：
/// - 有预览在跑就跳过（<c>_previewInFlight</c>），跳过的音频在下次预览一并处理
/// - partial 只在文本变化时下发
/// - <c>_isFinalizing</c> 后不再补发 partial
/// - 预览异常 → <c>OnWarning</c>，会话继续
/// - 单段会话上限 → 一次性 <c>OnWarning</c> + <c>OnSessionCapped</c>，之后静默丢弃新音频
/// - finalize → 离线整段识别 → （可选）LLM 纠错 → <c>OnFinal</c>
///
/// 所有公共方法必须在 UI 线程调用；所有回调都在 UI 线程触发。
/// </summary>
internal sealed class LocalAsrSession : IDictationSession
{
    public Action<string>? OnPartial { get; set; }
    public Action<string>? OnFinal { get; set; }
    public Action<string>? OnWarning { get; set; }
    public Action<string>? OnError { get; set; }
    /// <summary>
    /// 达到单段录音上限时触发一次：只发 <see cref="OnWarning"/> 无法让用户知道后续说的话已经
    /// 不会被录入（HUD 的警告闪烁只持续 1.2s，随后恢复"录音中"）。调用方应据此立即结束本次
    /// 录音、把已录到的内容正常上屏，而不是任由用户继续说下去、内容却被静默丢弃（R3-03）。
    /// </summary>
    public Action? OnSessionCapped { get; set; }
    /// <summary>ASR 已出结果、开始等待 LLM 纠错时触发一次。没有这个信号时 HUD 会一直停在"识别中"，
    /// 用户分不清自己在等本地推理还是在等网络（后者可能长达 <c>llm.timeout</c> 秒）。</summary>
    public Action? OnCorrectionStarted { get; set; }
    /// <summary>引擎仍在加载、松键后的识别在等它。见 <see cref="IDictationSession.OnEngineWait"/>。</summary>
    public Action<bool>? OnEngineWait { get; set; }

    /// <summary>
    /// 单段录音上限：桌面听写场景 5 分钟不是合理假设，且更长的会话意味着更大的
    /// finalize 峰值内存与耗时。若要恢复到 300 秒，需先补 60/90/300 秒的峰值 RSS
    /// 与 finalize 耗时实测（F-07b）。
    /// </summary>
    private const int MaxSessionSamples = 120 * AppConstants.TargetSampleRate;

    /// <summary>
    /// 会话缓冲区的初始预留。多数听写在半分钟以内，按上限（约 7.7MB）预留既浪费又每次都进大对象堆；
    /// 超过后按倍数扩容（一次几 MB 的 memcpy，约一毫秒，只发生在少数长听写的 30s、60s 处）。
    /// </summary>
    private const int InitialBufferSamples = 30 * AppConstants.TargetSampleRate;

    private readonly AsrPump _pump;
    private readonly Func<IAsrEngine?> _engineAccessor;
    /// <summary>引擎加载已确定失败时返回原因文本（State==Failed/ModelMissing），否则 null。
    /// 让"等引擎"的轮询在加载失败时立刻带真实原因收尾，而不是空转到超时再报无信息的
    /// "识别引擎尚未就绪"（R2-2）。</summary>
    private readonly Func<string?>? _engineLoadError;
    private readonly LlmCorrector? _llmCorrector;
    private readonly int _previewWindowSamples;
    /// <summary>会话关闭时调用一次，归还 <see cref="AsrService"/> 的会话租约。</summary>
    private readonly Action? _onClosed;

    internal int PreviewWindowSamples => _previewWindowSamples;

    /// <summary>分阶段打点，会话收尾前由控制器读取并入 <see cref="DictationMetrics"/>。</summary>
    public AsrSessionTimings Timings { get; } = new();
    private bool _hasReceivedAudio;
    private long? _engineWaitStartedAt;
    /// <summary>已经通知过"在等引擎"（<see cref="OnEngineWait"/>(true)），就绪时需要对应地通知结束。</summary>
    private bool _engineWaitNotified;
    /// <summary>等引擎超过这么多次轮询（每次 100ms）才通知用户；多数冷恢复在几百毫秒内就绪，不值得闪一下提示。</summary>
    private const int EngineWaitNoticeAttempts = 3;

    /// <summary>
    /// 自上一次预览以来，是否收到过能量高于静音阈值的音频。SenseVoice 非流式，预览靠"对已累积
    /// 音频重跑"实现；如果这 600ms 全是静音，重跑一遍窗口只会得到和上次一样的结果，纯属浪费 CPU
    /// （用户思考停顿在口述里占比并不低）。跳过它对结果没有任何影响：松手后的 finalize 永远对完整
    /// 音频整段重跑（VW-10，对齐 macOS）。
    /// </summary>
    private bool _hasSpeechSinceLastPreview;

    private RecognitionBuffer? _buffer;
    /// <summary>引擎尚未加载完成时，音频先攒在这里；引擎就绪后的第一次 SendAudio 会把它们一并灌入 buffer。</summary>
    private readonly List<float> _pendingAudio = new();

    private bool _previewInFlight;
    private bool _isFinalizing;
    private bool _capped;
    private bool _closed;
    /// <summary>
    /// 与 <see cref="_closed"/> 同步置位，供 <see cref="AsrPump"/> 线程上的推理闭包在跑之前
    /// 短路判断（F-07d）：<see cref="Close"/> 之后已入队的闭包仍会执行，但不应该真的跑一遍
    /// CTC 解码只为丢弃结果。用 <c>volatile</c> 保证跨线程可见性。
    /// </summary>
    private volatile bool _cancelled;
    private string _lastPreview = "";
    private CancellationTokenSource? _finalizeWatchdogCts;
    /// <summary>会话级取消源：Close 时取消，传给 LLM 纠错请求，取消后的迟到结果被静默丢弃（R2-4）。</summary>
    private readonly CancellationTokenSource _sessionCts = new();
    /// <summary>仅覆盖"等 LLM 纠错"这一段，是 <see cref="_sessionCts"/> 的子令牌：<see cref="SkipCorrection"/>
    /// 只取消它，会话本身继续并以识别原文收尾。只在 UI 线程读写。</summary>
    private CancellationTokenSource? _correctionCts;
    private bool _correctionSkipped;

    public LocalAsrSession(AsrPump pump, Func<IAsrEngine?> engineAccessor, LlmCorrector? llmCorrector,
        int previewWindowSamples, Func<string?>? engineLoadError = null, Action? onClosed = null)
    {
        _onClosed = onClosed;
        _pump = pump;
        _engineAccessor = engineAccessor;
        _llmCorrector = llmCorrector;
        _previewWindowSamples = previewWindowSamples;
        _engineLoadError = engineLoadError;
    }

    public void SendAudio(byte[] data)
    {
        if (_closed || _isFinalizing) return;
        if (data.Length % 4 != 0)
        {
            OnWarning?.Invoke(L10n.F("音频帧长度 {0} 不是 4 的倍数，已丢弃", data.Length));
            return;
        }
        if (data.Length == 0) return;

        var samples = new float[data.Length / 4];
        Buffer.BlockCopy(data, 0, samples, 0, data.Length);

        EnsureBufferIfPossible();
        if (!_hasReceivedAudio)
        {
            _hasReceivedAudio = true;
            Timings.ColdAtStart = _buffer is null;
        }
        if (_buffer is null)
        {
            // 引擎仍在加载：先攒着，下次 SendAudio（或 FinalizeStream）时补上。
            // _pendingAudio 同样严格不超过单段上限——引擎长时间不就绪时不能无限积累
            // （R3-03 同源）；单个超大 chunk 也只追加剩余容量内的部分。
            var pending = AcceptWithinCap(samples, _pendingAudio.Count);
            _pendingAudio.AddRange(pending);
            Timings.ReceivedSamples += pending.Length;
            return;
        }

        var accepted = AcceptWithinCap(samples, _buffer.SampleCount);
        if (accepted.Length == 0) return;
        Timings.ReceivedSamples += accepted.Length;

        if (ContainsSpeech(accepted)) _hasSpeechSinceLastPreview = true;
        _buffer.Append(accepted);
        SchedulePreview();
    }

    /// <summary>
    /// 统一的单段上限裁剪：<see cref="_pendingAudio"/> 与已创建 buffer 共用。
    /// 返回可安全追加的样本前缀，保证 <c>currentCount + 返回值.Length &lt;= MaxSessionSamples</c>，
    /// 即单个 chunk 也不会跨越上限。仅当本次 chunk **严格超过**剩余容量时触发一次
    /// <see cref="OnWarning"/> + <see cref="OnSessionCapped"/>（控制器据此走与松键相同的收尾
    /// 路径）；恰好填满不触发，保留"下一入口才触发"的既有兼容语义。
    /// </summary>
    private float[] AcceptWithinCap(float[] samples, int currentCount)
    {
        var remaining = Math.Max(0, MaxSessionSamples - currentCount);
        if (samples.Length <= remaining) return samples;
        TriggerCapOnce();
        return samples[..remaining];
    }

    private void TriggerCapOnce()
    {
        if (_capped) return;
        _capped = true;
        OnWarning?.Invoke(L10n.F("录音已达 {0} 秒上限，自动结束本次听写", MaxSessionSamples / AppConstants.TargetSampleRate));
        OnSessionCapped?.Invoke();
    }

    /// <summary>
    /// 这一段音频里是否有可能是语音（线性 RMS 超过静音阈值）。判断只用于"要不要跑这次预览"，
    /// 判错的代价上限是多跑或少跑一次预览，最终文本不受影响——因此刻意用最简单的能量门限，
    /// 不引入真正的 VAD 模型。
    /// </summary>
    internal static bool ContainsSpeech(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return false;
        double sumSquares = 0;
        foreach (var sample in samples) sumSquares += sample * (double)sample;
        return Math.Sqrt(sumSquares / samples.Length) >= AppConstants.SilenceRmsThreshold;
    }

    /// <summary>按 100ms 窗口逐段判断：缓存音频可能很长，整体 RMS 会把一小段语音稀释到阈值以下。</summary>
    private static bool ContainsSpeechInWindows(ReadOnlySpan<float> samples)
    {
        const int window = AppConstants.TargetSampleRate / 10;
        for (int offset = 0; offset < samples.Length; offset += window)
        {
            if (ContainsSpeech(samples.Slice(offset, Math.Min(window, samples.Length - offset)))) return true;
        }
        return false;
    }

    /// <param name="timeout">
    /// 等待识别完成的最长时间；本地无网络往返，这里纯粹是防止推理卡死的看门狗。
    /// 传 <see cref="TimeSpan.Zero"/> 或负值表示不设超时。
    /// </param>
    public void FinalizeStream(TimeSpan timeout)
    {
        if (_closed || _isFinalizing) return;
        _isFinalizing = true;
        Timings.FinalizeStartedAt = Stopwatch.GetTimestamp();
        _finalizeWatchdogCts?.Cancel();

        if (timeout > TimeSpan.Zero)
        {
            var cts = new CancellationTokenSource();
            _finalizeWatchdogCts = cts;
            _ = RunFinalizeWatchdogAsync(timeout, cts);
        }

        EnsureBufferIfPossible();
        if (_buffer is null)
        {
            // 引擎仍未就绪（极短录音、模型刚好还没加载完）：等一小段时间重试，而不是立即报错——
            // Preload 已经在后台跑，多数情况下几百毫秒内就绪。
            _engineWaitStartedAt = Stopwatch.GetTimestamp();
            WaitForEngineThenFinalize();
            return;
        }
        RunFinalize(_buffer);
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _cancelled = true;
        _finalizeWatchdogCts?.Cancel();
        _finalizeWatchdogCts = null;
        try { _sessionCts.Cancel(); } catch (ObjectDisposedException) { }
        _buffer = null;
        _onClosed?.Invoke(); // 上方 _closed 守卫保证只释放一次
    }

    // ─── 私有实现 ──────────────────────────────────────────────

    private async Task RunFinalizeWatchdogAsync(TimeSpan timeout, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(timeout, cts.Token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return; // finalize 已在超时前完成，正常路径。
        }

        UiDispatcher.Post(() =>
        {
            if (_closed) return;
            AppLog.Error("asr", $"finalize 超时（{timeout.TotalSeconds}s）");
            OnError?.Invoke(L10n.T("识别超时"));
        });
    }

    private void EnsureBufferIfPossible()
    {
        if (_buffer is not null) return;
        var engine = _engineAccessor();
        if (engine is null) return;

        var newBuffer = new RecognitionBuffer(engine, _previewWindowSamples, reservedSampleCapacity: InitialBufferSamples);
        if (_pendingAudio.Count > 0)
        {
            // 缓存期间收到的音频没有走过 SendAudio 的语音判定；补上，否则引擎就绪后用户恰好停顿时
            // 预览会一直被跳过，直到松键。
            var cached = CollectionsMarshal.AsSpan(_pendingAudio);
            if (ContainsSpeechInWindows(cached)) _hasSpeechSinceLastPreview = true;
            newBuffer.Append(cached.ToArray());
            _pendingAudio.Clear();
            _pendingAudio.TrimExcess();
            _buffer = newBuffer;
            SchedulePreview();
            return;
        }
        _buffer = newBuffer;
    }

    private void SchedulePreview()
    {
        if (_isFinalizing || _previewInFlight || _buffer is null) return;
        // 这一轮没有新的语音：跳过整窗重跑，HUD 保持上一次的预览文本。
        if (!_hasSpeechSinceLastPreview)
        {
            Timings.PreviewSkipped++;
            return;
        }
        _hasSpeechSinceLastPreview = false;
        _previewInFlight = true;
        var buffer = _buffer;

        _pump.Post(() =>
        {
            if (_cancelled) return;
            string? text = null;
            Exception? error = null;
            var startedAt = Stopwatch.GetTimestamp();
            try { text = buffer.Preview(); }
            catch (Exception ex) { error = ex; }
            var elapsed = Stopwatch.GetTimestamp() - startedAt;

            UiDispatcher.Post(() =>
            {
                _previewInFlight = false;
                Timings.PreviewRuns++;
                Timings.PreviewMaxTicks = Math.Max(Timings.PreviewMaxTicks, elapsed);
                if (_closed || _isFinalizing) return;

                if (error is not null)
                {
                    AppLog.Warn("asr", $"预览识别异常: {error}");
                    OnWarning?.Invoke(error.Message);
                    return;
                }
                if (!string.IsNullOrEmpty(text) && text != _lastPreview)
                {
                    _lastPreview = text!;
                    OnPartial?.Invoke(text!);
                }
            });
        });
    }

    // 冷启动加载真实模型可能耗时十几秒；每 100ms 探测一次，最多等 20s。
    private const int MaxEngineWaitAttempts = 200;

    private void WaitForEngineThenFinalize(int attempt = 0)
    {
        if (_closed) return;

        // 加载已确定失败：立刻带真实原因收尾，不再空转（R2-2）。
        var loadError = _engineLoadError?.Invoke();
        if (loadError is not null)
        {
            _isFinalizing = false;
            OnError?.Invoke(loadError);
            return;
        }

        if (attempt >= MaxEngineWaitAttempts)
        {
            _isFinalizing = false;
            OnError?.Invoke(L10n.T("识别引擎加载超时，请稍后重试"));
            return;
        }

        if (attempt >= EngineWaitNoticeAttempts && !_engineWaitNotified)
        {
            _engineWaitNotified = true;
            OnEngineWait?.Invoke(true);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(100).ConfigureAwait(false);
            UiDispatcher.Post(() =>
            {
                if (_closed) return;
                EnsureBufferIfPossible();
                if (_buffer is not null)
                {
                    if (_engineWaitStartedAt is { } waitStart)
                    {
                        Timings.EngineWaitTicks = Stopwatch.GetTimestamp() - waitStart;
                    }
                    if (_engineWaitNotified)
                    {
                        _engineWaitNotified = false;
                        OnEngineWait?.Invoke(false);
                    }
                    RunFinalize(_buffer);
                }
                else
                {
                    WaitForEngineThenFinalize(attempt + 1);
                }
            });
        });
    }

    private void RunFinalize(RecognitionBuffer buffer)
    {
        _pump.Post(() =>
        {
            if (_cancelled) return;
            string? text = null;
            Exception? error = null;
            try { text = buffer.Finalize(); }
            catch (Exception ex) { error = ex; }

            UiDispatcher.Post(() =>
            {
                if (_closed) return;
                _finalizeWatchdogCts?.Cancel();
                _finalizeWatchdogCts = null;

                if (error is not null)
                {
                    AppLog.Error("asr", $"离线复识别失败: {error}");
                    OnError?.Invoke(error.Message);
                    return;
                }
                Timings.AsrCompletedAt = Stopwatch.GetTimestamp();
                CompleteWithAsrText(text ?? "");
            });
        });
    }

    /// <summary>
    /// 拿到 ASR 原文后：若启用了 LLM 纠错，先把原文通过 OnPartial 顶到 HUD 上（让用户立刻看到
    /// 结果，纠错中不会像"卡住"），再异步纠错，最终以纠错结果调用 OnFinal。这一步在跨进程架构下
    /// 没有意义（要多一次网络往返），进程内是免费的。
    /// </summary>
    internal void CompleteWithAsrText(string text)
    {
        if (_llmCorrector is null || string.IsNullOrWhiteSpace(text))
        {
            Timings.LlmResult = AsrSessionTimings.LlmOutcome.Off;
            OnFinal?.Invoke(text);
            return;
        }

        OnPartial?.Invoke(text);
        OnCorrectionStarted?.Invoke();
        Timings.LlmStartedAt = Stopwatch.GetTimestamp();
        _ = CorrectAndFinishAsync(text);
    }

    /// <summary>
    /// 放弃等待 LLM 纠错：取消在飞的请求并以识别原文收尾。用户等纠错时最想要的往往就是"别等了，
    /// 直接用原文"；没有在纠错（尚未开始、已结束、会话已关闭）时什么也不做。
    /// </summary>
    public void SkipCorrection()
    {
        if (_closed || _correctionSkipped || _correctionCts is not { } cts) return;
        _correctionSkipped = true;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task CorrectAndFinishAsync(string text)
    {
        LlmCorrector.CorrectionReport report;
        var correctionCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        _correctionCts = correctionCts;
        try
        {
            report = await _llmCorrector!.CorrectWithReportAsync(text, correctionCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            if (!_correctionSkipped || _closed) return; // 会话已取消：丢弃迟到的纠错结果。
            Timings.LlmCompletedAt = Stopwatch.GetTimestamp();
            Timings.LlmResult = AsrSessionTimings.LlmOutcome.Skipped;
            OnFinal?.Invoke(text);
            return;
        }
        finally
        {
            _correctionCts = null;
            correctionCts.Dispose();
        }
        if (_closed) return;
        Timings.LlmCompletedAt = Stopwatch.GetTimestamp();
        Timings.LlmRetriedWithoutThinking = report.Thinking == LlmCorrector.ThinkingParameterUsage.RejectedThenOmitted;
        if (report.Outcome.DidFallBack)
        {
            Timings.LlmResult = AsrSessionTimings.LlmOutcome.FellBack;
            // 控制器据此在成功提示上附带"已使用识别原文"的说明（InsertedWithCorrectionFallback）；
            // 这条 warning 本身会被随后的成功提示覆盖，只作即时反馈。
            OnWarning?.Invoke(L10n.T("智能纠错未成功，已使用识别原文"));
        }
        else
        {
            Timings.LlmResult = AsrSessionTimings.LlmOutcome.Corrected;
        }
        OnFinal?.Invoke(report.Outcome.Text);
    }
}
