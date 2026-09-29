using System.Collections.Generic;

namespace VoiceTyper.Services;

/// <summary>
/// 设置页「录制热键」对一次按键的解释（纯逻辑，可单测；不依赖 WinForms）。对应 macOS 的
/// <c>HotkeyRecorderField</c> 的判定部分：单独按修饰键继续等待，Esc 取消，裸键不允许，
/// 不支持的键给出明确提示，其余接受。
/// </summary>
internal static class HotkeyRecording
{
    internal enum Kind { WaitForMore, Cancel, NeedModifier, Unsupported, Accepted }

    internal readonly record struct Result(Kind Kind, string? Key = null, IReadOnlyList<string>? Modifiers = null);

    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_ESCAPE = 0x1B;

    public static Result Interpret(int vkCode, bool ctrl, bool alt, bool shift, bool win)
    {
        // 只按下了修饰键本身：继续等待主键。
        if (vkCode is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN
            or HotkeyStateMachine.VK_LSHIFT or HotkeyStateMachine.VK_RSHIFT
            or HotkeyStateMachine.VK_LCONTROL or HotkeyStateMachine.VK_RCONTROL
            or HotkeyStateMachine.VK_LMENU or HotkeyStateMachine.VK_RMENU)
        {
            return new Result(Kind.WaitForMore);
        }

        if (vkCode == VK_ESCAPE && !ctrl && !alt && !shift && !win) return new Result(Kind.Cancel);

        var key = HotkeyService.NameForVirtualKey(vkCode);
        if (key is null) return new Result(Kind.Unsupported);

        var modifiers = new List<string>();
        if (ctrl) modifiers.Add("ctrl");
        if (alt) modifiers.Add("alt");
        if (shift) modifiers.Add("shift");
        if (win) modifiers.Add("win");
        // 裸键（无修饰键）会在正常打字时触发录音，配置校验也不接受。
        if (modifiers.Count == 0) return new Result(Kind.NeedModifier, key);

        return new Result(Kind.Accepted, key, modifiers);
    }
}
