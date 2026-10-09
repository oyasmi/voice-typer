using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using VoiceTyper.Services;

namespace VoiceTyper.Core;

/// <summary><see cref="Asr.LocalAsrSession"/> 侧的分阶段打点，会话收尾时由控制器并入 <see cref="DictationMetrics"/>。</summary>
internal sealed class AsrSessionTimings
{
    /// <summary>Skipped：用户在等待纠错时再次按下热键，放弃等待、直接使用识别原文（不算纠错失败）。</summary>
    public enum LlmOutcome { Off, Corrected, FellBack, Skipped }

    /// <summary>会话实际接受的样本数（16kHz 单声道）。</summary>
    public int ReceivedSamples;
    /// <summary>会话开始收音时引擎是否尚未就绪。</summary>
    public bool ColdAtStart;
    /// <summary>finalize 时等待引擎加载的时长（Stopwatch tick）；引擎已就绪则为 0。</summary>
    public long EngineWaitTicks;
    public long? FinalizeStartedAt;
    public long? AsrCompletedAt;
    public long? LlmStartedAt;
    public long? LlmCompletedAt;
    public LlmOutcome? LlmResult;
    /// <summary>是否因服务拒绝 <c>thinking</c> 字段而去掉字段重发过。</summary>
    public bool LlmRetriedWithoutThinking;
    public int PreviewRuns;
    public int PreviewSkipped;
    public long PreviewMaxTicks;
}

internal enum DictationOutcome
{
    Inserted,
    FocusChanged,
    ModifiersHeld,
    InsertFailed,
    Empty,
    Cancelled,
    Discarded,
    GestureCancelled,
    Failed,
    StartFailed,
    /// <summary>麦克风还没启动完成就松开了热键：没有录到任何音频。</summary>
    NotReady,
}

internal enum HotkeyKind { Combo, Modifier }

/// <summary>
/// 一次听写的耗时与结果摘要，收尾时以一行日志输出（<see cref="SummaryLine"/>）。对应 macOS 的
/// <c>DictationMetrics</c>。
///
/// <b>本类型禁止新增任何承载用户文本的字段</b>（识别文本、LLM 输出、窗口标题、设备名等）：
/// 摘要会原样写入日志，只允许数字、布尔与枚举。<c>DictationMetricsTests</c> 会用反射钉死
/// "不存在 string 类型的公开字段/属性"。所有时间戳均为 <see cref="Stopwatch.GetTimestamp"/> tick。
/// </summary>
internal sealed class DictationMetrics
{
    public ushort SessionId;
    public DictationOutcome Outcome = DictationOutcome.Failed;
    public HotkeyMode Mode;
    public HotkeyKind Hotkey;

    /// <summary>控制器开始处理热键的时刻；下列"距按下"的耗时都以它为起点。</summary>
    public long PressedAt;
    /// <summary>钩子回调收到这次按键的时刻（早于 <see cref="PressedAt"/>，差值是投递到 UI 线程的排队时间）。</summary>
    public long? HookAt;
    /// <summary>系统按键事件时间戳到钩子回调的毫秒数。</summary>
    public int? KeyEventLagMs;
    /// <summary>麦克风启动完成（采集已开始）的时刻；启动在采集服务的控制线程上异步进行。</summary>
    public long? CaptureStartedAt;
    public long? FirstBufferAt;
    /// <summary>打开麦克风的分段耗时，见 <see cref="AudioStartTimings"/>。</summary>
    public AudioStartTimings? AudioStart;
    /// <summary>HUD 第一次画到屏幕上（"麦克风启动中"或直接"录音中"）的时刻。</summary>
    public long? HudShownAt;
    /// <summary>HUD 第一次画出"录音中"（用户可以开口）的时刻。</summary>
    public long? HudReadyAt;
    public long? ReleasedAt;
    public long? FinalizeCalledAt;
    public long? DoneAt;

    public long? InsertTicks;
    /// <summary>识别完成后因 Alt/Shift/Win 仍被按住而推迟插入所等待的时长（Stopwatch tick）；没有等待为 0。</summary>
    public long ModifierWaitTicks;
    public AsrSessionTimings Timings = new();

    /// <summary>实际使用的输入设备类型；<see cref="InputSwitchedByAuto"/> 表示由「自动」策略从系统默认输入切换而来。</summary>
    public AudioTransport? InputTransport;
    public bool InputSwitchedByAuto;

    private static long? Interval(long? from, long? to) => from is { } f && to is { } t && t >= f ? t - f : null;

    private static string Ms(long? ticks) =>
        ticks is { } t ? ((long)(t * 1000.0 / Stopwatch.Frequency)).ToString(CultureInfo.InvariantCulture) : "-";

    private static string OutcomeName(DictationOutcome outcome) => outcome switch
    {
        DictationOutcome.Inserted => "inserted",
        DictationOutcome.FocusChanged => "focus_changed",
        DictationOutcome.ModifiersHeld => "modifiers_held",
        DictationOutcome.InsertFailed => "insert_failed",
        DictationOutcome.Empty => "empty",
        DictationOutcome.Cancelled => "cancelled",
        DictationOutcome.Discarded => "discarded",
        DictationOutcome.GestureCancelled => "gesture_cancelled",
        DictationOutcome.StartFailed => "start_failed",
        DictationOutcome.NotReady => "not_ready",
        _ => "failed",
    };

    /// <summary>单行摘要：字段顺序固定，缺失值输出 <c>-</c>。</summary>
    public string SummaryLine()
    {
        var inv = CultureInfo.InvariantCulture;
        var audioSeconds = (Timings.ReceivedSamples / 16000.0).ToString("F1", inv);
        var asr = Interval(Timings.FinalizeStartedAt, Timings.AsrCompletedAt);
        var llm = Interval(Timings.LlmStartedAt, Timings.LlmCompletedAt);
        var input = InputTransport is { } transport ? transport.LogName() + (InputSwitchedByAuto ? "*" : "") : "-";
        var llmResult = Timings.LlmResult switch
        {
            AsrSessionTimings.LlmOutcome.Corrected => "corrected",
            AsrSessionTimings.LlmOutcome.FellBack => "fell_back",
            AsrSessionTimings.LlmOutcome.Skipped => "skipped",
            AsrSessionTimings.LlmOutcome.Off => "off",
            _ => "-",
        };

        var fields = new List<(string Key, string Value)>
        {
            ("session", SessionId.ToString("x4", inv)),
            ("outcome", OutcomeName(Outcome)),
            ("mode", Mode.ToYamlValue()),
            ("hotkey", Hotkey == HotkeyKind.Modifier ? "modifier" : "combo"),
            ("input", input),
            ("capture_start", Ms(Interval(PressedAt, CaptureStartedAt))),
            ("first_buffer", Ms(Interval(PressedAt, FirstBufferAt))),
            ("key_lag", KeyEventLagMs is { } lag ? lag.ToString(inv) : "-"),
            ("dispatch", Ms(Interval(HookAt, PressedAt))),
            ("start_queue", Ms(AudioStart?.QueueTicks)),
            ("dev_resolve", Ms(AudioStart?.ResolveTicks)),
            ("dev_cached", AudioStart is null ? "-" : (AudioStart.DeviceCached ? "1" : "0")),
            ("activate", Ms(AudioStart?.ActivateTicks)),
            ("init", Ms(AudioStart?.InitTicks)),
            ("hud_shown", Ms(Interval(PressedAt, HudShownAt))),
            ("hud_ready", Ms(Interval(PressedAt, HudReadyAt))),
            ("audio", audioSeconds + "s"),
            ("release_to_finalize", Ms(Interval(ReleasedAt, FinalizeCalledAt))),
            ("engine_wait", Ms(Timings.FinalizeStartedAt is null ? null : Timings.EngineWaitTicks)),
            ("asr", Ms(asr)),
            ("llm", Ms(llm)),
            ("llm_result", llmResult),
            ("llm_retry", Timings.LlmResult is null ? "-" : (Timings.LlmRetriedWithoutThinking ? "1" : "0")),
            ("insert", Ms(InsertTicks)),
            ("mod_wait", Ms(ModifierWaitTicks)),
            ("release_to_done", Ms(Interval(ReleasedAt, DoneAt))),
            ("previews", Timings.PreviewRuns.ToString(inv)),
            ("previews_skipped", Timings.PreviewSkipped.ToString(inv)),
            ("preview_max", Timings.PreviewRuns == 0 ? "-" : Ms(Timings.PreviewMaxTicks)),
            ("cold", Timings.ColdAtStart ? "1" : "0"),
        };
        return "dictation " + string.Join(" ", fields.ConvertAll(f => f.Key + "=" + f.Value));
    }
}
