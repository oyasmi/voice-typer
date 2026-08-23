using VoiceTyper.Core;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// W-14（F-13 / R2-12 / R4-05）：手改 YAML 或历史脏数据里越界的值必须在 <see cref="AppConfig.Validated"/>
/// 里被夹逼回合法范围，不能未经校验就进入运行态。C# 直译自 macos/Tests/VoiceTyperTests/AppConfigTests.swift
/// 中与本项对应的用例。
/// </summary>
public class AppConfigValidationTests
{
    [Fact]
    public void ClampsThreads_ToDocumentedRange()
    {
        var config = new AppConfig();
        config.Asr.Threads = 9999;
        Assert.Equal(32, config.Validated().Asr.Threads);

        config.Asr.Threads = -5;
        Assert.Equal(0, config.Validated().Asr.Threads);
    }

    [Fact]
    public void ClampsIdleUnloadMinutes_ToDocumentedRange()
    {
        var config = new AppConfig();
        config.Asr.IdleUnloadMinutes = -5;
        Assert.Equal(0, config.Validated().Asr.IdleUnloadMinutes);

        config.Asr.IdleUnloadMinutes = 999_999;
        Assert.Equal(24 * 60, config.Validated().Asr.IdleUnloadMinutes);
    }

    [Fact]
    public void ClampsPreviewWindowSeconds_ToDocumentedRange()
    {
        var config = new AppConfig();
        config.Asr.PreviewWindowSeconds = -1;
        Assert.Equal(0, config.Validated().Asr.PreviewWindowSeconds);

        config.Asr.PreviewWindowSeconds = 999;
        Assert.Equal(30, config.Validated().Asr.PreviewWindowSeconds);
    }

    [Fact]
    public void ResetsNonFiniteTemperature_ToLowerBound()
    {
        var config = new AppConfig();
        config.Llm.Temperature = double.NaN;
        Assert.Equal(0, config.Validated().Llm.Temperature);

        config.Llm.Temperature = double.PositiveInfinity;
        Assert.Equal(0, config.Validated().Llm.Temperature);
    }

    [Fact]
    public void ClampsTemperature_ToDocumentedRange()
    {
        var config = new AppConfig();
        config.Llm.Temperature = 5.0;
        Assert.Equal(2, config.Validated().Llm.Temperature);
    }

    [Fact]
    public void ClampsMaxTokens_ToDocumentedRange()
    {
        var config = new AppConfig();
        config.Llm.MaxTokens = 1;
        Assert.Equal(64, config.Validated().Llm.MaxTokens);

        config.Llm.MaxTokens = 100_000;
        Assert.Equal(8192, config.Validated().Llm.MaxTokens);
    }

    [Fact]
    public void ClampsTimeout_ToDocumentedRange()
    {
        var config = new AppConfig();
        config.Llm.Timeout = -1;
        Assert.Equal(1, config.Validated().Llm.Timeout);

        config.Llm.Timeout = 999;
        Assert.Equal(120, config.Validated().Llm.Timeout);
    }

    [Fact]
    public void ResetsNonFiniteOpacity_ToLowerBound()
    {
        var config = new AppConfig();
        config.UI.Opacity = double.NaN;
        Assert.Equal(0.1, config.Validated().UI.Opacity);
    }

    [Fact]
    public void ClampsOpacity_ToDocumentedRange()
    {
        var config = new AppConfig();
        config.UI.Opacity = 5.0;
        Assert.Equal(1.0, config.Validated().UI.Opacity);
    }

    /// <summary>R4-05：不支持的主键必须回落默认热键，而不是原样进入运行态。</summary>
    [Fact]
    public void FallsBackToDefaultHotkey_WhenKeyUnsupported()
    {
        var config = new AppConfig();
        config.Hotkey = new HotkeyConfig { Modifiers = new() { "ctrl" }, Key = "not-a-real-key" };

        var validated = config.Validated().Hotkey;
        Assert.Equal("f2", validated.Key);
        Assert.Contains("ctrl", validated.Modifiers);
    }

    /// <summary>
    /// R4-05 核心场景：主键受支持但不带任何修饰键——手改配置文件可达
    /// （HotkeyService 只检查键名是否支持，不要求修饰键非空），会导致正常打字敲这个字母
    /// 都触发一次录音。必须回落默认热键，不能静默接受。
    /// </summary>
    [Fact]
    public void FallsBackToDefaultHotkey_WhenNoModifiers()
    {
        var config = new AppConfig();
        config.Hotkey = new HotkeyConfig { Modifiers = new(), Key = "d" };

        var validated = config.Validated().Hotkey;
        Assert.Equal("f2", validated.Key);
        Assert.NotEmpty(validated.Modifiers);
    }

    [Fact]
    public void KeepsValidHotkey_Unchanged()
    {
        var config = new AppConfig();
        config.Hotkey = new HotkeyConfig { Modifiers = new() { "ctrl", "shift" }, Key = "d" };

        var validated = config.Validated().Hotkey;
        Assert.Equal("d", validated.Key);
        Assert.Equal(new[] { "ctrl", "shift" }, validated.Modifiers);
    }

    [Fact]
    public void KeepsInRangeValues_Unchanged()
    {
        var config = new AppConfig();
        config.Asr.Threads = 4;
        config.Asr.IdleUnloadMinutes = 10;
        config.Llm.Temperature = 0.7;
        config.Llm.MaxTokens = 800;
        config.Llm.Timeout = 5;
        config.UI.Opacity = 0.85;

        var validated = config.Validated();
        Assert.Equal(4, validated.Asr.Threads);
        Assert.Equal(10, validated.Asr.IdleUnloadMinutes);
        Assert.Equal(0.7, validated.Llm.Temperature);
        Assert.Equal(800, validated.Llm.MaxTokens);
        Assert.Equal(5, validated.Llm.Timeout);
        Assert.Equal(0.85, validated.UI.Opacity);
    }
}
