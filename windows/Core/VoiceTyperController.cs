using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using VoiceTyper.Asr;
using VoiceTyper.Llm;
using VoiceTyper.Services;
using VoiceTyper.Support;

namespace VoiceTyper.Core;

/// <summary>
/// 中央状态机。串起热键 → 录音 → 本地 ASR → 文本插入。所有公共方法和事件回调都在 UI 线程上完成。
/// 逻辑对应 <c>macos/Sources/VoiceTyper/Core/VoiceTyperController.swift</c>。
///
/// <b>一次听写 = 一个 <see cref="Utterance"/></b>，控制器是它的唯一强持有者，同一时刻至多一个
/// （主流程本就是单段 Idle → Recording → Recognizing → Inserting）。所有终止路径（识别完成、
/// 出错、Esc 取消、短录音丢弃、组合手势作废、Stop）都只走 <see cref="Finish"/>：靠"先取走
/// <c>_active</c> 再处理"保证幂等，已关闭会话的迟到回调不会二次收尾。
/// </summary>
internal sealed class VoiceTyperController : IDisposable
{
    private enum Phase { Recording, Recognizing }

    private sealed class Utterance
    {
        public required IDictationSession Session { get; init; }
        /// <summary>录音开始时的前台窗口，插入前据此判断焦点是否已变化。局部于本次听写，
        /// 天然按会话隔离。</summary>
        public required ForegroundTarget Target { get; init; }
        public required long StartedAt { get; init; }
        public required DictationMetrics Metrics { get; init; }
        public Phase Phase { get; set; } = Phase.Recording;
    }

    private abstract record Outcome
    {
        public sealed record Text(string Value) : Outcome;
        public sealed record Failed(string Message) : Outcome;
        public sealed record Cancelled : Outcome;
        /// <summary>过短录音（误触）丢弃。</summary>
        public sealed record Discarded : Outcome;
        /// <summary>单独修饰键被用作组合快捷键：静默丢弃，不弹"已取消"。</summary>
        public sealed record GestureDiscarded : Outcome;
        /// <summary>Stop()：控制器整体停止，不发任何状态变化，也不上报耗时。</summary>
        public sealed record Shutdown : Outcome;
    }

    /// <summary>
    /// 录音时长低于此阈值的会话直接丢弃。单进程架构下这不再是"省流量"的约定，纯粹是防误触
    /// （见 client-server/PROTOCOL.md §5.1 的历史出处）。
    /// </summary>
    private static readonly TimeSpan MinimumRecordingDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// 录音开始后的两次"是不是根本没采到声音"探测时刻。1.5s 足以越过设备启动延迟和用户按下热键后的
    /// 起始停顿；4.5s 再补一次，因为 HUD 的警告闪现只有 1.2s，一次很容易被没在看屏幕的人错过。
    /// 只探两次：真正静音说明设备有问题，多提示无益；正常说话时第一次探测就会被电平否掉，不会打扰。
    /// </summary>
    internal static readonly TimeSpan[] SilenceProbeDelays = { TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4.5) };

    // ─── 事件（均在 UI 线程触发）─────────────────────────────────

    public Action<AppStateInfo>? StateChanged;
    public Action<string>? PreviewUpdate;
    public Action<string>? RecognizedText;
    /// <summary>非致命提示（预览失败、上一段听写尚未完成等），UI 可短暂闪烁但不打断录音。</summary>
    public Action<string>? PreviewWarning;
    /// <summary>用户主动取消（录音中或识别中按 Esc）。与 Idle 区分，便于 UI 给出"已取消"提示。</summary>
    public Action? Cancelled;
    /// <summary>识别成功但结果为空（没说话、麦克风静音、环境噪声被判为无语音）。与"插入成功"和
    /// "识别失败"都要区分：否则 HUD 静默消失，用户分不清是没识别到还是插进去了没看见。</summary>
    public Action? EmptyRecognition;
    /// <summary>未就绪时按下热键。参数是 <see cref="BlockedReason"/> 的内容，UI 据此引导用户。</summary>
    public Action<string>? BlockedAttempt;
    /// <summary>ASR 已出结果、开始等待 LLM 纠错。UI 据此把"识别中"改成"纠错中"。</summary>
    public Action? CorrectionStarted;
    /// <summary>录音期间的实时音量电平（0…1 量级），供 HUD 波形显示。</summary>
    public Action<float>? AudioLevel;
    /// <summary>每次听写收尾时恰好触发一次（<see cref="Stop"/> 除外），内容只含数字与枚举。</summary>
    public Action<DictationMetrics>? MetricsReported;

    private readonly AppConfig _config;
    private readonly IDictationSessionFactory _sessions;
    private readonly IHotkeyListening _hotkey;
    private readonly IAudioCapturing _audio;
    private readonly ITextInserting _text;
    private readonly LlmCorrector? _llm;
    private readonly Func<long> _now;
    private readonly Action<Action> _post;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;

    private Utterance? _active;
    private bool _isRunning;
    private string _previewText = "";
    private float _recordingPeakLevel;
    private readonly List<IDisposable> _silenceProbes = new();
    private string? _blockedReason;

    /// <summary>
    /// 非 null 表示"热键监听已启动，但现在还不能听写"，字符串是给用户看的原因（模型还在下载…）。
    /// 存在的意义：此前只要没完全就绪，协调器就干脆不启动控制器，于是按下热键什么都不会发生——
    /// 用户得到的反馈与"应用挂了"完全一致，无从判断缺什么。现在热键照常监听，按下时把缺什么说出来。
    /// </summary>
    public string? BlockedReason
    {
        get => _blockedReason;
        set
        {
            _blockedReason = value;
            // 门禁期间不可能有进行中的听写，顺手关掉识别阶段的 Esc 取消窗口。
            if (value is not null) _hotkey.AcceptsCancelWhenInactive = false;
        }
    }

    public bool IsRunning => _isRunning;

    /// <summary>派生自 <see cref="_active"/>，不是独立事实源。</summary>
    private bool IsRecording => _active?.Phase == Phase.Recording;

    /// <summary>当前录音实际使用的输入设备名，供 HUD 显示；Start 成功后才有值。</summary>
    public string? RecordingInputDeviceName
    {
        get
        {
            var name = _audio.ActiveDevice?.Name?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
    }

    /// <summary>生产环境入口：装配真实的钩子 / 麦克风 / 剪贴板服务。</summary>
    public VoiceTyperController(AppConfig config, AsrService asrService)
        : this(config, asrService, new HotkeyService(), new AudioCaptureService(), new TextInsertionService(),
            CreateLlmCorrector(config))
    {
    }

    /// <param name="post">把音频线程上的回调送回 UI 线程；测试传同步实现。</param>
    /// <param name="schedule">延迟执行（静音探测）；测试传手动触发的实现。回调必须在 UI 线程执行。</param>
    public VoiceTyperController(
        AppConfig config,
        IDictationSessionFactory sessions,
        IHotkeyListening hotkey,
        IAudioCapturing audio,
        ITextInserting text,
        LlmCorrector? llmCorrector,
        Func<long>? now = null,
        Action<Action>? post = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _config = config;
        _sessions = sessions;
        _hotkey = hotkey;
        _audio = audio;
        _text = text;
        _llm = llmCorrector;
        _now = now ?? Stopwatch.GetTimestamp;
        _post = post ?? UiDispatcher.Post;
        _schedule = schedule ?? DefaultSchedule;
    }

    private IDisposable DefaultSchedule(TimeSpan delay, Action action)
    {
        var post = _post;
        return new System.Threading.Timer(_ => post(action), null, delay, Timeout.InfiniteTimeSpan);
    }

    private static LlmCorrector? CreateLlmCorrector(AppConfig config)
    {
        if (!config.Llm.Enabled) return null;

        if (LlmEndpoint.ChatCompletionsUrl(config.Llm.BaseUrl) is not { } chatUrl)
        {
            AppLog.Error("controller", "LLM Base URL 无法解析为合法请求地址，本次运行禁用智能纠错");
            return null;
        }
        var (apiKeyStatus, apiKey) = SecretStore.LoadLlmApiKeyResult();
        if (apiKeyStatus == SecretReadStatus.Failed)
        {
            AppLog.Error("controller", "无法读取已保存的 LLM API Key，本次运行禁用智能纠错");
            return null;
        }
        return new LlmCorrector(new LlmCorrector.Config
        {
            ChatCompletionsUrl = chatUrl,
            ApiKey = apiKey,
            Model = config.Llm.Model,
            Temperature = config.Llm.Temperature,
            MaxTokens = config.Llm.MaxTokens,
            Timeout = config.Llm.Timeout,
        }, capabilityStore: FileLlmCapabilityStore.Shared);
    }

    // ─── 生命周期 ─────────────────────────────────────────────────

    public void Start()
    {
        if (_isRunning) return;

        BindHotkeyCallbacks();

        // 电平回调在音频线程触发，跳回 UI 线程再转发。音频引擎仅在录音期间运行，
        // 故无需按会话单独装卸此回调。
        _audio.OnLevel = level =>
        {
            // 首帧时间必须在音频线程上取，跳到 UI 线程之后再取会把调度延迟算进去。
            var receivedAt = _now();
            _post(() =>
            {
                if (_active is { } utterance && utterance.Metrics.FirstBufferAt is null && receivedAt >= utterance.Metrics.PressedAt)
                {
                    utterance.Metrics.FirstBufferAt = receivedAt;
                }
                _recordingPeakLevel = Math.Max(_recordingPeakLevel, level);
                AudioLevel?.Invoke(level);
            });
        };
        _audio.OnDeviceChanged = HandleDeviceChanged;

        _hotkey.Start(_config.Hotkey);
        _isRunning = true;
        // 被门禁时不能发 Idle：那会把协调器的当前状态从"下载中"覆盖成"就绪"，托盘显示与真实能力脱节。
        // 就绪后由协调器自己置 Idle。
        if (_blockedReason is null) StateChanged?.Invoke(AppStateInfo.Idle);
        AppLog.Info("controller", "Controller started");
    }

    private void BindHotkeyCallbacks()
    {
        _hotkey.OnPress = HandleHotkeyPress;
        _hotkey.OnRelease = HandleHotkeyRelease;
        _hotkey.OnCancel = CancelByUser;
        _hotkey.OnGestureCancelled = DiscardByGesture;
        _hotkey.OnHealthChanged = OnHotkeyHealthChanged;
    }

    /// <summary>热键监听健康状态变化（R3-5）：失效时向用户显示不可用，恢复后回到就绪。</summary>
    private void OnHotkeyHealthChanged(bool healthy)
    {
        if (!_isRunning) return;
        if (!healthy)
        {
            AppLog.Error("controller", "热键监听已失效，自愈重试中");
            StateChanged?.Invoke(AppStateInfo.ErrorWith(L10n.T("热键监听已失效，正在尝试自动恢复")));
        }
        else if (_active is null && _blockedReason is null)
        {
            StateChanged?.Invoke(AppStateInfo.Idle);
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _hotkey.AcceptsCancelWhenInactive = false;
        CancelSilenceProbes();
        _hotkey.Stop();
        _audio.StopWithoutResult();
        if (_active is not null) Finish(new Outcome.Shutdown());
        _isRunning = false;
        AppLog.Info("controller", "Controller stopped");
    }

    /// <summary>
    /// 暂停全局热键监听但不销毁进行中的听写（R2-04）：设置页保存热键、重新加载模型等
    /// 操作只应停掉按键钩子，不应该销毁用户正在说的内容。调用方须先确认当前没有进行中
    /// 的听写（<see cref="AppStateExtensions.IsActiveDictation"/>），否则应拒绝暂停请求。
    /// </summary>
    public void SuspendHotkeyListening() => _hotkey.Stop();

    /// <summary>
    /// 恢复热键监听。<see cref="Start"/> 过才有效。
    ///
    /// 恢复失败时把 <see cref="_isRunning"/> 复位为 false 再把异常抛出：
    /// <see cref="SuspendHotkeyListening"/> 已经停掉了底层键盘钩子，如果这里失败又不复位，
    /// 控制器会"自认为在跑、实际热键已死"，UI 却显示就绪。复位后交由外层
    /// <c>AppCoordinator.ActivateReadyState()</c> → <see cref="Start"/> 的重试路径重新拉起
    /// （失败会落到 .error）。对齐 macOS <c>VoiceTyperController.resumeHotkeyListening</c>。
    /// </summary>
    public void ResumeHotkeyListening()
    {
        if (!_isRunning) return;
        BindHotkeyCallbacks();
        try
        {
            _hotkey.Start(_config.Hotkey);
        }
        catch
        {
            _isRunning = false;
            throw;
        }
    }

    public void Dispose()
    {
        Stop();
        _hotkey.Dispose();
        _audio.Dispose();
        _llm?.Dispose();
    }

    // ─── 热键分发 ─────────────────────────────────────────────────

    /// <summary>
    /// 热键按下的唯一入口，按 <c>hotkey.mode</c> 分发。
    /// Hold：按下开始，交给 <see cref="HandleHotkeyRelease"/> 结束（默认）。
    /// Toggle：按下开始，<b>再按一次</b>结束；松开事件被忽略。
    /// </summary>
    private void HandleHotkeyPress()
    {
        var pressedAt = _now();
        if (!_isRunning) return;
        // 门禁只拦"新开一段听写"。已有听写进行中时（例如空闲卸载后首次按热键触发了模型加载，
        // 协调器因此把门禁置上）放行，否则按住模式下松键、切换模式下再按一次都会被吞掉，
        // 录音停不下来。
        if (_active is null && _blockedReason is { } reason)
        {
            // 单独修饰键在每次组合快捷键时都会"按下"，此时提示会变成骚扰；
            // 改为在干净的单击（松开）时才说明原因，见 HandleHotkeyRelease。
            if (!_config.Hotkey.IsModifierOnly) BlockedAttempt?.Invoke(reason);
            return;
        }

        switch (_config.Hotkey.ModeValue)
        {
            case HotkeyMode.Hold:
                BeginRecording(pressedAt);
                break;
            case HotkeyMode.Toggle:
                // 单独修饰键的 toggle：只有干净的单击才算，改在 Release 里切换，
                // 这样按住右 Ctrl 再按 C 之类的组合快捷键不会误开录音。
                if (_config.Hotkey.IsModifierOnly) return;
                if (IsRecording) FinishRecording();
                // 非录音态（含 _active 已进入 Recognizing）统一走 BeginRecording：
                // 它自带"上一段听写尚未完成"的拒绝分支，toggle 模式无需重复判断。
                else BeginRecording(pressedAt);
                break;
        }
    }

    /// <summary>热键松开。仅 Hold 模式有意义；Toggle 模式下松开不是结束信号（单独修饰键除外）。</summary>
    private void HandleHotkeyRelease()
    {
        if (!_isRunning) return;
        if (_active is null && _blockedReason is { } reason)
        {
            if (_config.Hotkey.IsModifierOnly) BlockedAttempt?.Invoke(reason);
            return;
        }

        switch (_config.Hotkey.ModeValue)
        {
            case HotkeyMode.Hold:
                FinishRecording();
                break;
            case HotkeyMode.Toggle:
                // 普通热键的松开不是结束信号；单独修饰键的松开才是"一次干净的单击"。
                if (!_config.Hotkey.IsModifierOnly) return;
                if (IsRecording) FinishRecording();
                else BeginRecording(_now());
                break;
        }
    }

    /// <summary>单独修饰键（Hold 模式）在按住期间被用作组合快捷键：静默丢弃本次录音，不弹任何提示。</summary>
    private void DiscardByGesture()
    {
        if (_config.Hotkey.ModeValue != HotkeyMode.Hold || _active is not { Phase: Phase.Recording }) return;
        _audio.StopWithoutResult();
        Finish(new Outcome.GestureDiscarded());
    }

    /// <summary>
    /// 用户按 Esc 主动取消，通过 <see cref="Cancelled"/> 让 UI 给出"已取消"提示。
    /// 录音中与识别中都受理。识别中取消<b>不会</b>中断已经在推理线程上跑的那次推理（不做盲改），
    /// 但会丢弃它的结果、不插入任何文本——用户真正要的是"别把这段写进去"，而不是省下几百毫秒 CPU。
    /// </summary>
    private void CancelByUser()
    {
        if (_active is not { } utterance) return;
        if (utterance.Phase == Phase.Recording)
        {
            _audio.StopWithoutResult();
        }
        // 识别阶段采集早已在松键时停止；这里只需走收尾，让 Session.Close() 抑制迟到回调。
        Finish(new Outcome.Cancelled());
    }

    // ─── 录音生命周期 ─────────────────────────────────────────────

    private void BeginRecording(long pressedAt)
    {
        if (!_isRunning) return;
        if (_active is not null)
        {
            // 上一段听写仍在识别 / 插入：拒绝新会话并给出可见反馈（R3-1）。
            PreviewWarning?.Invoke(L10n.T("上一段听写尚未完成，请稍候再试"));
            return;
        }
        BeginDictationSession(pressedAt);
    }

    private void FinishRecording()
    {
        if (_active is not { Phase: Phase.Recording } utterance) return;
        utterance.Metrics.ReleasedAt = _now();

        // 短录音过滤：低于阈值的录音视为误触，立即取消。
        if (Stopwatch.GetElapsedTime(utterance.StartedAt, _now()) < MinimumRecordingDuration)
        {
            AppLog.Info("controller", "录音时长低于阈值，已丢弃");
            _audio.StopWithoutResult();
            Finish(new Outcome.Discarded());
            return;
        }

        // Stop() 触发 OnTailChunk → SendAudio(tail) + FinalizeStream，Phase 在那里推进到 Recognizing。
        _audio.Stop();
    }

    /// <summary>录音期间输入设备变化：采集服务已自行走过收尾路径把已采到的音频交给当前会话继续识别，
    /// 这里只需要把"设备变了"这件事明确告知用户（R2-03）。</summary>
    private void HandleDeviceChanged()
    {
        if (_active is null) return;
        PreviewWarning?.Invoke(L10n.T("输入设备已变化，本次录音已结束"));
    }

    /// <summary>会话达到单段录音上限：不能任由用户继续说下去而内容被静默丢弃，主动走一次与松键完全
    /// 相同的收尾路径——<c>Stop()</c> 会触发 OnTailChunk，把已录到的内容正常推进到 Recognizing 并上屏（R3-03）。</summary>
    private void HandleSessionCapped()
    {
        if (_active is not { Phase: Phase.Recording }) return;
        _audio.Stop();
    }

    // ─── 识别路径 ─────────────────────────────────────────────────

    private void BeginDictationSession(long pressedAt)
    {
        var session = _sessions.MakeSession(_llm);
        var metrics = new DictationMetrics
        {
            SessionId = (ushort)Random.Shared.Next(0x10000),
            Mode = _config.Hotkey.ModeValue,
            Hotkey = _config.Hotkey.IsModifierOnly ? HotkeyKind.Modifier : HotkeyKind.Combo,
            PressedAt = pressedAt,
        };
        var utterance = new Utterance
        {
            Session = session,
            Target = _text.CaptureForegroundTarget(),
            StartedAt = _now(),
            Metrics = metrics,
        };

        // 回调只认"当前会话"：Finish 会同步关闭会话，正常情况下不会有迟到回调；这里的身份校验是
        // 防御——连按热键时旧会话的排队回调绝不能碰新会话的状态。
        bool IsCurrent() => ReferenceEquals(_active, utterance);

        // partial 是全量预览文本，直接替换：本地识别引擎对已累积音频滑窗重跑，后一次结果会修正
        // 前一次的文字，增量语义无法表达这种回溯修改。
        session.OnPartial = text =>
        {
            if (!IsCurrent()) return;
            _previewText = text;
            PreviewUpdate?.Invoke(_previewText);
        };
        session.OnFinal = text =>
        {
            if (IsCurrent()) Finish(new Outcome.Text(text));
        };
        session.OnWarning = message =>
        {
            if (!IsCurrent()) return;
            AppLog.Warn("controller", "识别预览告警");
            PreviewWarning?.Invoke(message);
        };
        session.OnError = message =>
        {
            // 具体识别异常已由会话在产生错误的位置记录；控制器只负责把错误转换为统一的收尾状态。
            if (IsCurrent()) Finish(new Outcome.Failed(message));
        };
        session.OnSessionCapped = () =>
        {
            if (IsCurrent()) HandleSessionCapped();
        };
        session.OnCorrectionStarted = () =>
        {
            if (IsCurrent()) CorrectionStarted?.Invoke();
        };

        _audio.OnChunk = data => _post(() =>
        {
            if (IsCurrent()) session.SendAudio(data);
        });
        _audio.OnTailChunk = data => _post(() =>
        {
            if (!IsCurrent()) return;
            if (data.Length > 0) session.SendAudio(data);
            utterance.Phase = Phase.Recognizing;
            CancelSilenceProbes(); // 录音已结束，不再需要"没采到声音"的提示
            // 设备变化 / 达到会话上限时没有"松手"，以尾音到达的时刻作为收音结束点。
            var tailAt = _now();
            utterance.Metrics.ReleasedAt ??= tailAt;
            utterance.Metrics.FinalizeCalledAt = tailAt;
            // 松手之后到结果上屏之前，Esc 仍可取消。收尾时由 Finish 统一关闭这个窗口。
            _hotkey.AcceptsCancelWhenInactive = true;
            // 本地推理没有网络往返，但仍设看门狗防止模型卡死导致 HUD 永久停在"识别中"。
            session.FinalizeStream(TimeSpan.FromSeconds(30));
            StateChanged?.Invoke(AppStateInfo.Recognizing);
        });

        try
        {
            _audio.Start(AudioInputPolicy.FromConfigValue(_config.Audio.InputDevice));
        }
        catch (AudioStartException ex)
        {
            AppLog.Error("controller", "启动录音失败", ex);
            session.Close();
            _sessions.SessionEnded();
            _audio.OnChunk = null;
            _audio.OnTailChunk = null;
            metrics.Outcome = DictationOutcome.StartFailed;
            metrics.DoneAt = _now();
            MetricsReported?.Invoke(metrics);
            StateChanged?.Invoke(AppStateInfo.ErrorWith(ex.IsAccessDenied
                ? L10n.T("麦克风权限被拒绝，请在 Windows 设置中允许应用访问麦克风")
                : L10n.T("开始录音失败")));
            return;
        }

        metrics.CaptureStartedAt = _now();
        if (_audio.ActiveDevice is { } device)
        {
            metrics.InputTransport = device.Transport;
            metrics.InputSwitchedByAuto = device.SwitchedByAuto;
        }
        _active = utterance;
        _previewText = "";
        _recordingPeakLevel = 0;
        // 切换模式下录音阶段没有"按住的主键"，Esc 必须在整个听写期间都能取消。
        _hotkey.AcceptsCancelWhenInactive = true;
        ScheduleSilenceProbes();
        StateChanged?.Invoke(AppStateInfo.Recording);
    }

    /// <summary>
    /// 录音开始后若迟迟采不到声音，主动提示检查输入设备。麦克风被静音、系统选中了错误的输入设备、
    /// 蓝牙耳机走了错误的输入端——这几种情况下用户会对着一个什么都没录到的会话一直说，直到松手
    /// 才发现结果是空的。这段录音时间是白花的，而信号（电平一直为 0）在第一秒就已经有了。
    /// </summary>
    private void ScheduleSilenceProbes()
    {
        CancelSilenceProbes();
        foreach (var delay in SilenceProbeDelays)
        {
            var seconds = delay.TotalSeconds;
            _silenceProbes.Add(_schedule(delay, () =>
            {
                if (!IsRecording || _recordingPeakLevel >= AppConstants.SilenceRmsThreshold) return;
                AppLog.Warn("audio", $"录音已进行 {seconds:F1}s 仍未检测到声音");
                PreviewWarning?.Invoke(L10n.T("没有检测到声音，请检查麦克风与输入设备"));
            }));
        }
    }

    private void CancelSilenceProbes()
    {
        foreach (var probe in _silenceProbes) probe.Dispose();
        _silenceProbes.Clear();
    }

    // ─── 唯一收尾入口 ─────────────────────────────────────────────

    private void Finish(Outcome outcome)
    {
        if (_active is not { } utterance) return;
        _active = null;
        CancelSilenceProbes();
        _hotkey.AcceptsCancelWhenInactive = false;

        // 必须在 Close() 之前取：这是会话内分阶段打点的唯一读取点。
        var metrics = utterance.Metrics;
        metrics.Timings = utterance.Session.Timings;
        utterance.Session.Close();
        _sessions.SessionEnded();
        _audio.OnChunk = null;
        _audio.OnTailChunk = null;
        _previewText = "";
        PreviewUpdate?.Invoke("");

        DictationOutcome result;
        switch (outcome)
        {
            case Outcome.Text text:
                result = HandleFinalText(text.Value, utterance.Target, metrics);
                break;
            case Outcome.Failed failed:
                StateChanged?.Invoke(AppStateInfo.ErrorWith(failed.Message));
                result = DictationOutcome.Failed;
                break;
            case Outcome.Cancelled:
                if (_isRunning)
                {
                    AppLog.Info("controller", "用户取消录音");
                    Cancelled?.Invoke();
                }
                result = DictationOutcome.Cancelled;
                break;
            case Outcome.Discarded:
                if (_isRunning) StateChanged?.Invoke(AppStateInfo.Idle);
                result = DictationOutcome.Discarded;
                break;
            case Outcome.GestureDiscarded:
                if (_isRunning) StateChanged?.Invoke(AppStateInfo.Idle);
                result = DictationOutcome.GestureCancelled;
                break;
            default: // Shutdown：Stop() 不是一次完整听写，不上报。
                return;
        }

        metrics.Outcome = result;
        metrics.DoneAt = _now();
        MetricsReported?.Invoke(metrics);
    }

    /// <summary>插入最终文本并更新状态，返回本次听写的结局。调用方（<see cref="Finish"/>）负责在调用前完成会话拆解。</summary>
    private DictationOutcome HandleFinalText(string text, ForegroundTarget target, DictationMetrics metrics)
    {
        var trimmed = (text ?? "").Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            // 识别链路跑通了但一个字都没有：几乎总是"没说话/麦克风静音/选错输入设备"。
            // 必须给一个可见反馈，否则 HUD 静默消失，与"插进去了但没看见"无法区分。
            AppLog.Info("controller", "识别结果为空，未插入任何文本");
            // 先回 Idle 再通知：协调器在 Idle 分支会收起 HUD，"没有识别到内容"的提示要在其后浮出。
            StateChanged?.Invoke(AppStateInfo.Idle);
            EmptyRecognition?.Invoke();
            return DictationOutcome.Empty;
        }

        if (!_isRunning) return DictationOutcome.Discarded;

        StateChanged?.Invoke(AppStateInfo.Inserting);
        var insertStartedAt = _now();
        var result = _text.Insert(trimmed, target);
        metrics.InsertTicks = _now() - insertStartedAt;

        switch (result)
        {
            case TextInsertionResult.Inserted:
                RecognizedText?.Invoke(trimmed);
                StateChanged?.Invoke(AppStateInfo.Idle);
                return DictationOutcome.Inserted;

            case TextInsertionResult.FocusChanged:
                // 录音开始到插入之间前台窗口已切换：不写入用户未预期的窗口，只复制到剪贴板。
                AppLog.Warn("controller", "目标窗口已变化，插入已取消，改为复制到剪贴板");
                StateChanged?.Invoke(AppStateInfo.ErrorWith(_text.CopyToClipboard(trimmed)
                    ? L10n.T("目标窗口已变化，结果已复制到剪贴板，可手动粘贴")
                    : L10n.T("目标窗口已变化，且复制到剪贴板也失败了，请重新听写")));
                return DictationOutcome.FocusChanged;

            case TextInsertionResult.ModifiersHeld:
                AppLog.Warn("controller", "检测到修饰键被按住，未自动粘贴，改为复制到剪贴板");
                StateChanged?.Invoke(AppStateInfo.ErrorWith(_text.CopyToClipboard(trimmed)
                    ? L10n.T("检测到有修饰键按住，未自动粘贴，结果已复制到剪贴板")
                    : L10n.T("检测到有修饰键按住，且复制到剪贴板失败，请重新听写")));
                return DictationOutcome.ModifiersHeld;

            default:
                // 插入失败兜底：把结果写入剪贴板，避免长听写内容彻底丢失。
                var copied = _text.CopyToClipboard(trimmed);
                string reason;
                if (!copied)
                {
                    reason = L10n.T("插入失败，且复制到剪贴板也失败了，请重新听写");
                }
                else
                {
                    // UIPI 会阻止向提权窗口 SendInput；无法判定权限时用不确定语气（R3-2）。
                    reason = _text.CheckForegroundElevation() switch
                    {
                        ForegroundElevation.Elevated => L10n.T("目标窗口以管理员身份运行，Windows 安全机制阻止了输入注入，已复制到剪贴板，可手动粘贴"),
                        ForegroundElevation.Unknown => L10n.T("无法判断目标窗口权限，输入注入未生效，已复制到剪贴板，可手动粘贴"),
                        _ => L10n.T("插入失败，已复制到剪贴板，可手动粘贴"),
                    };
                }
                AppLog.Error("controller", "文本插入失败，已复制到剪贴板");
                StateChanged?.Invoke(AppStateInfo.ErrorWith(reason));
                return DictationOutcome.InsertFailed;
        }
    }
}
