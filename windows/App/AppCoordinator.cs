using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using VoiceTyper.Llm;
using VoiceTyper.Services;
using VoiceTyper.Support;
using VoiceTyper.UI;

namespace VoiceTyper.App;

/// <summary>
/// 中央调度器。负责装配、生命周期、配置变更处理、模型下载编排。
/// C# 直译自 <c>macos/Sources/VoiceTyper/App/AppCoordinator.swift</c>，并保留
/// <c>client-server/client_windows_native/App/AppCoordinator.cs</c> 的托盘/HUD 装配骨架。
///
/// 与两者的关键差异：Windows 没有 macOS TCC 式的强制权限门（无法阻止热键监听），
/// 也没有 Windows 原生的"申请麦克风权限"API——麦克风可用性只能靠实际尝试打开设备判断
/// （见 <see cref="MicPermissionProbe"/>），且探测结果不阻塞 Idle：热键监听不需要麦克风，
/// 只有实际录音时才会暴露权限问题（<see cref="VoiceTyperController"/> 已处理该失败路径）。
/// 唯一的硬门槛是本地识别模型是否就绪，由 <see cref="AsrService.State"/> 驱动。
///
/// 所有公共方法必须在 UI 线程调用。
/// </summary>
internal sealed class AppCoordinator : IDisposable
{
    private readonly ConfigStore _configStore = new();
    private readonly TrayController _tray = new();
    private readonly AsrService _asrService = new();
    /// <summary>应用级唯一的剪贴板插入服务：控制器重建时只是借用它，剪贴板恢复状态不随控制器丢失。</summary>
    private readonly TextInsertionService _textInsertion = new();

    /// <summary>设置页 / 引导页的「测试麦克风」。懒创建；听写开始、锁屏 / 睡眠、退出时都会停掉。</summary>
    private MicLevelTest? _micTest;
    private RecordingHud? _hud;
    private SetupForm? _setupForm;
    private OnboardingForm? _onboardingForm;
    private OnboardingModel? _onboarding;
    private VoiceTyperController? _controller;
    /// <summary>是否有一次听写正在进行。破坏性操作的门禁依据：显示状态（<see cref="_currentState"/>）
    /// 会被错误提示等覆盖，不能当作事实源。</summary>
    private bool IsDictating => _controller?.HasActiveDictation == true;
    /// <summary>最近一次听写的最终文本，供托盘"复制上一次识别结果"。只在内存里，不落盘、不进日志。</summary>
    private string? _lastResultText;
    /// <summary>最近 20 次听写的精简耗时，供设置页诊断。只在内存里，只含数字与枚举。</summary>
    private readonly DictationHistory _history = new();
    /// <summary>本次听写成功插入时附带的 HUD 副标题（纠错回落说明）；在 Idle 分支消费一次。</summary>
    private string? _pendingSuccessNote;

    private AppConfig _config = new();
    private AppStateInfo _currentState = AppStateInfo.Booting;
    private MicProbeResult _micProbe = MicProbeResult.Unknown;
    /// <summary>最近一次探测失败的详情（真实异常消息与 HRESULT），来自 <see cref="MicPermissionProbe"/>。</summary>
    private string? _micProbeDetail;
    private bool _userOpenedSetup;
    /// <summary>用户是否已从托盘菜单主动暂停听写。暂停时不监听热键（W-28）。</summary>
    private bool _isPaused;

    /// <summary>已经"强制弹窗"过的理由。每个理由在一次未就绪期内只抢一次焦点。
    ///
    /// 此前 <see cref="ReevaluateReadinessAsync"/> 的 ModelMissing 分支每次重评估都会
    /// 无条件 <see cref="OpenSetup"/>——用户刚点完"取消下载"，窗口反而抢焦点跳到最前面
    /// （VW-11，对齐 macOS 的 B7）。恢复就绪时清空（见 <see cref="ActivateReadyState"/>），
    /// 这样"就绪 → 又不就绪"仍然会再提醒一次。</summary>
    private readonly HashSet<ForcedPresentation> _forcedPresentations = new();
    private enum ForcedPresentation { Permissions, Model }

    private ModelDownloader? _modelDownloader;
    private bool _isDownloadingModel;
    private double _downloadProgress;
    private readonly DownloadSpeedTracker _downloadSpeed = new();
    /// <summary>下载进度的文字（已下载 / 速度 / 剩余时间）；不在下载时为 null。</summary>
    private string? DownloadStatusLine => _isDownloadingModel
        ? DownloadStatusText.Format(_downloadProgress, ModelDownloader.TotalBytes,
            _downloadSpeed.BytesPerSecond(ModelDownloader.TotalBytes))
        : null;
    /// <summary>最近一次下载失败的说明（含自动重试进度），成功或重新开始时清空。展示在设置页与引导页。</summary>
    private string? _modelDownloadError;
    private string? _modelDownloadDetails;
    /// <summary>
    /// 模型下载失败后的自动重试退避序列（秒）。网络抖动时用户什么都不用做；重试的成本只是续上而不是
    /// 从头再来（<see cref="ModelDownloader"/> 保留 .part 数据）。也不能无限重试：真的没网时那会变成一个
    /// 用户看不见的死循环，所以给定次数用完后停下来，把手动重试交还给用户。
    /// </summary>
    private static readonly int[] DownloadRetryDelaysSeconds = { 5, 20, 60 };
    private int _downloadRetryAttempt;
    private System.Windows.Forms.Timer? _downloadRetryTimer;
    /// <summary>未就绪时按热键会把设置窗口摆到面前；连按只在窗口期内做一次，避免反复抢焦点。</summary>
    private long _lastBlockedGuidanceTicks;
    private static readonly TimeSpan BlockedGuidanceThrottle = TimeSpan.FromSeconds(5);
    /// <summary>单独修饰键热键下浮窗延迟出现，避免 Ctrl+点击这类组合用法让浮窗一闪。</summary>
    private System.Windows.Forms.Timer? _hudShowTimer;
    /// <summary>本次听写的麦克风是否已经出声（<see cref="VoiceTyperController.MicrophoneReady"/>）。
    /// 防闪烁延迟到点时据此决定显示"麦克风启动中"还是直接"录音中"。</summary>
    private bool _microphoneReady;
    /// <summary>每个进程对缺失模型至少自动尝试一次，同一进程内失败后不无限重试。
    /// 下次启动会再自动尝试，并复用 <see cref="ModelDownloader"/> 已保存的断点数据（W-29）。</summary>
    private bool _hasAttemptedAutomaticModelDownload;
    /// <summary>听写流程内的一次性错误（识别失败/插入失败/焦点变化）超时自愈到 Idle。
    /// HUD 已在 2.5s 后自动隐藏该提示，但此前托盘状态没有对应的回落，会一直停留在
    /// 红色错误态直到下一次听写（R3-04）。</summary>
    private System.Windows.Forms.Timer? _dictationErrorRecoveryTimer;

    public AppCoordinator()
    {
        _tray.OnOpenSetup = () => OpenSetup();
        _tray.OnQuit = () => Application.Exit();
        _tray.OnTogglePause = TogglePause;
        _tray.OnCopyLastResult = CopyLastResult;
        _tray.OnOpenOnboarding = PresentOnboarding;
        _tray.OnCheckForUpdates = () => _ = CheckForUpdatesAsync();

        _asrService.OnStateChange = OnAsrStateChanged;
    }

    // ─── 锁屏 / 睡眠 / 会话切换 ───────────────────────────────

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case Microsoft.Win32.SessionSwitchReason.SessionLock:
            case Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect:
            case Microsoft.Win32.SessionSwitchReason.RemoteDisconnect:
                UiDispatcher.PostAsync(HandleSystemInterruption);
                break;
            case Microsoft.Win32.SessionSwitchReason.SessionUnlock:
            case Microsoft.Win32.SessionSwitchReason.ConsoleConnect:
            case Microsoft.Win32.SessionSwitchReason.RemoteConnect:
                UiDispatcher.PostAsync(HandleSystemResumed);
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend) UiDispatcher.PostAsync(HandleSystemInterruption);
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume) UiDispatcher.PostAsync(HandleSystemResumed);
    }

    private void HandleSystemInterruption()
    {
        StopMicTest();
        _controller?.HandleSystemInterruption();
    }

    private void HandleSystemResumed() => _controller?.HandleSystemResumed();

    /// <summary>ASR 状态变化的统一入口：具名方法避免 lambda 形参名为 `_` 时把 Task 误赋给 AsrState（R0-1）。</summary>
    private void OnAsrStateChanged(AsrState state) => _ = ReevaluateReadinessAsync();

    public void Start()
    {
        try
        {
            ReloadConfigurationFromDisk();
        }
        catch (Exception ex)
        {
            AppLog.Error("coordinator", "加载配置失败", ex);
            _currentState = AppStateInfo.ErrorWith(L10n.T("配置加载失败"));
            UpdateTray();
            return;
        }

        UpdateTray();
        _asrService.UpdateConfig(_config.Asr);

        // 订阅在 Start 里、退订在 Dispose 里；这两个事件由系统在专用线程上触发，处理一律转回 UI 线程。
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // 首启引导：没走完就先带用户走完，否则热键第一次"按了没反应"时用户无从判断缺了什么。
        var onboardingCompleted = OnboardingRecord.IsCompleted();
        if (!onboardingCompleted) PresentOnboarding();

        // 权限与模型是两条互不依赖的准备线：麦克风探测要听半秒钟，模型准备不必等它。
        PrepareEngineForLaunch();
        _ = ReevaluateReadinessAsync();
        // 探测要真的打开一次麦克风：系统托盘的"麦克风使用中"指示会闪一下，"隐私 → 麦克风"里也会留下记录。
        // 引导已走完的老用户每次开机都来这一下没有必要（真正听写时的失败有明确提示），改为在用户打开设置页
        // 或引导页时按需探测（见 ProbeMicrophoneIfUnknown）。
        if (!onboardingCompleted) ProbeMicrophone(isFirstProbe: true);
    }

    public void Dispose()
    {
        _dictationErrorRecoveryTimer?.Stop();
        _dictationErrorRecoveryTimer?.Dispose();
        _downloadRetryTimer?.Stop();
        _downloadRetryTimer?.Dispose();
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _micTest?.Dispose();
        _hudShowTimer?.Stop();
        _hudShowTimer?.Dispose();
        _modelDownloader?.Dispose();
        _controller?.Dispose();
        _textInsertion.FlushPendingRestore();
        _hud?.Dispose();
        _setupForm?.Dispose();
        _onboardingForm?.Dispose();
        _tray.Dispose();
        _asrService.Dispose();
    }

    // ─── 配置 ──────────────────────────────────────────────────

    private void ReloadConfigurationFromDisk()
    {
        _config = _configStore.LoadOrCreate();
        // config.yaml 是界面语言的事实来源（Program.Main 里的 Bootstrap 只是启动期的快照）。
        L10n.Apply(_config.UI.InterfaceLanguageValue);
        // HUD 是长生命周期组件：配置变更就地生效，不重建。此前每次保存非 UI 配置
        // （例如改热键）都会丢弃整个 HUD 实例、连带丢弃窗口句柄与几何状态（VW-15，
        // 对齐 macOS 的 B8）。界面语言文案在每次 Show*() 调用时通过 L10n.T 现取，
        // 不需要重建实例就能反映语言变化。
        if (_hud is null)
        {
            _hud = new RecordingHud(_config.UI);
            // 浮窗真正画到屏幕上的时刻并入本次听写的耗时摘要（控制器可能被重建，按当前实例转发）。
            _hud.ProgressPainted = (ready, timestamp) => _controller?.NoteHudPainted(ready, timestamp);
        }
        else
        {
            _hud.ApplyConfig(_config.UI);
        }
    }

    /// <summary>
    /// 设置窗口统一保存的单一事务（R2-4）：先做全部校验、先判断当前是否允许应用，<b>通过之后</b>再
    /// 依次写密钥和配置。任一步失败给出准确的分状态提示（密钥写失败 / 配置写失败 / 活跃听写拒绝），
    /// 不出现"密钥已落盘但提示保存失败"，也不出现"只改密钥后实际听写仍用旧密钥"
    /// （密钥变化被当作显式更新信号，走控制器重建路径，让新密钥立即在下次听写生效）。
    /// </summary>
    private async Task SaveRecognitionAsync(AppConfig draft, string? newApiKey)
    {
        if (draft.Llm.Enabled)
        {
            var endpoint = LlmEndpoint.Resolve(draft.Llm.BaseUrl);
            if (endpoint.Url is null) throw new InvalidOperationException(endpoint.ErrorMessage);
            if (string.IsNullOrWhiteSpace(draft.Llm.Model))
                throw new InvalidOperationException(L10n.T("启用智能纠错时必须填写模型名称。"));
        }
        bool keyChanged = newApiKey is not null;
        bool audioChanged = !AudioConfigEquals(_config.Audio, draft.Audio);
        bool sectionsChanged = !(AsrConfigEquals(_config.Asr, draft.Asr) && LlmConfigEquals(_config.Llm, draft.Llm)
            && HotkeyConfigEquals(_config.Hotkey, draft.Hotkey) && AudioConfigEquals(_config.Audio, draft.Audio));

        if ((keyChanged || sectionsChanged) && IsDictating)
        {
            // 尚未写任何东西就拒绝。
            throw new InvalidOperationException(L10n.T("正在录音/识别/输入，请等待当前听写完成后再保存此项设置"));
        }

        if (keyChanged && !SecretStore.SaveLlmApiKey(newApiKey!))
        {
            throw new InvalidOperationException(L10n.T("API Key 写入失败，请重试（配置尚未保存）"));
        }

        try
        {
            _configStore.Save(draft);
        }
        catch (Exception error)
        {
            // 密钥与 YAML 分开存放：明确告知部分成功，不能声称两者都未写入。
            AppLog.Error("config", "设置文件写入失败", error);
            throw new InvalidOperationException(keyChanged
                ? L10n.T("API Key 已更新，但配置保存失败，请重试保存。")
                : L10n.T("配置保存失败，请检查配置目录的写入权限。"), error);
        }

        if (keyChanged || sectionsChanged)
        {
            // 走重建路径：控制器构造时会重新从 SecretStore 读密钥，新密钥立即生效。
            await ReloadAndReevaluateAsync().ConfigureAwait(true);
            // 换了输入设备：之前的探测结果测的是旧设备，重新测一次。
            if (audioChanged) ProbeMicrophone(isFirstProbe: false);
        }
        else
        {
            _config = draft.Validated();
            _hud?.ApplyConfig(_config.UI);
            _setupForm?.LoadEditableContent(_config);
        }
    }

    private static bool AsrConfigEquals(AsrConfig a, AsrConfig b) =>
        a.Language == b.Language && a.Threads == b.Threads && a.ModelDir == b.ModelDir
        && a.PreviewWindowSeconds == b.PreviewWindowSeconds && a.IdleUnloadMinutes == b.IdleUnloadMinutes
        && a.PreloadOnLaunch == b.PreloadOnLaunch;

    private static bool LlmConfigEquals(LlmConfig a, LlmConfig b) =>
        a.Enabled == b.Enabled && a.BaseUrl == b.BaseUrl && a.Model == b.Model
        && a.Temperature.Equals(b.Temperature) && a.MaxTokens == b.MaxTokens && a.Timeout.Equals(b.Timeout);

    private static bool HotkeyConfigEquals(HotkeyConfig a, HotkeyConfig b) =>
        a.Key == b.Key && a.Modifiers.SequenceEqual(b.Modifiers) && a.ModeValue == b.ModeValue;

    private static bool AudioConfigEquals(AudioConfig a, AudioConfig b) => a.InputDevice == b.InputDevice;

    private async Task ReloadAndReevaluateAsync()
    {
        _controller?.Stop();
        _controller?.Dispose();
        _controller = null;

        ReloadConfigurationFromDisk();
        _asrService.UpdateConfig(_config.Asr);
        _setupForm?.LoadEditableContent(_config);
        UpdateTray();
        await ReevaluateReadinessAsync().ConfigureAwait(true);
    }

    // ─── 麦克风探测 ────────────────────────────────────────────

    /// <summary>当前是否有探测在飞；UI 线程读写。</summary>
    private bool _probeInFlight;
    /// <summary>探测期间又收到请求：当前探测结束后补跑一次（多次请求合并成一次）。</summary>
    private bool _probeRequestedAgain;
    private bool _probeAgainIsFirst;

    /// <param name="isFirstProbe">启动后的首次探测：被拒绝时会弹出权限引导。</param>
    /// <param name="automatic">设置页轮询触发的自动探测：听写中跳过，避免再开一个采集器。人工点击照常执行。</param>
    private void ProbeMicrophone(bool isFirstProbe, bool automatic = false)
    {
        if (automatic && IsDictating) return;
        // 单个在飞：探测要真开一次 WASAPI 采集，重叠执行既浪费又会让结果按完成顺序乱序覆盖。
        if (_probeInFlight)
        {
            _probeRequestedAgain = true;
            _probeAgainIsFirst |= isFirstProbe;
            return;
        }
        _probeInFlight = true;

        // 按当前配置的输入设备探测，与真实录音所用的设备一致；补跑的探测会重新读取配置。
        var policy = AudioInputPolicy.FromConfigValue(_config.Audio.InputDevice);
        _ = Task.Run(() => MicPermissionProbe.Probe(policy)).ContinueWith(t =>
        {
            UiDispatcher.Post(() =>
            {
                _probeInFlight = false;
                var rerun = _probeRequestedAgain;
                var rerunIsFirst = _probeAgainIsFirst;
                _probeRequestedAgain = false;
                _probeAgainIsFirst = false;

                if (t.IsFaulted) AppLog.Warn("coordinator", $"麦克风探测异常: {t.Exception?.GetBaseException().Message}");
                // 已排了补跑：这次结果即将过期，直接丢弃，只展示补跑的最新结果。
                if (rerun)
                {
                    ProbeMicrophone(rerunIsFirst || isFirstProbe); // 被丢弃的首次探测的"弹引导"职责并入补跑
                    return;
                }

                if (t.IsCompletedSuccessfully)
                {
                    _micProbe = t.Result.Result;
                    _micProbeDetail = t.Result.FailureDetail;
                }
                else
                {
                    _micProbe = MicProbeResult.Unknown;
                    _micProbeDetail = null;
                }
                if (isFirstProbe && _micProbe == MicProbeResult.AccessDenied)
                {
                    PresentBlockingGuidance(ForcedPresentation.Permissions, SetupTab.Permissions);
                }
                SyncSetupWindow();
                UpdateTray();
            });
        });
    }

    /// <summary>尚无探测结果时才探测：打开设置页 / 引导页时用，让诊断页有内容可显示，又不在每次开机时打扰用户。</summary>
    private void ProbeMicrophoneIfUnknown()
    {
        if (_micProbe == MicProbeResult.Unknown && !IsDictating) ProbeMicrophone(isFirstProbe: false);
    }

    // ─── 就绪状态机 ────────────────────────────────────────────

    /// <summary>关闭「启动时预加载」（默认开启）时只确认模型文件在不在，不把引擎拉进内存——
    /// 首次按热键才加载，且加载与录音并行（<see cref="AsrService.MakeSession"/>）。</summary>
    private void PrepareEngineForLaunch()
    {
        if (_config.Asr.PreloadOnLaunch) _ = _asrService.PreloadAsync();
        else _asrService.PrepareWithoutLoading();
    }

    private async Task ReevaluateReadinessAsync()
    {
        // 模型准备与暂停状态完全独立。首次定位到模型缺失后立即下载，用户暂停听写也不
        // 阻断后台准备（W-29）。
        if (!_hasAttemptedAutomaticModelDownload && !_isDownloadingModel && _asrService.State == AsrState.ModelMissing)
        {
            _hasAttemptedAutomaticModelDownload = true;
            StartModelDownload();
        }

        if (_isPaused)
        {
            _currentState = AppStateInfo.Paused;
            SyncSetupWindow();
            UpdateTray();
            await Task.CompletedTask;
            return;
        }

        var previous = _currentState;
        if (_isDownloadingModel)
        {
            _currentState = AppStateInfo.DownloadingModelWith(_downloadProgress);
            GateHotkeyListening(CurrentBlockedReason());
            SyncSetupWindow();
            UpdateTray();
            return;
        }

        switch (_asrService.State)
        {
            case AsrState.Unloaded:
                // 「启动时预加载」关闭时不借机把引擎拉进内存，只确认文件在不在。
                if (_config.Asr.PreloadOnLaunch)
                {
                    _ = _asrService.PreloadAsync();
                    _currentState = AppStateInfo.ModelLoading;
                    GateHotkeyListening(CurrentBlockedReason());
                }
                else
                {
                    _asrService.PrepareWithoutLoading();
                    _currentState = AppStateInfo.ModelLoading;
                }
                break;
            case AsrState.Loading:
                // 空闲卸载后首次按热键：MakeSession() 会一边异步加载模型、一边立刻开始录音，
                // 因此模型进入 Loading 时上一轮状态已经是活动听写态（Recording/Recognizing/
                // Inserting）。若这里无条件覆盖成 ModelLoading，托盘状态会被"未就绪"覆盖，
                // 而 LocalAsrSession 已支持引擎未就绪时缓存 pendingAudio 并在就绪后回灌，
                // 录音本身并未受影响——只是状态显示被错误地打断（VW-13，对齐 macOS
                // computeTargetState 对 .loading 分支的 previous 保护）。
                // Unloaded（引擎从未加载）不做同样的保护：那是真正需要等待的情形。
                if (!_currentState.State.IsActiveDictation())
                {
                    _currentState = AppStateInfo.ModelLoading;
                    GateHotkeyListening(CurrentBlockedReason());
                }
                break;
            case AsrState.ModelMissing:
                // 一段进行中的听写被"未就绪"覆盖（例如运行中模型被重载）：必须把它正常拆掉，
                // 否则会留下一段永远收不了尾的会话。
                if (previous.State.IsActiveDictation()) _controller?.Stop();
                _currentState = AppStateInfo.ModelMissing;
                _hud?.HideHud();
                GateHotkeyListening(CurrentBlockedReason());
                PresentBlockingGuidance(ForcedPresentation.Model, SetupTab.Recognition);
                break;
            case AsrState.Failed:
                if (previous.State.IsActiveDictation()) _controller?.Stop();
                _currentState = AppStateInfo.ErrorWith(_asrService.FailureMessage ?? L10n.T("未知错误"));
                _hud?.HideHud();
                GateHotkeyListening(CurrentBlockedReason());
                break;
            case AsrState.Ready:
            case AsrState.SuspendedForIdle:
                // 空闲卸载后的状态保持"就绪"：热键监听继续运行，真正的重新加载
                // 由 MakeSession() 在下次按热键时按需触发，不在这里主动 preload（F-04）。
                ActivateReadyState();
                break;
        }

        SyncSetupWindow();
        UpdateTray();
        await Task.CompletedTask;
    }

    /// <summary>
    /// 未就绪的原因文案：具体说出缺什么，而不是笼统的"无法使用"。热键照常监听，
    /// 按下时把这句话交给用户，而不是让他对着一个毫无反应的热键猜。
    /// </summary>
    private string? CurrentBlockedReason()
    {
        if (_isDownloadingModel)
        {
            return L10n.F("语音模型正在下载（{0}%），完成后即可开始听写。", (int)(_downloadProgress * 100));
        }
        return _asrService.State switch
        {
            AsrState.ModelMissing => L10n.T("语音模型还没有下载，无法开始听写。"),
            AsrState.Failed => L10n.F("语音模型加载失败：{0}", _asrService.FailureMessage ?? L10n.T("未知错误")),
            AsrState.Loading or AsrState.Unloaded => L10n.T("识别引擎正在加载，请稍候再试。"),
            _ => null,
        };
    }

    /// <summary>门禁态：热键照常监听，但按下时只给提示、不开始录音。</summary>
    private void GateHotkeyListening(string? reason)
    {
        EnsureController();
        var controller = _controller!;
        controller.BlockedReason = reason;
        if (controller.IsRunning) return;
        try
        {
            controller.Start();
        }
        catch (Exception ex)
        {
            // 门禁态下热键起不来不算故障：用户本来就还没走完准备流程。记日志即可，
            // 不要用一个红色错误态盖住"还缺什么"这条真正有用的信息。
            AppLog.Warn("coordinator", $"门禁态热键监听启动失败: {ex.Message}");
        }
    }

    /// <summary>托盘菜单"暂停/恢复听写"。暂停时停掉热键监听与 HUD，恢复时重新走一遍就绪判定（W-28）。</summary>
    private void TogglePause()
    {
        if (_isPaused)
        {
            _isPaused = false;
            _ = ReevaluateReadinessAsync();
        }
        else
        {
            _isPaused = true;
            _controller?.Stop();
            _hud?.HideHud();
            _currentState = AppStateInfo.Paused;
            UpdateTray();
            SyncSetupWindow();
        }
    }

    /// <summary>托盘"复制上一次识别结果"：把最近一次的最终文本放回剪贴板，供插入未生效时手动粘贴。</summary>
    private void CopyLastResult()
    {
        if (_lastResultText is not { } text) return;
        // 听写进行中复制会和插入流程抢剪贴板（还会盖掉浮窗的录音状态），等这一段结束。
        if (IsDictating)
        {
            _hud?.FlashWarning(L10n.T("正在听写，请等这一段结束后再复制"));
            return;
        }
        if (_textInsertion.CopyToClipboard(text)) _hud?.ShowCopied();
        else _hud?.ShowNotice(L10n.T("复制失败"), L10n.T("复制到剪贴板失败，请重试。"));
    }

    private void ActivateReadyState()
    {
        // 就绪：解除门禁，并允许"下次再不就绪时"重新提醒一次（VW-11）。
        _forcedPresentations.Clear();
        EnsureController();
        _controller!.BlockedReason = null;
        if (!_controller.IsRunning)
        {
            try
            {
                _controller.Start();
            }
            catch (Exception ex)
            {
                AppLog.Error("coordinator", "启动 controller 失败", ex);
                _currentState = AppStateInfo.ErrorWith(L10n.F("热键监听失败：{0}", ex.Message));
                _hud?.HideHud();
                return;
            }
        }

        if (_currentState.State is not (AppState.Recording or AppState.Recognizing or AppState.Inserting))
        {
            _currentState = AppStateInfo.Idle;
        }
        HideSetupWindowIfVisible();
    }

    private void EnsureController()
    {
        if (_controller is not null) return;
        var controller = new VoiceTyperController(_config, _asrService, _textInsertion);
        BindControllerEvents(controller);
        _controller = controller;
    }

    private void BindControllerEvents(VoiceTyperController controller)
    {
        controller.StateChanged = state =>
        {
            var previous = _currentState;
            _currentState = state;
            if (state.State != AppState.Recording) CancelPendingHudShow();
            switch (state.State)
            {
                case AppState.Recording:
                    StopMicTest(); // 两路采集同时开没有意义，测试让位给真正的听写
                    CancelDictationErrorRecovery();
                    _microphoneReady = false;
                    ShowRecordingHud(controller);
                    break;
                case AppState.Recognizing:
                    _hud?.SetRecognizing();
                    ForwardToOnboarding(new OnboardingDictationEvent.Recognizing());
                    break;
                case AppState.Inserting:
                    break;
                case AppState.Error:
                    _hud?.ShowError(state.Message ?? "");
                    ScheduleDictationErrorRecovery();
                    ForwardToOnboarding(new OnboardingDictationEvent.Failed(state.Message ?? ""));
                    break;
                case AppState.Idle:
                    CancelDictationErrorRecovery();
                    var successNote = _pendingSuccessNote;
                    _pendingSuccessNote = null;
                    if (previous.State == AppState.Inserting) _hud?.ShowSuccess(successNote);
                    else _hud?.HideHud();
                    break;
                default:
                    _hud?.HideHud();
                    break;
            }
            UpdateTray();
        };

        controller.MicrophoneReady = () =>
        {
            _microphoneReady = true;
            // 防闪烁延迟还没到：交给计时器到点后直接显示"录音中"。
            if (_hudShowTimer is null) _hud?.ShowRecording(controller.RecordingInputDeviceName);
            // 引导页的"录音中"也以麦克风真正出声为准。
            ForwardToOnboarding(new OnboardingDictationEvent.RecordingStarted());
        };
        controller.ReleasedBeforeReady = () =>
        {
            var message = L10n.T("请等浮窗显示「录音中」后再开口。本次没有录到声音。");
            _hud?.ShowNotice(L10n.T("麦克风还没准备好"), message);
            ForwardToOnboarding(new OnboardingDictationEvent.Failed(message));
        };
        controller.PreviewUpdate = preview => _hud?.ShowPreview(preview);
        controller.PreviewWarning = message => _hud?.FlashWarning(message);
        controller.CorrectionStarted = () => _hud?.SetCorrecting(_config.Hotkey.DisplayString);
        controller.EngineWait = waiting => _hud?.SetEngineWait(waiting);
        controller.AudioLevel = level =>
        {
            _hud?.UpdateLevel(level);
            ForwardToOnboarding(new OnboardingDictationEvent.Level(level));
        };
        controller.Cancelled = () =>
        {
            _currentState = AppStateInfo.Idle;
            _hud?.ShowCanceled();
            ForwardToOnboarding(new OnboardingDictationEvent.Cancelled());
            UpdateTray();
        };
        controller.EmptyRecognition = () =>
        {
            _hud?.ShowNoSpeech();
            ForwardToOnboarding(new OnboardingDictationEvent.EmptyResult());
        };
        controller.InsertedWithCorrectionFallback = () =>
            _pendingSuccessNote = L10n.T("已使用识别原文（纠错未成功）");
        controller.BlockedAttempt = _ => HandleBlockedAttempt();
        // 识别文本本身不落日志，只记字数——AGENTS.md 明确禁止日志包含不必要的用户文本。
        controller.RecognizedText = text =>
        {
            AppLog.Info("coordinator", $"识别完成 chars={text?.Length ?? 0}");
            ForwardToOnboarding(new OnboardingDictationEvent.Inserted(text ?? ""));
        };
        // 摘要只含数字与枚举（DictationMetrics 禁止承载用户文本），可以放心落日志。
        controller.FinalTextReady = text =>
        {
            _lastResultText = text;
            _tray.SetLastResultAvailable(true);
        };
        controller.MetricsReported = metrics =>
        {
            AppLog.Info("metrics", metrics.SummaryLine());
            _history.Add(DateTime.Now, metrics);
            _setupForm?.UpdateRecentDictations(_history.NewestFirst());
        };
    }

    /// <summary>
    /// 按下热键即显示浮窗："麦克风启动中"，等 <see cref="VoiceTyperController.MicrophoneReady"/> 再切到"录音中"。
    /// 单独修饰键（右 Ctrl / 右 Alt）作为组合快捷键使用时也会短暂进入录音，浮窗若立即出现会闪烁，
    /// 因此延迟 150ms 再显示（从按下算起，与麦克风启动并行）；录音本身不延迟。其他热键立即显示。
    /// </summary>
    private void ShowRecordingHud(VoiceTyperController controller)
    {
        CancelPendingHudShow();
        if (!_config.Hotkey.IsModifierOnly)
        {
            _hud?.ShowPreparing();
            return;
        }
        var timer = new System.Windows.Forms.Timer { Interval = 150 };
        timer.Tick += (_, _) =>
        {
            CancelPendingHudShow();
            if (_currentState.State != AppState.Recording) return;
            if (_microphoneReady) _hud?.ShowRecording(controller.RecordingInputDeviceName);
            else _hud?.ShowPreparing();
        };
        _hudShowTimer = timer;
        timer.Start();
    }

    private void CancelPendingHudShow()
    {
        _hudShowTimer?.Stop();
        _hudShowTimer?.Dispose();
        _hudShowTimer = null;
    }

    /// <summary>未就绪时按下热键：HUD 说明原因；对可自行处理的原因（缺模型 / 加载失败）把设置窗口摆到面前，
    /// 连按 5 秒内只做一次，避免反复抢焦点。</summary>
    private void HandleBlockedAttempt()
    {
        var reason = CurrentBlockedReason() ?? L10n.T("还有准备工作没有完成。");
        _hud?.ShowNotice(L10n.T("还不能听写"), reason);
        ForwardToOnboarding(new OnboardingDictationEvent.Blocked(reason));

        if (_asrService.State is not (AsrState.ModelMissing or AsrState.Failed) || _isDownloadingModel) return;
        if (_onboardingForm is { Visible: true }) return;
        var now = Environment.TickCount64;
        if (_lastBlockedGuidanceTicks != 0 && now - _lastBlockedGuidanceTicks < BlockedGuidanceThrottle.TotalMilliseconds) return;
        _lastBlockedGuidanceTicks = now;
        PresentSetupForced(SetupTab.Recognition);
    }

    /// <summary>与 <see cref="UI.RecordingHud.ShowError"/> 的自动隐藏时长（2.5s）保持一致，
    /// 让托盘状态与 HUD 同步回落，而不是一直停在红色错误态直到下一次听写（R3-04）。</summary>
    private void ScheduleDictationErrorRecovery()
    {
        CancelDictationErrorRecovery();
        var timer = new System.Windows.Forms.Timer { Interval = 2500 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            _dictationErrorRecoveryTimer = null;
            if (_currentState.State == AppState.Error)
            {
                _currentState = AppStateInfo.Idle;
                UpdateTray();
            }
        };
        _dictationErrorRecoveryTimer = timer;
        timer.Start();
    }

    private void CancelDictationErrorRecovery()
    {
        _dictationErrorRecoveryTimer?.Stop();
        _dictationErrorRecoveryTimer?.Dispose();
        _dictationErrorRecoveryTimer = null;
    }

    // ─── 模型下载 ──────────────────────────────────────────────

    /// <param name="isAutomaticRetry">由退避重试触发时为 true。用户手动点「重试下载」算作一次新的尝试序列，
    /// 会把退避计数清零——那是明确的"我要它继续试"的信号。</param>
    private void StartModelDownload() => StartModelDownload(isAutomaticRetry: false);

    private void StartModelDownload(bool isAutomaticRetry)
    {
        if (_isDownloadingModel) return;
        CancelDownloadRetry();
        if (!isAutomaticRetry) _downloadRetryAttempt = 0;
        _isDownloadingModel = true;
        _downloadProgress = 0;
        _downloadSpeed.Reset();
        _modelDownloadError = null;
        _modelDownloadDetails = null;
        if (!_isPaused) _currentState = AppStateInfo.DownloadingModelWith(0);
        GateHotkeyListeningIfRunning();
        UpdateTray();
        SyncSetupWindow();

        var downloader = new ModelDownloader();
        _modelDownloader = downloader;

        _ = Task.Run(async () =>
        {
            try
            {
                await downloader.DownloadAllAsync(progress =>
                {
                    UiDispatcher.Post(() =>
                    {
                        if (!_isDownloadingModel || !ReferenceEquals(_modelDownloader, downloader)) return;
                        _downloadProgress = progress;
                        _downloadSpeed.Add(System.Diagnostics.Stopwatch.GetTimestamp(), progress);
                        if (!_isPaused) _currentState = AppStateInfo.DownloadingModelWith(progress);
                        UpdateTray();
                        SyncSetupWindow();
                    });
                }).ConfigureAwait(false);

                UiDispatcher.Post(() => _ = FinishModelDownloadAsync(downloader));
            }
            catch (OperationCanceledException)
            {
                UiDispatcher.Post(() =>
                {
                    ReleaseDownloader(downloader);
                    // 用户主动取消：回到「需要下载」原地重试，而不是显示为红色失败态；
                    // .part 数据已由 ModelDownloader 保留。
                    _ = ReevaluateReadinessAsync();
                });
            }
            catch (Exception ex)
            {
                AppLog.Error("model", "模型下载失败: " + DownloadFailure.Diagnostics(ex));
                UiDispatcher.Post(() =>
                {
                    ReleaseDownloader(downloader);
                    HandleModelDownloadFailure(ex);
                });
            }
        });
    }

    private void ReleaseDownloader(ModelDownloader downloader)
    {
        if (!ReferenceEquals(_modelDownloader, downloader)) return;
        _isDownloadingModel = false;
        _modelDownloader?.Dispose();
        _modelDownloader = null;
    }

    private void HandleModelDownloadFailure(Exception error)
    {
        var reason = DownloadFailure.Summary(error);
        _modelDownloadDetails = DownloadFailure.Diagnostics(error);
        if (DownloadFailure.CanRetry(error) && _downloadRetryAttempt < DownloadRetryDelaysSeconds.Length)
        {
            var delay = DownloadRetryDelaysSeconds[_downloadRetryAttempt];
            _downloadRetryAttempt++;
            _modelDownloadError = L10n.F("{0} 将在 {1} 秒后自动重试（第 {2}/{3} 次）。",
                reason, delay, _downloadRetryAttempt, DownloadRetryDelaysSeconds.Length);
            ScheduleDownloadRetry(TimeSpan.FromSeconds(delay));
        }
        else
        {
            _modelDownloadError = DownloadFailure.CanRetry(error)
                ? L10n.F("{0} 已自动重试 {1} 次仍未成功，请检查网络后手动重试。", reason, DownloadRetryDelaysSeconds.Length)
                : reason + "\n" + L10n.T("请处理上述问题后手动重试。");
        }

        if (!_isPaused) _currentState = AppStateInfo.ErrorWith(L10n.F("模型下载失败: {0}", reason));
        UpdateTray();
        SyncSetupWindow();
        // 下载失败是用户必须知道的事：把设置窗口（语音模型页，带失败说明与重试按钮）摆出来——但同一段
        // 未就绪期内只做一次，自动重试的每次失败都抢一次焦点就成了骚扰。引导窗口打开时由它自己呈现。
        PresentBlockingGuidance(ForcedPresentation.Model, SetupTab.Recognition);
        GateHotkeyListeningIfRunning();
    }

    private void ScheduleDownloadRetry(TimeSpan delay)
    {
        CancelDownloadRetry();
        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, (int)delay.TotalMilliseconds) };
        timer.Tick += (_, _) =>
        {
            CancelDownloadRetry();
            // 期间用户可能已经手动下载完、或主动取消并不想再试了。
            if (_isDownloadingModel || _asrService.State != AsrState.ModelMissing) return;
            AppLog.Info("model", $"模型下载自动重试（第 {_downloadRetryAttempt} 次）");
            StartModelDownload(isAutomaticRetry: true);
        };
        _downloadRetryTimer = timer;
        timer.Start();
    }

    private void CancelDownloadRetry()
    {
        _downloadRetryTimer?.Stop();
        _downloadRetryTimer?.Dispose();
        _downloadRetryTimer = null;
    }

    /// <summary>下载状态变化后，若控制器已在监听，刷新它的门禁文案（不启动新的监听）。</summary>
    private void GateHotkeyListeningIfRunning()
    {
        if (_controller is { IsRunning: true } && !_isPaused) GateHotkeyListening(CurrentBlockedReason());
    }

    private async Task FinishModelDownloadAsync(ModelDownloader downloader)
    {
        try
        {
            ReleaseDownloader(downloader);
            _modelDownloadError = null;
        _modelDownloadDetails = null;
            _downloadRetryAttempt = 0;
            // 「启动时预加载」关闭时，下载完成只需确认文件就绪；引擎留到第一次听写再加载。
            if (_config.Asr.PreloadOnLaunch) await _asrService.ReloadAsync().ConfigureAwait(true);
            else _asrService.PrepareWithoutLoading();
            await ReevaluateReadinessAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("coordinator", "下载完成后重新加载模型失败", ex);
            _currentState = AppStateInfo.ErrorWith(L10n.F("模型加载失败: {0}", ex.Message));
            UpdateTray();
            SyncSetupWindow();
        }
    }

    /// <summary>用户主动取消下载：同时取消尚未触发的自动重试——他刚刚表达了"先不下"。</summary>
    private void CancelModelDownload()
    {
        CancelDownloadRetry();
        _modelDownloader?.Cancel();
    }

    /// <summary>设置页"重新加载模型"：返回非 null 表示被拒绝，内容是给用户看的原因。</summary>
    private string? ReloadModel()
    {
        // 录音/识别/纠错期间重载会把正在使用的引擎 Dispose 掉（R2-3）。拒绝时不改显示状态：
        // 改成 Error 会让依赖显示状态的门禁在 2.5 秒后失效，而会话其实还在。
        if (IsDictating)
        {
            var reason = L10n.T("正在听写，请等待当前听写完成后再重新加载模型");
            _hud?.FlashWarning(reason);
            return reason;
        }
        _ = ReloadModelAsync();
        return null;
    }

    private async Task ReloadModelAsync()
    {
        try
        {
            await _asrService.ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("coordinator", "重新加载模型失败", ex);
        }
    }

    /// <summary>
    /// 与 <see cref="LlmCorrector.CorrectAsync"/> 不同，失败时把具体错误抛出而不是回落原文——
    /// 用户需要看到"401 未授权 / 超时 / 网络不通"等真实原因，否则"网络不通"和"模型认为
    /// 无需修改"会被显示成同一个结果（R3-13）。
    /// </summary>
    private async Task<LlmTestResult> TestLlmCorrectionAsync(LlmConfig llmConfig, string apiKey)
    {
        var resolved = LlmEndpoint.Resolve(llmConfig.BaseUrl);
        if (resolved.Url is not { } chatUrl)
        {
            return new LlmTestResult(false, resolved.ErrorMessage);
        }

        using var corrector = new LlmCorrector(new LlmCorrector.Config
        {
            ChatCompletionsUrl = chatUrl,
            ApiKey = apiKey,
            Model = llmConfig.Model,
            Temperature = llmConfig.Temperature,
            MaxTokens = llmConfig.MaxTokens,
            Timeout = llmConfig.Timeout,
        }, capabilityStore: FileLlmCapabilityStore.Shared);
        const string sample = "呃，这个功能和并之后应该可以用了吧";
        try
        {
            var result = await corrector.TestWithReportAsync(sample).ConfigureAwait(true);
            var detail = result.Thinking switch
            {
                LlmCorrector.ThinkingParameterUsage.RejectedThenOmitted => L10n.T("该服务不支持关闭深度思考的参数，已自动改为不带该参数请求。"),
                LlmCorrector.ThinkingParameterUsage.OmittedByCache => L10n.T("该服务不支持关闭深度思考的参数，已自动改为不带该参数请求。"),
                _ => L10n.T("已带上关闭深度思考的参数（服务接受）。"),
            };
            return new LlmTestResult(true, L10n.T("纠错测试成功，配置可用。") + " " + detail);
        }
        catch (Exception ex)
        {
            return new LlmTestResult(false, ex.Message);
        }
    }

    // ─── 设置窗口 ──────────────────────────────────────────────

    private void OpenSetup(SetupTab? preferredTab = null)
    {
        _userOpenedSetup = true;
        PresentSetupForced(preferredTab);
    }

    /// <summary>
    /// 强制把设置窗口摆到用户面前，但**不**把它标记为"用户主动打开"——与 <see cref="OpenSetup"/>
    /// 的区别在于 <see cref="_userOpenedSetup"/>：这里的调用方（未就绪时的引导弹窗）不应该让
    /// <see cref="HideSetupWindowIfVisible"/> 从此永久失效，就绪后仍要能自动收起窗口（VW-11）。
    /// </summary>
    private void PresentSetupForced(SetupTab? preferredTab)
    {
        EnsureSetupForm();
        _setupForm!.LoadEditableContent(_config);
        ProbeMicrophoneIfUnknown();
        SyncSetupWindow();
        if (preferredTab is { } tab) _setupForm.SelectTab(tab);
        _setupForm.Present();
    }

    /// <summary>未就绪时的强制弹窗：同一个理由在一次未就绪期内只抢一次焦点（VW-11，对齐
    /// macOS 的 <c>presentBlockingGuidance</c>）。之后只同步窗口内容，不再抢焦点。</summary>
    private void PresentBlockingGuidance(ForcedPresentation reason, SetupTab preferredTab)
    {
        // 引导窗口本身就在带用户处理这些事，再弹设置窗口只会抢焦点。
        if (_onboardingForm is { Visible: true }) return;
        if (_forcedPresentations.Contains(reason))
        {
            if (_setupForm is { IsDisposed: false })
            {
                _setupForm.LoadEditableContent(_config);
                SyncSetupWindow();
            }
            return;
        }
        _forcedPresentations.Add(reason);
        PresentSetupForced(preferredTab);
    }

    private void EnsureSetupForm()
    {
        if (_setupForm is not null && !_setupForm.IsDisposed) return;

        var form = new SetupForm();
        form.OnSaveRecognition = (draft, apiKey) => SaveRecognitionAsync(draft, apiKey);
        form.OnLoadLlmApiKey = () => SecretStore.LoadLlmApiKeyResult();
        form.OnStartModelDownload = StartModelDownload;
        form.OnCancelModelDownload = CancelModelDownload;
        form.OnReloadModel = ReloadModel;
        form.OnTestLlmCorrection = (llmConfig, apiKey) => TestLlmCorrectionAsync(llmConfig, apiKey);
        form.OnRetryMicProbe = () => ProbeMicrophone(isFirstProbe: false);
        form.OnPollMicProbe = () => ProbeMicrophone(isFirstProbe: false, automatic: true);
        form.OnPreviewHudOpacity = PreviewHudOpacity;
        form.OnPreviewHudPlacement = placement =>
        {
            if (!IsDictating) _hud?.ShowSample(placement);
        };
        form.OnBuildDiagnostics = BuildDiagnosticsReport;
        form.OnStartMicTest = StartMicTest;
        form.OnStopMicTest = StopMicTest;
        form.UpdateRecentDictations(_history.NewestFirst());
        form.OnUserClosedWindow = () => _userOpenedSetup = false;
        form.OnBeginHotkeyRecording = BeginHotkeyRecording;
        form.OnEndHotkeyRecording = EndHotkeyRecording;
        _setupForm = form;
    }

    /// <summary>
    /// 设置页的浮窗预览。值与已保存的不透明度相同 = 恢复（撤销、关闭窗口、改回原值）：把浮窗整个还原为已保存的设置，
    /// 位置的预览也一并撤销；否则应用新的不透明度并弹一个示例，让用户看到效果。
    /// </summary>
    private void PreviewHudOpacity(double opacity)
    {
        if (_hud is null) return;
        if (Math.Abs(opacity - _config.UI.Opacity) < 1e-9)
        {
            _hud.ApplyConfig(_config.UI);
            return;
        }
        _hud.ApplyOpacity(opacity);
        if (!IsDictating) _hud.ShowSample();
    }

    // ─── 测试麦克风 / 引导页换设备 ──────────────────────────────

    /// <summary>设置页 / 引导页点「测试麦克风」。听写进行中拒绝（两路采集同时开没有意义，还会让电平条对不上）。</summary>
    private void StartMicTest(string deviceValue)
    {
        if (IsDictating)
        {
            ReportMicTestStopped(L10n.T("正在听写，请等这一段结束后再测试麦克风。"));
            return;
        }
        _micTest ??= new MicLevelTest();
        _micTest.Start(AudioInputPolicy.FromConfigValue(deviceValue), level =>
        {
            _setupForm?.MicTestLevel(level);
            _onboardingForm?.MicTestLevel(level);
        }, ReportMicTestStopped);
    }

    private void StopMicTest()
    {
        if (_micTest is not { IsRunning: true }) return;
        _micTest.Stop();
        ReportMicTestStopped(null);
    }

    private void ReportMicTestStopped(string? reason)
    {
        _setupForm?.MicTestStopped(reason);
        _onboardingForm?.MicTestStopped(reason);
    }

    /// <summary>引导页换输入设备：与设置页保存走同一条事务（换设备会重建控制器并重新探测麦克风）。</summary>
    private void SelectInputDevice(string value)
    {
        if (IsDictating || string.Equals(value, _config.Audio.InputDevice, StringComparison.Ordinal)) return;
        var draft = _config.Clone();
        draft.Audio.InputDevice = value;
        _ = SaveInputDeviceAsync(draft);
    }

    private async Task SaveInputDeviceAsync(AppConfig draft)
    {
        try
        {
            await SaveRecognitionAsync(draft, null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("coordinator", $"引导页保存输入设备失败：{ex.GetType().Name}");
            _hud?.ShowNotice(L10n.T("保存失败"), ex.Message);
        }
    }

    /// <summary>「复制诊断信息」的正文。只放数字、布尔与枚举名：不含识别文本、设备名、路径与密钥。</summary>
    private string BuildDiagnosticsReport()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var facts = new List<(string Key, string Value)>
        {
            ("version", AppConstants.Version),
            ("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription),
            ("arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
            ("cpu_cores", Environment.ProcessorCount.ToString(inv)),
            ("asr_state", _asrService.State.ToString()),
            ("asr_threads", _config.Asr.Threads == 0 ? "auto" : _config.Asr.Threads.ToString(inv)),
            ("asr_rtf", _asrService.CalibratedRtf is { } rtf ? rtf.ToString("F3", inv) : "-"),
            ("preview_window_s", _asrService.PreviewWindowSecondsInUse.ToString(inv)),
            ("preload_on_launch", _config.Asr.PreloadOnLaunch ? "1" : "0"),
            ("idle_unload_min", _config.Asr.IdleUnloadMinutes.ToString(inv)),
            ("hotkey", _config.Hotkey.DisplayString + " / " + _config.Hotkey.ModeValue.ToYamlValue()),
            ("input_device", _config.Audio.InputDevice == AudioConfig.Auto ? "auto" : "custom"),
            ("llm_enabled", _config.Llm.Enabled ? "1" : "0"),
            ("qos_opt_out", PowerThrottling.OptedOut is { } qos ? (qos ? "1" : "0") : "-"),
            ("mic_probe", _micProbe.ToString()),
        };
        return DiagnosticsReport.Build(facts, _history.NewestFirst());
    }

    /// <summary>录制热键期间暂停全局热键监听（R2-04）：不然按下当前热键会触发一次听写，而不是被录进设置页。
    /// 正在听写时拒绝，避免销毁用户正在说的内容。</summary>
    private bool BeginHotkeyRecording()
    {
        if (IsDictating) return false;
        _controller?.SuspendHotkeyListening();
        return true;
    }

    private void EndHotkeyRecording()
    {
        try
        {
            _controller?.ResumeHotkeyListening();
        }
        catch (Exception ex)
        {
            // 恢复失败时控制器已把自己标记为未运行，交给就绪重评估的重试路径重新拉起。
            AppLog.Error("coordinator", "恢复热键监听失败，交由就绪重评估重试", ex);
            _ = ReevaluateReadinessAsync();
        }
    }

    private void HideSetupWindowIfVisible()
    {
        if (_userOpenedSetup) return;
        if (_setupForm?.HasUnsavedChanges == true) return;
        if (_setupForm is { Visible: true } form) form.Hide();
    }

    private void SyncSetupWindow()
    {
        _setupForm?.UpdateStatus(
            micProbe: _micProbe,
            asrState: _asrService.State,
            asrFailureMessage: _asrService.FailureMessage,
            downloadProgress: _isDownloadingModel ? _downloadProgress : null,
            hotkeyDisplay: _config.Hotkey.DisplayString,
            engineStatus: EngineStatusText(),
            downloadError: _modelDownloadError,
            downloadDetails: _modelDownloadDetails,
            modelDirectory: _asrService.ModelDirectory,
            micProbeDetail: _micProbeDetail,
            downloadStatus: DownloadStatusLine
        );
        SyncOnboarding();
    }

    // ─── 首启引导 ──────────────────────────────────────────────

    /// <summary>用户点托盘菜单「使用引导…」或首次启动时进入。已经打开则只是摆到面前。</summary>
    private void PresentOnboarding()
    {
        if (_onboardingForm is { IsDisposed: false } existing)
        {
            existing.Present();
            return;
        }

        var model = new OnboardingModel
        {
            HotkeyDisplay = _config.Hotkey.DisplayString,
            HotkeyMode = _config.Hotkey.ModeValue,
        };
        var form = new OnboardingForm(model)
        {
            OnRetryMicProbe = () => ProbeMicrophone(isFirstProbe: false),
            OnStartModelDownload = () => StartModelDownload(),
            OnCancelModelDownload = CancelModelDownload,
            OnSelectInputDevice = SelectInputDevice,
            OnStartMicTest = StartMicTest,
            OnStopMicTest = StopMicTest,
        };
        model.OnRefreshStatus = () => ProbeMicrophone(isFirstProbe: false);
        model.OnFinish = () => form.Close();
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_onboardingForm, form)) { _onboardingForm = null; _onboarding = null; }
            // 引导期间被压住的"未就绪"引导（缺模型等）在引导结束后恢复。
            _ = ReevaluateReadinessAsync();
        };
        _onboarding = model;
        _onboardingForm = form;
        ProbeMicrophoneIfUnknown();
        // 引导窗口出现时，设置窗口不该同时抢在它前面。
        if (_setupForm is { Visible: true } && !_userOpenedSetup) _setupForm.Hide();
        SyncOnboarding();
        form.Present();
    }

    private void SyncOnboarding()
    {
        if (_onboarding is not { } model || _onboardingForm is not { IsDisposed: false } form) return;
        model.Mic = _micProbe;
        model.MicDetail = _micProbeDetail;
        model.AsrState = _asrService.State;
        model.DownloadProgress = _isDownloadingModel ? _downloadProgress : null;
        model.DownloadStatus = DownloadStatusLine;
        model.DownloadError = _modelDownloadError;
        model.HotkeyDisplay = _config.Hotkey.DisplayString;
        model.HotkeyMode = _config.Hotkey.ModeValue;
        model.InputDevice = _config.Audio.InputDevice;
        form.RefreshFromModel();
    }

    private void ForwardToOnboarding(OnboardingDictationEvent dictationEvent)
    {
        if (_onboarding is not { } model || _onboardingForm is not { Visible: true } form) return;
        if (model.Handle(dictationEvent)) form.RefreshFromModel();
    }

    // ─── 检查更新 ──────────────────────────────────────────────

    /// <summary>只在用户主动点托盘菜单时跑一次，不做后台自动检查（见 <see cref="Services.UpdateChecker"/> 的注释）。</summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var outcome = await UpdateChecker.CheckForUpdateAsync().ConfigureAwait(true);
            switch (outcome)
            {
                case UpdateChecker.Outcome.UpToDate upToDate:
                    ShowInfo(L10n.T("已是最新版本"), L10n.F("当前版本 {0}。", upToDate.Current));
                    break;
                case UpdateChecker.Outcome.UpdateAvailable available:
                    if (AskOpenPage(L10n.T("有新版本可用"),
                            L10n.F("最新版本 {0}，当前版本 {1}。", available.Release.Version, AppConstants.Version)
                            + "\n\n" + L10n.T("是否打开发布页？")))
                    {
                        OpenUrl(available.Release.PageUrl);
                    }
                    break;
                case UpdateChecker.Outcome.Indeterminate indeterminate:
                    if (AskOpenPage(L10n.T("无法比较版本号"),
                            L10n.T("已获取到最新的发布信息，但无法解析版本号。请自行到发布页确认。") + "\n\n" + L10n.T("是否打开发布页？")))
                    {
                        OpenUrl(indeterminate.PageUrl);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("update", $"检查更新失败: {ex.Message}");
            if (AskOpenPage(L10n.T("无法检查更新"),
                    L10n.F("{0}\n可以稍后重试，或直接到 GitHub 发布页查看。", ex.Message) + "\n\n" + L10n.T("是否打开发布页？")))
            {
                OpenUrl(AppConstants.RepositoryUrl + "/releases");
            }
        }
    }

    private static void ShowInfo(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static bool AskOpenPage(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes;

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("update", $"打开发布页失败: {ex.Message}");
        }
    }

    private void UpdateTray()
    {
        _tray.Update(_currentState, _config.Hotkey.DisplayString, EngineStatusText());
    }

    /// <summary>
    /// 下载态不是 <see cref="AsrState"/> 的成员——下载是 <see cref="AppCoordinator"/> 的职责
    /// （<see cref="_isDownloadingModel"/> + <see cref="_downloadProgress"/>），不是引擎状态，
    /// 与 macOS <c>ASRService.State</c> 同样不含下载态一致（W-00）。
    /// </summary>
    private string EngineStatusText()
    {
        if (_isDownloadingModel) return L10n.F("下载中 {0}%", (int)(_downloadProgress * 100));

        return _asrService.State switch
        {
            AsrState.Ready => L10n.T("引擎已就绪"),
            AsrState.SuspendedForIdle => L10n.T("引擎已空闲卸载，下次录音自动加载"),
            AsrState.Loading => L10n.T("模型加载中…"),
            AsrState.ModelMissing => L10n.T("需要下载语音模型"),
            AsrState.Unloaded => L10n.T("引擎未加载"),
            AsrState.Failed => L10n.F("模型加载失败: {0}", _asrService.FailureMessage),
            _ => "",
        };
    }
}
