using System.Drawing;
using System.Windows.Forms;

namespace VoiceTyper.UI;

/// <summary>
/// 设置字段：根据实际可用宽度排列说明和编辑器，不让长文字的首选宽度反向撑大页面。
/// 只重排原控件，不重建、移除或改变 Tab 顺序；DPI 缩放由宿主窗体负责。
/// </summary>
internal sealed class SettingsField : Panel
{
    private readonly Label _title;
    private readonly Label _description;
    private readonly Control _editor;
    private readonly bool _fullWidth;
    private readonly int _editorWidth;
    private bool _layingOut;

    public SettingsField(Label title, Label description, Control editor, bool fullWidth, int editorWidth)
    {
        _title = title;
        _description = description;
        _editor = editor;
        _fullWidth = fullWidth;
        _editorWidth = editorWidth;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        Margin = new Padding(0, 8, 0, 8);
        foreach (var control in new Control[] { title, description, editor })
        {
            control.Dock = DockStyle.None;
            control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            control.Margin = Padding.Empty;
        }
        title.AutoSize = description.AutoSize = false;
        title.TabStop = description.TabStop = false;
        Controls.AddRange(new Control[] { title, description, editor });
        editor.TabIndex = 0;
    }

    private (Rectangle Title, Rectangle Description, Rectangle Editor, int Height) Measure(int width)
    {
        width = Math.Max(1, width);
        var stacked = _fullWidth || width < LogicalToDeviceUnits(560);
        var editorWidth = _fullWidth ? width : Math.Min(width, LogicalToDeviceUnits(_editorWidth));
        var textWidth = stacked ? width : Math.Max(1, width - editorWidth - LogicalToDeviceUnits(20));
        var titleHeight = _title.GetPreferredSize(new Size(textWidth, 0)).Height;
        var descriptionHeight = _description.GetPreferredSize(new Size(textWidth, 0)).Height;
        var descriptionTop = titleHeight + LogicalToDeviceUnits(3);
        var textHeight = descriptionTop + descriptionHeight;
        var editorHeight = _editor.GetPreferredSize(new Size(editorWidth, 0)).Height;
        var editorTop = stacked ? textHeight + LogicalToDeviceUnits(8) : LogicalToDeviceUnits(4);
        return (new Rectangle(0, 0, textWidth, titleHeight),
            new Rectangle(0, descriptionTop, textWidth, descriptionHeight),
            new Rectangle(stacked ? 0 : width - editorWidth, editorTop, editorWidth, editorHeight),
            Math.Max(textHeight, editorTop + editorHeight));
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (_editor is null) return base.GetPreferredSize(proposedSize);
        var width = proposedSize.Width is > 0 and < int.MaxValue ? proposedSize.Width : Width;
        return new Size(width, Measure(width).Height);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_editor is null || _layingOut) return;
        _layingOut = true;
        try
        {
            var layout = Measure(ClientSize.Width);
            _title.Bounds = layout.Title;
            _description.Bounds = layout.Description;
            _editor.Bounds = layout.Editor;
        }
        finally { _layingOut = false; }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PerformLayout();
    }
}
