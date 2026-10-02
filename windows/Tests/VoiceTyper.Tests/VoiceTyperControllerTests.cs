using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        public Action? OnCancel { get; set; }
        public Action? OnGestureCancelled { get; set; }
        public Action<bool>? OnHealthChanged { get; set; }
        public bool AcceptsCancelWhenInactive { get; set; }
        public int StartCount, StopCount;
        public void Start(HotkeyConfig hotkey) => StartCount++;
        public void Stop() => StopCount++;
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
        public int StartCount, StopCount, StopWithoutResultCount;

        public void Start(AudioInputPolicy policy)
        {
            if (StartError is not null) throw StartError;
            LastPolicy = policy;
            StartCount++;
        }

        public void Stop()
        {
            StopCount++;
            OnTailChunk?.Invoke(new byte[16]);
        }

        public void StopWithoutResult() => StopWithoutResultCount++;
        public void Dispose() { }
    }

    private sealed class FakeText : ITextInserting
    {
        public TextInsertionResult Result = TextInsertionResult.Inserted;
        public bool CopySucceeds = true;
        public ForegroundElevation Elevation = ForegroundElevation.NotElevated;
        public readonly List<string> Inserted = new();
        public readonly List<string> Copied = new();

        public ForegroundTarget CaptureForegroundTarget() => new(new IntPtr(42), 7);

        public TextInsertionResult Insert(string text, ForegroundTarget expected)
        {
            Inserted.Add(text);
            return Result;
        }

        public bool CopyToClipboard(string text)
        {
            Copied.Add(text);
            return CopySucceeds;
        }

        public ForegroundElevation CheckForegroundElevation() => Elevation;
    }

    private sealed class FakeSession : IDictationSession
    {
        public Action<string>? OnPartial { get; set; }
        public Action<string>? OnFinal { get; set; }
        public Action<string>? OnWarning { get; set; }
        public Action<string>? OnError { get; set; }
        public Action? OnSessionCapped { get; set; }
        public Action? OnCorrectionStarted { get; set; }
        public AsrSessionTimings Timings { get; } = new();
        public readonly List<int> Sent = new();
        public int FinalizeCount;
        public bool Closed;

        public void SendAudio(byte[] data) => Sent.Add(data.Length);
        public void FinalizeStream(TimeSpan timeout) => FinalizeCount++;
        public void Close() => Closed = true;
    }

    private sealed class FakeFactory : IDictationSessionFactory
    {
        public readonly List<FakeSession> Sessions = new();
        public int EndedCount;

        public IDictationSession MakeSession(LlmCorrector? corrector)
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
        public int CancelledCount, EmptyCount, CorrectionCount;
        public readonly List<float> Levels = new();
        public readonly VoiceTyperController Controller;
        private long _ticks = 1_000;

        public Harness(Action<AppConfig>? configure = null)
        {
            var config = new AppConfig();
            configure?.Invoke(config);
            Config = config;
            Controller = new VoiceTyperController(config, Factory, Hotkey, Audio, Text, llmCorrector: null,
                now: () => _ticks, post: action => action(),
                schedule: (_, action) =>
                {
                    var probe = new Probe { Action = action };
                    Probes.Add(probe);
                    return probe;
                });
            Controller.StateChanged = s => States.Add(s);
            Controller.PreviewWarning = m => Warnings.Add(m);
            Controller.BlockedAttempt = r => Blocked.Add(r);
            Controller.MetricsReported = m => Metrics.Add(m);
            Controller.RecognizedText = t => Recognized.Add(t);
            Controller.Cancelled = () => CancelledCount++;
            Controller.EmptyRecognition = () => EmptyCount++;
            Controller.CorrectionStarted = () => CorrectionCount++;
            Controller.AudioLevel = l => Levels.Add(l);
        }

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
    public void PartialAndCorrectionCallbacks_AreForwarded()
    {
        var h = Started();
        var previews = new List<string>();
        h.Controller.PreviewUpdate = previews.Add;
        h.Hotkey.OnPress!();
        h.Session.OnPartial!("你好");
        h.Session.OnCorrectionStarted!();

        Assert.Equal(new[] { "你好" }, previews);
        Assert.Equal(1, h.CorrectionCount);
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
    public void Toggle_PressWhileRecognizing_WarnsInsteadOfStartingNew()
    {
        var h = Started(UseToggle);
        h.Hotkey.OnPress!();
        h.Advance(1000);
        h.Hotkey.OnPress!(); // → Recognizing
        h.Hotkey.OnPress!(); // 上一段还没完成

        Assert.Single(h.Factory.Sessions);
        Assert.Contains(h.Warnings, w => w.Contains("上一段听写尚未完成"));
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

    [Fact]
    public void Blocked_Set_TurnsOffCancelWindow()
    {
        var h = Started();
        h.Hotkey.AcceptsCancelWhenInactive = true;
        h.Controller.BlockedReason = "x";
        Assert.False(h.Hotkey.AcceptsCancelWhenInactive);
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
    public void InsertFailed_ClipboardAlsoFails_SaysSo()
    {
        var h = Started();
        h.Text.Result = TextInsertionResult.Failed;
        h.Text.CopySucceeds = false;
        h.HoldFor(600);
        h.Session.OnFinal!("结果");
        Assert.Contains("剪贴板也失败", h.States.Last().Message);
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
    public void DeviceChange_WarnsUser()
    {
        var h = Started();
        h.Hotkey.OnPress!();
        h.Audio.OnDeviceChanged!();
        Assert.Contains(h.Warnings, w => w.Contains("输入设备已变化"));
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

    [Fact]
    public void Suspend_And_Resume_ForwardToHotkeyService()
    {
        var h = Started();
        h.Controller.SuspendHotkeyListening();
        Assert.Equal(1, h.Hotkey.StopCount);
        h.Controller.ResumeHotkeyListening();
        Assert.Equal(2, h.Hotkey.StartCount);
    }

    [Fact]
    public void AudioPolicy_FollowsConfig()
    {
        var h = Started(c => c.Audio.InputDevice = AudioConfig.System);
        h.Hotkey.OnPress!();
        Assert.IsType<AudioInputPolicy.SystemDefault>(h.Audio.LastPolicy);

        var device = Started(c => c.Audio.InputDevice = "{0.0.1.00000000}.{abc}");
        device.Hotkey.OnPress!();
        Assert.Equal(new AudioInputPolicy.Device("{0.0.1.00000000}.{abc}"), device.Audio.LastPolicy);
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
    public void HasActiveDictation_TracksSessionAcrossNormalFlow()
    {
        var h = Started();
        Assert.False(h.Controller.HasActiveDictation);

        h.Hotkey.OnPress!();
        Assert.True(h.Controller.HasActiveDictation);
        h.Advance(800);
        h.Hotkey.OnRelease!();
        Assert.True(h.Controller.HasActiveDictation); // 识别中仍算进行中

        h.Session.OnFinal!("好");
        Assert.False(h.Controller.HasActiveDictation);
    }

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

    [Fact]
    public void HotkeyHealthFailure_WhenIdle_StillReportsError()
    {
        var h = Started();
        h.Hotkey.OnHealthChanged!(false);
        Assert.Equal(AppState.Error, h.States.Last().State);
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
}
