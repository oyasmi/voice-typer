using System;
using System.Collections.Generic;
using static VoiceTyper.Support.NativeMethods;

namespace VoiceTyper.Services;

[Flags]
internal enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

internal enum HotkeyAction { None, Press, Release, Cancel }

/// <summary>
/// 热键按键状态机（R2-1）。与 Win32 低级钩子解耦，纯逻辑、可脱离真机单测。
///
/// 状态：
/// <list type="bullet">
/// <item><c>Idle</c>：未接管任何组合键。</item>
/// <item><c>Engaged</c>：修饰键 + 主键命中并已触发 <see cref="HotkeyAction.Press"/>，录音进行中。</item>
/// <item><c>AwaitingFullRelease</c>：录音已因松修饰键 / Esc 结束，但主键仍按住——吞掉此状态下
///   的主键 auto-repeat，直到主键真正抬起才回到 <c>Idle</c>，避免"取消即重启"。</item>
/// </list>
///
/// 自维护物理按键状态（由 key down/up 事件驱动），不在当前事件尚未反映到系统状态时用
/// <c>GetAsyncKeyState</c> 推断释放；同时按住左右 Ctrl 时，松开其中一个不判定为 Ctrl 已释放。
/// </summary>
internal sealed class HotkeyStateMachine
{
    // 左右键具体 vkCode（低级钩子交付的是这些，而非通用的 VK_CONTROL/VK_MENU/VK_SHIFT）。
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;

    /// <summary>安装钩子时用系统快照播种物理修饰键状态时，需要遍历的全部修饰键 vkCode。</summary>
    public static readonly int[] AllModifierVks =
        { VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN };

    private enum S { Idle, Engaged, AwaitingFullRelease }

    private readonly int _targetVk;
    private readonly HotkeyModifiers _expected;
    private readonly HashSet<int> _pressedModifierKeys = new();
    private S _state = S.Idle;
    private bool _mainKeyDown;

    public HotkeyStateMachine(int targetVk, HotkeyModifiers expected)
    {
        _targetVk = targetVk;
        _expected = expected;
    }

    /// <summary>把任意（左/右/通用）修饰键 vkCode 归一化为一个 <see cref="HotkeyModifiers"/> 位；
    /// 非修饰键返回 <see cref="HotkeyModifiers.None"/>。</summary>
    public static HotkeyModifiers NormalizeModifier(int vk) => vk switch
    {
        VK_CONTROL or VK_LCONTROL or VK_RCONTROL => HotkeyModifiers.Ctrl,
        VK_MENU or VK_LMENU or VK_RMENU => HotkeyModifiers.Alt,
        VK_SHIFT or VK_LSHIFT or VK_RSHIFT => HotkeyModifiers.Shift,
        VK_LWIN or VK_RWIN => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    /// <summary>仅在钩子安装这一刻用系统快照播种一次（应对自愈重装时用户仍按着键）。</summary>
    public void SeedModifier(int vk)
    {
        if (NormalizeModifier(vk) != HotkeyModifiers.None)
        {
            _pressedModifierKeys.Add(vk);
        }
    }

    public HotkeyModifiers CurrentModifiers
    {
        get
        {
            var mask = HotkeyModifiers.None;
            foreach (var vk in _pressedModifierKeys)
            {
                mask |= NormalizeModifier(vk);
            }
            return mask;
        }
    }

    /// <summary>
    /// 处理一个键盘 down/up 事件。返回要触发的业务动作，以及是否"消费"该事件
    /// （消费 = 不再下发给前台应用）。<b>修饰键事件永不消费</b>——否则会破坏其他应用
    /// 看到的修饰键 down/up 配对。
    /// </summary>
    public (HotkeyAction action, bool consume) OnKey(int vk, bool isDown)
    {
        var modBit = NormalizeModifier(vk);
        bool isModifier = modBit != HotkeyModifiers.None;
        bool isMainKey = vk == _targetVk;

        // 先按事件更新物理按键状态，再做判定。
        if (isModifier)
        {
            if (isDown) _pressedModifierKeys.Add(vk);
            else _pressedModifierKeys.Remove(vk);
        }

        var mods = CurrentModifiers;
        // Esc 作为主键时不当取消键处理（否则其 auto-repeat 会把刚触发的录音立刻取消）。
        bool escCancels = vk == VK_ESCAPE && _targetVk != VK_ESCAPE;

        switch (_state)
        {
            case S.Idle:
                if (isMainKey && isDown && mods == _expected)
                {
                    _state = S.Engaged;
                    _mainKeyDown = true;
                    return (HotkeyAction.Press, true);
                }
                // 修饰键不匹配的主键、或纯修饰键：放行，保持用户正常输入。
                return (HotkeyAction.None, false);

            case S.Engaged:
                if (isDown && escCancels)
                {
                    _state = _mainKeyDown ? S.AwaitingFullRelease : S.Idle;
                    return (HotkeyAction.Cancel, true);
                }
                if (isMainKey && !isDown)
                {
                    _mainKeyDown = false;
                    _state = S.Idle;
                    return (HotkeyAction.Release, true);
                }
                if (isMainKey && isDown)
                {
                    return (HotkeyAction.None, true); // 主键 auto-repeat：吞掉，不重复触发。
                }
                if (isModifier && !isDown && mods != _expected)
                {
                    // 按住组合键时先松开了修饰键：按"松开"处理。修饰键事件本身不消费。
                    _state = _mainKeyDown ? S.AwaitingFullRelease : S.Idle;
                    return (HotkeyAction.Release, false);
                }
                return (HotkeyAction.None, false);

            case S.AwaitingFullRelease:
                if (isMainKey && !isDown)
                {
                    _mainKeyDown = false;
                    _state = S.Idle;
                    return (HotkeyAction.None, true);
                }
                if (isMainKey && isDown)
                {
                    // 取消 / 松修饰键之后主键仍按住的 auto-repeat：吞掉，不重启录音。
                    return (HotkeyAction.None, true);
                }
                return (HotkeyAction.None, false);

            default:
                return (HotkeyAction.None, false);
        }
    }
}
