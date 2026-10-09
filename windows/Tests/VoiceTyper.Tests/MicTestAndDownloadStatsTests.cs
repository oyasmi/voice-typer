using System;
using System.Collections.Generic;
using System.Diagnostics;
using VoiceTyper.Core;
using VoiceTyper.Services;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>2026-10 第三批：下载速度 / 剩余时间的文字，以及「测试麦克风」的会话逻辑（不依赖 WASAPI 与 UI）。</summary>
public class MicTestAndDownloadStatsTests
{
    private static long Seconds(double s) => (long)(s * Stopwatch.Frequency);

    // ─── 下载速度与剩余时间 ──────────────────────────────────────

    [Fact]
    public void Speed_IsDeltaOverElapsed()
    {
        var tracker = new DownloadSpeedTracker();
        tracker.Add(Seconds(0), 0.0);
        tracker.Add(Seconds(2), 0.1);
        // 100MB 总量，2 秒涨了 10%：5MB/s。
        Assert.Equal(5_000_000, tracker.BytesPerSecond(100_000_000)!.Value, 1);
    }

    [Fact]
    public void Speed_IsUnknownUntilOneSecondOfData()
    {
        var tracker = new DownloadSpeedTracker();
        Assert.Null(tracker.BytesPerSecond(100_000_000));
        tracker.Add(Seconds(0), 0.0);
        Assert.Null(tracker.BytesPerSecond(100_000_000)); // 只有一个样本
        tracker.Add(Seconds(0.5), 0.05);
        Assert.Null(tracker.BytesPerSecond(100_000_000)); // 不足 1 秒
    }

    [Fact]
    public void Speed_IsUnknownWhenNothingProgressed()
    {
        var tracker = new DownloadSpeedTracker();
        tracker.Add(Seconds(0), 0.3);
        tracker.Add(Seconds(3), 0.3);
        Assert.Null(tracker.BytesPerSecond(100_000_000));
    }

    [Fact]
    public void Speed_OnlyLooksAtTheRecentWindow()
    {
        var tracker = new DownloadSpeedTracker();
        tracker.Add(Seconds(0), 0.0);
        tracker.Add(Seconds(1), 0.5);   // 很快的一段
        tracker.Add(Seconds(20), 0.5);  // 之后停滞
        tracker.Add(Seconds(22), 0.52);
        // 窗口内只剩最近的样本：约 2% / 2 秒，而不是被最初那一段拉高。
        var speed = tracker.BytesPerSecond(100_000_000)!.Value;
        Assert.InRange(speed, 0.9e6, 1.1e6);
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        var tracker = new DownloadSpeedTracker();
        tracker.Add(Seconds(0), 0.0);
        tracker.Add(Seconds(2), 0.2);
        tracker.Reset();
        Assert.Null(tracker.BytesPerSecond(100_000_000));
    }

    [Theory]
    [InlineData(11.2, "12 秒")]
    [InlineData(0, "0 秒")]
    [InlineData(59.1, "1 分 0 秒")]
    [InlineData(125, "2 分 5 秒")]
    [InlineData(3600, "超过 1 小时")]
    public void Remaining_IsHumanReadable(double seconds, string expected)
    {
        Assert.Equal(expected, DownloadStatusText.FormatRemaining(seconds));
    }

    [Fact]
    public void Format_WithoutSpeed_OmitsRateAndEta()
    {
        var text = DownloadStatusText.Format(0.5, 200_000_000, null);
        Assert.Equal("已下载 100.0 / 200.0 MB · 50%", text);
    }

    [Fact]
    public void Format_WithSpeed_AppendsRateAndEta()
    {
        // 还剩 100MB，4MB/s：25 秒。
        var text = DownloadStatusText.Format(0.5, 200_000_000, 4_000_000);
        Assert.Equal("已下载 100.0 / 200.0 MB · 50% · 4.0 MB/s，约剩 25 秒", text);
    }

    // ─── 测试麦克风 ──────────────────────────────────────────────

    private sealed class FakeCapture : IAudioCapturing
    {
        public Action<byte[]>? OnChunk { get; set; }
        public Action<byte[]>? OnTailChunk { get; set; }
        public Action? OnDeviceChanged { get; set; }
        public Action<float>? OnLevel { get; set; }
        public ActiveInputDevice? ActiveDevice => null;
        public AudioInputPolicy? Policy;
        public Action<AudioStartResult>? PendingStart;
        public int StopWithoutResultCount;
        public bool Disposed;

        public void PrepareInput(AudioInputPolicy policy) { }
        public void BeginStart(AudioInputPolicy policy, Action<AudioStartResult> completed)
        {
            Policy = policy;
            PendingStart = completed;
        }
        public void Stop() { }
        public void StopWithoutResult() => StopWithoutResultCount++;
        public void Dispose() => Disposed = true;
    }

    private static (MicLevelTest Test, FakeCapture Capture, List<float> Levels, List<string> Stops) Rig()
    {
        var capture = new FakeCapture();
        var test = new MicLevelTest(() => capture, post: action => action());
        return (test, capture, new List<float>(), new List<string>());
    }

    [Fact]
    public void Start_ForwardsLevels_AndOpensTheRequestedDevice()
    {
        var (test, capture, levels, stops) = Rig();
        test.Start(AudioInputPolicy.FromConfigValue("device-1"), levels.Add, stops.Add);
        Assert.True(test.IsRunning);
        Assert.Equal(new AudioInputPolicy.Device("device-1"), capture.Policy);

        capture.PendingStart!(new AudioStartResult.Started(new AudioStartTimings()));
        capture.OnLevel!(0.1f);
        capture.OnLevel!(0.2f);

        Assert.Equal(new[] { 0.1f, 0.2f }, levels);
        Assert.Empty(stops);
    }

    [Fact]
    public void Stop_ClosesCapture_IgnoresLateLevels_AndDoesNotNotify()
    {
        var (test, capture, levels, stops) = Rig();
        test.Start(new AudioInputPolicy.Automatic(), levels.Add, stops.Add);
        capture.PendingStart!(new AudioStartResult.Started(new AudioStartTimings()));

        test.Stop();

        Assert.False(test.IsRunning);
        Assert.Equal(1, capture.StopWithoutResultCount);
        capture.OnLevel!(0.5f); // 停止之后才到的电平
        Assert.Empty(levels);
        Assert.Empty(stops);
    }

    [Fact]
    public void StartFailure_NotifiesWithTheReason_AndStops()
    {
        var (test, capture, levels, stops) = Rig();
        test.Start(new AudioInputPolicy.Automatic(), levels.Add, stops.Add);

        capture.PendingStart!(new AudioStartResult.Failed(
            new AudioStartException("没有可用的麦克风", AudioStartFailureKind.NoDevice)));

        Assert.False(test.IsRunning);
        Assert.Equal(new[] { "没有可用的麦克风" }, stops);
        Assert.Equal(1, capture.StopWithoutResultCount);
    }

    [Fact]
    public void DeviceChangedMidTest_StopsAndNotifiesOnce()
    {
        var (test, capture, levels, stops) = Rig();
        test.Start(new AudioInputPolicy.Automatic(), levels.Add, stops.Add);
        capture.PendingStart!(new AudioStartResult.Started(new AudioStartTimings()));

        capture.OnDeviceChanged!();
        capture.OnDeviceChanged!(); // 重复通知不应再触发

        Assert.False(test.IsRunning);
        Assert.Single(stops);
    }

    [Fact]
    public void RestartingInvalidatesTheOldRun()
    {
        var (test, capture, levels, stops) = Rig();
        test.Start(new AudioInputPolicy.Automatic(), levels.Add, stops.Add);
        var firstStart = capture.PendingStart!;

        test.Start(new AudioInputPolicy.SystemDefault(), levels.Add, stops.Add);
        // 第一次启动的迟到结果（失败）属于旧一代，不能把新一次测试结束掉。
        firstStart(new AudioStartResult.Failed(new AudioStartException("旧的失败", AudioStartFailureKind.DeviceFailure)));

        Assert.True(test.IsRunning);
        Assert.Empty(stops);
    }

    [Fact]
    public void Dispose_ReleasesTheCapture()
    {
        var (test, capture, levels, stops) = Rig();
        test.Start(new AudioInputPolicy.Automatic(), levels.Add, stops.Add);
        test.Dispose();
        Assert.True(capture.Disposed);
    }
}
