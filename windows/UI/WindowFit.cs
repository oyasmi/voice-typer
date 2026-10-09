using System;
using System.Drawing;

namespace VoiceTyper.UI;

/// <summary>
/// 窗口尺寸与屏幕可用区域的适配（纯逻辑，可脱离 WinForms 单测）。设置窗口的设计尺寸是按
/// 逻辑像素写死的，在 1920×1080、125% 缩放这类常见笔记本上，缩放后的高度已经超过可用区域，
/// 标题栏或底部的保存按钮会落到屏幕外。
/// </summary>
internal static class WindowFit
{
    /// <summary>窗口最多占可用区域的比例，留出边距而不是贴满，避免与任务栏、屏幕边缘粘在一起。</summary>
    public const double MaxFraction = 0.94;

    /// <summary>
    /// 返回适配后的 (尺寸, 最小尺寸)：两者都不超过可用区域的 <see cref="MaxFraction"/>，
    /// 且最小尺寸不大于尺寸（否则 WinForms 会把尺寸抬回最小值，适配就白做了）。
    /// 两个方向独立处理：宽屏矮屏只缩高度。
    /// </summary>
    public static (Size Size, Size Minimum) Fit(Size desired, Size minimum, Rectangle workingArea)
    {
        var maxWidth = Math.Max(1, (int)(workingArea.Width * MaxFraction));
        var maxHeight = Math.Max(1, (int)(workingArea.Height * MaxFraction));
        var size = new Size(Math.Min(desired.Width, maxWidth), Math.Min(desired.Height, maxHeight));
        var min = new Size(Math.Min(minimum.Width, size.Width), Math.Min(minimum.Height, size.Height));
        return (size, min);
    }

    /// <summary>把 <paramref name="size"/> 大小的窗口居中放进 <paramref name="workingArea"/>。</summary>
    public static Point Center(Size size, Rectangle workingArea) => new(
        workingArea.Left + (workingArea.Width - size.Width) / 2,
        workingArea.Top + (workingArea.Height - size.Height) / 2);
}
