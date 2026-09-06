using System.Runtime.InteropServices;
using VoiceTyper.Support;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// SendInput 的 INPUT/InputUnion 原生 ABI 布局回归（R1-2）。
/// 这些断言只覆盖托管结构体大小与字段偏移，<b>不能替代</b>真机上"记事本收到粘贴文本"的验证。
/// </summary>
public class NativeLayoutTests
{
    [Fact]
    public void Input_MatchesNativeSize()
    {
        // x64/arm64 原生 INPUT = 4(type) + 4(pad) + 32(union, 由 MOUSEINPUT 决定) = 40。
        Assert.Equal(40, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    [Fact]
    public void InputUnion_StartsAfterTypeAndPadding()
    {
        // KEYBDINPUT 通过联合体 U 落在 INPUT 起始 +8 字节处（type 4 + 4 对齐填充）。
        Assert.Equal(8, (int)Marshal.OffsetOf<NativeMethods.INPUT>(nameof(NativeMethods.INPUT.U)));
    }

    [Fact]
    public void MouseInput_DominatesUnionSize()
    {
        // 联合体大小由最大成员 MOUSEINPUT（32 字节）决定，而非 KEYBDINPUT（24 字节）。
        Assert.Equal(32, Marshal.SizeOf<NativeMethods.MOUSEINPUT>());
        Assert.Equal(24, Marshal.SizeOf<NativeMethods.KEYBDINPUT>());
        Assert.Equal(32, Marshal.SizeOf<NativeMethods.InputUnion>());
    }
}
