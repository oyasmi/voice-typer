using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

/// <summary>
/// 系统托盘图标管理器。使用 WinForms NotifyIcon（内置，无外部依赖）。
/// 菜单结构与 macOS Swift 版 StatusBarController 对齐。
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _statsItem;
    private readonly ToolStripMenuItem _toggleItem;

    public event Action? OnToggleEnabled;
    public event Action? OnOpenConfig;
    public event Action? OnOpenHotwords;
    public event Action? OnOpenConfigDir;
    public event Action? OnOpenSetup;
    public event Action? OnQuit;

    public TrayIconManager()
    {
        var icon = LoadIcon();

        // 状态行（只读）
        _statusItem = new ToolStripMenuItem("状态: 启动中...") { Enabled = false };
        _statsItem = new ToolStripMenuItem("已输入: 0字（0次）") { Enabled = false };

        // 启用/禁用切换
        _toggleItem = new ToolStripMenuItem("启用语音输入")
        {
            CheckOnClick = true,
            Checked = true
        };
        _toggleItem.Click += (s, e) => OnToggleEnabled?.Invoke();

        // 构建菜单
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_statsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开配置文件", null, (s, e) => OnOpenConfig?.Invoke());
        menu.Items.Add("打开词库文件", null, (s, e) => OnOpenHotwords?.Invoke());
        menu.Items.Add("打开配置目录", null, (s, e) => OnOpenConfigDir?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置...", null, (s, e) => OnOpenSetup?.Invoke());
        menu.Items.Add("关于", null, (s, e) => ShowAbout());
        menu.Items.Add("退出", null, (s, e) => OnQuit?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = Constants.AppName,
            Visible = true,
            ContextMenuStrip = menu
        };
    }

    /// <summary>更新托盘状态显示</summary>
    public void Update(AppState state, string hotkeyDisplay)
    {
        _statusItem.Text = $"状态: {state.MenuTitle()}";

        // NotifyIcon.Text 最多 127 个字符
        var tooltip = $"{Constants.AppName} - {state.MenuTitle()}";
        if (!string.IsNullOrEmpty(hotkeyDisplay))
            tooltip += $" [{hotkeyDisplay}]";
        _notifyIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    /// <summary>更新统计信息</summary>
    public void UpdateStats(int charCount, int inputCount)
    {
        var chars = charCount >= 10000
            ? $"{charCount / 10000.0:F1}万字"
            : $"{charCount}字";
        _statsItem.Text = $"已输入: {chars}（{inputCount}次）";
    }

    /// <summary>设置启用/禁用状态</summary>
    public void SetEnabled(bool enabled)
    {
        _toggleItem.Checked = enabled;
    }

    private void ShowAbout()
    {
        var message = $"{Constants.AppName} v{Constants.Version}\n\n" +
                      $"配置目录: {ConfigStore.ConfigDir}\n\n" +
                      "基于 FunASR 的离线语音识别工具";
        MessageBox.Show(message, $"关于 {Constants.AppName}",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static Icon LoadIcon()
    {
        // 尝试从多个位置加载图标
        var paths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon.ico"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico"),
        };

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            try { return new Icon(path); }
            catch { /* 继续尝试下一个路径 */ }
        }

        // 降级：使用系统默认图标
        AppLog.Warning("未找到图标文件，使用系统默认图标");
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
