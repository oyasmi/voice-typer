using VoiceTyper.Core;
using Xunit;

namespace VoiceTyper.Tests;

public class RecognitionHistoryTests
{
    [Fact]
    public void EmptyHistory_HasNoCopyableText()
    {
        var history = new RecognitionHistory();
        Assert.Null(history.LatestText);
        Assert.Empty(history.NewestFirst());
        Assert.Equal("", history.AllText());
    }

    [Fact]
    public void Overflow_KeepsLastTwenty_NewestFirst_ButCopiesChronologically()
    {
        var history = new RecognitionHistory();
        for (var i = 1; i <= 25; i++) history.Add(DateTime.Today.AddSeconds(i), $"第{i}段");
        Assert.Equal("第25段", history.LatestText);
        Assert.Equal(Enumerable.Range(6, 20).Reverse().Select(i => $"第{i}段"), history.NewestFirst().Select(e => e.Text));
        Assert.Equal(string.Join("\r\n", Enumerable.Range(6, 20).Select(i => $"第{i}段")), history.AllText());
    }

    [Fact]
    public void EmptyResults_DoNotEvictExistingEntries_AndRepeatedSpeechIsKept()
    {
        var history = new RecognitionHistory();
        history.Add(DateTime.Now, "重复");
        history.Add(DateTime.Now, "重复");
        for (var i = 0; i < 30; i++) history.Add(DateTime.Now, i % 2 == 0 ? "" : " \r\n\t");
        Assert.Equal(2, history.NewestFirst().Count);
        Assert.Equal("重复\r\n重复", history.AllText());
    }

    [Fact]
    public void CopiesExactText_IncludingMultilineContent_WithoutTimestamps()
    {
        var history = new RecognitionHistory();
        history.Add(DateTime.Now, "  第一行\r\n第二行。  ");
        Assert.Equal("  第一行\r\n第二行。  ", history.AllText());
        Assert.Equal(history.LatestText, history.AllText());
        history.Add(DateTime.Now, "中文与 Python，🙂");
        Assert.Equal("  第一行\r\n第二行。  \r\n中文与 Python，🙂", history.AllText());
    }

    [Fact]
    public void SnapshotIsStable_AndClockChangesDoNotReorderSpeech()
    {
        var history = new RecognitionHistory();
        history.Add(DateTime.Today.AddHours(1), "先说");
        var snapshot = history.NewestFirst();
        history.Add(DateTime.Today, "后说");
        Assert.Single(snapshot);
        Assert.Equal(new[] { "后说", "先说" }, history.NewestFirst().Select(e => e.Text));
        Assert.Equal("先说\r\n后说", history.AllText());
    }
}
