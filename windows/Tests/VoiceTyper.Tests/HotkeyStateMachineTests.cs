using VoiceTyper.Services;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 热键按键状态机（R2-1）。纯逻辑、不依赖 Win32 钩子与 UI 线程。
/// 真机仍需验证：目标应用收不到被接管的热键、中文 IME / AltGr 不受影响。
/// </summary>
public class HotkeyStateMachineTests
{
    private const int F2 = 0x71;
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_ESCAPE = 0x1B;

    private static HotkeyStateMachine CtrlF2() => new(F2, HotkeyModifiers.Ctrl);

    [Fact]
    public void Press_FiresOnlyWhenModifiersMatchExactly()
    {
        var sm = CtrlF2();

        // 只按主键、无修饰键：放行，不触发。
        var (a1, c1) = sm.OnKey(F2, isDown: true);
        Assert.Equal(HotkeyAction.None, a1);
        Assert.False(c1);
        sm.OnKey(F2, isDown: false);

        // Ctrl 按下（放行、不消费），再按 F2：触发 Press 且消费。
        var (am, cm) = sm.OnKey(VK_LCONTROL, isDown: true);
        Assert.Equal(HotkeyAction.None, am);
        Assert.False(cm);

        var (a2, c2) = sm.OnKey(F2, isDown: true);
        Assert.Equal(HotkeyAction.Press, a2);
        Assert.True(c2);
    }

    [Fact]
    public void ExtraModifier_BlocksPress()
    {
        var sm = CtrlF2();
        sm.OnKey(VK_LCONTROL, isDown: true);
        sm.OnKey(VK_LSHIFT, isDown: true); // Ctrl+Shift ≠ 期望的 Ctrl
        var (a, c) = sm.OnKey(F2, isDown: true);
        Assert.Equal(HotkeyAction.None, a);
        Assert.False(c);
    }

    [Fact]
    public void ReleaseMainKeyFirst_FiresRelease_AndConsumesMainKeyEvents()
    {
        var sm = CtrlF2();
        sm.OnKey(VK_LCONTROL, isDown: true);
        sm.OnKey(F2, isDown: true);

        var (a, c) = sm.OnKey(F2, isDown: false);
        Assert.Equal(HotkeyAction.Release, a);
        Assert.True(c);

        // Ctrl up 之后放行（不消费），不再触发任何动作。
        var (a2, c2) = sm.OnKey(VK_LCONTROL, isDown: false);
        Assert.Equal(HotkeyAction.None, a2);
        Assert.False(c2);
    }

    [Fact]
    public void ReleaseModifierFirst_FiresRelease_ButDoesNotConsumeModifierEvent()
    {
        var sm = CtrlF2();
        sm.OnKey(VK_LCONTROL, isDown: true);
        sm.OnKey(F2, isDown: true);

        // 先松 Ctrl：按"松开"处理，但修饰键事件必须放行以保持前台应用的配对。
        var (a, c) = sm.OnKey(VK_LCONTROL, isDown: false);
        Assert.Equal(HotkeyAction.Release, a);
        Assert.False(c);

        // 主键仍按住：auto-repeat 被吞掉，不重启。
        var (a2, c2) = sm.OnKey(F2, isDown: true);
        Assert.Equal(HotkeyAction.None, a2);
        Assert.True(c2);

        // 主键最终抬起：消费，回到 Idle。
        var (a3, c3) = sm.OnKey(F2, isDown: false);
        Assert.Equal(HotkeyAction.None, a3);
        Assert.True(c3);
    }

    [Fact]
    public void BothCtrlsHeld_ReleasingOne_DoesNotCountAsCtrlReleased()
    {
        var sm = CtrlF2();
        sm.OnKey(VK_LCONTROL, isDown: true);
        sm.OnKey(VK_RCONTROL, isDown: true);
        sm.OnKey(F2, isDown: true); // Press

        // 松开右 Ctrl，左 Ctrl 仍按住：Ctrl 位仍在，不触发 Release。
        var (a, _) = sm.OnKey(VK_RCONTROL, isDown: false);
        Assert.Equal(HotkeyAction.None, a);

        // 松开左 Ctrl：现在 Ctrl 真正释放，触发 Release。
        var (a2, _) = sm.OnKey(VK_LCONTROL, isDown: false);
        Assert.Equal(HotkeyAction.Release, a2);
    }

    [Fact]
    public void EscCancels_ThenHeldMainKeyAutoRepeat_DoesNotRestart()
    {
        var sm = CtrlF2();
        sm.OnKey(VK_LCONTROL, isDown: true);
        sm.OnKey(F2, isDown: true); // Press

        var (a, c) = sm.OnKey(VK_ESCAPE, isDown: true);
        Assert.Equal(HotkeyAction.Cancel, a);
        Assert.True(c);

        // 手指还按在 F2 上，auto-repeat keydown 必须被吞掉，不能重新开始录音。
        for (int i = 0; i < 5; i++)
        {
            var (ar, cr) = sm.OnKey(F2, isDown: true);
            Assert.Equal(HotkeyAction.None, ar);
            Assert.True(cr);
        }

        // F2 真正抬起后回到 Idle，下一次 Ctrl+F2 能正常触发。
        sm.OnKey(F2, isDown: false);
        var (a2, _) = sm.OnKey(F2, isDown: true);
        Assert.Equal(HotkeyAction.Press, a2);
    }

    [Fact]
    public void GenericVkControl_IsNormalizedLikeLeftRight()
    {
        var sm = CtrlF2();
        sm.OnKey(VK_CONTROL, isDown: true); // 通用 0x11
        var (a, _) = sm.OnKey(F2, isDown: true);
        Assert.Equal(HotkeyAction.Press, a);
    }

    [Fact]
    public void EngagedMainKey_AutoRepeat_IsSwallowed()
    {
        var sm = CtrlF2();
        sm.OnKey(VK_LCONTROL, isDown: true);
        sm.OnKey(F2, isDown: true);

        var (a, c) = sm.OnKey(F2, isDown: true); // repeat
        Assert.Equal(HotkeyAction.None, a);
        Assert.True(c);
    }

    [Fact]
    public void IsEngaged_TracksActiveDictationWindow()
    {
        var sm = CtrlF2();
        Assert.False(sm.IsEngaged);

        sm.OnKey(VK_LCONTROL, isDown: true);
        Assert.False(sm.IsEngaged);

        sm.OnKey(F2, isDown: true);
        Assert.True(sm.IsEngaged); // Engaged

        sm.OnKey(VK_ESCAPE, isDown: true);
        Assert.True(sm.IsEngaged); // AwaitingFullRelease（主键仍按住）

        sm.OnKey(F2, isDown: false);
        Assert.False(sm.IsEngaged); // 回到 Idle
    }

    [Fact]
    public void ModifierDownDuringIdle_IsNeverConsumed()
    {
        var sm = CtrlF2();
        var (a, c) = sm.OnKey(VK_LCONTROL, isDown: true);
        Assert.Equal(HotkeyAction.None, a);
        Assert.False(c); // 修饰键事件必须放行
        var (a2, c2) = sm.OnKey(VK_LCONTROL, isDown: false);
        Assert.False(c2);
    }
}
