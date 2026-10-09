using System;
using VoiceTyper.Llm;
using VoiceTyper.Services;

namespace VoiceTyper.Core;

// 控制器依赖的四个外部能力。抽成接口的目的只有一个：让状态机（Esc 取消、切换模式、门禁、
// 连按、收尾幂等……）能脱离真实钩子 / 麦克风 / 剪贴板 / ONNX 单测（对应 macOS 的
// HotkeyListening / AudioCapturing / TextInserting 三个协议，VW-24）。

internal interface IHotkeyListening : IDisposable
{
    Action? OnPress { get; set; }
    Action? OnRelease { get; set; }
    /// <summary>用户按 Esc 主动取消。</summary>
    Action? OnCancel { get; set; }
    /// <summary>单独修饰键被用作组合快捷键（按住期间又按了别的键 / 点了鼠标）。</summary>
    Action? OnGestureCancelled { get; set; }
    /// <summary>钩子健康状态变化：false = 钩子已失效且自愈失败中，true = （重新）安装成功。</summary>
    Action<bool>? OnHealthChanged { get; set; }
    /// <summary>无进行中的组合键时是否仍受理 Esc（见 <see cref="HotkeyStateMachine.AcceptsCancelWhenInactive"/>）。</summary>
    bool AcceptsCancelWhenInactive { get; set; }
    /// <summary>最近一次触发 <see cref="OnPress"/>/<see cref="OnRelease"/> 的按键在钩子里的时间戳，只用于耗时日志。
    /// 钩子与这两个回调都在 UI 线程上，回调执行时读到的就是触发它的那次按键。</summary>
    HotkeyTriggerStamp? LastTrigger { get; }
    void Start(HotkeyConfig hotkey);
    void Stop();
}

/// <param name="HookTimestamp">钩子回调收到按键时的 <see cref="System.Diagnostics.Stopwatch"/> 时间戳。</param>
/// <param name="KeyEventLagMs">系统给按键事件打的时间戳到钩子回调之间的毫秒数；UI 线程被阻塞时会明显变大。</param>
internal readonly record struct HotkeyTriggerStamp(long HookTimestamp, int? KeyEventLagMs);

internal interface IAudioCapturing : IDisposable
{
    Action<byte[]>? OnChunk { get; set; }
    Action<byte[]>? OnTailChunk { get; set; }
    /// <summary>录音期间输入设备变化导致本次录音被迫结束时触发一次。在 UI 线程触发。</summary>
    Action? OnDeviceChanged { get; set; }
    /// <summary>录音期间的实时线性 RMS 电平（0…1 量级），在音频线程触发，调用方须自行回到 UI 线程。</summary>
    Action<float>? OnLevel { get; set; }
    /// <summary>本次录音实际使用的输入设备；<see cref="BeginStart"/> 成功后才有值。</summary>
    ActiveInputDevice? ActiveDevice { get; }
    /// <summary>空闲时提前解析输入端点（不打开麦克风），让下一次 <see cref="BeginStart"/> 省掉设备枚举。</summary>
    void PrepareInput(AudioInputPolicy policy);
    /// <summary>
    /// 异步打开麦克风（打开设备与 IAudioClient 初始化可能要数百毫秒，不得占用 UI 线程）。
    /// <paramref name="completed"/> 恰好触发一次，在后台线程上，调用方须自行回到 UI 线程。
    /// 完成前调用 <see cref="StopWithoutResult"/> 即撤销本次启动。
    /// </summary>
    void BeginStart(AudioInputPolicy policy, Action<AudioStartResult> completed);
    void Stop();
    void StopWithoutResult();
}

/// <summary>录音开始时的前台窗口；插入前据此判断焦点是否已变化（F-10）。
/// 进程 id 用来防止窗口句柄被系统复用给另一个窗口时误判为"焦点未变化"。</summary>
internal readonly record struct ForegroundTarget(IntPtr Window, uint ProcessId);

internal interface ITextInserting
{
    ForegroundTarget CaptureForegroundTarget();
    TextInsertionResult Insert(string text, ForegroundTarget expected);
    /// <summary>把文本写入剪贴板作为兜底；返回是否真正写入成功。</summary>
    bool CopyToClipboard(string text);
    /// <summary>目标窗口是否以更高权限运行（UIPI 会阻止向它注入输入）。</summary>
    ForegroundElevation CheckForegroundElevation();
}

/// <summary>单次录音会话的识别接缝（<see cref="Asr.LocalAsrSession"/> 的抽象）。</summary>
internal interface IDictationSession
{
    Action<string>? OnPartial { get; set; }
    Action<string>? OnFinal { get; set; }
    Action<string>? OnWarning { get; set; }
    Action<string>? OnError { get; set; }
    Action? OnSessionCapped { get; set; }
    /// <summary>ASR 已出结果、开始等待 LLM 纠错时触发一次。</summary>
    Action? OnCorrectionStarted { get; set; }
    /// <summary>松键后引擎仍在加载、会话在等它：true = 开始等待（已等了一小会儿，值得告诉用户），
    /// false = 等待结束、识别开始。每次等待最多各触发一次，只在触发过 true 之后才会触发 false。</summary>
    Action<bool>? OnEngineWait { get; set; }
    /// <summary>分阶段打点；收尾时由控制器并入 <see cref="DictationMetrics"/>。</summary>
    AsrSessionTimings Timings { get; }
    void SendAudio(byte[] data);
    /// <summary>正在等 LLM 纠错时放弃等待，直接以识别原文收尾；其他阶段无效果。</summary>
    void SkipCorrection();
    void FinalizeStream(TimeSpan timeout);
    void Close();
}

internal interface IDictationSessionFactory
{
    IDictationSession MakeSession(LlmCorrector? corrector);
    void SessionEnded();
}
