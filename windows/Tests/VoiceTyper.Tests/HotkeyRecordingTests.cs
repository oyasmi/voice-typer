using System.Linq;
using VoiceTyper.Services;
using Xunit;

namespace VoiceTyper.Tests;

public class HotkeyRecordingTests
{
    private const int VK_F2 = 0x71, VK_A = 0x41, VK_ESC = 0x1B, VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B;
    private const int VK_F13 = 0x7C, VK_NUMPAD1 = 0x61;

    [Fact]
    public void ModifierAlone_KeepsWaiting()
    {
        foreach (var vk in new[] { VK_CONTROL, VK_SHIFT, VK_MENU, VK_LWIN, 0xA2, 0xA3, 0xA0, 0xA5 })
        {
            Assert.Equal(HotkeyRecording.Kind.WaitForMore, HotkeyRecording.Interpret(vk, true, false, false, false).Kind);
        }
    }

    [Fact]
    public void PlainEsc_Cancels_ButEscWithModifierIsARealKey()
    {
        Assert.Equal(HotkeyRecording.Kind.Cancel, HotkeyRecording.Interpret(VK_ESC, false, false, false, false).Kind);
        var withCtrl = HotkeyRecording.Interpret(VK_ESC, true, false, false, false);
        Assert.Equal(HotkeyRecording.Kind.Accepted, withCtrl.Kind);
        Assert.Equal("esc", withCtrl.Key);
    }

    [Fact]
    public void BareKey_NeedsModifier()
    {
        Assert.Equal(HotkeyRecording.Kind.NeedModifier, HotkeyRecording.Interpret(VK_F2, false, false, false, false).Kind);
    }

    [Fact]
    public void UnsupportedKey_IsReportedAsSuch()
    {
        Assert.Equal(HotkeyRecording.Kind.Unsupported, HotkeyRecording.Interpret(VK_F13, true, false, false, false).Kind);
        Assert.Equal(HotkeyRecording.Kind.Unsupported, HotkeyRecording.Interpret(VK_NUMPAD1, true, false, false, false).Kind);
    }

    [Fact]
    public void Combination_IsAccepted_WithModifiersInStableOrder()
    {
        var result = HotkeyRecording.Interpret(VK_F2, ctrl: true, alt: true, shift: true, win: true);
        Assert.Equal(HotkeyRecording.Kind.Accepted, result.Kind);
        Assert.Equal("f2", result.Key);
        Assert.Equal(new[] { "ctrl", "alt", "shift", "win" }, result.Modifiers!.ToArray());

        var letter = HotkeyRecording.Interpret(VK_A, ctrl: true, alt: false, shift: false, win: false);
        Assert.Equal("a", letter.Key);
    }

    [Fact]
    public void EveryRecordableName_IsAcceptedByTheHotkeyService()
    {
        // 录制得到的名字必须能被 HotkeyService / 配置校验接受，否则录进去却存不了。
        for (int vk = 0; vk < 256; vk++)
        {
            var name = HotkeyService.NameForVirtualKey(vk);
            if (name is null) continue;
            Assert.True(HotkeyService.IsSupportedKey(name), $"vk=0x{vk:X2} name={name}");
        }
    }

    [Theory]
    [InlineData("return", true)]
    [InlineData("escape", true)]
    [InlineData("page_up", true)]
    [InlineData("caps_lock", true)]
    [InlineData("F2", true)]
    [InlineData("z", true)]
    [InlineData("f13", false)]
    [InlineData("", false)]
    public void Aliases_AreStillAccepted(string key, bool supported)
    {
        Assert.Equal(supported, HotkeyService.IsSupportedKey(key));
    }

    [Theory]
    [InlineData("c", "ctrl")]
    [InlineData("v", "ctrl")]
    [InlineData("s", "ctrl")]
    [InlineData("space", "ctrl")]
    [InlineData("f4", "alt")]
    [InlineData("tab", "alt")]
    [InlineData("space", "shift")]
    [InlineData("space", "win")]
    public void CommonShortcuts_GetAConflictNote(string key, string modifier)
    {
        var note = HotkeyRecording.ConflictNote(key, new[] { modifier });
        Assert.NotNull(note);
        // 说明里要点名具体的组合，用户才知道指的是哪一个。
        Assert.Contains(key.ToUpperInvariant(), note!);
    }

    [Theory]
    [InlineData("f2", "ctrl")]        // 默认热键
    [InlineData("space", "ctrl", "shift")]  // 修饰键集合不同：Ctrl+Shift+Space 不在冲突表里
    [InlineData("c", "ctrl", "alt")]
    [InlineData("q", "ctrl")]
    [InlineData("f4", "ctrl")]
    public void UncommonCombinations_HaveNoNote(string key, params string[] modifiers)
    {
        Assert.Null(HotkeyRecording.ConflictNote(key, modifiers));
    }
}
