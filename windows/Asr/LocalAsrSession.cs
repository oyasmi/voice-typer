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
/// - 300 秒会话上限 → 一次性 <c>OnWarning</c>，之后静默丢弃新音频
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

    private const int MaxSessionSamples = 300 * AppConstants.TargetSampleRate;

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
            _pendingAudio.AddRange(samples);
            return;
        }

        if (_buffer.SampleCount >= MaxSessionSamples)
        {
            if (!_capped)
            {
                _capped = true;
                OnWarning?.Invoke($"录音已达 {MaxSessionSamples / AppConstants.TargetSampleRate} 秒上限，后续音频不再录入");
            }
            return;
        }

        _buffer.Append(samples);
        SchedulePreview();
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
        var corrected = await _llmCorrector!.CorrectAsync(text).ConfigureAwait(true);
        if (_closed) return;
        OnFinal?.Invoke(corrected);
    }
}
