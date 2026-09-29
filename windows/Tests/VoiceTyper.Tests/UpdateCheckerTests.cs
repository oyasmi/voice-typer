using System;
using VoiceTyper.Services;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>版本号解析与比较的纯逻辑测试（不联网）。一旦出错，后果是"明明有新版本却说已是最新"或反过来，
/// 用户没有别的手段发现。</summary>
public class UpdateCheckerTests
{
    [Theory]
    [InlineData("3.2.1", "3,2,1")]
    [InlineData("v3.2.1", "3,2,1")]
    [InlineData("windows-3.2.1-beta", "3,2,1")]
    [InlineData("v10", "10")]
    [InlineData("0.0.0-dev", "0,0,0")]
    [InlineData("3.2.1 (build 4567)", "3,2,1")]
    [InlineData("3..2", "3")]
    public void ParsesCommonTagShapes(string raw, string expected)
    {
        Assert.Equal(expected, string.Join(",", UpdateChecker.VersionComponents(raw)!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("beta")]
    [InlineData("v.")]
    public void ReturnsNullWhenNoDigitsPresent(string raw)
    {
        Assert.Null(UpdateChecker.VersionComponents(raw));
    }

    [Fact]
    public void ComparisonTreatsMissingComponentsAsZero()
    {
        Assert.Equal(0, UpdateChecker.Compare(new[] { 3, 2 }, new[] { 3, 2, 0 }));
        Assert.True(UpdateChecker.Compare(new[] { 3, 2, 1 }, new[] { 3, 2, 0 }) > 0);
        Assert.True(UpdateChecker.Compare(new[] { 3, 2, 0 }, new[] { 3, 2, 1 }) < 0);
        Assert.True(UpdateChecker.Compare(new[] { 3, 10, 0 }, new[] { 3, 9, 9 }) > 0);
        Assert.True(UpdateChecker.Compare(new[] { 4 }, new[] { 3, 99, 99 }) > 0);
    }

    [Fact]
    public void ParseRelease_DetectsNewerVersion()
    {
        var outcome = UpdateChecker.ParseRelease(
            """{"tag_name":"v3.6.0","html_url":"https://github.com/oyasmi/voice-typer/releases/tag/v3.6.0"}""", "3.5.0");
        var available = Assert.IsType<UpdateChecker.Outcome.UpdateAvailable>(outcome);
        Assert.Equal("v3.6.0", available.Release.Version);
        Assert.EndsWith("v3.6.0", available.Release.PageUrl);
    }

    [Fact]
    public void ParseRelease_SameOrOlderIsUpToDate()
    {
        Assert.IsType<UpdateChecker.Outcome.UpToDate>(UpdateChecker.ParseRelease("""{"tag_name":"v3.5.0"}""", "3.5.0"));
        Assert.IsType<UpdateChecker.Outcome.UpToDate>(UpdateChecker.ParseRelease("""{"tag_name":"v3.4.0"}""", "3.5.0"));
    }

    [Fact]
    public void ParseRelease_UnparseableTag_ReturnsIndeterminateWithPageFallback()
    {
        var outcome = UpdateChecker.ParseRelease("""{"tag_name":"nightly"}""", "3.5.0");
        var indeterminate = Assert.IsType<UpdateChecker.Outcome.Indeterminate>(outcome);
        Assert.Equal("https://github.com/oyasmi/voice-typer/releases", indeterminate.PageUrl);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    public void ParseRelease_RejectsGarbage(string body)
    {
        Assert.Throws<InvalidOperationException>(() => UpdateChecker.ParseRelease(body, "3.5.0"));
    }
}
