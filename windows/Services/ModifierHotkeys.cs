using System;

namespace VoiceTyper.Services;

/// <summary>
/// 可单独作为热键的修饰键（<c>hotkey.key</c> 的取值）。对应 macOS 的 <c>ModifierHotkey</c>。
///
/// 只支持<b>右 Ctrl</b>，理由是其余修饰键单击本身在 Windows 上就有系统副作用，而低级钩子
/// 不能吞掉修饰键事件（吞掉会破坏其他应用看到的 down/up 配对）：
/// <list type="bullet">
/// <item>Alt：单独按下再松开会激活前台窗口的菜单栏，随后的 Ctrl+V 粘贴会落空；</item>
/// <item>Win：单击弹出开始菜单；</item>
/// <item>Shift：中文输入法里单击 Shift 切换中英文；</item>
/// <item>左 Ctrl：几乎所有快捷键的前缀，误触面过大。</item>
/// </list>
/// 右 Ctrl 单击没有任何系统行为，也是最常被闲置的修饰键。
/// </summary>
internal static class ModifierHotkeys
{
    public const string RightCtrl = "right_ctrl";

    public static bool IsModifierOnlyKey(string? key) =>
        string.Equals((key ?? "").Trim(), RightCtrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>目标键的虚拟键码（低级钩子交付的是区分左右的具体 vkCode）。</summary>
    public static int VirtualKey(string key) => HotkeyStateMachine.VK_RCONTROL;

    /// <summary>展示名；不是单独修饰键时返回 null。</summary>
    public static string? DisplayName(string? key) =>
        IsModifierOnlyKey(key) ? VoiceTyper.Support.L10n.T("右 Ctrl") : null;
}
