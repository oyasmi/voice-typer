using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using VoiceTyper.Core;
using VoiceTyper.UI;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 圆角策略的不变式：分层窗口（不透明度 &lt; 1，即 WS_EX_LAYERED + SetLayeredWindowAttributes）
/// 必须走 Region 裁剪并禁用 DWM 角偏好——Win11 真机上对这类窗口设置
/// DWMWA_WINDOW_CORNER_PREFERENCE 会导致客户区不再合成（HUD 只剩顶部细线）。
/// 不透明窗口允许 DWM 圆角（Win11）或 Region 回退（Win10），两者互斥即可。
/// 需要 STA 线程创建真实控件；不在 macOS 上运行（与 SetupFormTests 一致）。
/// </summary>
public class HudCornerStrategyTests
{
    private static void OnUiThread(Action test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception caught) { error = caught; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static bool UsesDwmRoundCorners(RecordingHud hud) =>
        (bool)typeof(RecordingHud).GetField("_useDwmRoundCorners", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(hud)!;

    /// <summary>DWM 圆角与 Region 裁剪互斥：开了 DWM 圆角就不能有 Region，反之必须有。</summary>
    private static void AssertCornerStrategyConsistent(RecordingHud hud)
    {
        if (UsesDwmRoundCorners(hud)) Assert.Null(hud.Region);
        else Assert.NotNull(hud.Region);
    }

    [Fact]
    public void LayeredHudNeverUsesDwmCorners() => OnUiThread(() =>
    {
        using var hud = new RecordingHud(new UIConfig { Opacity = 0.85 });
        var _ = hud.Handle; // 触发 HandleCreated → RefreshCornerStrategy
        Assert.False(UsesDwmRoundCorners(hud));
        Assert.NotNull(hud.Region);
    });

    [Fact]
    public void OpaqueHudKeepsDwmCornersAndRegionExclusive() => OnUiThread(() =>
    {
        using var hud = new RecordingHud(new UIConfig { Opacity = 1.0 });
        var _ = hud.Handle;
        AssertCornerStrategyConsistent(hud);
    });

    [Fact]
    public void OpacitySwitchSwapsCornerStrategy() => OnUiThread(() =>
    {
        using var hud = new RecordingHud(new UIConfig { Opacity = 1.0 });
        var _ = hud.Handle;
        AssertCornerStrategyConsistent(hud);

        // 调低到分层区间：必须立刻撤下 DWM 圆角、换上 Region（否则 Win11 上 HUD 消失）。
        hud.ApplyOpacity(0.85);
        Assert.False(UsesDwmRoundCorners(hud));
        Assert.NotNull(hud.Region);

        // 调回完全不透明：两者仍互斥（Win11 回 DWM 圆角时 Region 必须被清掉）。
        hud.ApplyOpacity(1.0);
        AssertCornerStrategyConsistent(hud);
    });
}
