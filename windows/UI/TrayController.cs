using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

/// <summary>
/// 系统托盘图标 + 上下文菜单。所有方法必须在 UI 线程调用。
///
/// 与 <c>client-server/client_windows_native/UI/TrayController.cs</c> 相比：删掉"重新连接服务"（本地引擎没有
/// 连接概念），新增"开机自启"复选与"关于"（见 windows/DESIGN.md §5.6）。
/// </summary>
internal sealed class TrayController : IDisposable
{
    public Action? OnOpenSetup;
    public Action? OnQuit;
    /// <summary>用户点击"暂停听写"菜单项。由调用方决定实际是否切换（托盘只负责发出请求）。</summary>
    public Action? OnTogglePause;
    public Action? OnOpenOnboarding;
    public Action? OnCheckForUpdates;

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _hotkeyItem;
    private readonly ToolStripMenuItem _engineItem;
    private readonly ToolStripMenuItem _pauseItem;
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

        _statusItem = new ToolStripMenuItem(L10n.T("启动中")) { Enabled = false };
        _hotkeyItem = new ToolStripMenuItem(L10n.F("热键：{0}", "-")) { Enabled = false };
        _engineItem = new ToolStripMenuItem(L10n.F("引擎：{0}", L10n.T("检查中"))) { Enabled = false };

        var setupItem = new ToolStripMenuItem(L10n.T("设置..."));
        setupItem.Click += (_, _) => OnOpenSetup?.Invoke();

        _pauseItem = new ToolStripMenuItem(L10n.T("暂停听写")) { CheckOnClick = false };
        _pauseItem.Click += (_, _) => OnTogglePause?.Invoke();

        _startupItem = new ToolStripMenuItem(L10n.T("开机自启")) { CheckOnClick = true };
        _startupItem.Checked = StartupRegistration.IsEnabled;
        _startupItem.Click += (_, _) =>
        {
            if (!StartupRegistration.SetEnabled(_startupItem.Checked))
            {
                // 写失败：勾选状态恢复为注册表真实值，两个入口读取同一状态源（R4-2）。
                _startupItem.Checked = StartupRegistration.IsEnabled;
            }
        };

        // 自启状态可能在别处（设置页）被改过：每次展开菜单时以注册表为准，而不是沿用构造时读到的值。
        _menu.Opening += (_, _) => _startupItem.Checked = StartupRegistration.IsEnabled;

        var onboardingItem = new ToolStripMenuItem(L10n.T("使用引导..."));
        onboardingItem.Click += (_, _) => OnOpenOnboarding?.Invoke();

        var updateItem = new ToolStripMenuItem(L10n.T("检查更新..."));
        updateItem.Click += (_, _) => OnCheckForUpdates?.Invoke();

        var aboutItem = new ToolStripMenuItem(L10n.F("关于 {0}", "VoiceTyper"));
        aboutItem.Click += (_, _) => ShowAbout();

        var quitItem = new ToolStripMenuItem(L10n.T("退出"));
        quitItem.Click += (_, _) => OnQuit?.Invoke();

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            _hotkeyItem,
            _engineItem,
            new ToolStripSeparator(),
            setupItem,
            _pauseItem,
            new ToolStripSeparator(),
            _startupItem,
            onboardingItem,
            updateItem,
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
        _hotkeyItem.Text = L10n.F("热键：{0}", hotkeyDisplay);
        _engineItem.Text = L10n.F("引擎：{0}", engineStatus);
        _pauseItem.Text = info.State == AppState.Paused ? L10n.T("恢复听写") : L10n.T("暂停听写");
        _pauseItem.Enabled = info.State is not (AppState.Booting or AppState.SetupRequired);

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
            $"VoiceTyper {AppConstants.Version}\n\n" + L10n.T("离线、安全、快捷的语音输入工具。"),
            L10n.F("关于 {0}", "VoiceTyper"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Information
        );
    }

    private void ApplyStateIcon(AppState state)
    {
        try
        {
            // 空闲态直接用品牌图标；其余状态在品牌图标右下角叠一个状态点。
            // 不再整体换成自绘的浅色麦克风：浅色任务栏上它几乎看不见，品牌图标也就此消失。
            if (BadgeColor(state) is not { } badge)
            {
                _notifyIcon.Icon = _appIcon;
                _currentIcon?.Dispose();
                _currentIcon = null;
                return;
            }
            var icon = RenderBadgedIcon(_appIcon, badge);
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

    /// <summary>状态点颜色；null 表示该状态不画状态点（空闲）。</summary>
    internal static Color? BadgeColor(AppState state) => state switch
    {
        AppState.Recording => Color.FromArgb(255, 230, 64, 60),
        AppState.Recognizing => Color.FromArgb(255, 240, 180, 30),
        AppState.Inserting => Color.FromArgb(255, 240, 130, 30),
        AppState.Error => Color.FromArgb(255, 220, 50, 50),
        AppState.SetupRequired => Color.FromArgb(255, 240, 170, 0),
        AppState.ModelMissing or AppState.DownloadingModel => Color.FromArgb(255, 230, 140, 30),
        AppState.ModelLoading or AppState.Booting => Color.FromArgb(255, 160, 160, 160),
        AppState.Paused => Color.FromArgb(255, 150, 150, 155),
        _ => null,
    };

    /// <summary>以系统托盘图标尺寸重绘品牌图标并叠加状态点。返回的 <see cref="Icon"/> 归调用方释放。</summary>
    private static Icon RenderBadgedIcon(Icon baseIcon, Color badge)
    {
        var size = SystemInformation.SmallIconSize;
        using var sized = new Icon(baseIcon, size);
        using var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.DrawIcon(sized, new Rectangle(Point.Empty, size));

            var diameter = Math.Max(7f, size.Width * 0.5f);
            var dotRect = new RectangleF(size.Width - diameter - 0.5f, size.Height - diameter - 0.5f, diameter, diameter);
            using var dotBrush = new SolidBrush(badge);
            using var dotPen = new Pen(Color.FromArgb(220, 30, 30, 32), Math.Max(1f, size.Width / 16f));
            g.FillEllipse(dotBrush, dotRect);
            g.DrawEllipse(dotPen, dotRect);
        }
        return IconFromBitmap(bmp);
    }

    private static Icon IconFromBitmap(Bitmap bmp)
    {
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            NativeIconCleanup.DestroyIcon(hIcon);
        }
    }

    /// <summary>找不到 icon.ico 时的兜底图标：简化的麦克风，不带状态点。</summary>
    private static Icon RenderFallbackIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var micBrush = new SolidBrush(Color.FromArgb(220, 235, 235, 240));
            using var stand = new Pen(Color.FromArgb(220, 235, 235, 240), 2.4f);

            var capRect = new RectangleF(11f, 5f, 10f, 14f);
            g.FillRoundedRectangle(micBrush, capRect, 5f);
            g.DrawLine(stand, 16f, 20f, 16f, 25f);
            g.DrawLine(stand, 11f, 25f, 21f, 25f);
            g.DrawArc(stand, 7f, 11f, 18f, 14f, 0, 180);
        }
        return IconFromBitmap(bmp);
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

        return RenderFallbackIcon();
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
