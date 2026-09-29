using VoiceTyper.Services;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 单独修饰键（右 Ctrl）的"干净单击"识别，以及 Esc 在无进行中组合键时的受理窗口
/// （对应 macOS <c>ModifierTapRecognizerTests</c>）。
/// </summary>
public class ModifierOnlyHotkeyTests
{
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_ESCAPE = 0x1B;
    private const int KeyC = 0x43;
    private const int F2 = 0x71;

    private static HotkeyStateMachine RightCtrl() => HotkeyStateMachine.ForModifierOnly(VK_RCONTROL);

    [Fact]
    public void CleanTap_PressesOnDown_ReleasesOnUp_NeverConsumes()
    {
        var sm = RightCtrl();
        Assert.Equal((HotkeyAction.Press, false), sm.OnKey(VK_RCONTROL, isDown: true));
        Assert.Equal((HotkeyAction.None, false), sm.OnKey(VK_RCONTROL, isDown: true)); // auto-repeat
        Assert.Equal((HotkeyAction.Release, false), sm.OnKey(VK_RCONTROL, isDown: false));
    }

    [Fact]
    public void OtherKeyWhileHeld_InvalidatesOnce_AndSuppressesRelease()
    {
        var sm = RightCtrl();
        sm.OnKey(VK_RCONTROL, isDown: true);

        Assert.Equal((HotkeyAction.GestureCancel, false), sm.OnKey(KeyC, isDown: true));
        // 每次手势最多作废一次；之后的按键与抬起都不再产生动作。
        Assert.Equal((HotkeyAction.None, false), sm.OnKey(F2, isDown: true));
        Assert.Equal((HotkeyAction.None, false), sm.OnKey(VK_RCONTROL, isDown: false));

        // 回到 Idle，下一次干净单击照常识别。
        Assert.Equal((HotkeyAction.Press, false), sm.OnKey(VK_RCONTROL, isDown: true));
    }

    [Fact]
    public void AnotherModifierWhileHeld_Invalidates()
    {
        var sm = RightCtrl();
        sm.OnKey(VK_RCONTROL, isDown: true);
        Assert.Equal(HotkeyAction.GestureCancel, sm.OnKey(VK_LSHIFT, isDown: true).action);
        Assert.Equal(HotkeyAction.None, sm.OnKey(VK_RCONTROL, isDown: false).action);
    }

    [Fact]
    public void OtherModifierAlreadyHeld_PreventsStart()
    {
        var sm = RightCtrl();
        sm.OnKey(VK_LCONTROL, isDown: true);
        Assert.Equal(HotkeyAction.None, sm.OnKey(VK_RCONTROL, isDown: true).action); // Ctrl+RCtrl 是别的东西
        Assert.Equal(HotkeyAction.None, sm.OnKey(VK_RCONTROL, isDown: false).action);
    }

    [Fact]
    public void MouseButtonWhileHeld_Invalidates()
    {
        var sm = RightCtrl();
        Assert.Equal(HotkeyAction.None, sm.OnMouseButton()); // 没按住时鼠标无关
        sm.OnKey(VK_RCONTROL, isDown: true);
        Assert.Equal(HotkeyAction.GestureCancel, sm.OnMouseButton());
        Assert.Equal(HotkeyAction.None, sm.OnMouseButton()); // 只作废一次
        Assert.Equal(HotkeyAction.None, sm.OnKey(VK_RCONTROL, isDown: false).action);
    }

    [Fact]
    public void Esc_WhileHeld_IsCancel_NotSilentGesture()
    {
        var sm = RightCtrl();
        sm.OnKey(VK_RCONTROL, isDown: true);
        Assert.Equal((HotkeyAction.Cancel, true), sm.OnKey(VK_ESCAPE, isDown: true));
    }

    [Fact]
    public void Esc_WhenIdle_OnlyCancelsWhenWindowIsOpen()
    {
        var sm = RightCtrl();
        Assert.Equal((HotkeyAction.None, false), sm.OnKey(VK_ESCAPE, isDown: true));

        sm.AcceptsCancelWhenInactive = true;
        Assert.Equal((HotkeyAction.Cancel, true), sm.OnKey(VK_ESCAPE, isDown: true));
        Assert.Equal((HotkeyAction.None, false), sm.OnKey(VK_ESCAPE, isDown: false)); // 抬起放行
    }

    [Fact]
    public void ComboHotkey_EscWhenIdle_OnlyCancelsWhenWindowIsOpen()
    {
        var sm = new HotkeyStateMachine(F2, HotkeyModifiers.Ctrl);
        Assert.Equal((HotkeyAction.None, false), sm.OnKey(VK_ESCAPE, isDown: true));

        sm.AcceptsCancelWhenInactive = true;
        Assert.Equal((HotkeyAction.Cancel, true), sm.OnKey(VK_ESCAPE, isDown: true));
    }

    [Fact]
    public void ComboHotkey_ToggleStyleUse_PressesAgainAfterRelease()
    {
        // 切换模式下每次按下都是一次 Press；松开后回到 Idle 才能再次触发。
        var sm = new HotkeyStateMachine(F2, HotkeyModifiers.Ctrl);
        sm.OnKey(VK_LCONTROL, isDown: true);
        Assert.Equal(HotkeyAction.Press, sm.OnKey(F2, isDown: true).action);
        Assert.Equal(HotkeyAction.Release, sm.OnKey(F2, isDown: false).action);
        Assert.Equal(HotkeyAction.Press, sm.OnKey(F2, isDown: true).action);
    }

    [Fact]
    public void ModifierHotkeys_KeyNames()
    {
        Assert.True(ModifierHotkeys.IsModifierOnlyKey("right_ctrl"));
        Assert.True(ModifierHotkeys.IsModifierOnlyKey(" RIGHT_CTRL "));
        Assert.False(ModifierHotkeys.IsModifierOnlyKey("f2"));
        Assert.False(ModifierHotkeys.IsModifierOnlyKey(null));
        Assert.True(HotkeyService.IsSupportedKey("right_ctrl"));
    }
}
