using System;
using System.Linq;
using System.Threading;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using VoiceTyper.Support;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>2026-10「跟嘴 · 跟手 · 稳健」批次（DESIGN §23）里可以脱离 Win32 / UI 线程验证的部分。</summary>
public class PerformanceBatchTests
{
    private static byte[] Speech(int samples, float value = 0.1f)
    {
        var data = new float[samples];
        Array.Fill(data, value);
        var bytes = new byte[samples * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) Thread.Sleep(10);
        return condition();
    }

    // ─── A1：预览调度 ────────────────────────────────────────────

    [Fact]
    public void Preview_WaitsForAtLeast300msOfNewAudio_ThenRuns()
    {
        var pump = new AsrPump("VoiceTyper.Test.GrowthPump");
        try
        {
            var fake = new FakeAsrEngine();
            var session = new LocalAsrSession(pump, () => fake, null, 15 * AppConstants.TargetSampleRate);

            session.SendAudio(Speech(AppConstants.ChunkSamples)); // 200ms：不够 300ms
            Thread.Sleep(200);
            Assert.Equal(0, fake.RecognizeCalls);

            session.SendAudio(Speech(AppConstants.ChunkSamples)); // 累计 400ms
            Assert.True(WaitFor(() => fake.RecognizeCalls >= 1), "累计超过 300ms 后应触发预览");
            session.Close();
        }
        finally { pump.Dispose(); }
    }

    [Fact]
    public void CaptureChunk_IsTwoHundredMilliseconds()
    {
        Assert.Equal(AppConstants.TargetSampleRate / 5, AppConstants.ChunkSamples);
    }

    // ─── A3：松键时中止在跑的预览 ────────────────────────────────

    [Fact]
    public void FinalizeWhilePreviewInFlight_AbortsPreview_AndResetsBeforeFinalRun()
    {
        var pump = new AsrPump("VoiceTyper.Test.AbortPump");
        var gate = new ManualResetEventSlim(false);
        try
        {
            var fake = new FakeAsrEngine { Gate = gate };
            var session = new LocalAsrSession(pump, () => fake, null, 15 * AppConstants.TargetSampleRate);

            session.SendAudio(Speech(AppConstants.ChunkSamples));
            session.SendAudio(Speech(AppConstants.ChunkSamples));
            Assert.True(WaitFor(() => fake.RecognizeCalls == 1), "预览应已开始并阻塞在引擎里");

            session.FinalizeStream(TimeSpan.Zero);
            Assert.Equal(1, fake.AbortCalls);
            Assert.True(session.Timings.PreviewAborted);
            Assert.True(session.Timings.TailSpeech);

            gate.Set(); // 预览结束，终稿接着跑
            Assert.True(WaitFor(() => fake.RecognizeCalls >= 2), "终稿应在预览之后执行");
            // 预览闭包开始时清一次、终稿闭包开始时再清一次：终止标志不会带进终稿。
            Assert.True(WaitFor(() => fake.ResetAbortCalls >= 2));
            Assert.True(WaitFor(() => session.Timings.FinalizeRunStartedAt is not null));
            session.Close();
        }
        finally
        {
            gate.Set();
            pump.Dispose();
        }
    }

    [Fact]
    public void FinalizeWithoutPreviewInFlight_DoesNotAbort()
    {
        var pump = new AsrPump("VoiceTyper.Test.NoAbortPump");
        try
        {
            var fake = new FakeAsrEngine();
            var session = new LocalAsrSession(pump, () => fake, null, 15 * AppConstants.TargetSampleRate);
            session.SendAudio(Speech(AppConstants.ChunkSamples, 0f)); // 静音：不会触发预览
            session.FinalizeStream(TimeSpan.Zero);

            Assert.Equal(0, fake.AbortCalls);
            Assert.False(session.Timings.PreviewAborted);
            Assert.False(session.Timings.TailSpeech);
            Assert.True(WaitFor(() => session.Timings.FinalizeRunStartedAt is not null));
            session.Close();
        }
        finally { pump.Dispose(); }
    }

    // ─── A2：预览窗口按耗时预算 ──────────────────────────────────

    [Theory]
    [InlineData(0.012, 15)]  // 快机器：上限
    [InlineData(0.02, 15)]
    [InlineData(0.03, 10)]
    [InlineData(0.05, 6)]
    [InlineData(0.15, 6)]    // 慢机器：下限
    [InlineData(1.0, 6)]
    [InlineData(0.0, 15)]    // 测不出：取上限
    [InlineData(-1.0, 15)]
    public void PreviewWindow_FollowsBudgetDividedByRtf(double rtf, int expectedSeconds)
    {
        Assert.Equal(expectedSeconds, AsrService.ChoosePreviewWindowSeconds(rtf));
    }

    // ─── 耗时摘要的新字段 ────────────────────────────────────────

    [Fact]
    public void SummaryLine_ContainsNewFields_WithDashesWhenUnknown()
    {
        var line = new DictationMetrics().SummaryLine();
        Assert.Contains("final_wait=-", line);
        Assert.Contains("backup_wait=0", line);
        Assert.Contains("preview_avg=-", line);
        Assert.Contains("preview_abort=0", line);
        Assert.Contains("tail_speech=-", line);
        Assert.Contains("qos=-", line);
    }

    [Fact]
    public void SummaryLine_ReportsNewFields()
    {
        var ms = System.Diagnostics.Stopwatch.Frequency / 1000;
        var metrics = new DictationMetrics { EcoQosOptedOut = true };
        metrics.Timings.FinalizeStartedAt = 100 * ms;
        metrics.Timings.FinalizeRunStartedAt = 130 * ms;
        metrics.Timings.PreviewRuns = 2;
        metrics.Timings.PreviewTotalTicks = 600 * ms;
        metrics.Timings.PreviewAborted = true;
        metrics.Timings.TailSpeech = true;
        var line = metrics.SummaryLine();
        Assert.Contains("final_wait=30", line);
        Assert.Contains("preview_avg=300", line);
        Assert.Contains("preview_abort=1", line);
        Assert.Contains("tail_speech=1", line);
        Assert.Contains("qos=1", line);
    }

    [Fact]
    public void BriefLine_IsShortAndTextFree()
    {
        var ms = System.Diagnostics.Stopwatch.Frequency / 1000;
        var metrics = new DictationMetrics
        {
            Outcome = DictationOutcome.Inserted,
            PressedAt = 0,
            HudReadyAt = 420 * ms,
            ReleasedAt = 3000 * ms,
            DoneAt = 3350 * ms,
        };
        metrics.Timings.ReceivedSamples = 16000 * 3;
        var line = metrics.BriefLine();
        Assert.Contains("inserted", line);
        Assert.Contains("audio=3.0s", line);
        Assert.Contains("ready=420", line);
        Assert.Contains("done=350", line);
        Assert.Contains("preview=-", line);
    }

    // ─── D3：最近听写与诊断信息 ──────────────────────────────────

    [Fact]
    public void History_KeepsNewest20_NewestFirst()
    {
        var history = new DictationHistory();
        for (int i = 0; i < DictationHistory.Capacity + 5; i++)
        {
            history.Add(new DateTime(2026, 10, 9, 12, 0, i % 60), new DictationMetrics { SessionId = (ushort)i, Outcome = DictationOutcome.Inserted });
        }
        var lines = history.NewestFirst();
        Assert.Equal(DictationHistory.Capacity, lines.Count);
        Assert.StartsWith("12:00:24", lines[0]); // 第 25 次（i = 24）最新
        Assert.StartsWith("12:00:05", lines[^1]); // 最早的 5 次被挤掉
    }

    [Fact]
    public void DiagnosticsReport_ListsFactsAndRecentLines()
    {
        var report = DiagnosticsReport.Build(
            new[] { ("version", "3.5.1"), ("asr_rtf", "0.021") },
            new[] { "12:00:01  inserted", "11:59:50  empty" });
        Assert.Contains("version: 3.5.1", report);
        Assert.Contains("asr_rtf: 0.021", report);
        var recent = report[report.IndexOf("recent dictations", StringComparison.Ordinal)..];
        Assert.True(recent.IndexOf("12:00:01", StringComparison.Ordinal) < recent.IndexOf("11:59:50", StringComparison.Ordinal));
    }

    [Fact]
    public void DiagnosticsReport_EmptyHistory_SaysSo()
    {
        Assert.Contains("(none)", DiagnosticsReport.Build(Array.Empty<(string, string)>(), Array.Empty<string>()));
    }
}
