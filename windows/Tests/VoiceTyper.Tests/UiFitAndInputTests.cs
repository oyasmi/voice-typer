using System.Drawing;
using VoiceTyper.Core;
using VoiceTyper.Services;
using VoiceTyper.UI;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>不依赖真实窗口/键盘的小块逻辑：窗口适配、粘贴按键序列、托盘状态点。</summary>
public class UiFitAndInputTests
{
    [Fact]
    public void WindowFit_ShrinksToWorkingArea_AndKeepsMinimumNotAboveSize()
    {
        // 1920×1080 @125%：设计尺寸 1350×1000 物理像素，可用高度约 1040。
        var (size, min) = WindowFit.Fit(new Size(1350, 1000), new Size(1075, 800), new Rectangle(0, 0, 1920, 1040));
        Assert.Equal(new Size(1350, (int)(1040 * WindowFit.MaxFraction)), size);
        Assert.True(min.Width <= size.Width && min.Height <= size.Height);
    }

    [Fact]
    public void WindowFit_CenterRespectsWorkingAreaOrigin()
    {
        var p = WindowFit.Center(new Size(1000, 600), new Rectangle(1920, 0, 1920, 1040));
        Assert.Equal(new Point(1920 + 460, 220), p);
    }

    [Theory]
    [InlineData(-3000, 100, -1920, 100)]
    [InlineData(-100, 900, -1000, 440)]
    [InlineData(-1800, 200, -1800, 200)]
    public void WindowFit_ClampsToNegativeOriginMonitor_WithoutRecentering(int x, int y, int expectedX, int expectedY)
    {
        var location = WindowFit.ClampLocation(new Rectangle(x, y, 1000, 600), new Rectangle(-1920, 0, 1920, 1040));
        Assert.Equal(new Point(expectedX, expectedY), location);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void WindowFit_LargeScaledWindow_FitsSmallWorkingArea(int dpi)
    {
        var area = new Rectangle(1366, -100, 1366, 728);
        var (size, minimum) = WindowFit.Fit(new Size(1000 * dpi / 96, 700 * dpi / 96),
            new Size(720 * dpi / 96, 480 * dpi / 96), area);
        var location = WindowFit.ClampLocation(new Rectangle(2500, 500, size.Width, size.Height), area);
        Assert.True(area.Contains(new Rectangle(location, size)));
        Assert.True(minimum.Width <= size.Width && minimum.Height <= size.Height);
    }

    [Fact]
    public void PasteSequence_OmitsCtrlWhenAlreadyHeld()
    {
        var plain = TextInsertionService.BuildPasteSequence(ctrlAlreadyDown: false);
        Assert.Equal(new[] { (0x11, false), (0x56, false), (0x56, true), (0x11, true) },
            plain.Select(i => ((int)i.Vk, i.KeyUp)).ToArray());

        var held = TextInsertionService.BuildPasteSequence(ctrlAlreadyDown: true);
        Assert.Equal(new[] { (0x56, false), (0x56, true) }, held.Select(i => ((int)i.Vk, i.KeyUp)).ToArray());
    }

}
