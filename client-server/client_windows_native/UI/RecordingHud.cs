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

    public RecordingHud(UIConfig uiConfig)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Opacity = Math.Clamp(uiConfig.Opacity, 0.4, 1.0);
        Size = new Size((int)Math.Max(260, uiConfig.Width), (int)Math.Max(80, uiConfig.Height));
        BackColor = Color.FromArgb(20, 20, 22);
        DoubleBuffered = true;

        _statusFont = new Font("Segoe UI", 10f, FontStyle.Regular);
        _timerFont = new Font("Consolas", 9f, FontStyle.Regular);
        _previewFont = new Font("Segoe UI", 9f, FontStyle.Regular);

        // 圆角 region 在 HandleCreated 之后赋值，避免构造期强制创建 handle 引发的边角问题
        HandleCreated += (_, _) =>
        {
            Region = BuildRoundedRegion(ClientRectangle, 14);
        };
        Resize += (_, _) =>
        {
            if (IsHandleCreated) Region = BuildRoundedRegion(ClientRectangle, 14);
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
        PositionToTopCenter();

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

    private void PositionToTopCenter()
    {
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var wa = screen.WorkingArea;
        var x = wa.Left + (wa.Width - Width) / 2;
        var y = wa.Top + 80;
        Location = new Point(x, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // 描边
        using (var border = new Pen(Color.FromArgb(40, 255, 255, 255), 1f))
        {
            using var path = BuildRoundedPath(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 13f);
            g.DrawPath(border, path);
        }

        const int paddingX = 18;
        const int paddingY = 14;

        // 呼吸点
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

        // 状态文字
        using var statusBrush = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        g.DrawString(_statusText, _statusFont, statusBrush, paddingX + 16, paddingY);

        // 计时（右上）
        using var timerBrush = new SolidBrush(Color.FromArgb(160, 255, 255, 255));
        var timerSize = g.MeasureString(_elapsedText, _timerFont);
        g.DrawString(
            _elapsedText,
            _timerFont,
            timerBrush,
            Width - paddingX - timerSize.Width,
            paddingY + 1
        );

        // 流式预览（右对齐，超长保留尾部）
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
        // 从尾部往回保留尽可能多的字符
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
