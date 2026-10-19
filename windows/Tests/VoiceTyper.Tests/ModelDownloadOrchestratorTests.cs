using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.App;
using VoiceTyper.Asr;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 模型下载编排：进度、完成、取消、退避自动重试与放弃。下载器、计时器与 UI 投递都由测试注入，
/// 不联网、不依赖 WinForms。文案随界面语言变化，只断言结构与关键片段。
/// </summary>
public class ModelDownloadOrchestratorTests
{
    private sealed class FakeDownloader : IModelDownloading
    {
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<double>? _progress;
        public bool CancelRequested { get; private set; }
        public bool Disposed { get; private set; }

        public Task DownloadAllAsync(Action<double> onProgress, CancellationToken ct = default)
        {
            Volatile.Write(ref _progress, onProgress);
            return Completion.Task;
        }

        public void Cancel()
        {
            CancelRequested = true;
            Completion.TrySetCanceled();
        }

        public void Dispose() => Disposed = true;

        /// <summary>下载在线程池上开跑后才拿得到进度回调。</summary>
        public void ReportProgress(double fraction)
        {
            var sw = Stopwatch.StartNew();
            Action<double>? progress;
            while ((progress = Volatile.Read(ref _progress)) is null)
            {
                if (sw.ElapsedMilliseconds > 5000) throw new TimeoutException("下载没有开跑");
                Thread.Sleep(5);
            }
            progress(fraction);
        }
    }

    private sealed class ManualTimer : IDisposable
    {
        public required TimeSpan Delay { get; init; }
        public required Action Fire { get; init; }
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Rig : IDisposable
    {
        public readonly List<FakeDownloader> Downloaders = new();
        public readonly List<ManualTimer> Timers = new();
        public readonly List<(string Reason, bool GaveUp)> Failures = new();
        public bool ShouldRetry = true;
        public int Changed, Completed, Cancelled;
        public readonly ModelDownloadOrchestrator Download;

        public Rig()
        {
            Download = new ModelDownloadOrchestrator(
                shouldRetry: () => ShouldRetry,
                create: () =>
                {
                    var downloader = new FakeDownloader();
                    Downloaders.Add(downloader);
                    return downloader;
                },
                post: action => action(),
                schedule: (delay, action) =>
                {
                    var timer = new ManualTimer { Delay = delay, Fire = action };
                    Timers.Add(timer);
                    return timer;
                })
            {
                Changed = () => Changed++,
                Completed = () => Completed++,
                Cancelled = () => Cancelled++,
                Failed = (reason, gaveUp) => Failures.Add((reason, gaveUp)),
            };
        }

        public FakeDownloader Current => Downloaders[^1];

        public async Task FailAsync(bool canRetry = true)
        {
            Current.Completion.SetException(new ModelDownloadException("boom", canRetry));
            await Download.Running!;
        }

        public void Dispose() => Download.Dispose();
    }

    [Fact]
    public async Task Success_ReportsProgress_ThenCompletes_AndReleasesDownloader()
    {
        using var rig = new Rig();
        rig.Download.Start();
        Assert.True(rig.Download.IsDownloading);
        Assert.Equal(1, rig.Changed);

        rig.Current.ReportProgress(0.5);
        Assert.Equal(0.5, rig.Download.Progress);
        Assert.Equal(2, rig.Changed);
        Assert.NotNull(rig.Download.StatusLine);

        rig.Current.Completion.SetResult();
        await rig.Download.Running!;
        Assert.Equal(1, rig.Completed);
        Assert.False(rig.Download.IsDownloading);
        Assert.True(rig.Current.Disposed);
        Assert.Null(rig.Download.StatusLine);
        Assert.Null(rig.Download.Error);
    }

    [Fact]
    public async Task RetryableFailures_BackOff_5_20_60_ThenGiveUp()
    {
        using var rig = new Rig();
        rig.Download.Start();

        var delays = new[] { 5, 20, 60 };
        for (int i = 0; i < delays.Length; i++)
        {
            await rig.FailAsync();
            Assert.Equal(("boom", false), rig.Failures[^1]);
            Assert.False(rig.Download.IsDownloading);
            Assert.Contains("boom", rig.Download.Error);
            Assert.Equal(TimeSpan.FromSeconds(delays[i]), rig.Timers[^1].Delay);

            rig.Timers[^1].Fire();
            Assert.Equal(i + 2, rig.Downloaders.Count);
            Assert.True(rig.Download.IsDownloading);
            Assert.Null(rig.Download.Error); // 重新开始时清空上一次的失败说明
        }

        await rig.FailAsync();
        Assert.Equal(("boom", true), rig.Failures[^1]);
        Assert.Equal(3, rig.Timers.Count); // 用尽后不再排重试
        Assert.NotNull(rig.Download.Details);
    }

    [Fact]
    public async Task NonRetryableFailure_GivesUpImmediately()
    {
        using var rig = new Rig();
        rig.Download.Start();
        await rig.FailAsync(canRetry: false);

        Assert.Equal(("boom", true), rig.Failures[^1]);
        Assert.Empty(rig.Timers);
        Assert.Contains("boom", rig.Download.Error);
    }

    [Fact]
    public async Task ManualStart_CancelsPendingRetry_AndResetsBackoff()
    {
        using var rig = new Rig();
        rig.Download.Start();
        await rig.FailAsync();
        var pending = rig.Timers[^1];

        rig.Download.Start(); // 用户手动点「重试下载」
        Assert.True(pending.Disposed);
        Assert.Equal(2, rig.Downloaders.Count);

        await rig.FailAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Timers[^1].Delay);
    }

    [Fact]
    public async Task Cancel_StopsInFlightDownload_AndPendingRetry()
    {
        using var rig = new Rig();
        rig.Download.Start();
        rig.Download.Cancel();
        await rig.Download.Running!;
        Assert.True(rig.Downloaders[0].CancelRequested);
        Assert.Equal(1, rig.Cancelled);
        Assert.False(rig.Download.IsDownloading);
        Assert.Empty(rig.Failures);

        rig.Download.Start();
        await rig.FailAsync();
        rig.Download.Cancel();
        Assert.True(rig.Timers[^1].Disposed);
    }

    [Fact]
    public async Task RetryTimer_DoesNothing_WhenModelIsNoLongerMissing()
    {
        using var rig = new Rig();
        rig.Download.Start();
        await rig.FailAsync();

        rig.ShouldRetry = false; // 期间用户手动放入了模型
        rig.Timers[^1].Fire();
        Assert.Single(rig.Downloaders);
        Assert.False(rig.Download.IsDownloading);
    }

    [Fact]
    public async Task LateResult_AfterDispose_IsIgnored()
    {
        var rig = new Rig();
        rig.Download.Start();
        var downloader = rig.Current;
        rig.Download.Dispose();
        Assert.True(downloader.Disposed);

        downloader.Completion.SetResult();
        await rig.Download.Running!;
        Assert.Equal(0, rig.Completed);
        Assert.False(rig.Download.IsDownloading);
    }
}
