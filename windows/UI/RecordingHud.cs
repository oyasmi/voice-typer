using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using VoiceTyper.Core;
using static VoiceTyper.Support.NativeMethods;

namespace VoiceTyper.UI;

/// <summary>
/// 无边框、置顶、不抢焦点的录音指示器。
/// 上方一行：呼吸红点 + 状态文字（"录音中..." / "识别中..."）+ 计时。
/// 下方一行：右对齐流式预览，超长右起截断（保留尾部）。
///
/// 与 <c>client-server/client_windows_native/UI/RecordingHud.cs</c> 相比的两处增量（见 windows/DESIGN.md §4.1）：
/// 跟随前台窗口所在屏幕定位（而非总在主屏），Win11 上用 DWM 圆角替代 Region 硬边裁剪。
/// </summary>
internal sealed class RecordingHud : Form
{
    private enum HudMode { Recording, Recognizing }

    private readonly System.Windows.Forms.Timer _timer;
    private DateTime _startedAt = DateTime.UtcNow;
    private HudMode _mode = HudMode.Recording;
    private double _pulsePhase;
    private string _preview = "";
    private string _statusText = "录音中...";
    private string _elapsedText = "0s";
    private readonly Font _statusFont;
    private readonly Font _timerFont;
    private readonly Font _previewFont;
    private bool _useDwmRoundCorners;

    public RecordingHud(UIConfig uiConfig)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Opacity = Math.Clamp(uiConfig.Opacity, 0.4, 1.0);
        Size = new Size(320, 90);
        BackColor = Color.FromArgb(20, 20, 22);
        DoubleBuffered = true;

        _statusFont = new Font("Segoe UI", 10f, FontStyle.Regular);
        _timerFont = new Font("Consolas", 9f, FontStyle.Regular);
        _previewFont = new Font("Segoe UI", 9f, FontStyle.Regular);

        HandleCreated += (_, _) =>
        {
            _useDwmRoundCorners = TryEnableDwmRoundCorners();
            if (!_useDwmRoundCorners)
            {
                Region = BuildRoundedRegion(ClientRectangle, 14);
            }
        };
        Resize += (_, _) =>
        {
            if (IsHandleCreated && !_useDwmRoundCorners)
            {
                Region = BuildRoundedRegion(ClientRectangle, 14);
            }
        };

        _timer = new System.Windows.Forms.Timer { Interval = 50 };
        _timer.Tick += (_, _) =>
        {
            _pulsePhase = (_pulsePhase + 0.10) % (Math.PI * 2);
            var elapsed = (int)(DateTime.UtcNow - _startedAt).TotalSeconds;
            var newText = $"{elapsed}s";
            if (newText != _elapsedText)
            {
                _elapsedText = newText;
            }
            Invalidate();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void ShowRecording()
    {
        _mode = HudMode.Recording;
        _statusText = "录音中...";
        _preview = "";
        _startedAt = DateTime.UtcNow;
        _elapsedText = "0s";
        _pulsePhase = 0;
        PositionNearForegroundWindow();

        if (!Visible)
        {
            Show();
        }
        _timer.Start();
        Invalidate();
    }

    public void SetRecognizing()
    {
        _mode = HudMode.Recognizing;
        _statusText = "识别中...";
        Invalidate();
    }

    public void HideHud()
    {
        _timer.Stop();
        _preview = "";
        if (Visible) Hide();
    }

    public void ShowPreview(string accumulated)
    {
        _preview = accumulated ?? "";
        Invalidate();
    }

    /// <summary>透明度是唯一可在运行期热更新的外观项；由设置页预览调用。</summary>
    public void ApplyOpacity(double opacity)
    {
        Opacity = Math.Clamp(opacity, 0.4, 1.0);
    }

    /// <summary>
    /// 定位到前台窗口所在的屏幕（而不是总在主屏）——听写目标在哪块屏，HUD 就在哪块屏。
    /// <see cref="Screen.FromHandle"/> 内部就是对目标窗口做 <c>MonitorFromWindow</c>，
    /// 不需要自己再做一次 P/Invoke 映射。
    /// </summary>
    private void PositionNearForegroundWindow()
    {
        Screen screen;
        try
        {
            var fg = GetForegroundWindow();
            screen = fg != IntPtr.Zero ? Screen.FromHandle(fg) : (Screen.PrimaryScreen ?? Screen.AllScreens[0]);
        }
        catch
        {
            screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        }

        var wa = screen.WorkingArea;
        var x = wa.Left + (wa.Width - Width) / 2;
        var y = wa.Top + 80;
        Location = new Point(x, y);
    }

    /// <summary>
    /// DWMWA_WINDOW_CORNER_PREFERENCE 仅 Windows 11 22000+ 支持；老版本调用会失败，
    /// 静默回退到 Region 裁剪（不判断系统版本号，直接以调用结果为准更可靠）。
    /// </summary>
    private bool TryEnableDwmRoundCorners()
    {
        try
        {
            int pref = DWMWCP_ROUND;
            var hr = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        if (!_useDwmRoundCorners)
        {
            using var border = new Pen(Color.FromArgb(40, 255, 255, 255), 1f);
            using var path = BuildRoundedPath(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 13f);
            g.DrawPath(border, path);
        }

        const int paddingX = 18;
        const int paddingY = 14;

        var dotColor = _mode == HudMode.Recording
            ? Color.FromArgb(255, 235, 70, 60)
            : Color.FromArgb(255, 250, 190, 40);
        var alpha = _mode == HudMode.Recording
            ? (int)(180 + 75 * Math.Sin(_pulsePhase))
            : 255;
        using (var dotBrush = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 80, 255), dotColor)))
        {
            g.FillEllipse(dotBrush, paddingX, paddingY + 5, 10, 10);
        }

        using var statusBrush = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        g.DrawString(_statusText, _statusFont, statusBrush, paddingX + 16, paddingY);

        using var timerBrush = new SolidBrush(Color.FromArgb(160, 255, 255, 255));
        var timerSize = g.MeasureString(_elapsedText, _timerFont);
        g.DrawString(
            _elapsedText,
            _timerFont,
            timerBrush,
            Width - paddingX - timerSize.Width,
            paddingY + 1
        );

        if (!string.IsNullOrEmpty(_preview))
        {
            using var previewBrush = new SolidBrush(Color.FromArgb(165, 255, 255, 255));
            var availableWidth = Width - paddingX * 2;
            var preview = TruncateToFitFromStart(g, _preview, _previewFont, availableWidth);
            var previewSize = g.MeasureString(preview, _previewFont);
            g.DrawString(
                preview,
                _previewFont,
                previewBrush,
                Width - paddingX - previewSize.Width,
                Height - paddingY - previewSize.Height
            );
        }
    }

    private static string TruncateToFitFromStart(Graphics g, string text, Font font, float maxWidth)
    {
        if (g.MeasureString(text, font).Width <= maxWidth) return text;

        const string ellipsis = "…";
        int lo = 1, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            var candidate = ellipsis + text.Substring(text.Length - mid);
            if (g.MeasureString(candidate, font).Width <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return ellipsis + text.Substring(text.Length - lo);
    }

    private static Region BuildRoundedRegion(Rectangle rect, float radius)
    {
        using var path = BuildRoundedPath(rect, radius);
        return new Region(path);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _statusFont.Dispose();
            _timerFont.Dispose();
            _previewFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
