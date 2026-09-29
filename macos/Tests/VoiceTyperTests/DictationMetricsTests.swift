import XCTest
@testable import VoiceTyper

/// 耗时摘要是排障与后续优化的实测依据：格式要稳定，且绝不能带用户文本。
final class DictationMetricsTests: XCTestCase {
    private let ms: UInt64 = 1_000_000

    func testSummaryLineWithAllFields() {
        let pressed: UInt64 = 1_000 * ms
        var metrics = DictationMetrics(sessionID: 0x7f3a, mode: .hold, hotkey: .fn, pressedAt: pressed)
        metrics.outcome = .inserted
        metrics.captureStartedAt = pressed + 41 * ms
        metrics.firstBufferAt = pressed + 162 * ms
        metrics.releasedAt = pressed + 4_300 * ms
        metrics.finalizeCalledAt = pressed + 4_303 * ms
        metrics.doneAt = pressed + 4_300 * ms + 921 * ms
        metrics.insertNanos = 34 * ms
        metrics.insertPath = .accessibility
        metrics.inputTransport = .builtIn
        metrics.inputSwitchedByAuto = true
        metrics.timings.receivedSamples = 67_200
        metrics.timings.finalizeStartedAt = 5_000 * ms
        metrics.timings.asrCompletedAt = 5_071 * ms
        metrics.timings.llmStartedAt = 5_071 * ms
        metrics.timings.llmCompletedAt = 5_071 * ms + 812 * ms
        metrics.timings.llmResult = .corrected
        metrics.timings.previewRuns = 7
        metrics.timings.previewSkipped = 3
        metrics.timings.previewMaxNanos = 88 * ms

        XCTAssertEqual(
            metrics.summaryLine(),
            "dictation session=7f3a outcome=inserted mode=hold hotkey=fn input=builtin* capture_start=41 "
                + "first_buffer=162 audio=4.2s release_to_finalize=3 engine_wait=0 asr=71 llm=812 "
                + "llm_result=corrected llm_retry=0 insert=34 insert_path=ax release_to_done=921 "
                + "previews=7 previews_skipped=3 preview_max=88 cold=0"
        )
    }

    func testMissingValuesAreRenderedAsDash() {
        let metrics = DictationMetrics(sessionID: 1, mode: .toggle, hotkey: .modifier, pressedAt: 0)
        XCTAssertEqual(
            metrics.summaryLine(),
            "dictation session=0001 outcome=failed mode=toggle hotkey=modifier input=- capture_start=- "
                + "first_buffer=- audio=0.0s release_to_finalize=- engine_wait=- asr=- llm=- llm_result=- "
                + "llm_retry=- insert=- insert_path=- release_to_done=- previews=0 previews_skipped=0 "
                + "preview_max=- cold=0"
        )
    }

    func testIntervalNeverGoesNegative() {
        // 上一段会话迟到的首帧时间早于本次按下：必须显示 `-` 而不是下溢成一个巨大的数。
        var metrics = DictationMetrics(sessionID: 2, mode: .hold, hotkey: .combo, pressedAt: 500 * ms)
        metrics.firstBufferAt = 100 * ms
        XCTAssertTrue(metrics.summaryLine().contains("first_buffer=-"))
    }

    func testLLMOffAndRetryFlags() {
        var metrics = DictationMetrics(sessionID: 3, mode: .hold, hotkey: .fn, pressedAt: 0)
        metrics.timings.llmResult = .off
        XCTAssertTrue(metrics.summaryLine().contains("llm_result=off llm_retry=0"))
        metrics.timings.llmResult = .fellBack
        metrics.timings.llmRetriedWithoutThinking = true
        XCTAssertTrue(metrics.summaryLine().contains("llm_result=fell_back llm_retry=1"))
    }

    /// 钉死"不记文本"：摘要以 `.public` 写入系统日志，结构体里不允许出现任何 String 存储属性
    /// （包括 Optional<String>），只能是数字、布尔与枚举。
    func testMetricsContainNoStringStoredProperties() {
        func assertNoStrings(_ value: Any, path: String) {
            for child in Mirror(reflecting: value).children {
                let label = "\(path).\(child.label ?? "?")"
                let typeName = String(describing: type(of: child.value))
                XCTAssertFalse(typeName.contains("String"), "\(label) 是 \(typeName)，不允许承载文本")
                if child.value is ASRSessionTimings { assertNoStrings(child.value, path: label) }
            }
        }
        assertNoStrings(DictationMetrics(sessionID: 1, mode: .hold, hotkey: .fn, pressedAt: 0), path: "metrics")
        assertNoStrings(ASRSessionTimings(), path: "timings")
    }
}
