using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using VoiceTyper.Llm;
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

    private RecordingHud? _hud;
    private SetupForm? _setupForm;
    private VoiceTyperController? _controller;

    private AppConfig _config = new();
    private AppStateInfo _currentState = AppStateInfo.Booting;
    private bool _micAccessDenied;
    private bool _userOpenedSetup;
    /// <summary>用户是否已从托盘菜单主动暂停听写。暂停时不监听热键（W-28）。</summary>
    private bool _isPaused;

    private ModelDownloader? _modelDownloader;
    private bool _isDownloadingModel;
    private double _downloadProgress;
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
        _tray.OnOpenConfigDirectory = () => _configStore.OpenConfigDirectory();
        _tray.OnQuit = () => Application.Exit();
        _tray.OnTogglePause = TogglePause;

        _asrService.OnStateChange = _ => { _ = ReevaluateReadinessAsync(); };
    }

    public void Start()
    {
        try
        {
            ReloadConfigurationFromDisk();
        }
        catch (Exception ex)
        {
            AppLog.Error("coordinator", "加载配置失败", ex);
            _currentState = AppStateInfo.ErrorWith("配置加载失败");
            UpdateTray();
            return;
        }

        UpdateTray();
        _asrService.UpdateConfig(_config.Asr);

        ProbeMicrophone(isFirstProbe: true);
    }

    public void Dispose()
    {
        _dictationErrorRecoveryTimer?.Stop();
        _dictationErrorRecoveryTimer?.Dispose();
        _controller?.Dispose();
        _hud?.Dispose();
        _setupForm?.Dispose();
        _tray.Dispose();
        _asrService.Dispose();
    }

    // ─── 配置 ──────────────────────────────────────────────────

    private void ReloadConfigurationFromDisk()
    {
        _config = _configStore.LoadOrCreate();
        _hud?.Dispose();
        _hud = new RecordingHud(_config.UI);
    }

    /// <summary>
    /// 会破坏性重建控制器/热键监听的保存（非 UI-only）在听写进行中会丢已录制内容，
    /// 拒绝而不是静默销毁；HUD 透明度等 UI-only 改动不受影响，可随时保存（R2-04 / F-13）。
    /// </summary>
    private async Task ApplyConfigAsync(AppConfig draft)
    {
        var onlyUiChanged = AsrConfigEquals(_config.Asr, draft.Asr)
            && LlmConfigEquals(_config.Llm, draft.Llm)
            && HotkeyConfigEquals(_config.Hotkey, draft.Hotkey);

        if (!onlyUiChanged && _currentState.State.IsActiveDictation())
        {
            throw new InvalidOperationException("正在录音/识别/输入，请等待当前听写完成后再保存此项设置");
        }

        _configStore.Save(draft);
        if (onlyUiChanged)
        {
            // 只有 ui 段变化：不销毁/重建控制器，只更新内存配置与 HUD/设置窗口显示。
            _config = draft.Validated();
            _hud?.ApplyOpacity(_config.UI.Opacity);
            _setupForm?.LoadEditableContent(_config);
        }
        else
        {
            await ReloadAndReevaluateAsync().ConfigureAwait(true);
        }
    }

    private static bool AsrConfigEquals(AsrConfig a, AsrConfig b) =>
        a.Language == b.Language && a.Threads == b.Threads && a.ModelDir == b.ModelDir
        && a.PreviewWindowSeconds == b.PreviewWindowSeconds && a.IdleUnloadMinutes == b.IdleUnloadMinutes;

    private static bool LlmConfigEquals(LlmConfig a, LlmConfig b) =>
        a.Enabled == b.Enabled && a.BaseUrl == b.BaseUrl && a.Model == b.Model
        && a.Temperature.Equals(b.Temperature) && a.MaxTokens == b.MaxTokens && a.Timeout.Equals(b.Timeout);

    private static bool HotkeyConfigEquals(HotkeyConfig a, HotkeyConfig b) =>
        a.Key == b.Key && a.Modifiers.SequenceEqual(b.Modifiers);

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

    private void ProbeMicrophone(bool isFirstProbe)
    {
        _ = Task.Run(() =>
        {
            MicPermissionProbe.TryProbe(out var denied);
            return denied;
        }).ContinueWith(t =>
        {
            UiDispatcher.Post(() =>
            {
                _micAccessDenied = t.IsCompletedSuccessfully && t.Result;
                if (isFirstProbe)
                {
                    if (_micAccessDenied)
                    {
                        OpenSetup(SetupTab.Permissions);
                    }
                    // 权限与模型是两条互不依赖的准备线：麦克风没就绪时模型照样在后台加载。
                    _ = _asrService.PreloadAsync();
                    _ = ReevaluateReadinessAsync();
                }
                else
                {
                    SyncSetupWindow();
                    UpdateTray();
                }
            });
        });
    }

    // ─── 就绪状态机 ────────────────────────────────────────────

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

        if (_isDownloadingModel)
        {
            _currentState = AppStateInfo.DownloadingModelWith(_downloadProgress);
            SyncSetupWindow();
            UpdateTray();
            return;
        }

        switch (_asrService.State)
        {
            case AsrState.Unloaded:
                _ = _asrService.PreloadAsync();
                _currentState = AppStateInfo.ModelLoading;
                break;
            case AsrState.Loading:
                _currentState = AppStateInfo.ModelLoading;
                break;
            case AsrState.ModelMissing:
                _currentState = AppStateInfo.ModelMissing;
                OpenSetup(SetupTab.Recognition);
                break;
            case AsrState.Failed:
                _currentState = AppStateInfo.ErrorWith(_asrService.FailureMessage ?? "未知错误");
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

    private void ActivateReadyState()
    {
        EnsureController();
        if (_controller is { IsRunning: false } controller)
        {
            try
            {
                controller.Start();
            }
            catch (Exception ex)
            {
                AppLog.Error("coordinator", "启动 controller 失败", ex);
                _currentState = AppStateInfo.ErrorWith($"热键监听失败：{ex.Message}");
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
        var controller = new VoiceTyperController(_config, _asrService);
        BindControllerEvents(controller);
        _controller = controller;
    }

    private void BindControllerEvents(VoiceTyperController controller)
    {
        controller.StateChanged = state =>
        {
            var previous = _currentState;
            _currentState = state;
            switch (state.State)
            {
                case AppState.Recording:
                    CancelDictationErrorRecovery();
                    _hud?.ShowRecording();
                    break;
                case AppState.Recognizing:
                    _hud?.SetRecognizing();
                    break;
                case AppState.Inserting:
                    break;
                case AppState.Error:
                    _hud?.ShowError(state.Message ?? "");
                    ScheduleDictationErrorRecovery();
                    break;
                case AppState.Idle:
                    CancelDictationErrorRecovery();
                    if (previous.State == AppState.Inserting) _hud?.ShowSuccess();
                    else _hud?.HideHud();
                    break;
                default:
                    _hud?.HideHud();
                    break;
            }
            UpdateTray();
        };

        controller.PreviewUpdate = preview => _hud?.ShowPreview(preview);
        controller.PreviewWarning = message => _hud?.FlashWarning(message);
        controller.Cancelled = () =>
        {
            _currentState = AppStateInfo.Idle;
            _hud?.ShowCanceled();
            UpdateTray();
        };
        // 识别文本本身不落日志，只记字数——AGENTS.md 明确禁止日志包含不必要的用户文本。
        controller.RecognizedText = text => AppLog.Info("coordinator", $"识别完成 chars={text?.Length ?? 0}");
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

    private void StartModelDownload()
    {
        if (_isDownloadingModel) return;
        _isDownloadingModel = true;
        _downloadProgress = 0;
        if (!_isPaused) _currentState = AppStateInfo.DownloadingModelWith(0);
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
                        if (!_isDownloadingModel) return;
                        _downloadProgress = progress;
                        if (!_isPaused) _currentState = AppStateInfo.DownloadingModelWith(progress);
                        UpdateTray();
                        SyncSetupWindow();
                    });
                }).ConfigureAwait(false);

                UiDispatcher.Post(() => _ = FinishModelDownloadAsync());
            }
            catch (OperationCanceledException)
            {
                UiDispatcher.Post(() =>
                {
                    _isDownloadingModel = false;
                    _modelDownloader?.Dispose();
                    _modelDownloader = null;
                    if (!_isPaused) _currentState = AppStateInfo.ModelMissing;
                    UpdateTray();
                    SyncSetupWindow();
                });
            }
            catch (Exception ex)
            {
                AppLog.Error("model", "模型下载失败", ex);
                UiDispatcher.Post(() =>
                {
                    _isDownloadingModel = false;
                    _modelDownloader?.Dispose();
                    _modelDownloader = null;
                    if (!_isPaused) _currentState = AppStateInfo.ErrorWith($"模型下载失败: {ex.Message}");
                    UpdateTray();
                    SyncSetupWindow();
                });
            }
        });
    }

    private async Task FinishModelDownloadAsync()
    {
        try
        {
            _isDownloadingModel = false;
            _modelDownloader?.Dispose();
            _modelDownloader = null;
            await _asrService.ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("coordinator", "下载完成后重新加载模型失败", ex);
            _currentState = AppStateInfo.ErrorWith($"模型加载失败: {ex.Message}");
            UpdateTray();
            SyncSetupWindow();
        }
    }

    private void CancelModelDownload() => _modelDownloader?.Cancel();

    private void ReloadModel() => _ = ReloadModelAsync();

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
        });
        const string sample = "呃，这个功能和并之后应该可以用了吧";
        try
        {
            await corrector.TestAsync(sample).ConfigureAwait(true);
            return new LlmTestResult(true, "纠错测试成功，配置可用。");
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
        EnsureSetupForm();
        _setupForm!.LoadEditableContent(_config);
        SyncSetupWindow();
        if (preferredTab is { } tab) _setupForm.SelectTab(tab);
        _setupForm.Present();
    }

    private void EnsureSetupForm()
    {
        if (_setupForm is not null && !_setupForm.IsDisposed) return;

        var form = new SetupForm();
        form.OnSaveConfig = draft => ApplyConfigAsync(draft);
        form.OnSaveLlmApiKey = apiKey => SecretStore.SaveLlmApiKey(apiKey);
        form.OnLoadLlmApiKey = () => SecretStore.LoadLlmApiKeyResult();
        form.OnStartModelDownload = StartModelDownload;
        form.OnCancelModelDownload = CancelModelDownload;
        form.OnReloadModel = ReloadModel;
        form.OnTestLlmCorrection = (llmConfig, apiKey) => TestLlmCorrectionAsync(llmConfig, apiKey);
        form.OnRetryMicProbe = () => ProbeMicrophone(isFirstProbe: false);
        form.OnPreviewHudOpacity = opacity => _hud?.ApplyOpacity(opacity);
        form.OnUserClosedWindow = () => _userOpenedSetup = false;
        _setupForm = form;
    }

    private void HideSetupWindowIfVisible()
    {
        if (_userOpenedSetup) return;
        if (_setupForm is { Visible: true } form) form.Hide();
    }

    private void SyncSetupWindow()
    {
        _setupForm?.UpdateStatus(
            micAccessDenied: _micAccessDenied,
            asrState: _asrService.State,
            asrFailureMessage: _asrService.FailureMessage,
            downloadProgress: _isDownloadingModel ? _downloadProgress : null,
            hotkeyDisplay: _config.Hotkey.DisplayString,
            engineStatus: EngineStatusText()
        );
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
        if (_isDownloadingModel) return $"下载中 {(int)(_downloadProgress * 100)}%";

        return _asrService.State switch
        {
            AsrState.Ready => "引擎已就绪",
            AsrState.SuspendedForIdle => "引擎已空闲卸载，下次录音自动加载",
            AsrState.Loading => "模型加载中…",
            AsrState.ModelMissing => "需要下载语音模型",
            AsrState.Unloaded => "引擎未加载",
            AsrState.Failed => $"模型加载失败: {_asrService.FailureMessage}",
            _ => "",
        };
    }
}
