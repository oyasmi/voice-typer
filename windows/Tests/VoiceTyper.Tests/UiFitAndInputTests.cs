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
    public void WindowFit_LeavesRoomyScreensUntouched()
    {
        var (size, min) = WindowFit.Fit(new Size(1080, 800), new Size(860, 640), new Rectangle(0, 0, 2560, 1400));
        Assert.Equal(new Size(1080, 800), size);
        Assert.Equal(new Size(860, 640), min);
    }

    [Fact]
    public void WindowFit_TinyScreen_ReducesMinimumToo()
    {
        var (size, min) = WindowFit.Fit(new Size(1350, 1000), new Size(1075, 800), new Rectangle(0, 0, 1366, 728));
        Assert.True(size.Height <= 728 && size.Width <= 1366);
        Assert.Equal(size.Height, min.Height); // 最小高度被压到不超过窗口本身
    }

    [Fact]
    public void WindowFit_CenterRespectsWorkingAreaOrigin()
    {
        var p = WindowFit.Center(new Size(1000, 600), new Rectangle(1920, 0, 1920, 1040));
        Assert.Equal(new Point(1920 + 460, 220), p);
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

    [Fact]
    public void TrayBadge_IdleHasNone_ActiveStatesHaveOne()
    {
        Assert.Null(TrayController.BadgeColor(AppState.Idle));
        foreach (var state in new[] { AppState.Recording, AppState.Recognizing, AppState.Error, AppState.Paused, AppState.DownloadingModel })
        {
            Assert.NotNull(TrayController.BadgeColor(state));
        }
    }
}
