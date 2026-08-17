using System.Reflection;
using VoiceTyper.Core;
using Xunit;
using YamlDotNet.Serialization;

namespace VoiceTyper.Tests;

/// <summary>
/// 只测配置模型与序列化的纯逻辑；不接触真实 <c>%APPDATA%</c>（<see cref="ConfigStore"/>/
/// <see cref="ConfigMigrator"/> 的文件路径来自固定的 <c>AppConstants</c>，在开发机上跑测试
/// 时读写真实用户配置目录是不可接受的副作用）。文件系统层面的读写/迁移行为需要在真实
/// Windows 环境下手工验证一次（见 windows/DESIGN.md P0/P1 阶段）。
/// </summary>
public class ConfigStoreTests
{
    [Fact]
    public void Clone_IsDeepCopy_MutatingCloneDoesNotAffectOriginal()
    {
        var original = new AppConfig();
        original.Hotkey.Modifiers.Add("ctrl");

        var clone = original.Clone();
        clone.Hotkey.Modifiers.Add("alt");
        clone.Asr.Language = "zh";
        clone.UI.Opacity = 0.5;

        Assert.Single(original.Hotkey.Modifiers);
        Assert.Equal("auto", original.Asr.Language);
        Assert.Equal(0.85, original.UI.Opacity);
    }

    [Fact]
    public void HotkeyDisplayString_NormalizesModifierNames()
    {
        var hotkey = new HotkeyConfig { Modifiers = new() { "control", "option", "cmd" }, Key = "f2" };
        Assert.Equal("Ctrl+Alt+Win+F2", hotkey.DisplayString);
    }

    [Theory]
    [InlineData("zh", AsrLanguage.Zh)]
    [InlineData("EN", AsrLanguage.En)]
    [InlineData("", AsrLanguage.Auto)]
    [InlineData("bogus", AsrLanguage.Auto)]
    public void AsrLanguageParse_FallsBackToAutoOnUnknown(string raw, AsrLanguage expected)
    {
        Assert.Equal(expected, AsrLanguageExtensions.Parse(raw));
    }

    [Fact]
    public void SerializeYaml_RoundTripsThroughDeserializer()
    {
        var config = new AppConfig();
        config.Asr.LanguageValue = AsrLanguage.Zh;
        config.Asr.Threads = 4;
        config.Asr.IdleUnloadMinutes = 7;
        config.Llm.Enabled = true;
        config.Llm.BaseUrl = "https://example.com/v1";
        config.Hotkey = new HotkeyConfig { Modifiers = new() { "ctrl", "alt" }, Key = "f9" };
        config.UI.Opacity = 0.77;

        // SerializeYaml 是 ConfigStore 的私有静态方法（手写 YAML 拼接，保持字段顺序/格式），
        // 用反射调用以验证它产出的文本能被同一套 Deserializer 正确解析回去。
        var method = typeof(ConfigStore).GetMethod("SerializeYaml", BindingFlags.NonPublic | BindingFlags.Static)!;
        var yaml = (string)method.Invoke(null, new object[] { config })!;

        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        var roundTripped = deserializer.Deserialize<AppConfig>(yaml);

        Assert.Equal("zh", roundTripped.Asr.Language);
        Assert.Equal(4, roundTripped.Asr.Threads);
        Assert.Equal(7, roundTripped.Asr.IdleUnloadMinutes);
        Assert.True(roundTripped.Llm.Enabled);
        Assert.Equal("https://example.com/v1", roundTripped.Llm.BaseUrl);
        Assert.Equal(new[] { "ctrl", "alt" }, roundTripped.Hotkey.Modifiers);
        Assert.Equal("f9", roundTripped.Hotkey.Key);
        Assert.Equal(0.77, roundTripped.UI.Opacity, 3);
    }

    [Fact]
    public void Deserializer_IgnoresUnknownLegacyServerSection()
    {
        // 老客户端配置带 server 段；新 schema 没有对应字段，IgnoreUnmatchedProperties
        // 必须能容忍它，这是 ConfigMigrator 读取老配置文件时依赖的前提。
        var yaml = "hotkey:\n  modifiers:\n    - \"ctrl\"\n  key: \"f2\"\nui:\n  opacity: 0.6\nserver:\n  host: \"127.0.0.1\"\n";
        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        var legacy = deserializer.Deserialize<AppConfig>(yaml);

        Assert.Contains("ctrl", legacy.Hotkey.Modifiers);
        Assert.Equal("f2", legacy.Hotkey.Key);
        Assert.Equal(0.6, legacy.UI.Opacity, 3);
    }
}
