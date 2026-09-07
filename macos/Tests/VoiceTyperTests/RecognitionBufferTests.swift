import XCTest
@testable import VoiceTyper

/// 用假引擎（不加载真实模型）验证 RecognitionBuffer 的滑窗调度逻辑：
/// 未超窗口时不滚动、超窗口时按 previewWindowSamples/2 就近下刀、
/// finalize 永远对完整音频重新整段识别。
final class RecognitionBufferTests: XCTestCase {
    private final class FakeEngine: SenseVoiceRecognizing, @unchecked Sendable {
        private let lock = NSLock()
        private(set) var callLengths: [Int] = []

        func recognize(_ samples: [Float]) throws -> String {
            lock.lock()
            let idx = callLengths.count
            callLengths.append(samples.count)
            lock.unlock()
            return "[call\(idx):\(samples.count)]"
        }
    }

    func testSampleCountTracksAppendedAudio() {
        let engine = FakeEngine()
        let buffer = RecognitionBuffer(engine: engine)
        buffer.append([Float](repeating: 0, count: 1000))
        buffer.append([Float](repeating: 0, count: 500))
        XCTAssertEqual(buffer.sampleCount, 1500)
    }

    func testPreviewBelowWindowDoesNotRoll() throws {
        let engine = FakeEngine()
        let buffer = RecognitionBuffer(engine: engine)
        buffer.append([Float](repeating: 0, count: 5000))

        let text = try buffer.preview()

        XCTAssertEqual(engine.callLengths, [5000], "未超窗口时只应对全部累积音频识别一次")
        XCTAssertEqual(text, "[call0:5000]")
    }

    func testPreviewAboveWindowRollsOnceAndCommitsPrefix() throws {
        let engine = FakeEngine()
        let buffer = RecognitionBuffer(engine: engine)
        let totalSamples = 250_000 // > previewWindowSamples (240_000)
        buffer.append([Float](repeating: 0, count: totalSamples))

        let text = try buffer.preview()

        // 全零音频下，findSeam 的能量比较全等，第一个候选点获胜：
        // target = 0 + 240_000/2 = 120_000，lo = 120_000 - 1_600 = 118_400。
        XCTAssertEqual(engine.callLengths, [118_400, 131_600])
        XCTAssertEqual(text, "[call0:118400][call1:131600]", "预览文本应是已固化前缀 + 窗口内识别结果")
    }

    /// VAD 会跳过"整段静音"的预览，于是两次预览之间可能积累了**超过一个窗口**的音频。
    /// 那时必须连续滚动直到窗口右侧回到阈值以内——只滚一次的话，窗口会持续超长，
    /// 每次预览的推理耗时随录音时长一路涨上去。
    func testPreviewRollsRepeatedlyWhenBacklogExceedsMultipleWindows() throws {
        let engine = FakeEngine()
        let buffer = RecognitionBuffer(engine: engine)
        // 一次性灌入约 3 个窗口的音频，模拟长时间没有触发预览之后突然恢复。
        buffer.append([Float](repeating: 0, count: RecognitionBuffer.previewWindowSamples * 3))

        _ = try buffer.preview()

        XCTAssertGreaterThan(engine.callLengths.count, 2, "积压超过一个窗口时应连续滚动，而不是只滚一次")
        // 最后一次调用是窗口内的识别，长度必须已经回落到窗口以内。
        XCTAssertLessThanOrEqual(
            engine.callLengths.last ?? .max,
            RecognitionBuffer.previewWindowSamples,
            "滚动结束后窗口右侧不应再超过窗口长度"
        )
    }

    func testReservedCapacityDoesNotChangeContents() {
        let engine = FakeEngine()
        let buffer = RecognitionBuffer(engine: engine, reservedSampleCapacity: 1_920_000)
        buffer.append([Float](repeating: 0.5, count: 1_000))
        XCTAssertEqual(buffer.sampleCount, 1_000, "预留容量只影响分配策略，不应改变已累积的样本数")
    }

    func testSecondPreviewAfterRollDoesNotRollAgainImmediately() throws {
        let engine = FakeEngine()
        let buffer = RecognitionBuffer(engine: engine)
        buffer.append([Float](repeating: 0, count: 250_000))
        _ = try buffer.preview() // 触发一次滚动

        _ = try buffer.preview() // 音频没有新增，窗口内仍在阈值以下，不应再滚动

        XCTAssertEqual(engine.callLengths, [118_400, 131_600, 131_600], "第二次 preview 只重跑窗口内未固化部分")
    }

    func testFinalizeAlwaysRecognizesFullAudioIndependentOfPreview() throws {
        let engine = FakeEngine()
        let buffer = RecognitionBuffer(engine: engine)
        buffer.append([Float](repeating: 0, count: 250_000))
        _ = try buffer.preview() // 产生固化前缀，finalize 不应复用它

        let finalText = try buffer.finalize()

        XCTAssertEqual(engine.callLengths.last, 250_000, "finalize 必须对完整音频整段重跑")
        XCTAssertEqual(finalText, "[call2:250000]")
    }
}
