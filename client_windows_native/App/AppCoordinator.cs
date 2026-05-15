using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VoiceTyper.Core;
using VoiceTyper.Services;
using VoiceTyper.Support;
using VoiceTyper.UI;

namespace VoiceTyper.App;

/// <summary>
/// 中央调度器。负责装配、生命周期、配置变更处理。
/// 所有公共方法必须在 UI 线程调用。
/// </summary>
internal sealed class AppCoordinator : IDisposable
{
    private readonly ConfigStore _configStore = new();
    private readonly TrayController _tray = new();
    private RecordingHud? _hud;
    private SetupForm? _setupForm;
    private VoiceTyperController? _controller;

    private AppConfig _config = new();
    private IReadOnlyList<string> _hotwords = Array.Empty<string>();
    private string _managedHotwordsText = "";

    private AppStateInfo _currentState = AppStateInfo.Booting;
    private bool _serverReady;
    private bool _micPermissionDenied;

    public AppCoordinator()
    {
        _tray.OnOpenSetup = () => OpenSetup();
        _tray.OnReconnectServer = () => _ = RefreshServerStatusAsync();
        _tray.OnOpenConfigDirectory = () => _configStore.OpenConfigDirectory();
        _tray.OnQuit = () => System.Windows.Forms.Application.Exit();
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
        _ = ReevaluateReadinessAsync();
    }

    public void Dispose()
    {
        _controller?.Dispose();
        _hud?.Dispose();
        _setupForm?.Dispose();
        _tray.Dispose();
    }

    public void OpenSetup()
    {
        EnsureSetupForm();
        _setupForm!.LoadEditableContent(_config, _managedHotwordsText, _configStore.AdditionalHotwordFileCount(_config));
        _setupForm!.UpdateServerStatus(_serverReady, ServerDisplay(), _micPermissionDenied);
        _setupForm!.Present();
    }

    // ─── 配置 ──────────────────────────────────────────────────

    private void ReloadConfigurationFromDisk()
    {
        _config = _configStore.LoadOrCreate();
        _hotwords = _configStore.LoadHotwords(_config);
        _managedHotwordsText = _configStore.LoadManagedHotwordsText(_config);

        // 重建 HUD（UI 配置可能变了）
        _hud?.Dispose();
        _hud = new RecordingHud(_config.UI);
    }

    private async Task ApplyConfigAndReloadAsync(AppConfig draft)
    {
        _configStore.Save(draft);
        await ReloadAndReevaluateAsync().ConfigureAwait(true);
    }

    private async Task ApplyHotwordsAndReloadAsync(string text)
    {
        _configStore.SaveManagedHotwordsText(text, _config);
        await ReloadAndReevaluateAsync().ConfigureAwait(true);
    }

    private async Task ReloadAndReevaluateAsync()
    {
        _controller?.Stop();
        _controller?.Dispose();
        _controller = null;
        _serverReady = false;

        ReloadConfigurationFromDisk();

        if (_setupForm is { } form)
        {
            form.LoadEditableContent(_config, _managedHotwordsText, _configStore.AdditionalHotwordFileCount(_config));
        }
        UpdateTray();
        await ReevaluateReadinessAsync().ConfigureAwait(true);
    }

    // ─── 服务/录音可用性 ─────────────────────────────────────

    private async Task ReevaluateReadinessAsync()
    {
        await RefreshServerStatusAsync().ConfigureAwait(true);

        if (_serverReady)
        {
            EnsureController();
            try
            {
                _controller!.Start();
                if (_currentState.State is not AppState.Recording and not AppState.Recognizing and not AppState.Inserting)
                {
                    _currentState = AppStateInfo.Idle;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("coordinator", "启动 controller 失败", ex);
                _currentState = AppStateInfo.ErrorWith($"热键监听失败：{ex.Message}");
            }
        }
        else
        {
            _controller?.Stop();
            _currentState = AppStateInfo.SetupRequired;
        }

        UpdateTray();
        if (_setupForm is { } form)
        {
            form.UpdateServerStatus(_serverReady, ServerDisplay(), _micPermissionDenied);
        }
    }

    private async Task RefreshServerStatusAsync()
    {
        try
        {
            _serverReady = await ASRClient.HealthCheckAsync(_config.Server).ConfigureAwait(true);
        }
        catch
        {
            _serverReady = false;
        }
        UpdateTray();
        if (_setupForm is { } form)
        {
            form.UpdateServerStatus(_serverReady, ServerDisplay(), _micPermissionDenied);
        }
    }

    private void EnsureController()
    {
        if (_controller is not null) return;
        var controller = new VoiceTyperController(_config, _hotwords);

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
                case AppState.Idle:
                case AppState.Inserting:
                case AppState.Error:
                case AppState.SetupRequired:
                case AppState.Booting:
                default:
                    _hud?.HideHud();
                    break;
            }

            // 录音异常时持续显示麦克风权限提示
            if (state.State == AppState.Error && state.Message?.Contains("麦克风") == true)
            {
                _micPermissionDenied = true;
                if (_setupForm is { } form)
                {
                    form.UpdateServerStatus(_serverReady, ServerDisplay(), _micPermissionDenied);
                }
            }

            UpdateTray();
        };

        controller.PreviewUpdate = preview => _hud?.ShowPreview(preview);
        controller.RecognizedText = text => AppLog.Info("coordinator", $"识别结果: {Truncate(text, 80)}");

        _controller = controller;
    }

    private void EnsureSetupForm()
    {
        if (_setupForm is not null && !_setupForm.IsDisposed) return;

        var form = new SetupForm();
        form.OnTestServerConnection = async server => await ASRClient.HealthCheckAsync(server).ConfigureAwait(true);
        form.OnSaveConfig = async draft => await ApplyConfigAndReloadAsync(draft).ConfigureAwait(true);
        form.OnSaveHotwords = async text => await ApplyHotwordsAndReloadAsync(text).ConfigureAwait(true);
        form.OnRetryServerCheck = () => _ = RefreshServerStatusAsync();
        _setupForm = form;
    }

    private void UpdateTray()
    {
        _tray.Update(_currentState, _config.Hotkey.DisplayString, ServerDisplay());
    }

    private string ServerDisplay()
    {
        var addr = $"{_config.Server.Host}:{_config.Server.Port}";
        return _serverReady ? $"已连接 {addr}" : $"未连接 {addr}";
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";
}
