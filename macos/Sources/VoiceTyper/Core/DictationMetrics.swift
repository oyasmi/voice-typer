import Foundation

/// 单调时钟（纳秒）。可注入，测试用假时钟；会在音频线程与 asrQueue 上被调用，必须线程安全。
typealias MonotonicClock = @Sendable () -> UInt64

let systemMonotonicClock: MonotonicClock = { DispatchTime.now().uptimeNanoseconds }

/// `LocalASRSession` 侧的分阶段打点，会话收尾时由控制器并入 `DictationMetrics`。
struct ASRSessionTimings: Equatable {
    enum LLMResult: String {
        case corrected
        case fellBack = "fell_back"
        case off
    }

    /// 会话实际接受的样本数（16kHz 单声道）。
    var receivedSamples = 0
    /// 会话开始收音时引擎是否尚未就绪。
    var coldAtStart = false
    /// finalize 时等待引擎加载的时长；引擎已就绪则为 0。
    var engineWaitNanos: UInt64 = 0
    var finalizeStartedAt: UInt64?
    var asrCompletedAt: UInt64?
    var llmStartedAt: UInt64?
    var llmCompletedAt: UInt64?
    var llmResult: LLMResult?
    /// 是否因服务拒绝 `thinking` 字段而重发过（A1）。
    var llmRetriedWithoutThinking = false
    var previewRuns = 0
    var previewSkipped = 0
    var previewMaxNanos: UInt64 = 0
}

/// 一次听写的耗时与结果摘要，收尾时以一行日志输出（`summaryLine()`）。
///
/// **本结构体禁止新增任何承载用户文本的字段**（识别文本、LLM 输出、窗口标题等）：
/// 摘要以 `.public` 隐私级别写入系统日志，只允许数字、布尔与枚举。
/// `DictationMetricsTests` 会用反射钉死"不存在 String 类型的存储属性"。
struct DictationMetrics {
    enum Outcome: String {
        case inserted
        case focusChanged = "focus_changed"
        case insertFailed = "insert_failed"
        case empty
        case cancelled
        case discarded
        case gestureCancelled = "gesture_cancelled"
        case failed
        case startFailed = "start_failed"
    }

    enum HotkeyKind: String {
        case fn
        case combo
        case modifier
    }

    /// 会话短 ID（4 位十六进制），仅用于把同一次听写的多行日志关联起来。
    var sessionID: UInt16
    var outcome: Outcome = .failed
    var mode: HotkeyMode
    var hotkey: HotkeyKind

    var pressedAt: UInt64
    var captureStartedAt: UInt64?
    var firstBufferAt: UInt64?
    var releasedAt: UInt64?
    var finalizeCalledAt: UInt64?
    var doneAt: UInt64?

    var insertNanos: UInt64?
    var insertPath: InsertionPath?
    var timings = ASRSessionTimings()

    /// 实际使用的输入设备类型；`inputSwitchedByAuto` 表示由「自动」策略从系统默认输入切换而来。
    var inputTransport: AudioTransport?
    var inputSwitchedByAuto = false

    init(sessionID: UInt16, mode: HotkeyMode, hotkey: HotkeyKind, pressedAt: UInt64) {
        self.sessionID = sessionID
        self.mode = mode
        self.hotkey = hotkey
        self.pressedAt = pressedAt
    }

    /// 单行摘要：字段顺序固定，缺失值输出 `-`。
    func summaryLine() -> String {
        let audioSeconds = String(format: "%.1f", Double(timings.receivedSamples) / 16000.0)
        let asrNanos = Self.interval(timings.finalizeStartedAt, timings.asrCompletedAt)
        let llmNanos = Self.interval(timings.llmStartedAt, timings.llmCompletedAt)
        let input = inputTransport.map { $0.rawValue + (inputSwitchedByAuto ? "*" : "") } ?? "-"

        let fields: [(String, String)] = [
            ("session", String(format: "%04x", sessionID)),
            ("outcome", outcome.rawValue),
            ("mode", mode.rawValue),
            ("hotkey", hotkey.rawValue),
            ("input", input),
            ("capture_start", Self.ms(Self.interval(pressedAt, captureStartedAt))),
            ("first_buffer", Self.ms(Self.interval(pressedAt, firstBufferAt))),
            ("audio", audioSeconds + "s"),
            ("release_to_finalize", Self.ms(Self.interval(releasedAt, finalizeCalledAt))),
            ("engine_wait", Self.ms(timings.finalizeStartedAt == nil ? nil : timings.engineWaitNanos)),
            ("asr", Self.ms(asrNanos)),
            ("llm", Self.ms(llmNanos)),
            ("llm_result", timings.llmResult?.rawValue ?? "-"),
            ("llm_retry", timings.llmResult == nil ? "-" : (timings.llmRetriedWithoutThinking ? "1" : "0")),
            ("insert", Self.ms(insertNanos)),
            ("insert_path", insertPath?.rawValue ?? "-"),
            ("release_to_done", Self.ms(Self.interval(releasedAt, doneAt))),
            ("previews", String(timings.previewRuns)),
            ("previews_skipped", String(timings.previewSkipped)),
            ("preview_max", timings.previewRuns == 0 ? "-" : Self.ms(timings.previewMaxNanos)),
            ("cold", timings.coldAtStart ? "1" : "0"),
        ]
        return "dictation " + fields.map { "\($0.0)=\($0.1)" }.joined(separator: " ")
    }

    private static func interval(_ from: UInt64?, _ to: UInt64?) -> UInt64? {
        guard let from, let to, to >= from else { return nil }
        return to - from
    }

    private static func ms(_ nanos: UInt64?) -> String {
        guard let nanos else { return "-" }
        return String(nanos / 1_000_000)
    }
}
