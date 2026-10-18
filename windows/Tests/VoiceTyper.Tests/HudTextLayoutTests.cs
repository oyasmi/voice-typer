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
    public void NewlinesAreFlattened()
    {
        Assert.Equal(new[] { "你好 世界" }, HudTextLayout.FitTail("你好\n世界", 2, 200, Measure));
    }

    /// <summary>中英混排时英文单词整体换到下一行，而不是 "rev" / "iew" 各占一行；行首不留空格。
    /// 行拼起来必须仍是原文——尾部淡显按字符数从末尾倒数，少一个空格就会错位。</summary>
    [Fact]
    public void EnglishWordsAreNotSplit_AndLinesConcatenateToOriginal()
    {
        const string text = "今天我们 review 一下代码";
        var lines = HudTextLayout.FitTail(text, 3, 100, Measure);
        Assert.Equal(new[] { "今天我们 ", "review 一下代", "码" }, lines);
        Assert.Equal(text, string.Concat(lines));
        Assert.All(lines, l => Assert.False(l.StartsWith(' ')));
        Assert.All(lines, l => Assert.True(Measure(l.TrimEnd()) <= 100));
    }

    [Fact]
    public void PunctuationDoesNotStartALine()
    {
        var lines = HudTextLayout.FitTail("一二三四五六七八九十，好", 2, 100, Measure);
        Assert.Equal(new[] { "一二三四五六七八九", "十，好" }, lines);
    }

    /// <summary>一个词比整行还宽（长网址、长串数字）时只能逐字符断，不能卡死或溢出。</summary>
    [Fact]
    public void WordWiderThanLine_FallsBackToCharacterBreaks()
    {
        var lines = HudTextLayout.FitTail("abcdefghijklmnopqrstuvwxyz", 3, 100, Measure);
        Assert.Equal(new[] { "abcdefghij", "klmnopqrst", "uvwxyz" }, lines);
    }

}

public class HudTextLayoutPerformanceTests
{
}
