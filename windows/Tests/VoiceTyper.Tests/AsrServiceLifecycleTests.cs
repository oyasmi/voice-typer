using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using VoiceTyper.Support;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// <see cref="AsrService"/> 的加载 / 重载 / 空闲卸载 / 会话租约生命周期。定位模型、构建引擎与空闲计时
/// 都由测试注入，不加载 ONNX、不依赖 WinForms 计时器。
/// </summary>
public class AsrServiceLifecycleTests
{
    private sealed class ManualTimer : IDisposable
    {
        public required Action Fire { get; init; }
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Rig : IDisposable
    {
        public ModelBundle? Bundle = MakeBundle();
        public readonly List<FakeAsrEngine> Engines = new();
        public readonly List<int> BuildThreads = new();
        public readonly List<ManualTimer> Timers = new();
        public Func<FakeAsrEngine>? OnBuild;
        public int LocateCalls;
        public readonly AsrPump Pump = new("VoiceTyper.Test.AsrServicePump");
        public readonly AsrService Service;

        public Rig()
        {
            Service = new AsrService(
                locate: _ => { Interlocked.Increment(ref LocateCalls); return Bundle; },
                build: (_, _, threads) =>
                {
                    lock (BuildThreads) BuildThreads.Add(threads);
                    var engine = OnBuild?.Invoke() ?? new FakeAsrEngine();
                    lock (Engines) Engines.Add(engine);
                    return engine;
                },
                schedule: (_, action) =>
                {
                    var timer = new ManualTimer { Fire = action };
                    Timers.Add(timer);
                    return timer;
                },
                pump: Pump);
        }

        public int BuildCount { get { lock (BuildThreads) return BuildThreads.Count; } }

        /// <summary>FIFO 屏障：等到此前已入队的 pump 任务全部执行完。</summary>
        public Task DrainAsync() => Pump.PostAsync(() => { });

        public void Dispose() => Service.Dispose();
    }

    private static ModelBundle MakeBundle() => new()
    {
        ModelDir = "model",
        OnnxFilePath = "model/model.onnx",
        TokensPath = "model/tokens.json",
        CmvnPath = "model/am.mvn",
        Quantized = false,
        FbankOptions = new FbankOptions(),
        LfrM = 7,
        LfrN = 6,
    };

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("等待条件超时");
            await Task.Delay(5);
        }
    }

    // ─── T1-1：加载任务占位 ────────────────────────────────────

    [Fact]
    public async Task ReloadAfterModelMissing_BuildsExactlyOnce_AndReachesReady()
    {
        using var rig = new Rig { Bundle = null };
        await rig.Service.PreloadAsync();
        Assert.Equal(AsrState.ModelMissing, rig.Service.State);
        Assert.Equal(0, rig.BuildCount);

        rig.Bundle = MakeBundle(); // 模拟下载完成
        await rig.Service.ReloadAsync();
        Assert.Equal(1, rig.BuildCount);
        Assert.Equal(AsrState.Ready, rig.Service.State);
    }

    [Fact]
    public async Task RepeatedReload_AfterModelMissing_StillWorks()
    {
        // 回归：同步完成的加载曾把已完成的 Task 留在 _inFlightLoad，之后每次重载都无效。
        using var rig = new Rig { Bundle = null };
        await rig.Service.PreloadAsync();
        await rig.Service.ReloadAsync();
        await rig.Service.ReloadAsync();
        Assert.Equal(AsrState.ModelMissing, rig.Service.State);
        Assert.True(rig.LocateCalls >= 3, "每次重载都应真正重新定位模型");
    }

    [Fact]
    public async Task BuildFailure_ClearsInFlight_AndNextReloadRetries()
    {
        using var rig = new Rig();
        var fail = true;
        rig.OnBuild = () => fail ? throw new InvalidOperationException("boom") : new FakeAsrEngine();

        await rig.Service.PreloadAsync();
        Assert.Equal(AsrState.Failed, rig.Service.State);

        fail = false;
        await rig.Service.ReloadAsync();
        Assert.Equal(AsrState.Ready, rig.Service.State);
        Assert.Equal(2, rig.BuildCount);
    }

    [Fact]
    public async Task PreloadWhileLoading_SharesTheInFlightLoad()
    {
        using var rig = new Rig();
        using var gate = new ManualResetEventSlim(false);
        rig.OnBuild = () => { gate.Wait(); return new FakeAsrEngine(); };

        var first = rig.Service.PreloadAsync();
        var second = rig.Service.PreloadAsync();
        var third = rig.Service.PreloadAsync();
        gate.Set();
        await Task.WhenAll(first, second, third);

        Assert.Equal(1, rig.BuildCount);
        Assert.Equal(AsrState.Ready, rig.Service.State);
    }

    [Fact]
    public async Task ConfigChangeDuringLoad_RebuildsOnceWithLatestThreads()
    {
        using var rig = new Rig();
        using var gate = new ManualResetEventSlim(false);
        rig.OnBuild = () => { gate.Wait(); return new FakeAsrEngine(); };

        var load = rig.Service.PreloadAsync();
        rig.Service.UpdateConfig(new AsrConfig { Threads = 2 });
        rig.Service.UpdateConfig(new AsrConfig { Threads = 2 }); // 重复请求只合并成一次重载
        gate.Set();
        await load; // 合并重载完成后才完成

        Assert.Equal(new[] { 0, 2 }, rig.BuildThreads);
        Assert.Equal(1, rig.Engines[0].DisposeCount);
        Assert.Equal(AsrState.Ready, rig.Service.State);
    }

    [Fact]
    public async Task StateCallbackReentrancy_DoesNotStartSecondLoad()
    {
        using var rig = new Rig { Bundle = null };
        rig.Service.OnStateChange = state =>
        {
            if (state == AsrState.ModelMissing) _ = rig.Service.PreloadAsync();
        };
        await rig.Service.PreloadAsync();
        Assert.Equal(1, rig.LocateCalls);
    }

    // ─── T1-2：会话租约 ────────────────────────────────────────

    private static async Task<Rig> ReadyRigWithIdleUnload()
    {
        var rig = new Rig();
        rig.Service.UpdateConfig(new AsrConfig { IdleUnloadMinutes = 1 });
        await rig.Service.PreloadAsync();
        await rig.DrainAsync();
        Assert.Equal(AsrState.Ready, rig.Service.State);
        return rig;
    }

    [Fact]
    public async Task IdleUnload_SkippedWhileSessionAlive()
    {
        using var rig = await ReadyRigWithIdleUnload();
        var staleTimer = rig.Timers[^1]; // 计时器已入队的"迟到"回调
        var session = rig.Service.MakeSession(null);

        staleTimer.Fire();
        await rig.DrainAsync();
        await rig.DrainAsync();

        Assert.Equal(0, rig.Engines[0].DisposeCount);
        Assert.NotNull(rig.Service.CurrentEngine());
        session.Close();
    }

    [Fact]
    public async Task IdleUnload_RunsAfterSessionEnds()
    {
        using var rig = await ReadyRigWithIdleUnload();
        var session = rig.Service.MakeSession(null);
        session.Close();
        rig.Service.SessionEnded();
        var timer = rig.Timers[^1];
        Assert.False(timer.Disposed);

        timer.Fire();
        await WaitUntil(() => rig.Service.State == AsrState.SuspendedForIdle);
        Assert.Equal(1, rig.Engines[0].DisposeCount);
    }

    [Fact]
    public async Task UnloadClosureQueuedBeforeNewSession_BacksOff()
    {
        using var rig = await ReadyRigWithIdleUnload();
        var staleTimer = rig.Timers[^1];
        using var gate = new ManualResetEventSlim(false);
        rig.Pump.Post(() => gate.Wait()); // 占住 pump：卸载闭包只能排在后面

        staleTimer.Fire();
        var session = rig.Service.MakeSession(null); // 卸载闭包入队之后才开始新会话
        gate.Set();
        await rig.DrainAsync();

        Assert.Equal(0, rig.Engines[0].DisposeCount);
        Assert.Equal(AsrState.Ready, rig.Service.State);
        session.Close();
    }

    [Fact]
    public async Task SessionClose_IsIdempotent_AndReleasesLeaseOnce()
    {
        using var rig = await ReadyRigWithIdleUnload();
        var first = rig.Service.MakeSession(null);
        first.Close();
        first.Close(); // 第二次不得再减计数

        var second = rig.Service.MakeSession(null);
        var staleTimer = rig.Timers[^1];
        staleTimer.Fire();
        await rig.DrainAsync();
        await rig.DrainAsync();
        Assert.Equal(0, rig.Engines[0].DisposeCount); // 计数若被减成负数，second 的租约就不起作用
    }

    [Fact]
    public async Task LoadCompletingDuringSession_DoesNotScheduleIdleUnload()
    {
        using var rig = new Rig();
        rig.Service.UpdateConfig(new AsrConfig { IdleUnloadMinutes = 1 });
        rig.Timers.Clear();

        var session = rig.Service.MakeSession(null); // 冷恢复：加载与录音并行
        await WaitUntil(() => rig.Service.State == AsrState.Ready);
        Assert.Empty(rig.Timers);

        session.Close();
        rig.Service.SessionEnded();
        Assert.Single(rig.Timers);
    }

    // ─── T2-2：预览窗口热更新与校准复用 ─────────────────────────

    [Fact]
    public async Task PreviewWindow_ConfigChange_AppliesToNextSession()
    {
        using var rig = new Rig();
        await rig.Service.PreloadAsync();
        rig.Service.UpdateConfig(new AsrConfig { PreviewWindowSeconds = 10 });

        var session = rig.Service.MakeSession(null);
        Assert.Equal(10 * AppConstants.TargetSampleRate, session.PreviewWindowSamples);
        session.Close();
    }

    [Fact]
    public async Task Calibration_IsReusedAcrossReload_ButRedoneWhenThreadsChange()
    {
        using var rig = new Rig();
        await rig.Service.PreloadAsync();
        await WaitUntil(() => rig.Engines[0].RecognizeCalls == 2); // 首次校准（预热 + 测量各一次）
        await Task.Delay(100); // 结果在续体里写回字段，等它落定

        await rig.Service.ReloadAsync(); // 同一模型、同一线程数：复用结果
        await rig.DrainAsync();
        Assert.Equal(2, rig.BuildCount);
        Assert.Equal(0, rig.Engines[1].RecognizeCalls);

        rig.Service.UpdateConfig(new AsrConfig { Threads = 2 }); // 线程数变化触发重载并作废缓存
        await WaitUntil(() => rig.BuildCount == 3 && rig.Service.State == AsrState.Ready);
        await WaitUntil(() => rig.Engines[2].RecognizeCalls == 2);
    }

    // ─── 校准让位（REVIEW_UX P-01）──────────────────────────────

    [Fact]
    public async Task Calibration_DefersWhileSessionActive_AndRunsAfterSessionEnded()
    {
        using var rig = new Rig();
        var session = rig.Service.MakeSession(null); // 冷恢复：加载与录音并行，会话租约已占用
        await WaitUntil(() => rig.Service.State == AsrState.Ready);
        await WaitUntil(() => rig.Service.HasPendingCalibration); // 校准任务已跑过检查并让位

        // 有会话在用：两遍合成基准推理（各 5 秒音频）不得插进用户的第一次预览/终稿前面。
        Assert.Equal(0, rig.Engines[0].RecognizeCalls);

        session.Close();
        rig.Service.SessionEnded();
        await WaitUntil(() => rig.Engines[0].RecognizeCalls == 2); // 会话结束后补跑
    }

    [Fact]
    public async Task DeferredCalibration_IsDiscarded_WhenEngineWasReplaced()
    {
        using var rig = new Rig();
        var session = rig.Service.MakeSession(null);
        await WaitUntil(() => rig.Service.State == AsrState.Ready);
        await WaitUntil(() => rig.Service.HasPendingCalibration);
        Assert.Equal(0, rig.Engines[0].RecognizeCalls);

        session.Close();
        await rig.Service.ReloadAsync(); // 引擎被换掉：推迟的校准必须作废（新引擎加载完成会自己发起）
        await rig.DrainAsync();
        await WaitUntil(() => rig.Engines[1].RecognizeCalls == 2);
        rig.Service.SessionEnded();
        await rig.DrainAsync();

        // 旧引擎从头到尾没被校准过；新引擎自己发起的校准已在加载完成时跑掉。
        Assert.Equal(0, rig.Engines[0].RecognizeCalls);
        Assert.Equal(2, rig.Engines[1].RecognizeCalls);
    }
}
