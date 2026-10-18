using System.Drawing;
using System.Windows.Forms;

namespace VoiceTyper.UI;

/// <summary>
/// 设置页的单列纵向容器。嵌套 TableLayoutPanel 默认会按内容的自然宽度测量高度，
/// 再把实际多出来的高度留给最后一行，导致宽卡片仍保留窄布局的空白。
/// 统一用分配到的宽度测量每个可见子项，实际排列仍由 WinForms 处理。
/// </summary>
internal sealed class SettingsStack : TableLayoutPanel
{
    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = proposedSize.Width is > 0 and < int.MaxValue ? proposedSize.Width : Math.Max(1, Width);
        var available = Math.Max(1, width - Padding.Horizontal);
        var height = Padding.Vertical;
        foreach (Control child in Controls)
        {
            if (!child.Visible) continue;
            var childWidth = Math.Max(1, available - child.Margin.Horizontal);
            // 多行诊断框保留用户可滚动的固定视口，其他 AutoSize 控件按当前宽度折行。
            var childHeight = child.AutoSize && child is not TextBox { Multiline: true }
                ? child.GetPreferredSize(new Size(childWidth, 0)).Height : child.Height;
            height += Math.Max(child.MinimumSize.Height, childHeight) + child.Margin.Vertical;
        }
        return new Size(width, height);
    }
}
