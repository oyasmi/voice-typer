using System;
using System.Windows.Forms;

namespace VoiceTyper.App;

/// <summary>
/// 自定义 <see cref="ApplicationContext"/>：没有主窗口，只有托盘。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppCoordinator _coordinator;

    public TrayApplicationContext()
    {
        _coordinator = new AppCoordinator();
        Application.ApplicationExit += OnApplicationExit;
        _coordinator.Start();
    }

    private void OnApplicationExit(object? sender, EventArgs e)
    {
        try { _coordinator.Dispose(); }
        catch { /* swallow */ }
    }
}
