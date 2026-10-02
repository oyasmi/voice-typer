using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Asr;
using Xunit;

namespace VoiceTyper.Tests;

public class AsrPumpTests
{
    /// <summary>退出时线程仍被长任务占住：Dispose 约 2 秒内返回，不抛异常，且不释放工作线程仍在用的队列。</summary>
    [Fact]
    public void Dispose_WithBusyWorker_ReturnsPromptlyAndDoesNotThrow()
    {
        var pump = new AsrPump("VoiceTyper.Test.BusyPump");
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        pump.Post(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

        var sw = Stopwatch.StartNew();
        pump.Dispose();
        sw.Stop();
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.5, 4.0);

        // 释放工作线程：它回到 GetConsumingEnumerable 时队列必须仍然可用，不能因 ObjectDisposedException 崩溃。
        release.Set();
        Thread.Sleep(200);
    }

    [Fact]
    public async Task PostAfterDispose_DoesNotThrow_AndAsyncPostIsCancelled()
    {
        var pump = new AsrPump("VoiceTyper.Test.ClosedPump");
        pump.Dispose();

        pump.Post(() => { });
        await Assert.ThrowsAsync<TaskCanceledException>(() => pump.PostAsync(() => { }));
        await Assert.ThrowsAsync<TaskCanceledException>(() => pump.PostAsync(() => 1));
    }
}
