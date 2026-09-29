using System;
using VoiceTyper.UI;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>HUD 预览文本排版（纯逻辑；用"每字符 10px"的假测量函数代替 GDI+）。</summary>
public class HudTextLayoutTests
{
    private static float Measure(string s) => s.Length * 10f;

    [Fact]
    public void ShortText_FitsOnOneLine_Untouched()
    {
        Assert.Equal(new[] { "你好世界" }, HudTextLayout.FitTail("你好世界", 2, 100, Measure));
    }

    [Fact]
    public void TextThatNeedsTwoLines_IsWrapped_WithoutEllipsis()
    {
        var lines = HudTextLayout.FitTail("一二三四五六七八九十甲乙", 2, 100, Measure);
        Assert.Equal(new[] { "一二三四五六七八九十", "甲乙" }, lines);
    }

    [Fact]
    public void Overflow_KeepsTheTail_AndPrefixesEllipsis()
    {
        var text = new string('字', 5) + "0123456789" + "abcdefghij"; // 25 字符，两行 × 10 装不下
        var lines = HudTextLayout.FitTail(text, 2, 100, Measure);
        Assert.InRange(lines.Length, 1, 2);
        Assert.StartsWith("…", lines[0]);
        Assert.EndsWith("abcdefghij", lines[^1]);   // 最新说的话一定在
        Assert.All(lines, l => Assert.True(Measure(l) <= 100));
        // 尽量塞满：省略号 + 后缀恰好占满两行（20 个字符位）。
        Assert.Equal(20, lines[0].Length + lines[1].Length);
    }

    [Fact]
    public void SingleLineBudget_ShowsOnlyTail()
    {
        var lines = HudTextLayout.FitTail("0123456789abcdefghij", 1, 100, Measure);
        Assert.Equal(new[] { "…bcdefghij" }, lines);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyText_ProducesNoLines(string text)
    {
        Assert.Empty(HudTextLayout.FitTail(text, 2, 100, Measure));
    }

    [Fact]
    public void VeryLongInput_IsBoundedAndStillFast()
    {
        var text = new string('x', 100_000);
        var lines = HudTextLayout.FitTail(text, 2, 200, Measure);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("…", lines[0]);
    }

    [Fact]
    public void NewlinesAreFlattened()
    {
        Assert.Equal(new[] { "你好 世界" }, HudTextLayout.FitTail("你好\n世界", 2, 200, Measure));
    }

    [Theory]
    [InlineData("", 14, "")]
    [InlineData("Built-in Mic", 14, "Built-in Mic")]
    [InlineData("Jabra Evolve2 65 立体声耳机", 14, "Jabra Evolve2…")]
    public void DeviceNameTruncation(string name, int limit, string expected)
    {
        Assert.Equal(limit, HudTextLayout.InputDeviceNameLimit);
        Assert.Equal(expected, HudTextLayout.TruncateDeviceName(name));
    }

    [Fact]
    public void LevelMapping_IsMonotonicAndClamped()
    {
        Assert.Equal(0f, HudTextLayout.LevelToBarHeight(0f));
        Assert.Equal(0f, HudTextLayout.LevelToBarHeight(float.NaN));
        Assert.Equal(0f, HudTextLayout.LevelToBarHeight(0.001f)); // −60 dBFS
        Assert.Equal(1f, HudTextLayout.LevelToBarHeight(1f));
        Assert.Equal(1f, HudTextLayout.LevelToBarHeight(5f));
        var quiet = HudTextLayout.LevelToBarHeight(0.01f);
        var loud = HudTextLayout.LevelToBarHeight(0.1f);
        Assert.InRange(quiet, 0.15f, 0.25f); // −40 dBFS
        Assert.True(loud > quiet);
    }
}

public class HudTextLayoutPerformanceTests
{
    [Fact]
    public void EachDistinctCharacterIsMeasuredOnlyOnce()
    {
        int calls = 0;
        float Measure(string s) { calls++; return s.Length * 10f; }

        var text = string.Concat(System.Linq.Enumerable.Repeat("识别结果abc123，", 40)); // 重复内容，不同字符只有十来个
        HudTextLayout.FitTail(text, 2, 300, Measure);

        // 之前是数千次；缓存后只与「不同字符数」相关。
        Assert.InRange(calls, 1, 40);
    }
}
