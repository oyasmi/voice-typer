using System.Reflection;
using VoiceTyper.Core;
using VoiceTyper.Services;
using Xunit;
using YamlDotNet.Serialization;

namespace VoiceTyper.Tests;

/// <summary>3.5 对齐 macOS 新增的配置项：<c>asr.preload_on_launch</c>、<c>hotkey.mode</c>、
/// <c>audio.input_device</c>、<c>ui.hud_position</c>，以及单独修饰键热键。</summary>
public class ConfigParityTests
{
    private static string Serialize(AppConfig config)
    {
        var method = typeof(ConfigStore).GetMethod("SerializeYaml", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, new object[] { config })!;
    }

    private static AppConfig Parse(string yaml) =>
        new DeserializerBuilder().IgnoreUnmatchedProperties().Build().Deserialize<AppConfig>(yaml).Validated();

    [Fact]
    public void Defaults_MatchMacOS()
    {
        var config = new AppConfig();
        Assert.True(config.Asr.PreloadOnLaunch);
        Assert.Equal(0, config.Asr.IdleUnloadMinutes);
        Assert.Equal(HotkeyMode.Hold, config.Hotkey.ModeValue);
        Assert.Equal("auto", config.Audio.InputDevice);
        Assert.Equal(HudPlacement.BottomCenter, config.UI.HudPositionValue);
        Assert.Equal("zh", config.UI.InterfaceLanguage);
    }

    [Fact]
    public void NewFields_RoundTripThroughYaml()
    {
        var config = new AppConfig();
        config.Asr.PreloadOnLaunch = false;
        config.Hotkey.ModeValue = HotkeyMode.Toggle;
        config.Audio.InputDevice = "{0.0.1.00000000}.{abc}";
        config.UI.HudPositionValue = HudPlacement.NearCursor;

        var back = Parse(Serialize(config));

        Assert.False(back.Asr.PreloadOnLaunch);
        Assert.Equal(HotkeyMode.Toggle, back.Hotkey.ModeValue);
        Assert.Equal("{0.0.1.00000000}.{abc}", back.Audio.InputDevice);
        Assert.Equal(HudPlacement.NearCursor, back.UI.HudPositionValue);
    }

    /// <summary>此前序列化根本没写 <c>interface_language</c>：用户在设置里切到英文，保存后重启又变回中文。</summary>
    [Fact]
    public void InterfaceLanguage_SurvivesSaveAndLoad()
    {
        var config = new AppConfig();
        config.UI.InterfaceLanguageValue = VoiceTyper.Support.AppLanguage.En;
        Assert.Equal("en", Parse(Serialize(config)).UI.InterfaceLanguage);
    }

    [Fact]
    public void OlderConfigWithoutNewFields_GetsDefaults()
    {
        var yaml = "asr:\n  language: \"zh\"\nhotkey:\n  modifiers:\n    - \"ctrl\"\n  key: \"f2\"\nui:\n  opacity: 0.6\n";
        var config = Parse(yaml);
        Assert.True(config.Asr.PreloadOnLaunch);
        Assert.Equal(HotkeyMode.Hold, config.Hotkey.ModeValue);
        Assert.Equal("auto", config.Audio.InputDevice);
        Assert.Equal(HudPlacement.BottomCenter, config.UI.HudPositionValue);
    }

    [Theory]
    [InlineData("hold", "Hold")]
    [InlineData("toggle", "Toggle")]
    [InlineData("TOGGLE", "Toggle")]
    [InlineData("garbage", "Hold")]
    [InlineData("", "Hold")]
    [InlineData(null, "Hold")]
    public void HotkeyMode_ParsesLeniently(string? raw, string expected)
    {
        // 不在公开测试签名里出现 internal 枚举（否则 CS0051）；用名称字符串比对。
        Assert.Equal(expected, HotkeyModeExtensions.Parse(raw).ToString());
    }

    [Fact]
    public void HudPosition_UnknownFallsBackToBottomCenter()
    {
        var config = new AppConfig();
        config.UI.HudPosition = "sideways";
        Assert.Equal("bottom_center", config.Validated().UI.HudPosition);
        config.UI.HudPosition = "hidden";
        Assert.Equal("hidden", config.Validated().UI.HudPosition);
    }

    [Fact]
    public void BlankInputDevice_FallsBackToAuto()
    {
        var config = new AppConfig();
        config.Audio.InputDevice = "   ";
        Assert.Equal("auto", config.Validated().Audio.InputDevice);
    }

    [Fact]
    public void InvalidKey_FallsBackToDefaultKey_ButKeepsMode()
    {
        var config = new AppConfig();
        config.Hotkey = new HotkeyConfig { Modifiers = new() { "ctrl" }, Key = "nonsense", ModeValue = HotkeyMode.Toggle };
        var validated = config.Validated().Hotkey;
        Assert.Equal("f2", validated.Key);
        Assert.Equal(HotkeyMode.Toggle, validated.ModeValue); // 模式是独立的用户选择，不被非法键名连坐
    }

    [Fact]
    public void NoModifier_FallsBackToDefaultKey_ButKeepsMode()
    {
        var config = new AppConfig();
        config.Hotkey = new HotkeyConfig { Modifiers = new(), Key = "d", ModeValue = HotkeyMode.Toggle };
        var validated = config.Validated().Hotkey;
        Assert.Equal("f2", validated.Key);
        Assert.Equal(HotkeyMode.Toggle, validated.ModeValue);
    }

    [Fact]
    public void ModifierOnlyKey_IsValidWithoutModifiers_AndIgnoresStrayOnes()
    {
        var config = new AppConfig();
        config.Hotkey = new HotkeyConfig { Modifiers = new() { "ctrl", "shift" }, Key = "Right_Ctrl" };
        var validated = config.Validated().Hotkey;
        Assert.Equal(ModifierHotkeys.RightCtrl, validated.Key);
        Assert.Empty(validated.Modifiers);
        Assert.True(validated.IsModifierOnly);
        Assert.Equal("右 Ctrl", validated.DisplayString);
    }

    [Fact]
    public void ModifierOnlyKey_SurvivesYamlRoundTrip()
    {
        var config = new AppConfig();
        config.Hotkey = new HotkeyConfig { Modifiers = new(), Key = ModifierHotkeys.RightCtrl, ModeValue = HotkeyMode.Toggle };
        var back = Parse(Serialize(config)).Hotkey;
        Assert.Equal(ModifierHotkeys.RightCtrl, back.Key);
        Assert.Empty(back.Modifiers);
        Assert.Equal(HotkeyMode.Toggle, back.ModeValue);
    }

    [Fact]
    public void RightAlt_ModifierOnlyKey_ValidWithoutModifiers_AndSurvivesYamlRoundTrip()
    {
        var config = new AppConfig();
        config.Hotkey = new HotkeyConfig { Modifiers = new() { "alt" }, Key = "Right_Alt", ModeValue = HotkeyMode.Hold };
        var validated = config.Validated().Hotkey;
        Assert.Equal(ModifierHotkeys.RightAlt, validated.Key);
        Assert.Empty(validated.Modifiers);
        Assert.True(validated.IsModifierOnly);
        Assert.Equal("右 Alt", validated.DisplayString);

        var roundTrip = Parse(Serialize(config)).Hotkey;
        Assert.Equal(ModifierHotkeys.RightAlt, roundTrip.Key);
        Assert.Empty(roundTrip.Modifiers);
        Assert.Equal(HotkeyMode.Hold, roundTrip.ModeValue);
    }

    [Fact]
    public void Clone_IsDeepForNewSections()
    {
        var original = new AppConfig();
        var clone = original.Clone();
        clone.Audio.InputDevice = "x";
        clone.Hotkey.ModeValue = HotkeyMode.Toggle;
        clone.UI.HudPositionValue = HudPlacement.Hidden;
        clone.Asr.PreloadOnLaunch = false;

        Assert.Equal("auto", original.Audio.InputDevice);
        Assert.Equal(HotkeyMode.Hold, original.Hotkey.ModeValue);
        Assert.Equal(HudPlacement.BottomCenter, original.UI.HudPositionValue);
        Assert.True(original.Asr.PreloadOnLaunch);
    }
}
