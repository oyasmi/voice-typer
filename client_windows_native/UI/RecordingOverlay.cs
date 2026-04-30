using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace VoiceTyper.UI;

/// <summary>
/// 录音状态浮窗。显示在屏幕底部中央，带有脉冲红点和录音时长。
/// 使用 WS_EX_NOACTIVATE 避免抢焦点，WS_EX_TOOLWINDOW 隐藏于 Alt+Tab。
/// 对应 macOS Swift 版的 RecordingHUDController。
/// Windows 11 自动应用圆角效果，Windows 10 为直角。
/// </summary>
internal sealed class RecordingOverlay : Form
{
    private readonly System.Windows.Forms.Timer _animTimer;
    private DateTime _recordStartTime;
    private float _dotAlpha = 1f;
    private bool _dotFading = true;
    private string _text = "正在听...";

    /// <summary>不抢焦点 + 不出现在 Alt+Tab</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public RecordingOverlay(int width = 260, int height = 60, double opacity = 0.88)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(width, height);
        BackColor = Color.FromArgb(38, 38, 38);
        Opacity = opacity;

        // 双缓冲, 流畅渲染
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer, true);

        // 动画定时器 (脉冲红点 + 时长刷新)
        _animTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _animTimer.Tick += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // ── 脉冲红点 ──
        int dotSize = 14;
        int dotX = 22;
        int dotY = (Height - dotSize) / 2;
        int alpha = (int)(255 * _dotAlpha);
        using var dotBrush = new SolidBrush(Color.FromArgb(alpha, 230, 60, 60));
        g.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);

        // ── 状态文字 ──
        using var font = new Font("Microsoft YaHei UI", 13, FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        float textX = dotX + dotSize + 14;
        float textY = (Height - font.Height) / 2f;
        g.DrawString(_text, font, textBrush, textX, textY);

        // ── 录音时长 ──
        var elapsed = DateTime.UtcNow - _recordStartTime;
        string duration = elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}"
            : $"0:{elapsed.Seconds:D2}";
        using var durFont = new Font("Microsoft YaHei UI", 11);
        using var durBrush = new SolidBrush(Color.FromArgb(180, 255, 255, 255));
        var durSize = g.MeasureString(duration, durFont);
        g.DrawString(duration, durFont, durBrush,
            Width - durSize.Width - 22, (Height - durFont.Height) / 2f);

        // ── 脉冲动画 ──
        if (_dotFading)
        {
            _dotAlpha -= 0.06f;
            if (_dotAlpha <= 0.3f) _dotFading = false;
        }
        else
        {
            _dotAlpha += 0.06f;
            if (_dotAlpha >= 1.0f) _dotFading = true;
        }
    }

    /// <summary>显示浮窗并开始计时</summary>
    public void ShowOverlay(string text = "正在听...")
    {
        _text = text;
        _recordStartTime = DateTime.UtcNow;
        _dotAlpha = 1f;
        _dotFading = true;

        // 定位: 屏幕底部中央, 偏上 100px
        var screen = Screen.PrimaryScreen?.WorkingArea
                  ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(
            screen.X + (screen.Width - Width) / 2,
            screen.Y + screen.Height - Height - 100);

        _animTimer.Start();
        Show();
    }

    /// <summary>更新状态文字 (例如 "识别中...")</summary>
    public void UpdateOverlayText(string text)
    {
        _text = text;
        Invalidate();
    }

    /// <summary>隐藏浮窗</summary>
    public void HideOverlay()
    {
        _animTimer.Stop();
        Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _animTimer?.Dispose();
        base.Dispose(disposing);
    }
}
