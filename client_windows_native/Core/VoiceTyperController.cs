using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Services;
using VoiceTyper.Support;

namespace VoiceTyper.Core;

/// <summary>
/// 中央状态机。串起热键 → 录音 → ASR → 文本插入。
/// 所有公共方法和事件回调都在 UI 线程上完成。
/// </summary>
internal sealed class VoiceTyperController : IDisposable
{
    /// <summary>
    /// 短于此时长的录音视为误触，直接丢弃：流式不发 finalize，非流式不发 HTTP 请求。
    /// 客户端侧约定，服务端不做强制校验。见 PROTOCOL.md §5.1。
    /// </summary>
    private static readonly TimeSpan MinimumRecordingDuration = TimeSpan.FromMilliseconds(300);

    public Action<AppStateInfo>? StateChanged;
    public Action<string>? PreviewUpdate;
    public Action<string>? RecognizedText;

    private readonly AppConfig _config;
    private readonly HotkeyService _hotkeyService;
    private readonly AudioCaptureService _audioService;
    private readonly TextInsertionService _textInsertion;

    private StreamingASRClient? _streamingClient;
    private List<byte[]>? _batchChunks;
    private string _accumulatedPreview = "";
    private bool _isRecording;
    private bool _isRunning;
    /// <summary>本轮录音真正开始的时间戳（Stopwatch tick），用于短录音过滤。</summary>
    private long _recordingStartedTicks;
    /// <summary>本轮录音因过短被判定丢弃；由 OnTailChunk 回调消费。</summary>
    private bool _discardCurrentSession;

    public bool IsRunning => _isRunning;

    public VoiceTyperController(AppConfig config)
    {
        _config = config;
        _hotkeyService = new HotkeyService();
        _audioService = new AudioCaptureService();
        _textInsertion = new TextInsertionService();
    }

    public void Start()
    {
        if (_isRunning) return;

        _hotkeyService.OnPress = BeginRecording;
        _hotkeyService.OnRelease = FinishRecording;
        _hotkeyService.Start(_config.Hotkey);

        _isRunning = true;
        StateChanged?.Invoke(AppStateInfo.Idle);
        AppLog.Info("controller", "Controller started");
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        _hotkeyService.Stop();
        _audioService.StopWithoutResult();
        TeardownStreamingClient();
        _batchChunks = null;
        _isRecording = false;
        _discardCurrentSession = false;
        AppLog.Info("controller", "Controller stopped");
    }

    public void Dispose()
    {
        Stop();
        _hotkeyService.Dispose();
        _audioService.Dispose();
    }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) =>
        ASRClient.HealthCheckAsync(_config.Server, ct);

    // ─── 录音生命周期 ─────────────────────────────────────────────

    private void BeginRecording()
    {
        if (!_isRunning || _isRecording) return;
        // 上一轮的丢弃标记若因异常路径未被 OnTailChunk 消费，这里兜底清掉。
        _discardCurrentSession = false;
        if (_config.Server.Streaming) BeginStreamingRecording();
        else BeginBatchRecording();
    }

    private void FinishRecording()
    {
        if (!_isRecording) return;
        _isRecording = false;

        // 短录音（误触）在这里就判定，交给 OnTailChunk 执行丢弃：
        // 走同一条收尾路径，避免录音流没被正常停掉。
        var elapsed = Stopwatch.GetElapsedTime(_recordingStartedTicks);
        _discardCurrentSession = elapsed < MinimumRecordingDuration;
        if (_discardCurrentSession)
        {
            AppLog.Info("controller", $"录音过短（{elapsed.TotalMilliseconds:F0}ms），已丢弃");
        }

        // 录音停止 → 触发 OnTailChunk → 流式：sendAudio(tail) + finalize；非流式：拼接整段后 POST
        _audioService.Stop();
    }

    /// <summary>
    /// 丢弃本轮过短录音的共同收尾：清预览、断连接、回到就绪。
    /// <paramref name="client"/> 为流式会话的连接，非流式路径传 null。
    /// </summary>
    private void DiscardShortSession(StreamingASRClient? client)
    {
        _discardCurrentSession = false;

        // 会话可能已被新一轮录音取代：只关掉自己的连接，不触碰当前会话的状态。
        if (client is not null && !ReferenceEquals(_streamingClient, client))
        {
            client.Close();
            return;
        }

        _accumulatedPreview = "";
        PreviewUpdate?.Invoke("");
        TeardownStreamingClient();
        if (!_isRecording) StateChanged?.Invoke(AppStateInfo.Idle);
    }

    // ─── 流式路径 ────────────────────────────────────────────────

    private void BeginStreamingRecording()
    {
        var client = new StreamingASRClient();

        // partial 是全量预览文本，直接替换（协议 v2，见 PROTOCOL.md §4.3）。
        // 服务端整段重跑会修正先前的文字，所以不能再做拼接。
        client.OnPartial = text =>
        {
            _accumulatedPreview = text;
            PreviewUpdate?.Invoke(_accumulatedPreview);
        };

        // 通过对象引用比较判断是否为当前会话；不是的话静默插入并关闭旧连接，
        // 不触碰当前会话的状态。
        client.OnFinal = text =>
        {
            if (ReferenceEquals(_streamingClient, client))
            {
                _accumulatedPreview = "";
                PreviewUpdate?.Invoke("");
                TeardownStreamingClient();
                HandleFinalText(text);
            }
            else
            {
                client.Close();
                var trimmed = (text ?? "").Trim();
                if (!_isRunning || string.IsNullOrEmpty(trimmed)) return;
                _textInsertion.Insert(trimmed);
                RecognizedText?.Invoke(trimmed);
            }
        };

        client.OnError = message =>
        {
            AppLog.Error("controller", $"ASR error: {message}");
            if (ReferenceEquals(_streamingClient, client))
            {
                TeardownStreamingClient();
                _accumulatedPreview = "";
                PreviewUpdate?.Invoke("");
                StateChanged?.Invoke(AppStateInfo.ErrorWith(message));
                _isRecording = false;
            }
            else
            {
                client.Close();
            }
        };

        // 先连接 WebSocket（异步），连接成功后再启动音频。
        // 连接是同步等待的（块在调用线程），但因为我们在 UI 线程，
        // 用 fire-and-forget Task.Run 避免卡 UI；启动录音放到连接完成的延续里。
        _streamingClient = client;
        _accumulatedPreview = "";

        _ = ConnectAndStartCaptureAsync(client);
    }

    private async Task ConnectAndStartCaptureAsync(StreamingASRClient client)
    {
        try
        {
            await client.ConnectAsync(_config.Server, _config.Server.LlmRecorrect).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("controller", "WebSocket 连接失败", ex);
            UiDispatcher.Post(() =>
            {
                if (ReferenceEquals(_streamingClient, client))
                {
                    TeardownStreamingClient();
                    StateChanged?.Invoke(AppStateInfo.ErrorWith("无法连接到识别服务"));
                }
                else
                {
                    client.Close();
                }
            });
            return;
        }

        UiDispatcher.Post(() => StartAudioCaptureForStreaming(client));
    }

    private void StartAudioCaptureForStreaming(StreamingASRClient client)
    {
        // 连接到位时，会话可能已经被新的录音覆盖（用户连按两次）。
        if (!ReferenceEquals(_streamingClient, client))
        {
            client.Close();
            return;
        }

        // 工作线程回调 → UI 线程后再 sendAudio（StreamingASRClient 自身是线程安全的，但保持模型一致）
        _audioService.OnChunk = data =>
        {
            UiDispatcher.Post(() => client.SendAudio(data));
        };
        _audioService.OnTailChunk = data =>
        {
            UiDispatcher.Post(() =>
            {
                // 误触录音：不发 finalize，直接断开连接。服务端会随连接关闭丢掉会话。
                if (_discardCurrentSession)
                {
                    DiscardShortSession(client);
                    return;
                }

                if (data.Length > 0) client.SendAudio(data);
                client.FinalizeStream();
                StateChanged?.Invoke(AppStateInfo.Recognizing);
            });
        };

        try
        {
            _audioService.Start();
        }
        catch (AudioStartException ex)
        {
            AppLog.Error("controller", "启动录音失败", ex);
            client.Close();
            _streamingClient = null;
            var msg = ex.IsAccessDenied ? "麦克风权限被拒绝，请在 Windows 设置中允许应用访问麦克风" : "开始录音失败";
            StateChanged?.Invoke(AppStateInfo.ErrorWith(msg));
            return;
        }

        // 计时从音频真正开始采集起算，不含 WebSocket 连接耗时。
        _recordingStartedTicks = Stopwatch.GetTimestamp();
        _isRecording = true;
        StateChanged?.Invoke(AppStateInfo.Recording);
    }

    // ─── 非流式路径 ──────────────────────────────────────────────

    private void BeginBatchRecording()
    {
        _batchChunks = new List<byte[]>();

        _audioService.OnChunk = data =>
        {
            UiDispatcher.Post(() => _batchChunks?.Add(data));
        };

        _audioService.OnTailChunk = data =>
        {
            UiDispatcher.Post(() =>
            {
                // 误触录音：不发出 HTTP 请求，直接丢掉已累积的音频。
                if (_discardCurrentSession)
                {
                    DiscardShortSession(null);
                    return;
                }

                if (data.Length > 0) _batchChunks?.Add(data);
                _ = PerformBatchRecognitionAsync();
            });
        };

        try
        {
            _audioService.Start();
        }
        catch (AudioStartException ex)
        {
            AppLog.Error("controller", "启动录音失败", ex);
            var msg = ex.IsAccessDenied ? "麦克风权限被拒绝，请在 Windows 设置中允许应用访问麦克风" : "开始录音失败";
            StateChanged?.Invoke(AppStateInfo.ErrorWith(msg));
            return;
        }

        _recordingStartedTicks = Stopwatch.GetTimestamp();
        _isRecording = true;
        StateChanged?.Invoke(AppStateInfo.Recording);
    }

    private async Task PerformBatchRecognitionAsync()
    {
        StateChanged?.Invoke(AppStateInfo.Recognizing);

        var chunks = _batchChunks ?? new List<byte[]>();
        _batchChunks = null;

        if (chunks.Count == 0)
        {
            StateChanged?.Invoke(AppStateInfo.Idle);
            return;
        }

        // 拼接为单段 float32 PCM
        int total = 0;
        foreach (var c in chunks) total += c.Length;
        var combined = new byte[total];
        int offset = 0;
        foreach (var c in chunks)
        {
            Buffer.BlockCopy(c, 0, combined, offset, c.Length);
            offset += c.Length;
        }


        try
        {
            using var client = new ASRClient(_config.Server);
            var text = await client.RecognizeAsync(combined, _config.Server.LlmRecorrect).ConfigureAwait(true);
            HandleFinalText(text);
        }
        catch (Exception ex)
        {
            AppLog.Error("controller", "批量识别失败", ex);
            StateChanged?.Invoke(AppStateInfo.ErrorWith($"识别失败：{ex.Message}"));
        }
    }

    // ─── 公共处理 ────────────────────────────────────────────────

    private void HandleFinalText(string text)
    {
        var trimmed = (text ?? "").Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            if (!_isRecording) StateChanged?.Invoke(AppStateInfo.Idle);
            return;
        }

        if (!_isRunning) return;

        StateChanged?.Invoke(AppStateInfo.Inserting);
        var inserted = _textInsertion.Insert(trimmed);
        if (inserted)
        {
            RecognizedText?.Invoke(trimmed);
            // 若此时已有新一轮录音正在进行，不要把状态拉回 Idle
            if (!_isRecording) StateChanged?.Invoke(AppStateInfo.Idle);
        }
        else
        {
            StateChanged?.Invoke(AppStateInfo.ErrorWith("文本插入失败"));
        }
    }

    private void TeardownStreamingClient()
    {
        _streamingClient?.Close();
        _streamingClient = null;
        _audioService.OnChunk = null;
        _audioService.OnTailChunk = null;
        _batchChunks = null;
    }
}
