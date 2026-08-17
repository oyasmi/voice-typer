using System;
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

    private ModelDownloader? _modelDownloader;
    private bool _isDownloadingModel;
    private double _downloadProgress;

    public AppCoordinator()
    {
        _tray.OnOpenSetup = () => OpenSetup();
        _tray.OnOpenConfigDirectory = () => _configStore.OpenConfigDirectory();
        _tray.OnQuit = () => Application.Exit();

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

    private async Task ApplyConfigAsync(AppConfig draft)
    {
        _configStore.Save(draft);
        await ReloadAndReevaluateAsync().ConfigureAwait(true);
    }

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
                ActivateReadyState();
                break;
        }

        SyncSetupWindow();
        UpdateTray();
        await Task.CompletedTask;
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
            _currentState = state;
            switch (state.State)
            {
                case AppState.Recording:
                    _hud?.ShowRecording();
                    break;
                case AppState.Recognizing:
                    _hud?.SetRecognizing();
                    break;
                default:
                    _hud?.HideHud();
                    break;
            }
            UpdateTray();
        };

        controller.PreviewUpdate = preview => _hud?.ShowPreview(preview);
        controller.RecognizedText = text => AppLog.Info("coordinator", $"识别结果: {Truncate(text, 80)}");
    }

    // ─── 模型下载 ──────────────────────────────────────────────

    private void StartModelDownload()
    {
        if (_isDownloadingModel) return;
        _isDownloadingModel = true;
        _downloadProgress = 0;
        _currentState = AppStateInfo.DownloadingModelWith(0);
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
                        _currentState = AppStateInfo.DownloadingModelWith(progress);
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
                    _currentState = AppStateInfo.ModelMissing;
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
                    _currentState = AppStateInfo.ErrorWith($"模型下载失败: {ex.Message}");
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

    private async Task<bool> TestLlmCorrectionAsync(LlmConfig llmConfig, string apiKey)
    {
        using var corrector = new LlmCorrector(new LlmCorrector.Config
        {
            BaseUrl = llmConfig.BaseUrl,
            ApiKey = apiKey,
            Model = llmConfig.Model,
            Temperature = llmConfig.Temperature,
            MaxTokens = llmConfig.MaxTokens,
            Timeout = llmConfig.Timeout,
        });
        const string sample = "呃，这个功能和并之后应该可以用了吧";
        var result = await corrector.CorrectAsync(sample).ConfigureAwait(true);
        return result != sample && !string.IsNullOrEmpty(result);
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

    private string EngineStatusText() => _asrService.State switch
    {
        AsrState.Ready => "引擎已就绪",
        AsrState.Loading => "模型加载中…",
        AsrState.ModelMissing => "需要下载语音模型",
        AsrState.DownloadingModel => $"下载中 {(int)(_downloadProgress * 100)}%",
        AsrState.Unloaded => "引擎未加载",
        AsrState.Failed => $"模型加载失败: {_asrService.FailureMessage}",
        _ => "",
    };

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";
}
