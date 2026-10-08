using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using VoiceTyper.Core;
using VoiceTyper.Services;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>耗时摘要（对应 macOS <c>DictationMetricsTests</c>）。</summary>
public class DictationMetricsTests
{
    private static long Ms(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000.0);

    /// <summary>摘要会原样写进日志，因此这两个类型绝不能长出承载用户文本的字段。用反射钉死：
    /// 不存在 string 类型的公开字段 / 属性（枚举、数字、布尔才允许）。</summary>
    [Theory]
    [InlineData(typeof(DictationMetrics))]
    [InlineData(typeof(AsrSessionTimings))]
    [InlineData(typeof(AudioStartTimings))]
    public void HasNoStringCarryingMembers(Type type)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var stringFields = type.GetFields(flags).Where(f => f.FieldType == typeof(string)).Select(f => f.Name)
            .Concat(type.GetProperties(flags).Where(p => p.PropertyType == typeof(string)).Select(p => p.Name))
            .ToList();
        Assert.Empty(stringFields);
    }

    [Fact]
    public void SummaryLine_HasStableFieldOrder_AndDashForMissingValues()
    {
        var metrics = new DictationMetrics { SessionId = 0xAB, PressedAt = 0, Mode = HotkeyMode.Toggle, Hotkey = HotkeyKind.Modifier };
        var line = metrics.SummaryLine();
        Assert.StartsWith("dictation session=00ab outcome=failed mode=toggle hotkey=modifier input=- ", line);
        Assert.Contains("capture_start=- first_buffer=- key_lag=- dispatch=- start_queue=- dev_resolve=- dev_cached=- "
            + "activate=- init=- hud_shown=- hud_ready=- audio=0.0s", line);
        Assert.Contains("llm=- llm_result=- llm_retry=-", line);
        Assert.EndsWith("cold=0", line);
    }

    [Fact]
    public void SummaryLine_ComputesIntervals()
    {
        var metrics = new DictationMetrics
        {
            SessionId = 1,
            PressedAt = Ms(0),
            CaptureStartedAt = Ms(120),
            FirstBufferAt = Ms(180),
            ReleasedAt = Ms(3000),
            FinalizeCalledAt = Ms(3010),
            DoneAt = Ms(3600),
            InsertTicks = Ms(45),
            Outcome = DictationOutcome.Inserted,
            InputTransport = AudioTransport.Bluetooth,
            InputSwitchedByAuto = true,
            HookAt = Ms(-4),
            KeyEventLagMs = 2,
            AudioStart = new AudioStartTimings { QueueTicks = Ms(1), ResolveTicks = Ms(8), ActivateTicks = Ms(30), InitTicks = Ms(70), DeviceCached = true },
            HudShownAt = Ms(16),
            HudReadyAt = Ms(190),
        };
        metrics.Timings.ReceivedSamples = 16000 * 3;
        metrics.Timings.FinalizeStartedAt = Ms(3010);
        metrics.Timings.AsrCompletedAt = Ms(3410);
        metrics.Timings.LlmStartedAt = Ms(3410);
        metrics.Timings.LlmCompletedAt = Ms(3560);
        metrics.Timings.LlmResult = AsrSessionTimings.LlmOutcome.Corrected;
        metrics.Timings.LlmRetriedWithoutThinking = true;
        metrics.Timings.PreviewRuns = 4;
        metrics.Timings.PreviewSkipped = 2;
        metrics.Timings.PreviewMaxTicks = Ms(310);
        metrics.Timings.ColdAtStart = true;

        var line = metrics.SummaryLine();
        foreach (var expected in new[]
        {
            "outcome=inserted", "input=bluetooth*", "capture_start=120", "first_buffer=180", "audio=3.0s",
            "key_lag=2", "dispatch=4", "start_queue=1", "dev_resolve=8", "dev_cached=1", "activate=30", "init=70",
            "hud_shown=16", "hud_ready=190",
            "release_to_finalize=10", "engine_wait=0", "asr=400", "llm=150", "llm_result=corrected", "llm_retry=1",
            "insert=45", "release_to_done=600", "previews=4", "previews_skipped=2", "preview_max=310", "cold=1",
        })
        {
            Assert.Contains(expected, line);
        }
    }

    [Fact]
    public void SummaryLine_NeverContainsAnythingResemblingText()
    {
        var line = new DictationMetrics { SessionId = 7 }.SummaryLine();
        Assert.All(line, ch => Assert.True(ch < 128, "摘要只应含 ASCII 数字与枚举"));
    }
}
