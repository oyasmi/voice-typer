import XCTest
@testable import VoiceTyper

/// `VoiceTyperController` 承载 R2-01（会话所有权）/R2-03（设备切换状态卡死）/R2-04
/// （暂停监听销毁会话）三个发布阻断/高优先级问题的全部状态机逻辑，此前零覆盖。
/// 用协议假实现驱动"热键 → 录音 → finalize → 插入"整条链，不碰麦克风、AX 与剪贴板。
@MainActor
final class VoiceTyperControllerTests: XCTestCase {
    // MARK: - 假实现

    private final class FakeHotkeyService: HotkeyListening {
        var onPress: (() -> Void)?
        var onRelease: (() -> Void)?
        var onCancel: (() -> Void)?
        private(set) var startCallCount = 0
        private(set) var stopCallCount = 0

        func start(with hotkey: HotkeyConfig) throws { startCallCount += 1 }
        func stop() { stopCallCount += 1 }
    }

    private final class FakeAudioCaptureService: AudioCapturing {
        var onChunk: (([Float]) -> Void)?
        var onTailChunk: (([Float]) -> Void)?
        var onLevel: ((Float) -> Void)?
        var onDeviceChanged: (() -> Void)?

        private(set) var startCallCount = 0
        private(set) var stopCallCount = 0
        private(set) var stopWithoutResultCallCount = 0
        var startError: Error?

        func start() throws {
            startCallCount += 1
            if let startError { throw startError }
        }

        /// 与真实实现一致：`stop()` 同步把（可能为空的）尾音通过 `onTailChunk` 交出去。
        func stop() {
            stopCallCount += 1
            onTailChunk?([])
        }

        func stopWithoutResult() {
            stopWithoutResultCallCount += 1
        }

        /// 模拟设备变化：真实 `AudioCaptureService` 会先自行 `stop()`（触发 onTailChunk），
        /// 随后才触发 `onDeviceChanged`。
        func simulateDeviceChange() {
            stop()
            onDeviceChanged?()
        }
    }

    private final class FakeTextInsertionService: TextInserting {
        private(set) var insertedTexts: [String] = []
        private(set) var copiedTexts: [String] = []
        var result: TextInsertionResult = .inserted

        func insert(text: String, expectedFrontmostPID: pid_t?) -> TextInsertionResult {
            insertedTexts.append(text)
            return result
        }

        func copyToClipboard(text: String) {
            copiedTexts.append(text)
        }
    }

    /// 可在测试中动态改写下一次 `recognize` 的返回文本/延迟/错误的假引擎。
    private final class ScriptedEngine: SenseVoiceRecognizing, @unchecked Sendable {
        private let lock = NSLock()
        private var text = ""
        private var delayNanoseconds: UInt64 = 0
        private var error: Error?

        func script(text: String = "", delayNanoseconds: UInt64 = 0, error: Error? = nil) {
            lock.lock()
            self.text = text
            self.delayNanoseconds = delayNanoseconds
            self.error = error
            lock.unlock()
        }

        func recognize(_ samples: [Float]) throws -> String {
            lock.lock()
            let (text, delay, error) = (self.text, self.delayNanoseconds, self.error)
            lock.unlock()
            if delay > 0 { Thread.sleep(forTimeInterval: Double(delay) / 1_000_000_000) }
            if let error { throw error }
            return text
        }
    }

    private final class FakeIdleScheduler: IdleUnloadScheduling {
        private(set) var scheduleCount = 0
        func schedule(afterSeconds interval: TimeInterval, _ handler: @escaping () -> Void) { scheduleCount += 1 }
        func cancel() {}
    }

    private struct StubError: Error {}

    // MARK: - 测试装配

    private struct Harness {
        let controller: VoiceTyperController
        let hotkey: FakeHotkeyService
        let audio: FakeAudioCaptureService
        let textInsertion: FakeTextInsertionService
        let engine: ScriptedEngine
        let idleScheduler: FakeIdleScheduler
        let recorder: Recorder

        var states: [AppState] { recorder.states }
        var warnings: [String] { recorder.warnings }
        var cancelledCount: Int { recorder.cancelledCount }
    }

    /// 引用类型：`onStateChange` 等回调需要跨 `makeHarness()` 的返回边界持续可见的
    /// 可变状态，值类型的 `Harness` 本身做不到（闭包捕获的是局部变量的一份拷贝）。
    private final class Recorder {
        var states: [AppState] = []
        var warnings: [String] = []
        var cancelledCount = 0
    }

    private func makeHarness() async -> Harness {
        let engine = ScriptedEngine()
        let scheduler = FakeIdleScheduler()
        let asrService = ASRService(
            idleScheduler: scheduler,
            modelLocate: { _ in
                let dummy = URL(fileURLWithPath: "/dev/null")
                return ModelBundle(
                    modelDir: dummy, onnxFileURL: dummy, tokensURL: dummy, cmvnURL: dummy,
                    quantized: true, fbankOptions: FbankOptions(), lfrM: 7, lfrN: 6
                )
            },
            engineFactory: { _, _, _ in engine }
        )
        asrService.updateConfig(ASRConfig(language: .auto, threads: 0, modelDir: "", idleUnloadMinutes: 1))
        await asrService.preload()
        // preload() 的 await 只保证 state == .ready；实际把引擎实例写进 engineLock 保护的
        // 存储是另一次 asrQueue.async 派发，二者不同步。给它一点时间真正落地，避免测试
        // 意外撞上 LocalASRSession 的"引擎未就绪，轮询等待"分支引入的额外延迟。
        try? await Task.sleep(nanoseconds: 100_000_000)

        let hotkey = FakeHotkeyService()
        let audio = FakeAudioCaptureService()
        let textInsertion = FakeTextInsertionService()
        let controller = VoiceTyperController(
            config: AppConfig(),
            asrService: asrService,
            hotkeyService: hotkey,
            audioCaptureService: audio,
            textInsertionService: textInsertion
        )

        let recorder = Recorder()
        controller.onStateChange = { recorder.states.append($0) }
        controller.onPreviewWarning = { recorder.warnings.append($0) }
        controller.onCancelled = { recorder.cancelledCount += 1 }
        try? controller.start()

        return Harness(
            controller: controller, hotkey: hotkey, audio: audio,
            textInsertion: textInsertion, engine: engine, idleScheduler: scheduler, recorder: recorder
        )
    }

    /// 给控制器内部由热键回调派生出的 `Task { @MainActor in ... }` 一个运行机会。
    private func settle() async {
        try? await Task.sleep(nanoseconds: 50_000_000)
    }

    /// 按住超过 `VoiceTyperController.minimumRecordingDuration`（300ms），避免被短录音
    /// 过滤逻辑当成误触丢弃——测试里除非明确想验证短录音丢弃，否则按下后都要等这个。
    private func holdPastMinimumDuration() async {
        try? await Task.sleep(nanoseconds: 350_000_000)
    }

    // MARK: - R2-01：会话所有权 / 重叠听写

    func testOverlappingPressDuringRecognizingIsRejectedNotOverwritten() async {
        let harness = await makeHarness()
        harness.engine.script(text: "第一段", delayNanoseconds: 300_000_000)

        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?() // 进入 .recognizing，finalize 在飞
        await settle()
        XCTAssertEqual(harness.audio.startCallCount, 1)

        // 上一段还没识别完就再按热键：必须被拒绝，不能开始第二段采集。
        harness.hotkey.onPress?()
        await settle()

        XCTAssertEqual(harness.audio.startCallCount, 1, "识别中不应该开始第二段录音")
        XCTAssertEqual(harness.warnings.last, "上一段听写尚未完成")

        // 等第一段真正识别完，确认还是走完了它自己的流程。
        try? await Task.sleep(nanoseconds: 500_000_000)
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["第一段"])
    }

    /// R2-01 回归钉子：旧实现里第二段录音会直接覆盖 `asrSession` 强引用，
    /// 第一段的 `onFinal`/`onError` 永远不会触发，结果永久不上屏。
    func testSlowFinalizeThenQuickSecondPressStillDeliversFirstResultOnly() async {
        let harness = await makeHarness()
        harness.engine.script(text: "第一段", delayNanoseconds: 200_000_000)

        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        await settle()

        // 松键后立刻再按（间隔远小于识别耗时）：必须被拒绝而不是覆盖当前会话。
        harness.hotkey.onPress?()
        await settle()

        try? await Task.sleep(nanoseconds: 500_000_000)

        XCTAssertEqual(harness.textInsertion.insertedTexts, ["第一段"], "第一段结果必须上屏，不能被静默丢弃")
        XCTAssertEqual(harness.states.last, .idle)
    }

    // MARK: - R2-03：录音中设备变化不应卡在 .inserting

    func testDeviceChangeAfterReleaseStillResolvesToIdle() async {
        let harness = await makeHarness()
        harness.engine.script(text: "已识别文本")

        harness.hotkey.onPress?()
        await settle()
        harness.audio.simulateDeviceChange() // 相当于 AudioCaptureService 自己 stop() 后通知设备变化
        await settle()

        XCTAssertEqual(harness.warnings.last, "输入设备已变化，本次录音已结束")
        XCTAssertEqual(harness.states.last, .idle, "设备切换后最终必须回到 idle，不能卡在 inserting/recognizing")
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["已识别文本"])
    }

    func testDeviceChangeDuringRecordingBeforeReleaseStillResolvesToIdle() async {
        let harness = await makeHarness()
        harness.engine.script(text: "已识别文本")

        harness.hotkey.onPress?()
        await settle()
        harness.audio.simulateDeviceChange()
        await settle()
        // 用户随后才松键：设备变化时录音已经结束，这次松键应当是无操作。
        harness.hotkey.onRelease?()
        await settle()

        XCTAssertEqual(harness.states.last, .idle)
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["已识别文本"])
    }

    // MARK: - 各终止路径都恰好收尾一次，最终回到可再次录音的状态

    func testShortRecordingIsDiscardedAndReturnsToIdle() async {
        let harness = await makeHarness()
        let scheduleCountBefore = harness.idleScheduler.scheduleCount
        harness.hotkey.onPress?()
        await settle()
        harness.hotkey.onRelease?() // 立即松开，短于 300ms 阈值
        await settle()

        XCTAssertEqual(harness.states.last, .idle)
        XCTAssertEqual(harness.audio.stopWithoutResultCallCount, 1)
        XCTAssertEqual(
            harness.idleScheduler.scheduleCount - scheduleCountBefore, 1,
            "sessionEnded() 必须被调用且只调用一次"
        )
        XCTAssertTrue(harness.textInsertion.insertedTexts.isEmpty)

        // 收尾后必须能立刻开始下一段录音，证明 active 已正确清空。
        harness.engine.script(text: "下一段")
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 200_000_000)
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["下一段"])
    }

    func testEscCancelDuringRecordingReturnsToIdleWithCancelledCallback() async {
        let harness = await makeHarness()
        harness.hotkey.onPress?()
        await settle()
        harness.hotkey.onCancel?()
        await settle()

        XCTAssertEqual(harness.cancelledCount, 1)
        XCTAssertEqual(harness.audio.stopWithoutResultCallCount, 1)
        XCTAssertTrue(harness.textInsertion.insertedTexts.isEmpty)
    }

    func testAudioCaptureStartFailureEmitsErrorAndAllowsRetry() async {
        let harness = await makeHarness()
        harness.audio.startError = StubError()

        harness.hotkey.onPress?()
        await settle()

        guard case .error = harness.states.last else {
            XCTFail("采集启动失败必须以 .error 状态收尾，实际是 \(String(describing: harness.states.last))")
            return
        }

        // 收尾后必须能立刻重试。
        harness.audio.startError = nil
        harness.engine.script(text: "重试成功")
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["重试成功"])
    }

    func testRecognitionErrorEmitsErrorStateOnce() async {
        let harness = await makeHarness()
        harness.engine.script(error: StubError())

        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        guard case .error = harness.states.last else {
            XCTFail("识别失败必须以 .error 状态收尾，实际是 \(String(describing: harness.states.last))")
            return
        }
        XCTAssertTrue(harness.textInsertion.insertedTexts.isEmpty)

        // 收尾后必须能立刻开始下一段录音。
        harness.engine.script(text: "恢复正常")
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["恢复正常"])
    }

    /// stop()：控制器整体停止时若识别仍在飞，必须立刻收尾（不发额外状态变化），
    /// 且迟到的 `onFinal` 不能在 `close()` 之后还把文本插进去（幂等性验证）。
    func testStopWhileRecognitionInFlightTearsDownSessionSilently() async {
        let harness = await makeHarness()
        harness.engine.script(text: "不应该上屏", delayNanoseconds: 300_000_000)

        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?() // 触发 finalize，识别在飞（阻塞 asrQueue 300ms）
        await settle()
        XCTAssertEqual(harness.states.last, .recognizing)

        let stateCountBeforeStop = harness.states.count
        harness.controller.stop()

        XCTAssertEqual(harness.states.count, stateCountBeforeStop, "stop() 不应该额外发出状态变化")

        // 等已在飞的（迟到的）识别任务跑完，确认它的回调因 session 已 close() 而是空操作。
        try? await Task.sleep(nanoseconds: 500_000_000)
        XCTAssertTrue(harness.textInsertion.insertedTexts.isEmpty, "stop() 之后迟到的识别结果不应该被插入")
    }
}
