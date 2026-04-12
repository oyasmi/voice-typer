using System.Diagnostics;
using System.IO.Pipes;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.Services;
using VoiceTyper.Support;
using VoiceTyper.UI;

namespace VoiceTyper.App;

/// <summary>
/// 应用中心调度器。继承 ApplicationContext 提供 WinForms 消息循环。
/// 集成: 控制器 + 托盘 + 录音浮窗 + 设置窗口 + Named Pipe 单实例激活。
/// 对应 macOS Swift 版的 AppCoordinator + AppDelegate 角色。
/// </summary>
internal sealed class AppCoordinator : ApplicationContext
{
    private readonly ConfigStore _configStore = new();
    private readonly TrayIconManager _trayIcon;
    private readonly SynchronizationContext? _uiContext;

    private VoiceTyperController? _controller;
    private RecordingOverlay? _overlay;
    private SetupForm? _activeSetupForm;
    private AppConfig? _config;
    private bool _enabled;
    private bool _serverConnected;
    private CancellationTokenSource? _pipeCts;

    public AppCoordinator()
    {
        _trayIcon = new TrayIconManager();

        // 在 NotifyIcon 创建后捕获 UI SynchronizationContext（用于 Pipe 回调）
        _uiContext = SynchronizationContext.Current;

        _trayIcon.OnToggleEnabled += ToggleEnabled;
        _trayIcon.OnOpenConfig += OpenConfig;
        _trayIcon.OnOpenHotwords += OpenHotwords;
        _trayIcon.OnOpenConfigDir += OpenConfigDir;
        _trayIcon.OnOpenSetup += ShowSetupWindow;
        _trayIcon.OnQuit += Quit;

        // 启动 Named Pipe 服务端（接收第二实例的激活信号）
        StartPipeServer();

        // 异步初始化（不阻塞应用启动）
        _ = InitializeAsync();
    }

    // ===================================================================
    //  初始化
    // ===================================================================

    private async Task InitializeAsync()
    {
        try
        {
            AppLog.Info("========================================");
            AppLog.Info($"{Constants.AppName} v{Constants.Version} 启动");
            AppLog.Info("========================================");

            // 1. 加载配置
            AppLog.Info("加载配置...");
            var (config, hotwords) = _configStore.LoadOrCreate();
            _config = config;
            AppLog.Info($"配置: {config.Server.Host}:{config.Server.Port}, " +
                        $"热键: {HotkeyService.FormatHotkey(config.Hotkey)}, " +
                        $"词库: {hotwords.Count} 词");

            // 2. 初始化控制器
            _controller = new VoiceTyperController(config, hotwords);
            _controller.OnStateChange += OnStateChange;

            // 3. 创建录音浮窗
            _overlay = new RecordingOverlay(config.Ui.Width, config.Ui.Height, config.Ui.Opacity);

            // 4. 健康检查
            AppLog.Info("检查服务端...");
            _serverConnected = await _controller.HealthCheckAsync();
            if (_serverConnected)
            {
                var llm = config.Server.LlmRecorrect ? "（LLM 修正: 已启用）" : "";
                AppLog.Info($"语音识别服务已连接 {llm}");
            }
            else
            {
                AppLog.Warning("语音识别服务未就绪，请确认服务端已启动");
            }

            // 5. 检测麦克风
            var micCount = AudioCaptureService.GetCaptureDeviceCount();
            AppLog.Info($"音频输入设备: {micCount} 个");

            // 6. 启动
            _controller.Start();
            _enabled = true;

            var hotkey = HotkeyService.FormatHotkey(config.Hotkey);
            _trayIcon.Update(AppState.Idle, hotkey);
            AppLog.Info($"启动完成，按住 {hotkey} 开始语音输入");
        }
        catch (Exception ex)
        {
            AppLog.Error("初始化失败", ex);
            _trayIcon.Update(AppState.Error, "");
            MessageBox.Show(
                $"VoiceTyper 初始化失败:\n\n{ex.Message}\n\n请检查日志文件。",
                Constants.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ===================================================================
    //  状态变化处理
    // ===================================================================

    private void OnStateChange(AppState state)
    {
        var hotkey = _config != null ? HotkeyService.FormatHotkey(_config.Hotkey) : "";
        _trayIcon.Update(state, hotkey);

        if (_controller != null)
            _trayIcon.UpdateStats(_controller.CharCount, _controller.InputCount);

        // 控制录音浮窗
        switch (state)
        {
            case AppState.Recording:
                _overlay?.ShowOverlay("正在听...");
                break;
            case AppState.Recognizing:
                _overlay?.UpdateOverlayText("识别中...");
                break;
            default:
                _overlay?.HideOverlay();
                break;
        }
    }

    // ===================================================================
    //  设置窗口
    // ===================================================================

    private void ShowSetupWindow()
    {
        // 防止重复打开
        if (_activeSetupForm != null && !_activeSetupForm.IsDisposed)
        {
            _activeSetupForm.BringToFront();
            _activeSetupForm.Activate();
            return;
        }

        var micCount = AudioCaptureService.GetCaptureDeviceCount();
        _activeSetupForm = new SetupForm(_config!, _serverConnected, micCount);

        if (_activeSetupForm.ShowDialog() == DialogResult.OK)
        {
            var newConfig = _activeSetupForm.GetConfig();
            var hotwordsText = _activeSetupForm.GetHotwordsText();

            // 保存词库文件
            try
            {
                File.WriteAllText(ConfigStore.DefaultHotwordsPath, hotwordsText);
                AppLog.Info("词库已保存");
            }
            catch (Exception ex)
            {
                AppLog.Error("保存词库失败", ex);
            }

            // 应用新配置
            ApplyNewConfig(newConfig);
        }

        _activeSetupForm = null;
    }

    /// <summary>应用新配置：保存 → 重启控制器 → 重建浮窗</summary>
    private async void ApplyNewConfig(AppConfig newConfig)
    {
        // 停止当前控制器
        _controller?.Stop();
        _controller?.Dispose();

        // 保存配置
        _configStore.Save(newConfig);
        _config = newConfig;

        // 重新加载词库
        var hotwords = _configStore.LoadHotwords(newConfig.HotwordFiles);

        // 重建控制器
        _controller = new VoiceTyperController(newConfig, hotwords);
        _controller.OnStateChange += OnStateChange;

        // 重建浮窗（尺寸/透明度可能已变化）
        _overlay?.Dispose();
        _overlay = new RecordingOverlay(newConfig.Ui.Width, newConfig.Ui.Height, newConfig.Ui.Opacity);

        // 重新检查服务连接
        _serverConnected = await _controller.HealthCheckAsync();

        // 重启
        if (_enabled)
        {
            _controller.Start();
            var hotkey = HotkeyService.FormatHotkey(newConfig.Hotkey);
            _trayIcon.Update(AppState.Idle, hotkey);
            AppLog.Info($"配置已更新，热键: {hotkey}");
        }
    }

    // ===================================================================
    //  Named Pipe 服务端 (单实例激活)
    // ===================================================================

    /// <summary>
    /// 启动 Named Pipe 服务端。当第二个 VoiceTyper 实例启动时,
    /// 它会通过 Pipe 发送 "activate" 信号，第一个实例收到后打开设置窗口。
    /// </summary>
    private void StartPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        "VoiceTyper.Activate", PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    using var reader = new StreamReader(server);
                    var message = await reader.ReadToEndAsync(token);

                    if (message.Trim() == "activate")
                    {
                        AppLog.Info("收到第二实例激活信号");
                        _uiContext?.Post(_ => ShowSetupWindow(), null);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AppLog.Warning($"Pipe server 异常: {ex.Message}");
                    try { await Task.Delay(1000, token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, token);
    }

    // ===================================================================
    //  托盘菜单操作
    // ===================================================================

    private void ToggleEnabled()
    {
        if (_controller == null) return;

        if (_enabled)
        {
            _controller.Stop();
            _enabled = false;
            _trayIcon.SetEnabled(false);
            _trayIcon.Update(AppState.Idle, "");
            _overlay?.HideOverlay();
            AppLog.Info("已暂停语音输入");
        }
        else
        {
            _controller.Start();
            _enabled = true;
            var hotkey = _config != null ? HotkeyService.FormatHotkey(_config.Hotkey) : "";
            _trayIcon.SetEnabled(true);
            _trayIcon.Update(AppState.Idle, hotkey);
            AppLog.Info("已启用语音输入");
        }
    }

    private void OpenConfig() => OpenFileOrDir(ConfigStore.ConfigPath);
    private void OpenHotwords() => OpenFileOrDir(ConfigStore.DefaultHotwordsPath);
    private void OpenConfigDir() => OpenFileOrDir(ConfigStore.ConfigDir);

    private static void OpenFileOrDir(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Error($"打开失败: {path}", ex); }
    }

    private void Quit()
    {
        AppLog.Info("用户退出");
        _pipeCts?.Cancel();
        _controller?.Stop();
        _controller?.Dispose();
        _overlay?.Dispose();
        _trayIcon.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pipeCts?.Cancel();
            _pipeCts?.Dispose();
            _controller?.Dispose();
            _overlay?.Dispose();
            _trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
