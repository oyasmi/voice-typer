using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Llm;
using VoiceTyper.Support;

namespace VoiceTyper.Asr;

/// <summary>
/// 单次录音会话的本地识别接缝层。C# 直译自
/// <c>macos/Sources/VoiceTyper/ASR/LocalASRSession.swift</c>。接口**刻意与旧的
/// <c>StreamingASRClient</c>（WebSocket 客户端）逐字一致**，使
/// <see cref="Core.VoiceTyperController"/> 的四个回调可以原样搬运，只需把
/// <c>client.ConnectAsync(...)</c> 换成 <c>asrService.MakeSession(...)</c>。
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
internal sealed class LocalAsrSession
{
    public Action<string>? OnPartial;
    public Action<string>? OnFinal;
    public Action<string>? OnWarning;
    public Action<string>? OnError;
    /// <summary>
    /// 达到单段录音上限时触发一次：只发 <see cref="OnWarning"/> 无法让用户知道后续说的话已经
    /// 不会被录入（HUD 的警告闪烁只持续 1.2s，随后恢复"录音中"）。调用方应据此立即结束本次
    /// 录音、把已录到的内容正常上屏，而不是任由用户继续说下去、内容却被静默丢弃（R3-03）。
    /// </summary>
    public Action? OnSessionCapped;

    /// <summary>
    /// 单段录音上限：桌面听写场景 5 分钟不是合理假设，且更长的会话意味着更大的
    /// finalize 峰值内存与耗时。若要恢复到 300 秒，需先补 60/90/300 秒的峰值 RSS
    /// 与 finalize 耗时实测（F-07b）。
    /// </summary>
    private const int MaxSessionSamples = 120 * AppConstants.TargetSampleRate;

    private readonly AsrPump _pump;
    private readonly Func<SenseVoiceEngine?> _engineAccessor;
    private readonly LlmCorrector? _llmCorrector;
    private readonly int _previewWindowSamples;

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

    public LocalAsrSession(AsrPump pump, Func<SenseVoiceEngine?> engineAccessor, LlmCorrector? llmCorrector, int previewWindowSamples)
    {
        _pump = pump;
        _engineAccessor = engineAccessor;
        _llmCorrector = llmCorrector;
        _previewWindowSamples = previewWindowSamples;
    }

    public void SendAudio(byte[] data)
    {
        if (_closed || _isFinalizing) return;
        if (data.Length % 4 != 0)
        {
            OnWarning?.Invoke($"音频帧长度 {data.Length} 不是 4 的倍数，已丢弃");
            return;
        }
        if (data.Length == 0) return;

        var samples = new float[data.Length / 4];
        Buffer.BlockCopy(data, 0, samples, 0, data.Length);

        EnsureBufferIfPossible();
        if (_buffer is null)
        {
            // 引擎仍在加载：先攒着，下次 SendAudio（或 FinalizeStream）时补上。
            // _pendingAudio 同样严格不超过单段上限——引擎长时间不就绪时不能无限积累
            // （R3-03 同源）；单个超大 chunk 也只追加剩余容量内的部分。
            _pendingAudio.AddRange(AcceptWithinCap(samples, _pendingAudio.Count));
            return;
        }

        var accepted = AcceptWithinCap(samples, _buffer.SampleCount);
        if (accepted.Length == 0) return;

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
        OnWarning?.Invoke($"录音已达 {MaxSessionSamples / AppConstants.TargetSampleRate} 秒上限，自动结束本次听写");
        OnSessionCapped?.Invoke();
    }

    /// <param name="timeout">
    /// 等待识别完成的最长时间；本地无网络往返，这里纯粹是防止推理卡死的看门狗。
    /// 传 <see cref="TimeSpan.Zero"/> 或负值表示不设超时。
    /// </param>
    public void FinalizeStream(TimeSpan timeout)
    {
        if (_closed || _isFinalizing) return;
        _isFinalizing = true;
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
        _buffer = null;
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
            OnError?.Invoke("识别超时");
        });
    }

    private void EnsureBufferIfPossible()
    {
        if (_buffer is not null) return;
        var engine = _engineAccessor();
        if (engine is null) return;

        var newBuffer = new RecognitionBuffer(engine, _previewWindowSamples);
        if (_pendingAudio.Count > 0)
        {
            newBuffer.Append(_pendingAudio.ToArray());
            _pendingAudio.Clear();
        }
        _buffer = newBuffer;
    }

    private void SchedulePreview()
    {
        if (_isFinalizing || _previewInFlight || _buffer is null) return;
        _previewInFlight = true;
        var buffer = _buffer;

        _pump.Post(() =>
        {
            if (_cancelled) return;
            string? text = null;
            Exception? error = null;
            try { text = buffer.Preview(); }
            catch (Exception ex) { error = ex; }

            UiDispatcher.Post(() =>
            {
                _previewInFlight = false;
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

    private void WaitForEngineThenFinalize(int attempt = 0)
    {
        // 引擎加载耗时量级待 P0 实测；每 100ms 探测一次，最多等 5s——超过这个时间基本
        // 意味着模型加载失败，交给外层 FinalizeWatchdog 的超时兜底报错。
        if (attempt >= 50 || _closed)
        {
            if (!_closed)
            {
                _isFinalizing = false;
                OnError?.Invoke("识别引擎尚未就绪");
            }
            return;
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
                CompleteWithAsrText(text ?? "");
            });
        });
    }

    /// <summary>
    /// 拿到 ASR 原文后：若启用了 LLM 纠错，先把原文通过 OnPartial 顶到 HUD 上（让用户立刻看到
    /// 结果，纠错中不会像"卡住"），再异步纠错，最终以纠错结果调用 OnFinal。这一步在跨进程架构下
    /// 没有意义（要多一次网络往返），进程内是免费的。
    /// </summary>
    private void CompleteWithAsrText(string text)
    {
        if (_llmCorrector is null || string.IsNullOrWhiteSpace(text))
        {
            OnFinal?.Invoke(text);
            return;
        }

        OnPartial?.Invoke(text);
        _ = CorrectAndFinishAsync(text);
    }

    private async Task CorrectAndFinishAsync(string text)
    {
        var outcome = await _llmCorrector!.CorrectAsync(text).ConfigureAwait(true);
        if (_closed) return;
        if (outcome.DidFallBack)
        {
            // DESIGN.md 约定：纠错失败回落原文时要给用户一个非致命提示。这条 warning 可能
            // 在最终成功 HUD 展示前被覆盖，保持当前 UI 行为，不引入额外的 UI 调度。
            OnWarning?.Invoke("智能纠错未成功，已使用识别原文");
        }
        OnFinal?.Invoke(outcome.Text);
    }
}
