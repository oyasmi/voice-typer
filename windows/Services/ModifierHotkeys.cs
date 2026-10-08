using System;

namespace VoiceTyper.Services;

/// <summary>
/// 可单独作为热键的修饰键（<c>hotkey.key</c> 的取值）。对应 macOS 的 <c>ModifierHotkey</c>。
///
/// 支持<b>右 Ctrl</b>与<b>右 Alt</b>。其余修饰键仍不支持，理由是单击本身在 Windows 上就有
/// 系统副作用，或误触面过大：
/// <list type="bullet">
/// <item>左 Alt：单独按下再松开激活前台窗口菜单栏（右 Alt 同样会——见下，但右 Alt 有掩码
///   方案兜住）；几乎一半快捷键的前缀，误触面大；</item>
/// <item>Win：单击弹出开始菜单，且无右键掩码之类的系统级豁免手段；</item>
/// <item>Shift：中文输入法里单击 Shift 切换中英文；</item>
/// <item>左 Ctrl：几乎所有快捷键的前缀，误触面过大。</item>
/// </list>
/// 右 Alt 的菜单栏副作用由 <see cref="HotkeyService"/> 在干净手势开始时注入一对哑键
/// （VK 0xFF）掩掉：系统据"Alt 按下期间出现过别的键"判定这不是孤立单击，松开时不再进入
/// 菜单模式。按键事件本身全部放行，AltGr（欧洲键盘的右 Alt 组合字符）与右 Alt+Tab 不受
/// 影响。该掩码技巧 2026-10-08 在 Win10 19045 + 记事本上以 GUI_INMENUMODE 实测验证
/// （孤立右 Alt menu=True，右 Alt+哑键 menu=False）。
/// </summary>
internal static class ModifierHotkeys
{
    public const string RightCtrl = "right_ctrl";
    public const string RightAlt = "right_alt";

    public static bool IsModifierOnlyKey(string? key) =>
        string.Equals((key ?? "").Trim(), RightCtrl, StringComparison.OrdinalIgnoreCase)
        || string.Equals((key ?? "").Trim(), RightAlt, StringComparison.OrdinalIgnoreCase);

    /// <summary>目标键的虚拟键码（低级钩子交付的是区分左右的具体 vkCode）。</summary>
    public static int VirtualKey(string key) =>
        string.Equals(key.Trim(), RightAlt, StringComparison.OrdinalIgnoreCase)
            ? HotkeyStateMachine.VK_RMENU
            : HotkeyStateMachine.VK_RCONTROL;

    /// <summary>该单独修饰键单击松开时会不会激活前台窗口的菜单栏（需要掩码）。
    /// 只有右 Alt 需要：右 Ctrl 单击没有任何系统行为。</summary>
    public static bool NeedsMenuBarMask(string? key) =>
        string.Equals((key ?? "").Trim(), RightAlt, StringComparison.OrdinalIgnoreCase);

    /// <summary>展示名；不是单独修饰键时返回 null。</summary>
    public static string? DisplayName(string? key) =>
        IsModifierOnlyKey(key)
            ? NeedsMenuBarMask(key) ? VoiceTyper.Support.L10n.T("右 Alt") : VoiceTyper.Support.L10n.T("右 Ctrl")
            : null;
}
