using System;
using VoiceTyper.UI;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>HUD 预览文本排版与宽度档位（纯逻辑；用"每字符 10px"的假测量函数代替 GDI+）。</summary>
public class HudTextLayoutTests
{
    private static float Measure(string s) => s.Length * 10f;

    [Fact]
    public void ShortText_FitsOnOneLine_Untouched()
    {
        Assert.Equal(new[] { "你好世界" }, HudTextLayout.LastLines("你好世界", 2, 100, Measure));
    }

    [Fact]
    public void TextThatNeedsTwoLines_IsWrapped_WithoutEllipsis()
    {
        var lines = HudTextLayout.LastLines("一二三四五六七八九十甲乙", 2, 100, Measure);
        Assert.Equal(new[] { "一二三四五六七八九十", "甲乙" }, lines);
    }

    /// <summary>超过行数上限时只显示最后几行：最新说的话一定在，显示的是从某一行行首开始的原文后缀，不加省略号。</summary>
    [Fact]
    public void Overflow_KeepsTheLastWholeLines()
    {
        var text = new string('字', 5) + "0123456789" + "abcdefghij"; // 全文排成三行：字×5 / 数字 / 字母
        var lines = HudTextLayout.LastLines(text, 2, 100, Measure);
        Assert.Equal(new[] { "0123456789", "abcdefghij" }, lines);
    }

    /// <summary>按整行滚动：逐字增长时，显示窗口的起点只会落在行首（这里每行 10 个字），
    /// 第四行出现的同时第一行整行消失，而不是每来一个字所有行都往前挪一格。</summary>
    [Fact]
    public void Overflow_ScrollsByWholeLines()
    {
        var source = "";
        for (var i = 0; i < 50; i++) source += (char)('一' + i);
        for (var n = 1; n <= source.Length; n++)
        {
            var prefix = source[..n];
            var lines = HudTextLayout.LastLines(prefix, 3, 100, Measure);
            var shown = string.Concat(lines);
            Assert.EndsWith(shown, prefix);
            Assert.Equal(0, (prefix.Length - shown.Length) % 10);
        }
        Assert.Equal(new[] { source[10..20], source[20..30], source[30..31] }, HudTextLayout.LastLines(source[..31], 3, 100, Measure));
    }

    [Fact]
    public void NewlinesAreFlattened()
    {
        Assert.Equal(new[] { "你好 世界" }, HudTextLayout.LastLines("你好\n世界", 2, 200, Measure));
    }

    /// <summary>中英混排时英文单词整体换到下一行，而不是 "rev" / "iew" 各占一行；行首不留空格。
    /// 行拼起来必须仍是原文——尾部淡显按字符数从末尾倒数，少一个空格就会错位。</summary>
    [Fact]
    public void EnglishWordsAreNotSplit_AndLinesConcatenateToOriginal()
    {
        const string text = "今天我们 review 一下代码";
        var lines = HudTextLayout.LastLines(text, 3, 100, Measure);
        Assert.Equal(new[] { "今天我们 ", "review 一下代", "码" }, lines);
        Assert.Equal(text, string.Concat(lines));
        Assert.All(lines, l => Assert.False(l.StartsWith(' ')));
        Assert.All(lines, l => Assert.True(Measure(l.TrimEnd()) <= 100));
    }

    [Fact]
    public void PunctuationDoesNotStartALine()
    {
        var lines = HudTextLayout.LastLines("一二三四五六七八九十，好", 2, 100, Measure);
        Assert.Equal(new[] { "一二三四五六七八九", "十，好" }, lines);
    }

    /// <summary>一个词比整行还宽（长网址、长串数字）时只能逐字符断，不能卡死或溢出。</summary>
    [Fact]
    public void WordWiderThanLine_FallsBackToCharacterBreaks()
    {
        var lines = HudTextLayout.LastLines("abcdefghijklmnopqrstuvwxyz", 3, 100, Measure);
        Assert.Equal(new[] { "abcdefghij", "klmnopqrst", "uvwxyz" }, lines);
    }

    // ─── 宽度档位 ───

    [Theory]
    [InlineData(300f, 0f, 0, 400)]   // 状态行很短：起步宽度
    [InlineData(430f, 0f, 0, 430)]   // 状态行稍长：按实测放宽
    [InlineData(520f, 0f, 0, 460)]   // 再长也不超出紧凑上限，状态行自己截断
    [InlineData(350f, 380f, 0, 400)] // 预览一行放得下：仍是紧凑宽度
    [InlineData(350f, 401f, 0, 600)] // 放不下：直接跳到最宽，不经过中间档
    [InlineData(350f, 0f, 600, 600)] // 只增不减：预览变短或清空也不回缩
    public void ChooseWidth_FollowsContent_AndNeverShrinks(float status, float preview, int floor, int expected)
    {
        Assert.Equal(expected, HudTextLayout.ChooseWidth(status, preview, floor));
    }
}

public class HudTextLayoutPerformanceTests
{
}
