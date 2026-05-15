using System;
using System.Collections.Generic;
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
    public Action<AppStateInfo>? StateChanged;
    public Action<string>? PreviewUpdate;
    public Action<string>? RecognizedText;

    private readonly AppConfig _config;
    private readonly IReadOnlyList<string> _hotwords;
    private readonly HotkeyService _hotkeyService;
    private readonly AudioCaptureService _audioService;
    private readonly TextInsertionService _textInsertion;

    private StreamingASRClient? _streamingClient;
    private List<byte[]>? _batchChunks;
    private string _accumulatedPreview = "";
    private bool _isRecording;
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public VoiceTyperController(AppConfig config, IReadOnlyList<string> hotwords)
    {
        _config = config;
        _hotwords = hotwords;
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
        if (_config.Server.Streaming) BeginStreamingRecording();
        else BeginBatchRecording();
    }

    private void FinishRecording()
    {
        if (!_isRecording) return;
        _isRecording = false;
        // 录音停止 → 触发 OnTailChunk → 流式：sendAudio(tail) + finalize；非流式：拼接整段后 POST
        _audioService.Stop();
    }

    // ─── 流式路径 ────────────────────────────────────────────────

    private void BeginStreamingRecording()
    {
        var client = new StreamingASRClient();

        client.OnPartial = fragment =>
        {
            _accumulatedPreview += fragment;
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
            await client.ConnectAsync(_config.Server, _hotwords, _config.Server.LlmRecorrect).ConfigureAwait(true);
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

        var hotwordsString = string.Join(" ", _hotwords);

        try
        {
            using var client = new ASRClient(_config.Server);
            var text = await client.RecognizeAsync(combined, hotwordsString, _config.Server.LlmRecorrect).ConfigureAwait(true);
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
