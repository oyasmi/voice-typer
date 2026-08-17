using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

/// <summary>
/// 系统托盘图标 + 上下文菜单。所有方法必须在 UI 线程调用。
///
/// 与 <c>client_windows_native/UI/TrayController.cs</c> 相比：删掉"重新连接服务"（本地引擎没有
/// 连接概念），新增"开机自启"复选与"关于"（见 windows/DESIGN.md §5.6）。
/// </summary>
internal sealed class TrayController : IDisposable
{
    public Action? OnOpenSetup;
    public Action? OnOpenConfigDirectory;
    public Action? OnQuit;

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _hotkeyItem;
    private readonly ToolStripMenuItem _engineItem;
    private readonly ToolStripMenuItem _startupItem;
    private Icon? _currentIcon;
    private AppState _lastState = AppState.Booting;
    private readonly Icon _appIcon;

    public TrayController()
    {
        _appIcon = LoadAppIcon();

        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
        };

        _statusItem = new ToolStripMenuItem("启动中") { Enabled = false };
        _hotkeyItem = new ToolStripMenuItem("热键：-") { Enabled = false };
        _engineItem = new ToolStripMenuItem("引擎：检查中") { Enabled = false };

        var setupItem = new ToolStripMenuItem("设置...");
        setupItem.Click += (_, _) => OnOpenSetup?.Invoke();

        var openConfigItem = new ToolStripMenuItem("打开配置目录");
        openConfigItem.Click += (_, _) => OnOpenConfigDirectory?.Invoke();

        _startupItem = new ToolStripMenuItem("开机自启") { CheckOnClick = true };
        _startupItem.Checked = StartupRegistration.IsEnabled;
        _startupItem.Click += (_, _) =>
        {
            StartupRegistration.SetEnabled(_startupItem.Checked);
        };

        var aboutItem = new ToolStripMenuItem("关于 VoiceTyper");
        aboutItem.Click += (_, _) => ShowAbout();

        var quitItem = new ToolStripMenuItem("退出");
        quitItem.Click += (_, _) => OnQuit?.Invoke();

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            _hotkeyItem,
            _engineItem,
            new ToolStripSeparator(),
            setupItem,
            openConfigItem,
            new ToolStripSeparator(),
            _startupItem,
            aboutItem,
            new ToolStripSeparator(),
            quitItem,
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = $"VoiceTyper {AppConstants.Version}",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.MouseClick += OnTrayClick;
    }

    public void Update(AppStateInfo info, string hotkeyDisplay, string engineStatus)
    {
        _statusItem.Text = info.MenuTitle;
        _hotkeyItem.Text = $"热键：{hotkeyDisplay}";
        _engineItem.Text = $"引擎：{engineStatus}";

        if (info.State != _lastState)
        {
            _lastState = info.State;
            ApplyStateIcon(info.State);
        }

        var tooltip = $"VoiceTyper · {info.MenuTitle} · {hotkeyDisplay}";
        if (tooltip.Length > 63) tooltip = tooltip.Substring(0, 63);
        _notifyIcon.Text = tooltip;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _currentIcon?.Dispose();
        _appIcon.Dispose();
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            OnOpenSetup?.Invoke();
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            $"VoiceTyper {AppConstants.Version}\n\n离线语音输入工具，基于 SenseVoice-Small。\nPowered by SenseVoice-Small (FunAudioLLM)",
            "关于 VoiceTyper",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information
        );
    }

    private void ApplyStateIcon(AppState state)
    {
        try
        {
            var icon = RenderStateIcon(state);
            _notifyIcon.Icon = icon;
            _currentIcon?.Dispose();
            _currentIcon = icon;
        }
        catch (Exception ex)
        {
            AppLog.Debug("ui", $"生成状态图标失败，回落到主图标: {ex.Message}");
            _notifyIcon.Icon = _appIcon;
        }
    }

    private static Icon RenderStateIcon(AppState state)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // 简化的麦克风：矩形话筒 + 底座
            using var micBrush = new SolidBrush(Color.FromArgb(220, 235, 235, 240));
            using var stand = new Pen(Color.FromArgb(220, 235, 235, 240), 2.4f);

            var capRect = new RectangleF(11f, 5f, 10f, 14f);
            g.FillRoundedRectangle(micBrush, capRect, 5f);
            g.DrawLine(stand, 16f, 20f, 16f, 25f);
            g.DrawLine(stand, 11f, 25f, 21f, 25f);
            g.DrawArc(stand, 7f, 11f, 18f, 14f, 0, 180);

            // 状态色点（右下角）
            var statusColor = state switch
            {
                AppState.Recording => Color.FromArgb(255, 230, 64, 60),
                AppState.Recognizing => Color.FromArgb(255, 240, 180, 30),
                AppState.Inserting => Color.FromArgb(255, 240, 130, 30),
                AppState.Error => Color.FromArgb(255, 220, 50, 50),
                AppState.SetupRequired => Color.FromArgb(255, 240, 170, 0),
                AppState.ModelMissing or AppState.DownloadingModel => Color.FromArgb(255, 230, 140, 30),
                AppState.ModelLoading => Color.FromArgb(255, 160, 160, 160),
                AppState.Booting => Color.FromArgb(255, 160, 160, 160),
                AppState.Idle => Color.FromArgb(0, 0, 0, 0), // 透明，不画
                _ => Color.FromArgb(0, 0, 0, 0),
            };

            if (statusColor.A > 0)
            {
                using var dotBrush = new SolidBrush(statusColor);
                using var dotPen = new Pen(Color.FromArgb(240, 30, 30, 32), 1.2f);
                var dotRect = new RectangleF(19f, 19f, 10f, 10f);
                g.FillEllipse(dotBrush, dotRect);
                g.DrawEllipse(dotPen, dotRect);
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            var icon = (Icon)Icon.FromHandle(hIcon).Clone();
            return icon;
        }
        finally
        {
            NativeIconCleanup.DestroyIcon(hIcon);
        }
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(asmDir))
            {
                var path = Path.Combine(asmDir, "Assets", "icon.ico");
                if (File.Exists(path)) return new Icon(path);
            }
        }
        catch { }

        return RenderStateIcon(AppState.Idle);
    }
}

internal static class NativeIconCleanup
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, RectangleF rect, float radius)
    {
        using var path = BuildRoundedPath(rect, radius);
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics g, Pen pen, RectangleF rect, float radius)
    {
        using var path = BuildRoundedPath(rect, radius);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath BuildRoundedPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
