using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Core;
using VoiceTyper.Llm;
using VoiceTyper.Services;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 控制器状态机测试（对应 macOS <c>VoiceTyperControllerTests</c>）。热键 / 麦克风 / 剪贴板 / 识别会话
/// 全部用假实现，时钟与静音探测的调度也由测试手动推进——不依赖 Win32、UI 线程与 ONNX。
/// </summary>
public class VoiceTyperControllerTests
{
    // ─── 假实现 ────────────────────────────────────────────────

    private sealed class FakeHotkey : IHotkeyListening
    {
        public Action? OnPress { get; set; }
        public Action? OnRelease { get; set; }
        public Action? OnForcedRelease { get; set; }
        public Action? OnCancel { get; set; }
        public Action? OnGestureCancelled { get; set; }
        public Action<bool>? OnHealthChanged { get; set; }
        public bool AcceptsCancelWhenInactive { get; set; }
        public HotkeyTriggerStamp? LastTrigger { get; set; }
        public int StartCount, StopCount, ReinstallCount;
        public void Start(HotkeyConfig hotkey) => StartCount++;
        public void Stop() => StopCount++;
        public void Reinstall() => ReinstallCount++;
        public void Dispose() { }
    }

    private sealed class FakeAudio : IAudioCapturing
    {
        public Action<byte[]>? OnChunk { get; set; }
        public Action<byte[]>? OnTailChunk { get; set; }
        public Action? OnDeviceChanged { get; set; }
        public Action<float>? OnLevel { get; set; }
        public ActiveInputDevice? ActiveDevice { get; set; } = new("Test Mic", AudioTransport.BuiltIn, false);
        public AudioStartException? StartError;
        public AudioInputPolicy? LastPolicy;
        public AudioInputPolicy? PreparedPolicy;
        public int StartCount, StopCount, StopWithoutResultCount, PrepareCount, InvalidateCount;
        /// <summary>true 时启动挂起，由测试调用 <see cref="CompleteStart"/> 完成——模拟真实的异步打开麦克风。</summary>
        public bool DeferStart;
        public Action<AudioStartResult>? PendingStart;
        public readonly AudioStartTimings Timings = new() { ResolveTicks = 1, ActivateTicks = 2, InitTicks = 3, DeviceCached = true };

        public void PrepareInput(AudioInputPolicy policy)
        {
            PrepareCount++;
            PreparedPolicy = policy;
        }

        public void BeginStart(AudioInputPolicy policy, Action<AudioStartResult> completed)
        {
            LastPolicy = policy;
            StartCount++;
            if (StartError is not null) completed(new AudioStartResult.Failed(StartError));
            else if (DeferStart) PendingStart = completed;
            else completed(new AudioStartResult.Started(Timings));
        }

        public void CompleteStart(AudioStartResult? result = null)
        {
            var completed = PendingStart ?? throw new InvalidOperationException("没有挂起的启动");
            PendingStart = null;
            completed(result ?? new AudioStartResult.Started(Timings));
        }

        public void Stop()
        {
            StopCount++;
            OnTailChunk?.Invoke(new byte[16]);
        }

        public void StopWithoutResult() => StopWithoutResultCount++;
        public void InvalidateInput() => InvalidateCount++;
        public void Dispose() { }
    }

    private sealed class FakeText : ITextInserting
    {
        public TextInsertionResult Result = TextInsertionResult.Inserted;
        /// <summary>非空时优先按顺序消费，耗尽后回到 <see cref="Result"/>。</summary>
        public readonly Queue<TextInsertionResult> Script = new();
        public bool CopySucceeds = true;
        public ForegroundElevation Elevation = ForegroundElevation.NotElevated;
        public readonly List<string> Inserted = new();
        public readonly List<string> Copied = new();

        public ForegroundTarget CaptureForegroundTarget() => new(new IntPtr(42), 7);

        public TextInsertionResult Insert(string text, ForegroundTarget expected)
        {
            Inserted.Add(text);
            return Script.Count > 0 ? Script.Dequeue() : Result;
        }

        public bool CopyToClipboard(string text)
        {
            Copied.Add(text);
            return CopySucceeds;
        }

        public ForegroundElevation CheckForegroundElevation() => Elevation;

        public int PrepareCount, DiscardCount;
        public void PrepareForInsert() => PrepareCount++;
        public void DiscardPreparedBackup() => DiscardCount++;
    }

    private sealed class FakeSession : IDictationSession
    {
        public Action<string>? OnPartial { get; set; }
        public Action<string>? OnFinal { get; set; }
        public Action<string>? OnWarning { get; set; }
        public Action<string>? OnError { get; set; }
        public Action? OnSessionCapped { get; set; }
        public Action? OnCorrectionStarted { get; set; }
        public Action<bool>? OnEngineWait { get; set; }
        public AsrSessionTimings Timings { get; } = new();
        public readonly List<int> Sent = new();
        public int FinalizeCount, SkipCount, StopPreviewsCount;
        public void StopPreviews() => StopPreviewsCount++;
        public bool Closed;

        public void SkipCorrection() => SkipCount++;

        public void SendAudio(byte[] data) => Sent.Add(data.Length);
        public void FinalizeStream(TimeSpan timeout) => FinalizeCount++;
        public void Close() => Closed = true;
    }

    private sealed class FakeFactory : IDictationSessionFactory
    {
        public readonly List<FakeSession> Sessions = new();
        public int EndedCount;

        public IDictationSession MakeSession(LlmCorrector? corrector, VocabularyReplacer vocabulary)
        {
            var session = new FakeSession();
            Sessions.Add(session);
            return session;
        }

        public void SessionEnded() => EndedCount++;
    }

    private sealed class Probe : IDisposable
    {
        public Action Action = () => { };
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private sealed class Harness
    {
        public readonly FakeHotkey Hotkey = new();
        public readonly FakeAudio Audio = new();
        public readonly FakeText Text = new();
        public readonly FakeFactory Factory = new();
        public readonly List<Probe> Probes = new();
        public readonly List<AppStateInfo> States = new();
        public readonly List<string> Warnings = new();
        public readonly List<string> Blocked = new();
        public readonly List<DictationMetrics> Metrics = new();
        public readonly List<string> Recognized = new();
        public readonly List<string> FinalTexts = new();
        public int CancelledCount, EmptyCount, CorrectionCount, ReadyCount, NotReadyCount;
        public readonly List<float> Levels = new();
        public readonly VoiceTyperController Controller;
        private long _ticks = 1_000;

        public Harness(Action<AppConfig>? configure = null, IFocusInspector? focus = null, TimeSpan? tail = null,
            Action<Action>? post = null)
        {
            // 基线用组合键热键（Ctrl+F2）；默认的单独右 Ctrl 语义由 UseModifierOnly 显式选用。
            var config = new AppConfig { Hotkey = new HotkeyConfig { Modifiers = new() { "ctrl" }, Key = "f2" } };
            configure?.Invoke(config);
            Config = config;
            Controller = new VoiceTyperController(config, Factory, Hotkey, Audio, Text, llmCorrector: null,
                now: () => _ticks, post: post ?? (action => action()),
                schedule: (_, action) =>
                {
                    var probe = new Probe { Action = action };
                    Probes.Add(probe);
                    return probe;
                }, focusInspector: focus, releaseTailCapture: tail);
            Controller.StateChanged = s => States.Add(s);
            Controller.PreviewWarning = m => Warnings.Add(m);
            Controller.BlockedAttempt = r => Blocked.Add(r);
            Controller.MetricsReported = m => Metrics.Add(m);
            Controller.RecognizedText = t => Recognized.Add(t);
            Controller.FinalTextReady = t => FinalTexts.Add(t);
            Controller.Cancelled = () => CancelledCount++;
            Controller.EmptyRecognition = () => EmptyCount++;
            Controller.CorrectionStarted = () => CorrectionCount++;
            Controller.AudioLevel = l => Levels.Add(l);
            Controller.MicrophoneReady = () => ReadyCount++;
            Controller.ReleasedBeforeReady = () => NotReadyCount++;
        }

        public long Now => _ticks;

        public AppConfig Config { get; }
        public FakeSession Session => Factory.Sessions.Last();

        public void Advance(double milliseconds) => _ticks += (long)(milliseconds * Stopwatch.Frequency / 1000.0);

        public IEnumerable<AppState> StateKinds => States.Select(s => s.State);

        /// <summary>按一次热键并录满 <paramref name="milliseconds"/> 毫秒后松开（hold 模式）。</summary>
        public void HoldFor(double milliseconds)
        {
            Hotkey.OnPress!();
            Advance(milliseconds);
            Hotkey.OnRelease!();
        }
    }

    private static Harness Started(Action<AppConfig>? configure = null)
    {
        var h = new Harness(configure);
        h.Controller.Start();
        return h;
    }

    private static void UseModifierOnly(AppConfig c) => c.Hotkey = new HotkeyConfig { Modifiers = new(), Key = ModifierHotkeys.RightCtrl };
    private static void UseToggle(AppConfig c) => c.Hotkey.ModeValue = HotkeyMode.Toggle;

    // ─── 基本流程 ──────────────────────────────────────────────

    [Fact]
    public void Start_ReportsIdle_UnlessBlocked()
    {
        var h = new Harness();
        h.Controller.BlockedReason = "还没好";
        h.Controller.Start();
        Assert.Empty(h.States); // 被门禁时不能发 Idle，否则会覆盖协调器的"下载中"等状态

        var unblocked = Started();
        Assert.Equal(new[] { AppState.Idle }, unblocked.StateKinds);
    }

    [Fact]
    public void Hold_PressRelease_RecognizesAndInserts_WithMetrics()
    {
        var h = Started();
        h.Hotkey.OnPress!();
        Assert.Equal(AppState.Recording, h.States.Last().State);
        Assert.IsType<AudioInputPolicy.Automatic>(h.Audio.LastPolicy);
        Assert.True(h.Hotkey.AcceptsCancelWhenInactive);

        h.Advance(800);
        h.Hotkey.OnRelease!();
        Assert.Equal(1, h.Audio.StopCount);
        Assert.Equal(AppState.Recognizing, h.States.Last().State);
        Assert.Equal(1, h.Session.FinalizeCount);
        Assert.Equal(new[] { 16 }, h.Session.Sent); // 尾音被送进会话

        h.Session.OnFinal!("你好，世界");

        Assert.Equal(new[] { "你好，世界" }, h.Text.Inserted);
        Assert.Equal(new[] { "你好，世界" }, h.Recognized);
        Assert.Equal(new[] { AppState.Idle, AppState.Recording, AppState.Recognizing, AppState.Inserting, AppState.Idle }, h.StateKinds);
        Assert.True(h.Session.Closed);
        Assert.Equal(1, h.Factory.EndedCount);
        Assert.False(h.Hotkey.AcceptsCancelWhenInactive);

        var metrics = Assert.Single(h.Metrics);
        Assert.Equal(DictationOutcome.Inserted, metrics.Outcome);
        Assert.Equal(HotkeyMode.Hold, metrics.Mode);
        Assert.Equal(AudioTransport.BuiltIn, metrics.InputTransport);
        Assert.NotNull(metrics.ReleasedAt);
        Assert.NotNull(metrics.FinalizeCalledAt);
        Assert.NotNull(metrics.InsertTicks);
        Assert.NotNull(metrics.DoneAt);
    }

    [Fact]
    public void ShortRecording_IsDiscarded_WithoutFinalizeOrInsert()
    {
        var h = Started();
        h.HoldFor(100);

        Assert.Equal(0, h.Audio.StopCount);
        Assert.Equal(1, h.Audio.StopWithoutResultCount);
        Assert.Empty(h.Text.Inserted);
        Assert.Equal(AppState.Idle, h.States.Last().State);
        Assert.True(h.Session.Closed);
        Assert.Equal(DictationOutcome.Discarded, Assert.Single(h.Metrics).Outcome);

        // 迟到的 final 不能再触发任何东西。
        h.Session.OnFinal!("不该出现");
        Assert.Empty(h.Text.Inserted);
    }

    [Fact]
    public void EmptyRecognition_ReportsIdleThenEmpty()
    {
        var h = Started();
        h.HoldFor(600);
        h.Session.OnFinal!("   ");

        Assert.Empty(h.Text.Inserted);
        Assert.Equal(1, h.EmptyCount);
        Assert.Equal(AppState.Idle, h.States.Last().State);
        Assert.Equal(DictationOutcome.Empty, Assert.Single(h.Metrics).Outcome);
    }

    [Fact]
    public void Finish_ClearsPreview()
    {
        var h = Started();
        var previews = new List<string>();
        h.Controller.PreviewUpdate = previews.Add;
        h.HoldFor(600);
        h.Session.OnPartial!("你好");
        h.Session.OnFinal!("你好");
        Assert.Equal("", previews.Last());
    }

    // ─── 触发方式 ──────────────────────────────────────────────

    [Fact]
    public void Toggle_SecondPressFinishes_ReleaseIsIgnored()
    {
        var h = Started(UseToggle);
        h.Hotkey.OnPress!();
        h.Hotkey.OnRelease!(); // 松开不是结束信号
        Assert.Equal(AppState.Recording, h.States.Last().State);
        Assert.Equal(0, h.Audio.StopCount);
        Assert.True(h.Hotkey.AcceptsCancelWhenInactive); // 切换模式录音期间没有"按住的主键"，Esc 必须仍可取消

        h.Advance(1000);
        h.Hotkey.OnPress!();
        Assert.Equal(1, h.Audio.StopCount);
        Assert.Equal(AppState.Recognizing, h.States.Last().State);
    }

    [Fact]
    public void Hold_PressWhileBusy_WarnsInsteadOfStartingNew()
    {
        var h = Started();
        h.HoldFor(600); // → Recognizing
        h.Hotkey.OnPress!();
        Assert.Single(h.Factory.Sessions);
        Assert.Single(h.Warnings);
    }

    [Fact]
    public void ModifierOnlyToggle_OnlyCleanTapToggles()
    {
        var h = Started(c => { UseModifierOnly(c); UseToggle(c); });

        h.Hotkey.OnPress!(); // 按下不算
        Assert.Empty(h.Factory.Sessions);

        h.Hotkey.OnRelease!(); // 干净单击 → 开始
        Assert.Equal(AppState.Recording, h.States.Last().State);

        h.Advance(1000);
        h.Hotkey.OnPress!();
        h.Hotkey.OnRelease!(); // 再一次单击 → 结束
        Assert.Equal(1, h.Audio.StopCount);

        h.Session.OnFinal!("好");
        Assert.Equal(HotkeyKind.Modifier, Assert.Single(h.Metrics).Hotkey);
    }

    // ─── 门禁 ─────────────────────────────────────────────────

    [Fact]
    public void Blocked_PressReportsReason_AndDoesNotRecord()
    {
        var h = Started();
        h.Controller.BlockedReason = "模型下载中";
        h.Hotkey.OnPress!();
        h.Hotkey.OnRelease!();

        Assert.Equal(new[] { "模型下载中" }, h.Blocked);
        Assert.Empty(h.Factory.Sessions);
        Assert.Equal(0, h.Audio.StartCount);
    }

    [Fact]
    public void Blocked_ModifierOnly_ReportsOnlyOnCleanRelease()
    {
        var h = Started(UseModifierOnly);
        h.Controller.BlockedReason = "模型下载中";
        h.Hotkey.OnPress!();
        Assert.Empty(h.Blocked); // 组合快捷键的"按下"不该被当成骚扰提示
        h.Hotkey.OnRelease!();
        Assert.Single(h.Blocked);
    }

    [Fact]
    public void Blocked_DoesNotSwallowReleaseOfAnUtteranceAlreadyInProgress()
    {
        // 空闲卸载后首次按热键触发模型加载，协调器随后把门禁置上；这时松键仍必须结束录音。
        var h = Started();
        h.Hotkey.OnPress!();
        h.Controller.BlockedReason = "识别引擎正在加载";
        h.Advance(700);
        h.Hotkey.OnRelease!();

        Assert.Equal(1, h.Audio.StopCount);
        Assert.Empty(h.Blocked);
        Assert.Equal(AppState.Recognizing, h.States.Last().State);
    }

    // ─── 剪贴板提前备份 / 上一次识别结果 ───────────────────────────

    [Fact]
    public void ClipboardBackup_StartsWhenRecognitionBegins_AndIsConsumedByInsert()
    {
        var h = Started();
        h.Hotkey.OnPress!();
        Assert.Equal(0, h.Text.PrepareCount); // 录音期间不碰剪贴板
        h.Advance(800);
        h.Hotkey.OnRelease!();
        Assert.Equal(1, h.Text.PrepareCount);

        h.Session.OnFinal!("你好");
        Assert.Equal(0, h.Text.DiscardCount); // 交给 Insert 取用，不丢弃
    }

    [Fact]
    public void ClipboardBackup_IsDiscarded_WhenNothingWillBeInserted()
    {
        var cancelled = Started();
        cancelled.HoldFor(600);
        cancelled.Hotkey.OnCancel!();
        Assert.Equal(1, cancelled.Text.DiscardCount);

        var empty = Started();
        empty.HoldFor(600);
        empty.Session.OnFinal!("  ");
        Assert.Equal(1, empty.Text.DiscardCount);

        var failed = Started();
        failed.HoldFor(600);
        failed.Session.OnError!("识别超时");
        Assert.Equal(1, failed.Text.DiscardCount);
    }

    [Fact]
    public void FinalTextReady_FiresBeforeInsert_EvenWhenInsertFallsBackToClipboard()
    {
        var inserted = Started();
        inserted.HoldFor(600);
        inserted.Session.OnFinal!("  你好  ");
        Assert.Equal(new[] { "你好" }, inserted.FinalTexts);

        var focusChanged = Started();
        focusChanged.Text.Result = TextInsertionResult.FocusChanged;
        focusChanged.HoldFor(600);
        focusChanged.Session.OnFinal!("换了窗口");
        Assert.Equal(new[] { "换了窗口" }, focusChanged.FinalTexts);

        var empty = Started();
        empty.HoldFor(600);
        empty.Session.OnFinal!("   ");
        Assert.Empty(empty.FinalTexts);
    }

    // ─── 取消 ─────────────────────────────────────────────────

    [Fact]
    public void Esc_DuringRecording_Cancels()
    {
        var h = Started();
        h.Hotkey.OnPress!();
        h.Hotkey.OnCancel!();

        Assert.Equal(1, h.Audio.StopWithoutResultCount);
        Assert.Equal(1, h.CancelledCount);
        Assert.True(h.Session.Closed);
        Assert.Equal(DictationOutcome.Cancelled, Assert.Single(h.Metrics).Outcome);
    }

    [Fact]
    public void Esc_DuringRecognizing_DiscardsResult()
    {
        var h = Started();
        h.HoldFor(600);
        var stopWithoutResultBefore = h.Audio.StopWithoutResultCount;

        h.Hotkey.OnCancel!();
        Assert.Equal(1, h.CancelledCount);
        Assert.Equal(stopWithoutResultBefore, h.Audio.StopWithoutResultCount); // 采集早已停止
        Assert.True(h.Session.Closed);

        h.Session.OnFinal!("迟到的结果");
        Assert.Empty(h.Text.Inserted);
        Assert.Single(h.Metrics);
    }

    [Fact]
    public void Esc_WithNothingActive_IsIgnored()
    {
        var h = Started();
        h.Hotkey.OnCancel!();
        Assert.Equal(0, h.CancelledCount);
        Assert.Empty(h.Metrics);
    }

    [Fact]
    public void Gesture_DiscardsHoldRecordingSilently()
    {
        var h = Started(UseModifierOnly);
        h.Hotkey.OnPress!();
        h.Hotkey.OnGestureCancelled!();

        Assert.Equal(0, h.CancelledCount); // 静默：不弹"已取消"
        Assert.Equal(AppState.Idle, h.States.Last().State);
        Assert.Equal(1, h.Audio.StopWithoutResultCount);
        Assert.Equal(DictationOutcome.GestureCancelled, Assert.Single(h.Metrics).Outcome);
    }

    [Fact]
    public void Gesture_IsIgnoredInToggleMode()
    {
        var h = Started(c => { UseModifierOnly(c); UseToggle(c); });
        h.Hotkey.OnRelease!(); // 开始录音
        h.Hotkey.OnGestureCancelled!();
        Assert.Equal(AppState.Recording, h.States.Last().State);
        Assert.Empty(h.Metrics);
    }

    // ─── 错误与插入结局 ────────────────────────────────────────

    [Fact]
    public void RecognitionError_ReportsErrorState()
    {
        var h = Started();
        h.HoldFor(600);
        h.Session.OnError!("识别超时");

        Assert.Equal(AppState.Error, h.States.Last().State);
        Assert.Equal("识别超时", h.States.Last().Message);
        Assert.Equal(DictationOutcome.Failed, Assert.Single(h.Metrics).Outcome);
        Assert.False(h.Hotkey.AcceptsCancelWhenInactive);
    }

    // ─── 终稿看门狗（REVIEW_UX W-01）────────────────────────────

    [Theory]
    [InlineData(0, 30)]     // 下限：短音频也要能抓到真卡死
    [InlineData(5, 30)]
    [InlineData(30, 150)]   // 5 × 音频时长，覆盖 RTF 到 0.2
    [InlineData(60, 300)]
    [InlineData(120, 300)]  // 上限 5 分钟：极端挂死不能让 HUD 永远停在"识别中"
    public void FinalizeWatchdogTimeout_ScalesWithAudioLength(double audioSeconds, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), VoiceTyperController.FinalizeWatchdogTimeout(audioSeconds));
    }

    [Fact]
    public void FinalizeTimeout_WithPreviewText_SalvagesPreviewToClipboard()
    {
        var h = Started();
        h.HoldFor(600);
        h.Session.OnPartial!("已经识别出的部分");
        h.Session.Timings.FinalizeTimedOut = true;
        h.Session.OnError!("识别超时");

        // 说了很久的话不能一个字都找不回来：预览文本进剪贴板，错误提示说明去向。
        Assert.Equal(new[] { "已经识别出的部分" }, h.Text.Copied);
        Assert.Contains("剪贴板", h.States.Last().Message);
        Assert.Equal(DictationOutcome.Failed, Assert.Single(h.Metrics).Outcome);
    }

    [Fact]
    public void RecognitionError_WithoutTimeoutMarker_DoesNotTouchClipboard()
    {
        var h = Started();
        h.HoldFor(600);
        h.Session.OnPartial!("已经识别出的部分");
        h.Session.OnError!("识别超时");

        // 普通识别失败（引擎异常等）不把可能残缺的预览塞给用户、覆盖他的剪贴板。
        Assert.Empty(h.Text.Copied);
        Assert.Equal("识别超时", h.States.Last().Message);
    }

    [Fact]
    public void FinalizeTimeout_WithoutPreviewText_KeepsOriginalMessage()
    {
        var h = Started();
        h.HoldFor(600);
        h.Session.Timings.FinalizeTimedOut = true;
        h.Session.OnError!("识别超时");

        Assert.Empty(h.Text.Copied); // 没有可兜底的预览文本
        Assert.Equal("识别超时", h.States.Last().Message);
    }

    [Fact]
    public void FocusChanged_CopiesToClipboardAndReportsError()
    {
        var h = Started();
        h.Text.Result = TextInsertionResult.FocusChanged;
        h.HoldFor(600);
        h.Session.OnFinal!("结果");

        Assert.Equal(new[] { "结果" }, h.Text.Copied);
        Assert.Equal(AppState.Error, h.States.Last().State);
        Assert.Empty(h.Recognized);
        Assert.Equal(DictationOutcome.FocusChanged, Assert.Single(h.Metrics).Outcome);
    }

    // ─── 修饰键仍按住时推迟插入 ────────────────────────────────────

    private static Probe PendingProbe(Harness h)
    {
        var probe = h.Probes.Last();
        Assert.False(probe.Disposed);
        return probe;
    }

    [Fact]
    public void ModifiersHeld_ThenReleased_InsertsOnRetry_WithoutClipboardFallback()
    {
        var h = Started();
        h.Text.Script.Enqueue(TextInsertionResult.ModifiersHeld);
        h.Text.Script.Enqueue(TextInsertionResult.ModifiersHeld);
        h.HoldFor(600);
        h.Session.OnFinal!("结果");

        // 第一次尝试被修饰键挡住：推迟，不降级、不上报、仍视为听写进行中。
        Assert.Single(h.Text.Inserted);
        Assert.Empty(h.Text.Copied);
        Assert.Empty(h.Metrics);
        Assert.Equal(AppState.Inserting, h.States.Last().State);
        Assert.True(h.Controller.HasActiveDictation);

        PendingProbe(h).Action();
        Assert.Equal(2, h.Text.Inserted.Count);
        Assert.Empty(h.Metrics);

        PendingProbe(h).Action(); // 第三次：修饰键已松开
        Assert.Equal(3, h.Text.Inserted.Count);
        Assert.Empty(h.Text.Copied);
        Assert.Equal(new[] { "结果" }, h.Recognized);
        Assert.Equal(AppState.Idle, h.States.Last().State);
        Assert.False(h.Controller.HasActiveDictation);
        var metrics = Assert.Single(h.Metrics);
        Assert.Equal(DictationOutcome.Inserted, metrics.Outcome);
        Assert.Contains("mod_wait=", metrics.SummaryLine());
    }

    [Fact]
    public void ModifiersHeld_StillHeldAfterAllRetries_FallsBackToClipboard()
    {
        var h = Started();
        h.Text.Result = TextInsertionResult.ModifiersHeld;
        h.HoldFor(600);
        h.Session.OnFinal!("结果");

        for (int i = 0; i < VoiceTyperController.ModifierReleaseMaxRetries; i++)
        {
            Assert.Empty(h.Metrics);
            PendingProbe(h).Action();
        }

        Assert.Equal(VoiceTyperController.ModifierReleaseMaxRetries + 1, h.Text.Inserted.Count);
        Assert.Equal(new[] { "结果" }, h.Text.Copied);
        Assert.Equal(AppState.Error, h.States.Last().State);
        Assert.Equal(DictationOutcome.ModifiersHeld, Assert.Single(h.Metrics).Outcome);
        Assert.False(h.Controller.HasActiveDictation);
    }

    [Fact]
    public void ModifiersHeld_NewDictationStartsMeanwhile_CopiesInsteadOfInserting()
    {
        var h = Started();
        h.Text.Result = TextInsertionResult.ModifiersHeld;
        h.HoldFor(600);
        h.Session.OnFinal!("结果");
        var pending = PendingProbe(h);

        h.Hotkey.OnPress!(); // 用户已经开始下一段
        Assert.Equal(AppState.Recording, h.States.Last().State);
        pending.Action();

        Assert.Single(h.Text.Inserted); // 没有再次插入
        Assert.Equal(new[] { "结果" }, h.Text.Copied);
        Assert.Equal(DictationOutcome.ModifiersHeld, Assert.Single(h.Metrics).Outcome);
        Assert.Equal(AppState.Recording, h.States.Last().State); // 新听写的状态没被覆盖
    }

    [Fact]
    public void ModifiersHeld_StopWhilePending_CopiesAndCancelsTimer()
    {
        var h = Started();
        h.Text.Result = TextInsertionResult.ModifiersHeld;
        h.HoldFor(600);
        h.Session.OnFinal!("结果");
        var pending = PendingProbe(h);

        h.Controller.Stop();

        Assert.True(pending.Disposed);
        Assert.Equal(new[] { "结果" }, h.Text.Copied);
        Assert.Equal(DictationOutcome.ModifiersHeld, Assert.Single(h.Metrics).Outcome);
        Assert.False(h.Controller.HasActiveDictation);
    }

    // ─── 锁屏 / 睡眠 / 唤醒（B4）─────────────────────────────────────

    [Fact]
    public void SystemInterruption_WhileRecording_DiscardsSilently()
    {
        var h = Started();
        h.Hotkey.OnPress!();
        h.Advance(800);

        h.Controller.HandleSystemInterruption();

        Assert.Equal(1, h.Audio.StopWithoutResultCount);
        Assert.Equal(AppState.Idle, h.States.Last().State);
        Assert.Equal(0, h.CancelledCount); // 静默：不弹"已取消"
        Assert.Empty(h.Text.Inserted);
        Assert.True(h.Session.Closed);
        Assert.Equal(DictationOutcome.GestureCancelled, Assert.Single(h.Metrics).Outcome);
        Assert.False(h.Controller.HasActiveDictation);
    }

    [Fact]
    public void SystemInterruption_WhileRecognizing_LeavesTheDictationAlone()
    {
        var h = Started();
        h.HoldFor(600); // 已松键，识别中
        Assert.True(h.Controller.HasActiveDictation);

        h.Controller.HandleSystemInterruption();

        Assert.True(h.Controller.HasActiveDictation);
        Assert.Empty(h.Metrics);
    }

    [Fact]
    public void SystemResumed_ReinstallsHookAndInvalidatesInputCache()
    {
        var h = Started();
        h.Controller.HandleSystemResumed();
        Assert.Equal(1, h.Hotkey.ReinstallCount);
        Assert.Equal(1, h.Audio.InvalidateCount);
    }

    // ─── 剪贴板备份未就绪：UI 线程不等，轮询重试（B1）──────────────────

    [Fact]
    public void BackupPending_ThenReady_InsertsOnRetry_WithoutTouchingClipboard()
    {
        var h = Started();
        h.Text.Script.Enqueue(TextInsertionResult.BackupPending);
        h.Text.Script.Enqueue(TextInsertionResult.BackupPending);
        h.HoldFor(600);
        h.Session.OnFinal!("结果");

        // 备份没读完：推迟，不复制、不上报、仍视为听写进行中，HUD 状态保持"插入中"。
        Assert.Single(h.Text.Inserted);
        Assert.Empty(h.Text.Copied);
        Assert.Empty(h.Metrics);
        Assert.Equal(AppState.Inserting, h.States.Last().State);
        Assert.True(h.Controller.HasActiveDictation);

        h.Advance(25);
        PendingProbe(h).Action();
        Assert.Equal(2, h.Text.Inserted.Count);
        h.Advance(25);
        PendingProbe(h).Action(); // 第三次：备份已就绪
        Assert.Equal(3, h.Text.Inserted.Count);

        Assert.Empty(h.Text.Copied);
        Assert.Equal(new[] { "结果" }, h.Recognized);
        Assert.Equal(AppState.Idle, h.States.Last().State);
        var metrics = Assert.Single(h.Metrics);
        Assert.Equal(DictationOutcome.Inserted, metrics.Outcome);
        Assert.True(metrics.BackupWaitTicks > 0);
        Assert.Contains("backup_wait=", metrics.SummaryLine());
    }

    [Fact]
    public void BackupPending_NeverReady_GivesUpWithoutOverwritingClipboard()
    {
        var h = Started();
        h.Text.Result = TextInsertionResult.BackupPending;
        h.HoldFor(600);
        h.Session.OnFinal!("结果");

        for (int i = 0; i < VoiceTyperController.BackupMaxRetries; i++)
        {
            Assert.Empty(h.Metrics);
            PendingProbe(h).Action();
        }

        Assert.Equal(VoiceTyperController.BackupMaxRetries + 1, h.Text.Inserted.Count);
        Assert.Empty(h.Text.Copied); // 用户原剪贴板始终没被动过
        Assert.True(h.Text.DiscardCount >= 1);
        Assert.Equal(AppState.Error, h.States.Last().State);
        Assert.Equal(DictationOutcome.InsertFailed, Assert.Single(h.Metrics).Outcome);
        Assert.Equal(new[] { "结果" }, h.FinalTexts); // 结果仍可从托盘找回
        Assert.False(h.Controller.HasActiveDictation);
    }

    [Fact]
    public void BackupPending_StopWhilePending_CopiesAndReportsInsertFailed()
    {
        var h = Started();
        h.Text.Result = TextInsertionResult.BackupPending;
        h.HoldFor(600);
        h.Session.OnFinal!("结果");
        var pending = PendingProbe(h);

        h.Controller.Stop();

        Assert.True(pending.Disposed);
        Assert.Equal(new[] { "结果" }, h.Text.Copied);
        Assert.Equal(DictationOutcome.InsertFailed, Assert.Single(h.Metrics).Outcome);
        Assert.False(h.Controller.HasActiveDictation);
    }

    // ─── 等纠错时再按热键 / 引擎等待提示 ─────────────────────────────

    [Fact]
    public void PressWhileCorrecting_SkipsCorrection_InsteadOfRejecting()
    {
        var h = Started();
        h.HoldFor(600);
        h.Session.OnCorrectionStarted!();
        var sessionCount = h.Factory.Sessions.Count;

        h.Hotkey.OnPress!();

        Assert.Equal(1, h.Session.SkipCount);
        Assert.Equal(sessionCount, h.Factory.Sessions.Count); // 没有开始新录音
        Assert.Equal("已跳过纠错，使用识别原文", h.Warnings.Last());
        Assert.NotEqual(AppState.Recording, h.States.Last().State);
    }

    [Fact]
    public void PressWhileRecognizing_NotCorrecting_StillRejects()
    {
        var h = Started();
        h.HoldFor(600);

        h.Hotkey.OnPress!();

        Assert.Equal(0, h.Session.SkipCount);
        Assert.Equal("上一段听写尚未完成，请稍候再试", h.Warnings.Last());
    }

    [Fact]
    public void EngineWait_IsForwardedForCurrentSessionOnly()
    {
        var h = Started();
        var seen = new List<bool>();
        h.Controller.EngineWait = seen.Add;
        h.HoldFor(600);
        var first = h.Session;

        first.OnEngineWait!(true);
        first.OnEngineWait!(false);
        Assert.Equal(new[] { true, false }, seen);

        first.OnFinal!("好");
        first.OnEngineWait!(true); // 会话已收尾：迟到的通知忽略
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void InsertFailed_CopiesAndExplainsElevation()
    {
        var h = Started();
        h.Text.Result = TextInsertionResult.Failed;
        h.Text.Elevation = ForegroundElevation.Elevated;
        h.HoldFor(600);
        h.Session.OnFinal!("结果");

        Assert.Equal(new[] { "结果" }, h.Text.Copied);
        Assert.Contains("管理员", h.States.Last().Message);
        Assert.Equal(DictationOutcome.InsertFailed, Assert.Single(h.Metrics).Outcome);
    }

    [Fact]
    public void AudioStartFailure_ReportsErrorAndReleasesSession()
    {
        var h = Started();
        h.Audio.StartError = new AudioStartException("denied", AudioStartFailureKind.AccessDenied);
        h.Hotkey.OnPress!();

        Assert.Equal(AppState.Error, h.States.Last().State);
        Assert.Contains("麦克风权限", h.States.Last().Message);
        Assert.True(h.Session.Closed);
        Assert.Equal(1, h.Factory.EndedCount);
        Assert.Equal(DictationOutcome.StartFailed, Assert.Single(h.Metrics).Outcome);

        // 失败后可以重新开始。
        h.Audio.StartError = null;
        h.Hotkey.OnPress!();
        Assert.Equal(AppState.Recording, h.States.Last().State);
    }

    // ─── 生命周期 ─────────────────────────────────────────────

    [Fact]
    public void Stop_TearsDownActiveUtterance_WithoutReporting()
    {
        var h = Started();
        h.Hotkey.OnPress!();
        var statesBefore = h.States.Count;

        h.Controller.Stop();

        Assert.True(h.Session.Closed);
        Assert.Empty(h.Metrics);
        Assert.Equal(statesBefore, h.States.Count);
        Assert.False(h.Controller.IsRunning);
        Assert.True(h.Probes.All(p => p.Disposed));
    }

    [Fact]
    public void SessionCap_FinishesRecordingLikeRelease()
    {
        var h = Started();
        h.Hotkey.OnPress!();
        h.Session.OnSessionCapped!();
        Assert.Equal(1, h.Audio.StopCount);
        Assert.Equal(AppState.Recognizing, h.States.Last().State);
    }

    [Fact]
    public void HotkeyHealth_ReportsErrorAndRecovers()
    {
        var h = Started();
        h.Hotkey.OnHealthChanged!(false);
        Assert.Equal(AppState.Error, h.States.Last().State);
        h.Hotkey.OnHealthChanged!(true);
        Assert.Equal(AppState.Idle, h.States.Last().State);
    }

    [Fact]
    public void HotkeyHealth_RecoveryDoesNotClobberActiveOrBlockedState()
    {
        var h = Started();
        h.Hotkey.OnPress!();
        var count = h.States.Count;
        h.Hotkey.OnHealthChanged!(true);
        Assert.Equal(count, h.States.Count);

        var blocked = Started();
        blocked.Controller.BlockedReason = "x";
        var blockedCount = blocked.States.Count;
        blocked.Hotkey.OnHealthChanged!(true);
        Assert.Equal(blockedCount, blocked.States.Count);
    }

    // ─── 电平与静音探测 ────────────────────────────────────────

    [Fact]
    public void Levels_AreForwarded_AndFirstBufferIsRecorded()
    {
        var h = Started();
        h.Hotkey.OnPress!();
        h.Advance(120);
        h.Audio.OnLevel!(0.05f);
        Assert.Equal(new[] { 0.05f }, h.Levels);

        h.Advance(500);
        h.Hotkey.OnRelease!();
        h.Session.OnFinal!("好");
        var metrics = Assert.Single(h.Metrics);
        Assert.NotNull(metrics.FirstBufferAt);
        Assert.True(metrics.FirstBufferAt > metrics.PressedAt);
    }

    [Fact]
    public void SilenceProbe_WarnsWhenNothingHeard()
    {
        var h = Started();
        h.Hotkey.OnPress!();
        Assert.Equal(VoiceTyperController.SilenceProbeDelays.Length, h.Probes.Count);

        h.Probes[0].Action();
        Assert.Contains(h.Warnings, w => w.Contains("没有检测到声音"));
    }

    [Fact]
    public void SilenceProbe_StaysQuietWhenSpeechWasHeard()
    {
        var h = Started();
        h.Hotkey.OnPress!();
        h.Audio.OnLevel!(0.05f);
        foreach (var probe in h.Probes) probe.Action();
        Assert.Empty(h.Warnings);
    }

    [Fact]
    public void SilenceProbe_IsCancelledWhenRecordingEnds()
    {
        var h = Started();
        h.Hotkey.OnPress!();
        h.Advance(700);
        h.Hotkey.OnRelease!();
        Assert.True(h.Probes.All(p => p.Disposed));

        // 即使探测回调仍被触发，录音已结束也不应再提示。
        foreach (var probe in h.Probes) probe.Action();
        Assert.Empty(h.Warnings);
    }

    // ─── HasActiveDictation（破坏性操作的门禁依据）──────────────

    [Fact]
    public void HasActiveDictation_IsFalseAfterEveryFinishPath()
    {
        // 取消
        var cancel = Started();
        cancel.Hotkey.OnPress!();
        cancel.Hotkey.OnCancel!();
        Assert.False(cancel.Controller.HasActiveDictation);

        // 过短丢弃
        var discard = Started();
        discard.HoldFor(100);
        Assert.False(discard.Controller.HasActiveDictation);

        // 手势取消
        var gesture = Started(UseModifierOnly);
        gesture.Hotkey.OnPress!();
        gesture.Hotkey.OnGestureCancelled!();
        Assert.False(gesture.Controller.HasActiveDictation);

        // 识别失败
        var failed = Started();
        failed.HoldFor(600);
        failed.Session.OnError!("boom");
        Assert.False(failed.Controller.HasActiveDictation);

        // Stop()
        var stopped = Started();
        stopped.Hotkey.OnPress!();
        stopped.Controller.Stop();
        Assert.False(stopped.Controller.HasActiveDictation);
    }

    [Fact]
    public void HotkeyHealthFailure_DuringDictation_WarnsWithoutChangingState()
    {
        var h = Started();
        h.Hotkey.OnPress!();
        var statesBefore = h.States.Count;

        h.Hotkey.OnHealthChanged!(false);

        Assert.Equal(statesBefore, h.States.Count); // 不发 Error 状态
        Assert.Contains(h.Warnings, w => w.Contains("热键监听已失效"));
        Assert.True(h.Controller.HasActiveDictation);

        // 会话继续，松键后照常出字。
        h.Advance(800);
        h.Hotkey.OnRelease!();
        h.Session.OnFinal!("照常");
        Assert.Equal(new[] { "照常" }, h.Text.Inserted);
    }

    // ─── 纠错回落提示 ──────────────────────────────────────────

    [Fact]
    public void InsertedWithCorrectionFallback_FiresBeforeIdle_OnlyWhenCorrectionFellBack()
    {
        var h = Started();
        var order = new List<string>();
        h.Controller.InsertedWithCorrectionFallback = () => order.Add("fallback");
        h.Controller.StateChanged = s => order.Add(s.State.ToString());

        h.Hotkey.OnPress!();
        h.Advance(800);
        h.Hotkey.OnRelease!();
        h.Session.Timings.LlmResult = AsrSessionTimings.LlmOutcome.FellBack;
        h.Session.OnFinal!("原文");

        Assert.Equal(new[] { "fallback", "Idle" }, order.SkipWhile(x => x != "fallback").ToArray());

        var corrected = Started();
        var fired = 0;
        corrected.Controller.InsertedWithCorrectionFallback = () => fired++;
        corrected.Hotkey.OnPress!();
        corrected.Advance(800);
        corrected.Hotkey.OnRelease!();
        corrected.Session.Timings.LlmResult = AsrSessionTimings.LlmOutcome.Corrected;
        corrected.Session.OnFinal!("纠错后");
        Assert.Equal(0, fired);
    }
    // ─── 异步打开麦克风（Starting → Recording → 就绪）─────────────

    private static Harness StartedDeferred(Action<AppConfig>? configure = null)
    {
        var h = Started(configure);
        h.Audio.DeferStart = true;
        return h;
    }

    [Fact]
    public void Start_PreparesInputDeviceForConfiguredPolicy()
    {
        var h = Started(c => c.Audio.InputDevice = AudioConfig.System);
        Assert.Equal(1, h.Audio.PrepareCount);
        Assert.IsType<AudioInputPolicy.SystemDefault>(h.Audio.PreparedPolicy);
    }

    [Fact]
    public void Press_EntersRecordingBeforeMicrophoneOpens_AndReadyWaitsForFirstBuffer()
    {
        var h = StartedDeferred();
        h.Hotkey.OnPress!();

        // HUD 立即出现（Recording），但麦克风还没打开：不能宣布就绪，也不该开始静音探测。
        Assert.Equal(AppState.Recording, h.States.Last().State);
        Assert.True(h.Controller.HasActiveDictation);
        Assert.True(h.Hotkey.AcceptsCancelWhenInactive);
        Assert.Equal(0, h.ReadyCount);
        Assert.Empty(h.Probes);

        h.Advance(400);
        h.Audio.CompleteStart();
        Assert.Equal(0, h.ReadyCount); // 采集已开始，但还没收到声音
        Assert.Equal(VoiceTyperController.SilenceProbeDelays.Length, h.Probes.Count);

        h.Advance(15);
        h.Audio.OnLevel!(0.01f);
        h.Audio.OnLevel!(0.02f);
        Assert.Equal(1, h.ReadyCount);

        h.Advance(800);
        h.Hotkey.OnRelease!();
        h.Session.OnFinal!("好");
        var metrics = Assert.Single(h.Metrics);
        Assert.Equal(DictationOutcome.Inserted, metrics.Outcome);
        Assert.Same(h.Audio.Timings, metrics.AudioStart);
        Assert.True(metrics.CaptureStartedAt > metrics.PressedAt);
        Assert.True(metrics.FirstBufferAt > metrics.CaptureStartedAt);
    }

    [Fact]
    public void MicrophoneReady_FiresWhenFirstBufferArrivesBeforeStartCompletion()
    {
        // 第一段音频与"启动完成"从不同线程投递到 UI 线程，先后不确定。
        var h = StartedDeferred();
        h.Hotkey.OnPress!();
        h.Advance(10);
        h.Audio.OnLevel!(0.01f);
        Assert.Equal(0, h.ReadyCount);

        h.Audio.CompleteStart();
        Assert.Equal(1, h.ReadyCount);
        h.Audio.OnLevel!(0.01f);
        Assert.Equal(1, h.ReadyCount);
    }

    [Fact]
    public void ReleaseWhileStarting_AfterThreshold_ReportsNotReady_AndIgnoresLateCompletion()
    {
        var h = StartedDeferred();
        h.Hotkey.OnPress!();
        h.Advance(600);
        h.Hotkey.OnRelease!();

        Assert.Equal(1, h.Audio.StopWithoutResultCount); // 撤销启动
        Assert.Equal(0, h.Audio.StopCount);              // 没有尾音可取
        Assert.Equal(0, h.Session.FinalizeCount);
        Assert.Equal(AppState.Idle, h.States.Last().State);
        Assert.Equal(1, h.NotReadyCount);
        Assert.True(h.Session.Closed);
        Assert.False(h.Hotkey.AcceptsCancelWhenInactive);
        Assert.Equal(DictationOutcome.NotReady, Assert.Single(h.Metrics).Outcome);

        // 撤销与启动完成竞态：迟到的 Started 不能让已结束的听写复活。
        var statesBefore = h.States.Count;
        h.Audio.CompleteStart();
        h.Audio.OnLevel!(0.05f);
        Assert.Equal(statesBefore, h.States.Count);
        Assert.Equal(0, h.ReadyCount);
        Assert.Empty(h.Probes);
        Assert.False(h.Controller.HasActiveDictation);
    }

    [Fact]
    public void ReleaseWhileStarting_BelowThreshold_IsSilentDiscard()
    {
        var h = StartedDeferred();
        h.HoldFor(100);

        Assert.Equal(1, h.Audio.StopWithoutResultCount);
        Assert.Equal(0, h.NotReadyCount);
        Assert.Equal(AppState.Idle, h.States.Last().State);
        Assert.Equal(DictationOutcome.Discarded, Assert.Single(h.Metrics).Outcome);
    }

    [Fact]
    public void EscWhileStarting_CancelsAndRevokesStart()
    {
        var h = StartedDeferred();
        h.Hotkey.OnPress!();
        h.Hotkey.OnCancel!();

        Assert.Equal(1, h.Audio.StopWithoutResultCount);
        Assert.Equal(1, h.CancelledCount);
        Assert.Equal(DictationOutcome.Cancelled, Assert.Single(h.Metrics).Outcome);

        h.Audio.CompleteStart(new AudioStartResult.Cancelled());
        Assert.Single(h.Metrics);
    }

    [Fact]
    public void GestureWhileStarting_DiscardsSilently()
    {
        var h = StartedDeferred(UseModifierOnly);
        h.Hotkey.OnPress!();
        h.Hotkey.OnGestureCancelled!();

        Assert.Equal(1, h.Audio.StopWithoutResultCount);
        Assert.Equal(0, h.CancelledCount);
        Assert.Equal(DictationOutcome.GestureCancelled, Assert.Single(h.Metrics).Outcome);
    }

    [Fact]
    public void Toggle_SecondPressWhileStarting_EndsTheUtterance()
    {
        var h = StartedDeferred(UseToggle);
        h.Hotkey.OnPress!();
        h.Advance(700);
        h.Hotkey.OnPress!();

        Assert.Single(h.Factory.Sessions); // 不是"上一段尚未完成"，也没开第二段
        Assert.Empty(h.Warnings);
        Assert.Equal(1, h.NotReadyCount);
        Assert.False(h.Controller.HasActiveDictation);
    }

    [Fact]
    public void StopWhileStarting_TearsDownWithoutReporting()
    {
        var h = StartedDeferred();
        h.Hotkey.OnPress!();
        h.Controller.Stop();

        Assert.Equal(1, h.Audio.StopWithoutResultCount);
        Assert.True(h.Session.Closed);
        Assert.Empty(h.Metrics);
        h.Audio.CompleteStart();
        Assert.False(h.Controller.HasActiveDictation);
    }

    [Fact]
    public void AsyncStartFailure_ReportsError()
    {
        var h = StartedDeferred();
        h.Hotkey.OnPress!();
        h.Audio.CompleteStart(new AudioStartResult.Failed(new AudioStartException("busy", AudioStartFailureKind.DeviceFailure)));

        Assert.Equal(AppState.Error, h.States.Last().State);
        Assert.Contains("开始录音失败", h.States.Last().Message);
        Assert.True(h.Session.Closed);
        Assert.Equal(DictationOutcome.StartFailed, Assert.Single(h.Metrics).Outcome);
        Assert.False(h.Controller.HasActiveDictation);
    }

    [Fact]
    public void UnexpectedCancellation_IsTreatedAsStartFailure()
    {
        var h = StartedDeferred();
        h.Hotkey.OnPress!();
        h.Audio.CompleteStart(new AudioStartResult.Cancelled());

        Assert.Equal(AppState.Error, h.States.Last().State);
        Assert.Equal(DictationOutcome.StartFailed, Assert.Single(h.Metrics).Outcome);
    }

    // ─── R8-F2：上屏文本与预览统一格式化 ─────────────────────────

    [Fact]
    public void FinalText_IsFormattedBeforeInsertion()
    {
        var h = Started();
        h.HoldFor(600);
        h.Session.OnFinal!("我用Python写代码");

        Assert.Equal(new[] { "我用 Python 写代码" }, h.Text.Inserted);
        Assert.Equal(new[] { "我用 Python 写代码" }, h.Recognized);
        Assert.Equal(new[] { "我用 Python 写代码" }, h.FinalTexts);
    }

    [Fact]
    public void PreviewCallback_ReceivesFormattedText()
    {
        var h = Started();
        var previews = new List<string>();
        h.Controller.PreviewUpdate = previews.Add;
        h.Hotkey.OnPress!();
        h.Session.OnPartial!("我用Python写代码");

        Assert.Equal(new[] { "我用 Python 写代码" }, previews);
    }

    [Fact]
    public void FinalizeTimeout_SalvagedPreviewIsFormatted()
    {
        var h = Started();
        h.HoldFor(600);
        h.Session.OnPartial!("已识别Python部分");
        h.Session.Timings.FinalizeTimedOut = true;
        h.Session.OnError!("识别超时");

        Assert.Equal(new[] { "已识别 Python 部分" }, h.Text.Copied);
    }

    private sealed class FakeFocus : IFocusInspector
    {
        public FocusSafety Safety = FocusSafety.Normal;
        public TaskCompletionSource<FocusSafety>? Pending;
        public Task<FocusSafety> InspectAsync(ForegroundTarget target, CancellationToken cancellationToken)
            => Pending?.Task ?? Task.FromResult(Safety);
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("toggle")]
    public void ModifierShortcutWhileCorrecting_DoesNotSkip_ButCleanReleaseDoes(string mode)
    {
        var h = Started(c => { c.Hotkey.Key = "right_ctrl"; c.Hotkey.Modifiers.Clear(); c.Hotkey.Mode = mode; });
        h.Hotkey.OnPress!();
        if (mode == "toggle") h.Hotkey.OnRelease!();
        h.Advance(600);
        if (mode == "toggle") h.Hotkey.OnPress!();
        h.Hotkey.OnRelease!();
        h.Session.OnCorrectionStarted!();
        h.Hotkey.OnPress!();
        Assert.Equal(0, h.Session.SkipCount);
        h.Hotkey.OnGestureCancelled!(); // Ctrl+C 手势没有干净的 Release
        Assert.Equal(0, h.Session.SkipCount);
        h.Hotkey.OnPress!();
        h.Hotkey.OnRelease!();
        Assert.Equal(1, h.Session.SkipCount);
        Assert.Single(h.Factory.Sessions);
    }

    [Fact]
    public void PasswordFocus_RefusesRecording_AndCanRecover()
    {
        var focus = new FakeFocus { Safety = FocusSafety.Password };
        var h = new Harness(focus: focus);
        var notices = new List<string>();
        h.Controller.RecordingRefused = notices.Add;
        h.Controller.Start();
        h.Hotkey.OnPress!();
        Assert.Empty(h.Factory.Sessions);
        Assert.Equal(0, h.Audio.StartCount);
        Assert.Single(notices);
        focus.Safety = FocusSafety.Normal;
        h.Hotkey.OnPress!();
        Assert.Single(h.Factory.Sessions);
    }

    [Fact]
    public void PasswordModifierShortcut_DoesNotShowRefusal()
    {
        var h = new Harness(c => { c.Hotkey.Key = "right_ctrl"; c.Hotkey.Modifiers.Clear(); },
            new FakeFocus { Safety = FocusSafety.Password });
        var notices = new List<string>();
        h.Controller.RecordingRefused = notices.Add;
        h.Controller.Start();
        h.Hotkey.OnPress!();
        Assert.Empty(notices);
        h.Hotkey.OnGestureCancelled!();
        Assert.Empty(notices);
        h.Hotkey.OnPress!();
        h.Hotkey.OnRelease!();
        Assert.Single(notices);
    }

    [Fact]
    public void ReleaseDuringFocusCheck_IgnoresLateResult()
    {
        var focus = new FakeFocus { Pending = new() };
        var h = new Harness(focus: focus);
        h.Controller.Start();
        h.Hotkey.OnPress!();
        Assert.True(h.Controller.HasActiveDictation);
        h.Hotkey.OnRelease!();
        focus.Pending.SetResult(FocusSafety.Normal);
        Assert.Empty(h.Factory.Sessions);
        Assert.False(h.Controller.HasActiveDictation);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void UnsafeInsertion_LeavesResultInHistory_WithoutPasting(int safety)
    {
        var focus = new FakeFocus();
        var h = new Harness(focus: focus);
        h.Controller.Start();
        h.HoldFor(600);
        focus.Safety = (FocusSafety)safety;
        h.Session.OnFinal!("找回结果");
        Assert.Empty(h.Text.Inserted);
        Assert.Empty(h.Text.Copied); // 检查不明时不主动污染剪贴板
        Assert.Equal(new[] { "找回结果" }, h.FinalTexts);
        Assert.False(h.Controller.HasActiveDictation);
        Assert.Equal(safety == (int)FocusSafety.Password ? DictationOutcome.ProtectedField : DictationOutcome.FocusUnverified,
            Assert.Single(h.Metrics).Outcome);
    }

    [Fact]
    public void StopDuringInsertionFocusCheck_DoesNotInsertLateResult()
    {
        var focus = new FakeFocus();
        var h = new Harness(focus: focus);
        h.Controller.Start();
        h.HoldFor(600);
        focus.Pending = new();
        h.Session.OnFinal!("保留结果");
        h.Controller.Stop();
        focus.Pending.SetResult(FocusSafety.Normal);
        Assert.Empty(h.Text.Inserted);
        Assert.Equal(new[] { "保留结果" }, h.Text.Copied);
        Assert.Single(h.Metrics);
    }

    [Fact]
    public void ReleaseTail_AnnouncesOnce_AcceptsAudio_AndStopsPreviewsBeforeTail()
    {
        var h = new Harness(tail: TimeSpan.FromMilliseconds(150));
        h.Controller.Start();
        h.HoldFor(600);
        Assert.Equal(0, h.Audio.StopCount);
        Assert.Equal(1, h.Session.StopPreviewsCount);
        Assert.Contains(AppState.Recognizing, h.StateKinds);
        h.Audio.OnChunk!(new byte[32]);
        h.Probes.Last().Action();
        Assert.Equal(1, h.Audio.StopCount);
        Assert.Equal(1, h.Session.FinalizeCount);
        Assert.Single(h.States, s => s.State == AppState.Recognizing);
    }

    [Fact]
    public void CancelTail_AndStartAgain_LateTimerCannotStopNewRecording()
    {
        var h = new Harness(tail: TimeSpan.FromMilliseconds(150));
        var retained = new List<string>();
        h.Controller.PartialTextReady = (text, _) => retained.Add(text);
        h.Controller.Start();
        h.HoldFor(600);
        h.Session.OnPartial!("部分Python");
        var timer = h.Probes.Last();
        h.Hotkey.OnCancel!();
        Assert.True(timer.Disposed);
        h.Hotkey.OnPress!();
        timer.Action();
        Assert.Equal(0, h.Audio.StopCount);
        Assert.Equal(2, h.Audio.StartCount);
        Assert.Equal(new[] { "部分 Python" }, retained);
    }

    [Fact]
    public void ForcedRelease_DoesNotCaptureExtraTail()
    {
        var h = new Harness(tail: TimeSpan.FromMilliseconds(150));
        h.Controller.Start();
        h.Hotkey.OnPress!();
        h.Advance(600);
        h.Hotkey.OnForcedRelease!();
        Assert.Equal(1, h.Audio.StopCount);
        Assert.Equal(1, h.Session.FinalizeCount);
    }

    [Fact]
    public void FailedPreview_IsRetainedOnlyOnce_WithoutAutomaticCopy()
    {
        var h = Started();
        var retained = new List<(string, RecognitionHistory.ResultKind)>();
        h.Controller.PartialTextReady = (text, kind) => retained.Add((text, kind));
        h.HoldFor(600);
        h.Session.OnPartial!("保留Python部分");
        h.Session.OnError!("失败");
        h.Session.OnError!("迟到失败");
        Assert.Equal(("保留 Python 部分", RecognitionHistory.ResultKind.Partial), Assert.Single(retained));
        Assert.Empty(h.Text.Copied);
    }

    [Fact]
    public void FocusCheckResultQueuedToUi_CanBeCancelledBeforeDelivery()
    {
        var posted = new Queue<Action>();
        var h = new Harness(focus: new FakeFocus(), post: posted.Enqueue);
        h.Controller.Start();
        h.Hotkey.OnPress!(); // 检查已完成，回调尚未回到 UI
        h.Hotkey.OnRelease!();
        while (posted.TryDequeue(out var action)) action();
        Assert.Empty(h.Factory.Sessions);
        Assert.False(h.Controller.HasActiveDictation);
    }

    [Fact]
    public void ShortRecording_WithTailEnabled_StillStopsImmediately()
    {
        var h = new Harness(tail: TimeSpan.FromMilliseconds(150));
        h.Controller.Start();
        h.HoldFor(100);
        Assert.Equal(1, h.Audio.StopWithoutResultCount);
        Assert.Equal(0, h.Audio.StopCount);
        Assert.Equal(0, h.Session.FinalizeCount);
        Assert.Equal(DictationOutcome.Discarded, Assert.Single(h.Metrics).Outcome);
    }

    [Fact]
    public async Task InsertionFocusCheck_RejectsNewRecording_UntilResultIsHandled()
    {
        var focus = new FakeFocus();
        var h = new Harness(focus: focus);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Controller.MetricsReported = _ => completed.SetResult();
        h.Controller.Start();
        h.HoldFor(600);
        focus.Pending = new();
        h.Session.OnFinal!("上一段");
        h.Hotkey.OnPress!();
        Assert.Single(h.Factory.Sessions);
        Assert.NotEmpty(h.Warnings);
        focus.Pending.SetResult(FocusSafety.Normal);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "上一段" }, h.Text.Inserted);
        Assert.False(h.Controller.HasActiveDictation);
    }

    [Fact]
    public void PasswordCheck_IsRepeatedAfterClipboardBackupWait()
    {
        var focus = new FakeFocus();
        var h = new Harness(focus: focus);
        h.Controller.Start();
        h.Text.Result = TextInsertionResult.BackupPending;
        h.HoldFor(600);
        h.Session.OnFinal!("最终结果");
        focus.Safety = FocusSafety.Password;
        PendingProbe(h).Action();
        Assert.Single(h.Text.Inserted); // 只发生过第一次备份未就绪的尝试
        Assert.Equal(DictationOutcome.ProtectedField, Assert.Single(h.Metrics).Outcome);
        Assert.Single(h.FinalTexts);
    }

    [Fact]
    public void ClipboardBackupWallClockBudget_StopsRetryingAfterLongAsyncWait()
    {
        var h = Started();
        h.Text.Result = TextInsertionResult.BackupPending;
        h.HoldFor(600);
        h.Session.OnFinal!("结果");
        h.Advance(5100);
        PendingProbe(h).Action();
        Assert.Equal(DictationOutcome.InsertFailed, Assert.Single(h.Metrics).Outcome);
        Assert.False(h.Controller.HasActiveDictation);
        Assert.Empty(h.Text.Copied);
    }

    [Fact]
    public void SessionCapDuringTail_StopsImmediately_InsteadOfWaitingForTimer()
    {
        var h = new Harness(tail: TimeSpan.FromMilliseconds(150));
        h.Controller.Start();
        h.HoldFor(600);
        var timer = h.Probes.Last();
        h.Session.OnSessionCapped!();
        Assert.Equal(1, h.Audio.StopCount);
        Assert.True(timer.Disposed);
        timer.Action();
        Assert.Equal(1, h.Audio.StopCount);
    }

}
