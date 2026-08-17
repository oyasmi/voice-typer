using System;
using System.Diagnostics;
using VoiceTyper.Asr;
using VoiceTyper.Llm;
using VoiceTyper.Services;
using VoiceTyper.Support;

namespace VoiceTyper.Core;

/// <summary>
/// 中央状态机。串起热键 → 录音 → 本地 ASR → 文本插入。所有公共方法和事件回调都在 UI 线程上完成。
///
/// 与 <c>client_windows_native/Core/VoiceTyperController.cs</c> 相比，改动只有 4 处
/// （见 windows/DESIGN.md §5.3）：删除非流式批量识别路径（本地引擎天然只有一条路径）、
/// <c>StreamingASRClient</c>（WebSocket）换成 <see cref="LocalAsrSession"/>（本地引擎，
/// 且不再需要"先连接再录音"的异步握手）、健康检查改为观察 <see cref="AsrService.State"/>、
/// 短录音过滤阈值保留但语义从"省流量"变为纯粹的"防误触"。
/// </summary>
internal sealed class VoiceTyperController : IDisposable
{
    /// <summary>
    /// 短于此时长的录音视为误触，直接丢弃。单进程架构下这不再是"省流量"的约定，
    /// 纯粹是防误触。见 PROTOCOL.md §5.1（该约定的历史出处）。
    /// </summary>
    private static readonly TimeSpan MinimumRecordingDuration = TimeSpan.FromMilliseconds(300);

    public Action<AppStateInfo>? StateChanged;
    public Action<string>? PreviewUpdate;
    public Action<string>? RecognizedText;

    private readonly AppConfig _config;
    private readonly AsrService _asrService;
    private readonly LlmCorrector? _llmCorrector;
    private readonly HotkeyService _hotkeyService;
    private readonly AudioCaptureService _audioService;
    private readonly TextInsertionService _textInsertion;

    private LocalAsrSession? _asrSession;
    private string _accumulatedPreview = "";
    private bool _isRecording;
    private bool _isRunning;
    /// <summary>本轮录音真正开始的时间戳（Stopwatch tick），用于短录音过滤。</summary>
    private long _recordingStartedTicks;
    /// <summary>本轮录音因过短被判定丢弃；由 OnTailChunk 回调消费。</summary>
    private bool _discardCurrentSession;

    public bool IsRunning => _isRunning;

    public VoiceTyperController(AppConfig config, AsrService asrService)
    {
        _config = config;
        _asrService = asrService;
        _hotkeyService = new HotkeyService();
        _audioService = new AudioCaptureService();
        _textInsertion = new TextInsertionService();

        if (config.Llm.Enabled && !string.IsNullOrWhiteSpace(config.Llm.BaseUrl))
        {
            _llmCorrector = new LlmCorrector(new LlmCorrector.Config
            {
                BaseUrl = config.Llm.BaseUrl,
                ApiKey = SecretStore.LoadLlmApiKey(),
                Model = config.Llm.Model,
                Temperature = config.Llm.Temperature,
                MaxTokens = config.Llm.MaxTokens,
                Timeout = config.Llm.Timeout,
            });
        }
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
        TeardownAsrSession();
        _isRecording = false;
        _discardCurrentSession = false;
        AppLog.Info("controller", "Controller stopped");
    }

    public void Dispose()
    {
        Stop();
        _hotkeyService.Dispose();
        _audioService.Dispose();
        _llmCorrector?.Dispose();
    }

    // ─── 录音生命周期 ─────────────────────────────────────────────

    private void BeginRecording()
    {
        if (!_isRunning || _isRecording) return;
        _discardCurrentSession = false;
        BeginLocalRecording();
    }

    private void FinishRecording()
    {
        if (!_isRecording) return;
        _isRecording = false;

        var elapsed = Stopwatch.GetElapsedTime(_recordingStartedTicks);
        _discardCurrentSession = elapsed < MinimumRecordingDuration;
        if (_discardCurrentSession)
        {
            AppLog.Info("controller", $"录音过短（{elapsed.TotalMilliseconds:F0}ms），已丢弃");
        }

        // 录音停止 → 触发 OnTailChunk → sendAudio(tail) + finalize
        _audioService.Stop();
    }

    /// <summary>
    /// 丢弃本轮过短录音的收尾：清预览、关闭会话、回到就绪。
    /// <paramref name="session"/> 可能已被新一轮录音取代：只关掉自己的会话，不触碰当前会话的状态。
    /// </summary>
    private void DiscardShortSession(LocalAsrSession? session)
    {
        _discardCurrentSession = false;

        if (session is not null && !ReferenceEquals(_asrSession, session))
        {
            session.Close();
            return;
        }

        _accumulatedPreview = "";
        PreviewUpdate?.Invoke("");
        TeardownAsrSession();
        if (!_isRecording) StateChanged?.Invoke(AppStateInfo.Idle);
    }

    // ─── 本地识别路径 ────────────────────────────────────────────

    private void BeginLocalRecording()
    {
        var session = _asrService.MakeSession(_llmCorrector);

        // partial 是全量预览文本，直接替换：本地识别引擎对已累积音频滑窗重跑，
        // 后一次结果会修正前一次的文字，增量语义无法表达这种回溯修改。
        session.OnPartial = text =>
        {
            _accumulatedPreview = text;
            PreviewUpdate?.Invoke(_accumulatedPreview);
        };

        // 通过对象引用比较判断是否为当前会话；不是的话静默插入并关闭旧会话，
        // 不触碰当前会话的状态（用户连按两次热键时会出现旧会话在后台完成的情况）。
        session.OnFinal = text =>
        {
            if (ReferenceEquals(_asrSession, session))
            {
                _accumulatedPreview = "";
                PreviewUpdate?.Invoke("");
                TeardownAsrSession();
                HandleFinalText(text);
            }
            else
            {
                session.Close();
                var trimmed = (text ?? "").Trim();
                if (!_isRunning || string.IsNullOrEmpty(trimmed)) return;
                InsertFinalText(trimmed);
            }
        };

        session.OnWarning = message =>
        {
            if (ReferenceEquals(_asrSession, session))
            {
                AppLog.Warn("controller", $"识别预览告警: {message}");
            }
        };

        session.OnError = message =>
        {
            AppLog.Error("controller", $"ASR error: {message}");
            if (ReferenceEquals(_asrSession, session))
            {
                TeardownAsrSession();
                _accumulatedPreview = "";
                PreviewUpdate?.Invoke("");
                StateChanged?.Invoke(AppStateInfo.ErrorWith(message));
                _isRecording = false;
            }
            else
            {
                session.Close();
            }
        };

        _audioService.OnChunk = data =>
        {
            UiDispatcher.Post(() => session.SendAudio(data));
        };
        _audioService.OnTailChunk = data =>
        {
            UiDispatcher.Post(() =>
            {
                // 误触录音：不发 finalize，直接关闭会话。
                if (_discardCurrentSession)
                {
                    DiscardShortSession(session);
                    return;
                }

                if (data.Length > 0) session.SendAudio(data);
                // 本地推理没有网络往返，但仍设看门狗防止模型卡死导致 HUD 永久停在"识别中"。
                session.FinalizeStream(TimeSpan.FromSeconds(30));
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
            session.Close();
            var msg = ex.IsAccessDenied ? "麦克风权限被拒绝，请在 Windows 设置中允许应用访问麦克风" : "开始录音失败";
            StateChanged?.Invoke(AppStateInfo.ErrorWith(msg));
            return;
        }

        _asrSession = session;
        // 计时从音频真正开始采集起算。
        _recordingStartedTicks = Stopwatch.GetTimestamp();
        _isRecording = true;
        _accumulatedPreview = "";
        StateChanged?.Invoke(AppStateInfo.Recording);
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

        InsertFinalText(trimmed);
    }

    private void InsertFinalText(string trimmed)
    {
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
            // UIPI 会阻止向提权窗口 SendInput；给出针对性提示而不是让用户以为识别坏了。
            // 文本插入前已先写入剪贴板，即便这里失败，用户通常仍可手动 Ctrl+V。
            var reason = TextInsertionService.IsForegroundWindowElevated()
                ? "目标窗口以管理员身份运行，Windows 安全机制阻止了输入注入，请手动粘贴"
                : "文本插入失败，请手动粘贴";
            StateChanged?.Invoke(AppStateInfo.ErrorWith(reason));
        }
    }

    private void TeardownAsrSession()
    {
        _asrSession?.Close();
        _asrSession = null;
        _asrService.SessionEnded();
        _audioService.OnChunk = null;
        _audioService.OnTailChunk = null;
    }
}
