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

internal enum HotkeyAction
{
    None,
    Press,
    Release,
    /// <summary>用户按 Esc 主动取消。</summary>
    Cancel,
    /// <summary>仅单独修饰键：按住期间又按了别的键 / 点了鼠标，说明这是一次组合快捷键而不是
    /// 听写手势，本次录音应静默丢弃（不提示"已取消"）。</summary>
    GestureCancel,
}

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

    /// <summary>
    /// Idle：未接管；Engaged：组合键命中；AwaitingFullRelease：取消 / 松修饰键后主键仍按住；
    /// HoldingClean / Invalidated：仅单独修饰键模式——目标键按住且仍是"干净单击" / 已作废但尚未抬起。
    /// </summary>
    private enum S { Idle, Engaged, AwaitingFullRelease, HoldingClean, Invalidated }

    private readonly int _targetVk;
    private readonly HotkeyModifiers _expected;
    private readonly bool _modifierOnly;
    private readonly HashSet<int> _pressedModifierKeys = new();
    private S _state = S.Idle;
    private bool _mainKeyDown;
    /// <summary><see cref="ResetIfReleased"/> 连续判定"物理上已松开"的次数。</summary>
    private int _releasedObservations;
    private volatile bool _acceptsCancelWhenInactive;

    /// <summary>卡键看门狗需要连续判定这么多次才动作，避免一次偶发的读数误判。</summary>
    public const int ReleasedObservationsRequired = 2;

    /// <summary>
    /// 无进行中的组合键时是否仍受理 Esc 取消。默认只在按住热键期间受理 Esc（否则会吞掉用户
    /// 平时正常使用的 Esc）；控制器在有听写进行中（松键后的识别阶段、切换模式的录音阶段）
    /// 把它打开，听写收尾时关闭。
    /// </summary>
    public bool AcceptsCancelWhenInactive
    {
        get => _acceptsCancelWhenInactive;
        // 控制器在 UI 线程写，钩子线程读：一个 bool，volatile 足够。
        set => _acceptsCancelWhenInactive = value;
    }

    public HotkeyStateMachine(int targetVk, HotkeyModifiers expected)
    {
        _targetVk = targetVk;
        _expected = expected;
    }

    private HotkeyStateMachine(int modifierVk)
    {
        _targetVk = modifierVk;
        _expected = HotkeyModifiers.None;
        _modifierOnly = true;
    }

    /// <summary>单独修饰键模式（如右 Ctrl）：目标键干净按下即 Press，干净抬起即 Release；
    /// 按住期间出现任何其他键 / 修饰键 / 鼠标按下则 GestureCancel。</summary>
    public static HotkeyStateMachine ForModifierOnly(int modifierVk) => new(modifierVk);

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

    /// <summary>是否处于"已接管组合键"的任一状态（Engaged 或等待整组释放）——
    /// 自愈重装时据此决定是否要替正在进行的听写补一个收尾信号（R3-5）。</summary>
    public bool IsEngaged => _state is S.Engaged or S.AwaitingFullRelease or S.HoldingClean or S.Invalidated;

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

        if (_modifierOnly) return OnKeyModifierOnly(vk, isDown, isModifier);

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
                // 有听写进行中（识别阶段 / 切换模式的录音阶段）：Esc 取消并吞掉，不再传给前台应用。
                if (isDown && escCancels && AcceptsCancelWhenInactive)
                {
                    return (HotkeyAction.Cancel, true);
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

    /// <summary>
    /// 单独修饰键的"干净单击"识别（对应 macOS <c>ModifierTapRecognizer</c>）：
    /// 开始：目标键按下，且此刻没有任何其他修饰键被按住 → Press；
    /// 作废：按住期间出现任意非修饰键按下 / 另一个修饰键按下 / 鼠标按下 → GestureCancel（每次手势最多一次）；
    /// 结束：目标键抬起，仍干净则 Release，已作废则无输出。修饰键事件永不消费。
    /// </summary>
    private (HotkeyAction action, bool consume) OnKeyModifierOnly(int vk, bool isDown, bool isModifier)
    {
        bool isTarget = vk == _targetVk;
        bool esc = vk == VK_ESCAPE;

        switch (_state)
        {
            case S.HoldingClean:
                if (isTarget)
                {
                    if (isDown) return (HotkeyAction.None, false); // 目标键的 auto-repeat
                    _state = S.Idle;
                    return (HotkeyAction.Release, false);
                }
                if (isDown && (!isModifier || OtherModifiersPressed()))
                {
                    _state = S.Invalidated;
                    // Esc 是用户明确的取消意图，要给"已取消"反馈；其余键是组合快捷键，静默丢弃。
                    return (esc ? HotkeyAction.Cancel : HotkeyAction.GestureCancel, esc);
                }
                return (HotkeyAction.None, false);

            case S.Invalidated:
                if (isTarget && !isDown) _state = S.Idle;
                return (HotkeyAction.None, false);

            default: // Idle
                if (isTarget && isDown && !OtherModifiersPressed())
                {
                    _state = S.HoldingClean;
                    return (HotkeyAction.Press, false);
                }
                if (isDown && esc && AcceptsCancelWhenInactive)
                {
                    return (HotkeyAction.Cancel, true);
                }
                return (HotkeyAction.None, false);
        }
    }

    /// <summary>除目标键自身外是否还有修饰键（含另一侧的同名修饰键）被按住。</summary>
    private bool OtherModifiersPressed()
    {
        foreach (var vk in _pressedModifierKeys)
        {
            if (vk != _targetVk) return true;
        }
        return false;
    }

    /// <summary>
    /// 卡键看门狗（B3）：状态机以为热键还按着，但系统的物理按键状态说已经松开了——典型场景是 UAC 弹窗、
    /// Ctrl+Alt+Del、Win+L 把输入切到安全桌面，松键事件根本到不了低级钩子，录音会一直进行到时长上限。
    /// 连续 <see cref="ReleasedObservationsRequired"/> 次判定才动作：状态回 Idle、清掉过期的修饰键记录，
    /// 并在原本正在录音时返回 <see cref="HotkeyAction.Release"/>。
    ///
    /// 判据只用<b>修饰键</b>（单独修饰键模式用目标键本身）：组合键的主键事件被钩子消费，
    /// 不会更新 <c>GetAsyncKeyState</c>（DESIGN §15.2 的实测），用它判断主键会永远得到"已松开"。
    /// 限制：用户在安全桌面期间仍然按住修饰键时无法发现，下一次真实按键事件会让状态机自行纠正。
    /// </summary>
    /// <param name="isPhysicallyDown">按虚拟键码查询物理按下状态（生产传 <c>GetAsyncKeyState</c>）。</param>
    public HotkeyAction ResetIfReleased(Func<int, bool> isPhysicallyDown)
    {
        if (!IsEngaged || !LooksReleased(isPhysicallyDown))
        {
            _releasedObservations = 0;
            return HotkeyAction.None;
        }
        if (++_releasedObservations < ReleasedObservationsRequired) return HotkeyAction.None;

        _releasedObservations = 0;
        var wasRecording = _state is S.Engaged or S.HoldingClean;
        _state = S.Idle;
        _mainKeyDown = false;
        _pressedModifierKeys.RemoveWhere(vk => !isPhysicallyDown(vk));
        return wasRecording ? HotkeyAction.Release : HotkeyAction.None;
    }

    private bool LooksReleased(Func<int, bool> isPhysicallyDown)
    {
        if (_modifierOnly) return !isPhysicallyDown(_targetVk);
        // 取消 / 松修饰键之后"等主键抬起"的状态本来就不满足修饰键条件，不能当成卡键。
        if (_state != S.Engaged) return false;
        foreach (var bit in new[] { HotkeyModifiers.Ctrl, HotkeyModifiers.Alt, HotkeyModifiers.Shift, HotkeyModifiers.Win })
        {
            if ((_expected & bit) == 0) continue;
            bool down = false;
            foreach (var vk in AllModifierVks)
            {
                if (NormalizeModifier(vk) == bit && isPhysicallyDown(vk)) { down = true; break; }
            }
            if (!down) return true;
        }
        return false;
    }

    /// <summary>鼠标按下 / 滚轮（Ctrl+点击、Ctrl+滚轮缩放都是常见的组合用法）。只影响单独修饰键模式。</summary>
    public HotkeyAction OnMouseButton()
    {
        if (_modifierOnly && _state == S.HoldingClean)
        {
            _state = S.Invalidated;
            return HotkeyAction.GestureCancel;
        }
        return HotkeyAction.None;
    }
}
