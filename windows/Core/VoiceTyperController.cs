using System;
using System.Diagnostics;
using VoiceTyper.Asr;
using VoiceTyper.Llm;
using VoiceTyper.Services;
using VoiceTyper.Support;
using static VoiceTyper.Support.NativeMethods;

namespace VoiceTyper.Core;

/// <summary>
/// 中央状态机。串起热键 → 录音 → 本地 ASR → 文本插入。所有公共方法和事件回调都在 UI 线程上完成。
///
/// 与 <c>client-server/client_windows_native/Core/VoiceTyperController.cs</c> 相比，改动只有 4 处
/// （见 windows/DESIGN.md §5.3）：删除非流式批量识别路径（本地引擎天然只有一条路径）、
/// <c>StreamingASRClient</c>（WebSocket）换成 <see cref="LocalAsrSession"/>（本地引擎，
/// 且不再需要"先连接再录音"的异步握手）、健康检查改为观察 <see cref="AsrService.State"/>、
/// 短录音过滤阈值保留但语义从"省流量"变为纯粹的"防误触"。
/// </summary>
internal sealed class VoiceTyperController : IDisposable
{
    /// <summary>
    /// 短于此时长的录音视为误触，直接丢弃。单进程架构下这不再是"省流量"的约定，
    /// 纯粹是防误触。见 client-server/PROTOCOL.md §5.1（该约定的历史出处）。
    /// </summary>
    private static readonly TimeSpan MinimumRecordingDuration = TimeSpan.FromMilliseconds(300);

    public Action<AppStateInfo>? StateChanged;
    public Action<string>? PreviewUpdate;
    public Action<string>? RecognizedText;
    /// <summary>非致命提示（预览失败、上一段听写尚未完成等），UI 可短暂闪烁但不打断录音。</summary>
    public Action<string>? PreviewWarning;
    /// <summary>用户主动取消（录音中按 Esc）。与 Idle 区分，便于 UI 给出"已取消"提示。</summary>
    public Action? Cancelled;

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

        if (config.Llm.Enabled && LlmEndpoint.ChatCompletionsUrl(config.Llm.BaseUrl) is { } chatUrl)
        {
            var (apiKeyStatus, apiKey) = SecretStore.LoadLlmApiKeyResult();
            if (apiKeyStatus == SecretReadStatus.Failed)
            {
                AppLog.Error("controller", "无法读取已保存的 LLM API Key，本次运行禁用智能纠错");
            }
            else
            {
                _llmCorrector = new LlmCorrector(new LlmCorrector.Config
                {
                    ChatCompletionsUrl = chatUrl,
                    ApiKey = apiKey,
                    Model = config.Llm.Model,
                    Temperature = config.Llm.Temperature,
                    MaxTokens = config.Llm.MaxTokens,
                    Timeout = config.Llm.Timeout,
                });
            }
        }
        else if (config.Llm.Enabled)
        {
            AppLog.Error("controller", "LLM Base URL 无法解析为合法请求地址，本次运行禁用智能纠错");
        }
    }

    public void Start()
    {
        if (_isRunning) return;

        _hotkeyService.OnPress = BeginRecording;
        _hotkeyService.OnRelease = FinishRecording;
        _hotkeyService.OnCancel = CancelByUser;
        _hotkeyService.Start(_config.Hotkey);

        _audioService.OnDeviceChanged = () =>
        {
            if (_asrSession is null) return;
            PreviewWarning?.Invoke("输入设备已变化，本次录音已结束");
        };

        _isRunning = true;
        StateChanged?.Invoke(AppStateInfo.Idle);
        AppLog.Info("controller", "Controller started");
    }

    /// <summary>用户在录音过程中按 Esc 主动取消，通过 <see cref="Cancelled"/> 让 UI 给出"已取消"提示。</summary>
    private void CancelByUser()
    {
        if (!_isRecording) return;
        _isRecording = false;
        _discardCurrentSession = false;
        AppLog.Info("controller", "用户取消录音");

        _audioService.StopWithoutResult();
        _accumulatedPreview = "";
        PreviewUpdate?.Invoke("");
        TeardownAsrSession();
        Cancelled?.Invoke();
    }

    /// <summary>
    /// 暂停全局热键监听但不销毁进行中的听写（R2-04）：设置页保存热键、重新加载模型等
    /// 操作只应停掉按键钩子，不应该销毁用户正在说的内容。调用方须先确认当前没有进行中
    /// 的听写（<see cref="AppStateExtensions.IsActiveDictation"/>），否则应拒绝暂停请求。
    /// </summary>
    public void SuspendHotkeyListening() => _hotkeyService.Stop();

    /// <summary>恢复热键监听。<see cref="Start"/> 过才有效。</summary>
    public void ResumeHotkeyListening()
    {
        if (!_isRunning) return;
        _hotkeyService.OnPress = BeginRecording;
        _hotkeyService.OnRelease = FinishRecording;
        _hotkeyService.OnCancel = CancelByUser;
        _hotkeyService.Start(_config.Hotkey);
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

        // 录音开始时的前台窗口；插入前据此判断焦点是否已变化（F-10）。用局部变量而非
        // 实例字段捕获，天然按会话隔离——用户连按两次热键时，旧会话在后台完成也不会
        // 用到新会话的前台窗口快照。
        var expectedForegroundWindow = GetForegroundWindow();
        GetWindowThreadProcessId(expectedForegroundWindow, out var expectedForegroundProcessId);

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
                HandleFinalText(text, expectedForegroundWindow, expectedForegroundProcessId);
            }
            else
            {
                session.Close();
                var trimmed = (text ?? "").Trim();
                if (!_isRunning || string.IsNullOrEmpty(trimmed)) return;
                InsertFinalText(trimmed, expectedForegroundWindow, expectedForegroundProcessId);
            }
        };

        session.OnWarning = message =>
        {
            if (ReferenceEquals(_asrSession, session))
            {
                AppLog.Warn("controller", $"识别预览告警: {message}");
                PreviewWarning?.Invoke(message);
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

        // 达到单段录音上限：不能任由用户继续说下去而内容被静默丢弃，主动走一次与松键
        // 完全相同的收尾路径——FinishRecording() 会触发 OnTailChunk，把已录到的内容
        // 正常推进到 finalize 上屏（R3-03）。
        session.OnSessionCapped = () =>
        {
            if (ReferenceEquals(_asrSession, session))
            {
                FinishRecording();
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

    private void HandleFinalText(string text, IntPtr expectedForegroundWindow, uint expectedForegroundProcessId)
    {
        var trimmed = (text ?? "").Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            if (!_isRecording) StateChanged?.Invoke(AppStateInfo.Idle);
            return;
        }

        if (!_isRunning) return;

        InsertFinalText(trimmed, expectedForegroundWindow, expectedForegroundProcessId);
    }

    private void InsertFinalText(string trimmed, IntPtr expectedForegroundWindow, uint expectedForegroundProcessId)
    {
        StateChanged?.Invoke(AppStateInfo.Inserting);
        var result = _textInsertion.Insert(trimmed, expectedForegroundWindow, expectedForegroundProcessId);
        switch (result)
        {
            case TextInsertionResult.Inserted:
                RecognizedText?.Invoke(trimmed);
                // 若此时已有新一轮录音正在进行，不要把状态拉回 Idle
                if (!_isRecording) StateChanged?.Invoke(AppStateInfo.Idle);
                break;

            case TextInsertionResult.FocusChanged:
                // 录音开始到插入之间前台窗口已切换：不写入用户未预期的窗口，只复制到剪贴板。
                _textInsertion.CopyToClipboard(trimmed);
                AppLog.Warn("controller", "目标窗口已变化，插入已取消，改为复制到剪贴板");
                StateChanged?.Invoke(AppStateInfo.ErrorWith("目标窗口已变化，结果已复制到剪贴板"));
                break;

            case TextInsertionResult.Failed:
                // 插入失败兜底：把结果写入剪贴板，避免长听写内容彻底丢失。
                _textInsertion.CopyToClipboard(trimmed);
                // UIPI 会阻止向提权窗口 SendInput；给出针对性提示而不是让用户以为识别坏了。
                var reason = TextInsertionService.IsForegroundWindowElevated()
                    ? "目标窗口以管理员身份运行，Windows 安全机制阻止了输入注入，已复制到剪贴板，可手动粘贴"
                    : "插入失败，已复制到剪贴板，可手动粘贴";
                AppLog.Error("controller", "文本插入失败，已复制到剪贴板");
                StateChanged?.Invoke(AppStateInfo.ErrorWith(reason));
                break;
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
