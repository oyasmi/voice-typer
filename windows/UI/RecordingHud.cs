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
/// 上方一行：呼吸红点 + 状态文字（"录音中" / "识别中"）+ 计时。
/// 下方一行：右对齐流式预览，超长右起截断（保留尾部）。
///
/// 与 <c>client-server/client_windows_native/UI/RecordingHud.cs</c> 相比的两处增量（见 windows/DESIGN.md §4.1）：
/// 跟随前台窗口所在屏幕定位（而非总在主屏），Win11 上用 DWM 圆角替代 Region 硬边裁剪。
/// </summary>
internal sealed class RecordingHud : Form
{
    private enum HudMode { Recording, Recognizing, Success, Error, Canceled }

    private readonly System.Windows.Forms.Timer _timer;
    private DateTime _startedAt = DateTime.UtcNow;
    private HudMode _mode = HudMode.Recording;
    private double _pulsePhase;
    private string _preview = "";
    private string _statusText = "录音中";
    private string _elapsedText = "";
    /// <summary>状态文字是否处于 FlashWarning 的警示色闪现期（约 1.2s）。</summary>
    private bool _statusWarning;
    private readonly Font _statusFont;
    private readonly Font _timerFont;
    private readonly Font _previewFont;
    private bool _useDwmRoundCorners;
    /// <summary>一次性提示（成功/错误/已取消）的自动隐藏任务；状态变化时作废。</summary>
    private System.Windows.Forms.Timer? _transientHideTimer;
    /// <summary><see cref="FlashWarning"/> 的状态文字恢复任务；状态变化时作废。</summary>
    private System.Windows.Forms.Timer? _warningRestoreTimer;

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

        // 层级约定与 macOS 一致：预览文字是 HUD 的正文（用户实时校对识别结果），
        // 字号与亮度都高于顶行的状态提示（呼吸点/波形已在传达录音状态）。
        _statusFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        _timerFont = new Font("Consolas", 9f, FontStyle.Regular);
        _previewFont = new Font("Segoe UI", 10f, FontStyle.Regular);

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
            // 首秒不显示（与 macOS 的 timeString 对齐），避免 "0s"→"1s" 的无谓跳变。
            var newText = elapsed > 0 ? $"{elapsed}s" : "";
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
        CancelTransientHide();
        CancelWarningRestore();

        _mode = HudMode.Recording;
        _statusText = "录音中";
        _preview = "";
        _startedAt = DateTime.UtcNow;
        _elapsedText = "";
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
        CancelTransientHide();
        CancelWarningRestore();
        _mode = HudMode.Recognizing;
        _statusText = "识别中";
        // 松键后冻结计时并停止重绘（与 macOS 对齐）：点为静态橙色、无波形、计时不走，
        // 继续 20fps Invalidate 只是无谓的空刷。下次 ShowRecording 会重新 Start。
        _timer.Stop();
        Invalidate();
    }

    public void HideHud()
    {
        _timer.Stop();
        CancelTransientHide();
        CancelWarningRestore();
        _preview = "";
        if (Visible) Hide();
    }

    public void ShowPreview(string accumulated)
    {
        _preview = accumulated ?? "";
        Invalidate();
    }

    /// <summary>final 文本插入成功后的一次性反馈，约 0.7s 后自动隐藏。</summary>
    public void ShowSuccess() => ShowTransient(HudMode.Success, "已输入", "", TimeSpan.FromSeconds(0.7));

    /// <summary>一次性错误提示，约 2.5s 后自动隐藏——菜单栏/托盘的错误态回落时长与此对齐（R3-04）。</summary>
    public void ShowError(string message) =>
        ShowTransient(HudMode.Error, "错误", string.IsNullOrEmpty(message) ? "服务异常" : message, TimeSpan.FromSeconds(2.5));

    /// <summary>用户按 Esc 取消录音后的一次性提示，约 1.0s 后自动隐藏。</summary>
    public void ShowCanceled() => ShowTransient(HudMode.Canceled, "已取消", "", TimeSpan.FromSeconds(1.0));

    /// <summary>
    /// 录音中或识别中的非致命提示（如预览失败、上一段听写尚未完成）。仅闪烁状态文字，
    /// 约 1.2s 后恢复；两个阶段都要能看到，否则识别中按热键会得到完全无反馈的"死键"观感。
    /// 警告是状态行上唯一必须被注意的内容：闪现期临时升为警示色（与 macOS 对齐）。
    /// </summary>
    public void FlashWarning(string message)
    {
        if (_mode is not (HudMode.Recording or HudMode.Recognizing)) return;

        CancelWarningRestore();
        _statusText = string.IsNullOrEmpty(message) ? "识别提示" : message;
        _statusWarning = true;
        Invalidate();

        var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            _warningRestoreTimer = null;
            _statusWarning = false;
            _statusText = _mode switch
            {
                HudMode.Recording => "录音中",
                HudMode.Recognizing => "识别中",
                _ => _statusText,
            };
            Invalidate();
        };
        _warningRestoreTimer = timer;
        timer.Start();
    }

    private void ShowTransient(HudMode mode, string status, string message, TimeSpan autoHideAfter)
    {
        CancelTransientHide();
        CancelWarningRestore();
        _timer.Stop();

        _mode = mode;
        _statusText = status;
        _preview = message;
        _elapsedText = "";
        PositionNearForegroundWindow();

        if (!Visible) Show();
        Invalidate();

        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, (int)autoHideAfter.TotalMilliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            _transientHideTimer = null;
            HideHud();
        };
        _transientHideTimer = timer;
        timer.Start();
    }

    private void CancelTransientHide()
    {
        _transientHideTimer?.Stop();
        _transientHideTimer?.Dispose();
        _transientHideTimer = null;
    }

    private void CancelWarningRestore()
    {
        _warningRestoreTimer?.Stop();
        _warningRestoreTimer?.Dispose();
        _warningRestoreTimer = null;
        // 警告被阶段切换打断时一并回落警示色，避免橙色被带进下一个状态。
        _statusWarning = false;
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

        var dotColor = _mode switch
        {
            HudMode.Recording => Color.FromArgb(255, 235, 70, 60),
            HudMode.Recognizing => Color.FromArgb(255, 250, 190, 40),
            HudMode.Success => Color.FromArgb(255, 60, 190, 90),
            HudMode.Error => Color.FromArgb(255, 235, 70, 60),
            HudMode.Canceled => Color.FromArgb(255, 170, 170, 170),
            _ => Color.FromArgb(255, 235, 70, 60),
        };
        var alpha = _mode == HudMode.Recording
            ? (int)(180 + 75 * Math.Sin(_pulsePhase))
            : 255;
        using (var dotBrush = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 80, 255), dotColor)))
        {
            g.FillEllipse(dotBrush, paddingX, paddingY + 5, 10, 10);
        }

        // 状态/计时属装饰行（约 65% 白），预览是正文行（约 92% 白），层级与 macOS 对齐。
        // FlashWarning 闪现期状态文字升为不透明警示橙，确保警告被注意到。
        using var statusBrush = new SolidBrush(_statusWarning
            ? Color.FromArgb(255, 250, 190, 40)
            : Color.FromArgb(165, 255, 255, 255));
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
            using var previewBrush = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
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
            _transientHideTimer?.Dispose();
            _warningRestoreTimer?.Dispose();
            _statusFont.Dispose();
            _timerFont.Dispose();
            _previewFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
