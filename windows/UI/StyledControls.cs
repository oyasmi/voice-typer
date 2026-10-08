using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace VoiceTyper.UI;

/// <summary>
/// 系统图标字体的解析结果：Windows 11 优先 <c>Segoe Fluent Icons</c>，Windows 10 用
/// <c>Segoe MDL2 Assets</c>；两者都不存在（精简系统）时返回 null，调用方跳过图标绘制，
/// 退化为纯文字——不做字体名硬编码，避免字形回退成豆腐块。
/// </summary>
internal static class IconFont
{
    private static readonly Lazy<string?> _name = new(Resolve);

    public static string? Name => _name.Value;

    private static string? Resolve()
    {
        try
        {
            using var installed = new System.Drawing.Text.InstalledFontCollection();
            var names = installed.Families.Select(family => family.Name).ToHashSet(StringComparer.Ordinal);
            if (names.Contains("Segoe Fluent Icons")) return "Segoe Fluent Icons";
            if (names.Contains("Segoe MDL2 Assets")) return "Segoe MDL2 Assets";
            return null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 主/次操作按钮：4px 圆角、悬停/按下/禁用/焦点四态全部自绘（<see cref="UiPalette"/> 提供颜色），
/// 摆脱 FlatAppearance 的系统默认悬停色。按钮文本与边距仍由 <c>StyleButton</c> 统一设置。
/// </summary>
internal sealed class RoundedButton : Button
{
    private bool _hover;
    private bool _pressed;
    private bool _primary;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary
    {
        get => _primary;
        set { if (_primary == value) return; _primary = value; Invalidate(); }
    }

    public RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    private float S(float pixels) => pixels * DeviceDpi / 96f;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color back, fore, border;
        if (!Enabled)
        {
            back = UiPalette.DisabledBackground;
            fore = UiPalette.TextDisabled;
            border = Color.FromArgb(235, 238, 242);
        }
        else if (Primary)
        {
            back = _pressed ? UiPalette.AccentPressed : _hover ? UiPalette.AccentHover : UiPalette.Accent;
            fore = Color.White;
            border = back;
        }
        else
        {
            back = _pressed ? UiPalette.SecondaryButtonPressed : _hover ? UiPalette.SecondaryButtonHover : UiPalette.Card;
            fore = UiPalette.TextPrimary;
            border = UiPalette.ControlBorder;
        }

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = GraphicsExtensions.BuildRoundedPath(rect, S(4f)))
        {
            using (var brush = new SolidBrush(back)) g.FillPath(brush, path);
            if (!Primary || !Enabled)
            {
                using var pen = new Pen(border);
                g.DrawPath(pen, path);
            }
        }

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

        if (Focused && Enabled && ShowFocusCues)
        {
            using var focusPen = new Pen(Color.FromArgb(90, Primary ? Color.White : UiPalette.Accent));
            g.DrawPath(focusPen, GraphicsExtensions.BuildRoundedPath(
                new RectangleF(2.5f, 2.5f, Width - 5f, Height - 5f), S(3f)));
        }
    }
}

/// <summary>
/// 侧栏导航项：图标字形 + 文字，选中/悬停在浅底色药丸上呈现；同样全部自绘。
/// 选中态由 <see cref="Selected"/> 驱动，不再依赖 BackColor / ForeColor。
/// </summary>
internal sealed class NavButton : Button
{
    private bool _hover;
    private bool _selected;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string IconGlyph { get; init; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Invalidate(); }
    }

    public NavButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    private int S(int pixels) => (int)Math.Round(pixels * DeviceDpi / 96.0);
    private float S(float pixels) => pixels * DeviceDpi / 96f;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var back = _selected ? UiPalette.NavSelectedBackground
            : _hover ? UiPalette.NavHoverBackground
            : UiPalette.SidebarBackground;
        using (var path = GraphicsExtensions.BuildRoundedPath(new RectangleF(1f, 1f, Width - 2f, Height - 2f), S(6f)))
        {
            using (var brush = new SolidBrush(back)) g.FillPath(brush, path);
            if (_selected)
            {
                using var edge = new Pen(Color.FromArgb(70, UiPalette.Accent));
                g.DrawPath(edge, path);
            }
        }

        var iconColor = _selected ? UiPalette.Accent : UiPalette.TextSecondary;
        var textColor = _selected ? UiPalette.Accent : UiPalette.NavText;
        var textStart = S(14);
        var iconName = IconFont.Name;
        if (iconName is not null && IconGlyph.Length > 0)
        {
            using var iconFont = new Font(iconName, S(15f), FontStyle.Regular, GraphicsUnit.Pixel);
            var iconSize = TextRenderer.MeasureText(g, IconGlyph, iconFont, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, IconGlyph, iconFont,
                new Rectangle(S(14), 0, iconSize.Width, Height), iconColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            textStart = S(14) + iconSize.Width + S(9);
        }

        using var textFont = new Font(Font, _selected ? FontStyle.Bold : FontStyle.Regular);
        TextRenderer.DrawText(g, Text, textFont,
            new Rectangle(textStart, 0, Width - textStart - S(6), Height), textColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// 折叠区开关：无边框链钮，左侧自绘 chevron（折叠向右 / 展开向下），颜色随主题 accent。
/// 替代此前"+ 高级设置"式的文本按钮。
/// </summary>
internal sealed class DisclosureToggle : Button
{
    private bool _hover;
    private bool _expanded;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Expanded
    {
        get => _expanded;
        set { if (_expanded == value) return; _expanded = value; Invalidate(); }
    }

    public DisclosureToggle()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    private int S(int pixels) => (int)Math.Round(pixels * DeviceDpi / 96.0);
    private float S(float pixels) => pixels * DeviceDpi / 96f;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var color = !Enabled ? UiPalette.TextDisabled : _hover ? UiPalette.AccentHover : UiPalette.Accent;
        var cx = S(9f);
        var cy = Height / 2f;
        var h = S(3.8f);
        // V 形折线：折叠时开口向左（指右），展开时开口向上（指下）。
        var points = Expanded
            ? new[] { new PointF(cx - h, cy - h * 0.62f), new PointF(cx, cy + h * 0.62f), new PointF(cx + h, cy - h * 0.62f) }
            : new[] { new PointF(cx - h * 0.62f, cy - h), new PointF(cx + h * 0.62f, cy), new PointF(cx - h * 0.62f, cy + h) };
        using (var pen = new Pen(color, Math.Max(1.6f, S(1.8f)))
               { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLines(pen, points);
        }

        var textRect = new Rectangle(S(20), 0, Width - S(20), Height);
        TextRenderer.DrawText(g, Text, Font, textRect, color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}
