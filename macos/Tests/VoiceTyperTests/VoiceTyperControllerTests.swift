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
        var onGestureCancelled: (() -> Void)?
        var acceptsCancelWhenInactive = false
        private(set) var startCallCount = 0
        private(set) var stopCallCount = 0
        /// 非 nil 时，下一次 `start` 抛出该错误（模拟 tap 创建失败/超时）。
        var startError: Error?

        func start(with hotkey: HotkeyConfig) throws {
            startCallCount += 1
            if let startError { throw startError }
        }
        func stop() {
            stopCallCount += 1
            acceptsCancelWhenInactive = false
        }
    }

    private final class FakeAudioCaptureService: AudioCapturing {
        var onChunk: (([Float]) -> Void)?
        var onTailChunk: (([Float]) -> Void)?
        var onLevel: ((Float) -> Void)?
        var onDeviceChanged: (() -> Void)?

        private(set) var startCallCount = 0
        private(set) var stopCallCount = 0
        private(set) var stopWithoutResultCallCount = 0
        private(set) var startedPolicies: [AudioInputPolicy] = []
        var startError: Error?
        var activeInputDevice: ActiveInputDevice?

        func start(inputPolicy: AudioInputPolicy) throws {
            startCallCount += 1
            startedPolicies.append(inputPolicy)
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
        var result: TextInsertionResult = .inserted(.accessibility)
        /// 插入过程中触发，供测试推进假时钟。
        var onInsert: (() -> Void)?

        func insert(text: String, expectedFrontmostPID: pid_t?) -> TextInsertionResult {
            insertedTexts.append(text)
            onInsert?()
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

    private final class FakeFocusInspector: FocusInspecting {
        var isSecure = false
        func isFocusedElementSecure() -> Bool { isSecure }
    }

    private final class FakeIdleScheduler: IdleUnloadScheduling {
        private(set) var scheduleCount = 0
        func schedule(afterSeconds interval: TimeInterval, _ handler: @escaping () -> Void) { scheduleCount += 1 }
        func cancel() {}
    }

    private struct StubError: Error {}

    /// 只在测试显式推进时才前进的假单调时钟（毫秒粒度）。
    private final class FakeClock: @unchecked Sendable {
        private let lock = NSLock()
        private var nanos: UInt64 = 0
        func advance(ms: UInt64) { lock.lock(); nanos += ms * 1_000_000; lock.unlock() }
        var clock: MonotonicClock {
            { [self] in lock.lock(); defer { lock.unlock() }; return nanos }
        }
    }

    // MARK: - 测试装配

    private struct Harness {
        let controller: VoiceTyperController
        let hotkey: FakeHotkeyService
        let audio: FakeAudioCaptureService
        let textInsertion: FakeTextInsertionService
        let engine: ScriptedEngine
        let idleScheduler: FakeIdleScheduler
        let focus: FakeFocusInspector
        let recorder: Recorder

        var states: [AppState] { recorder.states }
        var warnings: [String] { recorder.warnings }
        var cancelledCount: Int { recorder.cancelledCount }
        var emptyRecognitionCount: Int { recorder.emptyRecognitionCount }
        var blockedReasons: [String] { recorder.blockedReasons }
        var metrics: [DictationMetrics] { recorder.metrics }
        var results: [(text: String, kind: DictationHistory.Kind)] { recorder.results }
        var notes: [VoiceTyperController.InsertionNote?] { recorder.notes }
        var secureRefusals: Int { recorder.secureRefusals }
        var previews: [String] { recorder.previews }
    }

    /// 引用类型：`onStateChange` 等回调需要跨 `makeHarness()` 的返回边界持续可见的
    /// 可变状态，值类型的 `Harness` 本身做不到（闭包捕获的是局部变量的一份拷贝）。
    private final class Recorder {
        var states: [AppState] = []
        var warnings: [String] = []
        var cancelledCount = 0
        var emptyRecognitionCount = 0
        var blockedReasons: [String] = []
        var metrics: [DictationMetrics] = []
        var results: [(text: String, kind: DictationHistory.Kind)] = []
        var notes: [VoiceTyperController.InsertionNote?] = []
        var secureRefusals = 0
        var previews: [String] = []
    }

    private func makeHarness(
        config: AppConfig = AppConfig(),
        blockedReasonBeforeStart: String? = nil,
        clock: @escaping MonotonicClock = systemMonotonicClock,
        llmCorrector: LLMCorrector? = nil,
        watchdogTimeout: @escaping (Double) -> TimeInterval = VoiceTyperController.finalizeWatchdogTimeout(audioSeconds:),
        configureAudio: (FakeAudioCaptureService) -> Void = { _ in }
    ) async -> Harness {
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
        configureAudio(audio)
        let textInsertion = FakeTextInsertionService()
        let focus = FakeFocusInspector()
        let controller = VoiceTyperController(
            config: config,
            asrService: asrService,
            hotkeyService: hotkey,
            audioCaptureService: audio,
            textInsertionService: textInsertion,
            focusInspector: focus,
            llmCorrectorOverride: llmCorrector,
            finalizeWatchdogTimeout: watchdogTimeout,
            now: clock
        )

        let recorder = Recorder()
        controller.onMetrics = { recorder.metrics.append($0) }
        controller.onStateChange = { recorder.states.append($0) }
        controller.onPreviewWarning = { recorder.warnings.append($0) }
        controller.onCancelled = { recorder.cancelledCount += 1 }
        controller.onEmptyRecognition = { recorder.emptyRecognitionCount += 1 }
        controller.onBlockedAttempt = { recorder.blockedReasons.append($0) }
        controller.onResultAvailable = { recorder.results.append(($0, $1)) }
        controller.onInsertedWithNote = { recorder.notes.append($0) }
        controller.onSecureFieldRefused = { recorder.secureRefusals += 1 }
        controller.onPreviewUpdate = { recorder.previews.append($0) }
        controller.blockedReason = blockedReasonBeforeStart
        try? controller.start()

        return Harness(
            controller: controller, hotkey: hotkey, audio: audio,
            textInsertion: textInsertion, engine: engine, idleScheduler: scheduler, focus: focus, recorder: recorder
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

    // MARK: - 恢复热键监听失败：isStarted 必须复位，不能"显示就绪但热键已死"

    func testResumeHotkeyListeningFailureResetsIsStarted() async {
        let harness = await makeHarness()
        XCTAssertTrue(harness.controller.isStarted)

        harness.controller.suspendHotkeyListening()
        harness.hotkey.startError = StubError()

        XCTAssertThrowsError(try harness.controller.resumeHotkeyListening())
        XCTAssertFalse(harness.controller.isStarted, "恢复失败后 isStarted 必须复位为 false，交由外层重试")

        // 恢复正常后，重新 start() 能把监听真正拉起。
        harness.hotkey.startError = nil
        XCTAssertNoThrow(try harness.controller.start())
        XCTAssertTrue(harness.controller.isStarted)
    }

    // MARK: - toggle 触发方式（按一次开始，再按一次结束）

    private var toggleConfig: AppConfig {
        var config = AppConfig()
        config.hotkey = HotkeyConfig(modifiers: [], key: "fn", mode: .toggle)
        return config
    }

    func testToggleModeStartsOnFirstPressAndFinishesOnSecondPress() async {
        let harness = await makeHarness(config: toggleConfig)
        harness.engine.script(text: "切换模式听写")

        harness.hotkey.onPress?()
        await settle()
        XCTAssertEqual(harness.audio.startCallCount, 1)
        XCTAssertEqual(harness.states.last, .recording)

        // toggle 模式下松开不是结束信号：必须仍在录音。
        harness.hotkey.onRelease?()
        await holdPastMinimumDuration()
        XCTAssertEqual(harness.audio.stopCallCount, 0, "toggle 模式下松开热键不应结束录音")
        XCTAssertEqual(harness.states.last, .recording)

        // 再按一次才结束。
        harness.hotkey.onPress?()
        try? await Task.sleep(nanoseconds: 300_000_000)
        XCTAssertEqual(harness.audio.stopCallCount, 1)
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["切换模式听写"])
        XCTAssertEqual(harness.states.last, .idle)
    }

    /// toggle 模式下的第二次按下只在"正在录音"时才是结束信号；识别阶段再按仍应被拒绝，
    /// 不能覆盖尚未收尾的上一段听写（R2-01 的不变量在新模式下同样成立）。
    func testToggleModePressDuringRecognizingIsRejected() async {
        let harness = await makeHarness(config: toggleConfig)
        harness.engine.script(text: "第一段", delayNanoseconds: 300_000_000)

        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onPress?() // 结束录音，进入 .recognizing
        await settle()
        XCTAssertEqual(harness.states.last, .recognizing)

        harness.hotkey.onPress?() // 识别中再按
        await settle()
        XCTAssertEqual(harness.audio.startCallCount, 1, "识别中不应开始第二段录音")
        XCTAssertEqual(harness.warnings.last, "上一段听写尚未完成")

        try? await Task.sleep(nanoseconds: 500_000_000)
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["第一段"])
    }

    func testToggleModeShortTapIsStillDiscardedAsAccidental() async {
        let harness = await makeHarness(config: toggleConfig)
        harness.hotkey.onPress?()
        await settle()
        harness.hotkey.onPress?() // 远短于 300ms 阈值
        await settle()

        XCTAssertTrue(harness.textInsertion.insertedTexts.isEmpty)
        XCTAssertEqual(harness.audio.stopWithoutResultCallCount, 1)
        XCTAssertEqual(harness.states.last, .idle)
    }

    // MARK: - 识别阶段（松手之后）按 Esc 取消

    func testEscDuringRecognizingCancelsAndSuppressesInsertion() async {
        let harness = await makeHarness()
        harness.engine.script(text: "不该上屏", delayNanoseconds: 400_000_000)

        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        await settle()
        XCTAssertEqual(harness.states.last, .recognizing)
        XCTAssertTrue(
            harness.hotkey.acceptsCancelWhenInactive,
            "进入识别阶段后必须打开 Esc 取消窗口，否则松手就再也没有反悔机会"
        )

        harness.hotkey.onCancel?()
        await settle()
        XCTAssertEqual(harness.cancelledCount, 1)
        XCTAssertFalse(harness.hotkey.acceptsCancelWhenInactive, "收尾后必须关闭取消窗口")

        // 等那次仍在飞的推理跑完，确认它的结果被丢弃而不是插入。
        try? await Task.sleep(nanoseconds: 600_000_000)
        XCTAssertTrue(harness.textInsertion.insertedTexts.isEmpty, "取消后迟到的识别结果不应被插入")
    }

    func testCancelWindowIsClosedAfterNormalCompletion() async {
        let harness = await makeHarness()
        harness.engine.script(text: "正常完成")

        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        XCTAssertEqual(harness.textInsertion.insertedTexts, ["正常完成"])
        XCTAssertFalse(harness.hotkey.acceptsCancelWhenInactive)
    }

    // MARK: - 空识别结果

    func testEmptyRecognitionReportsExplicitlyAndInsertsNothing() async {
        let harness = await makeHarness()
        harness.engine.script(text: "   \n  ")

        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        XCTAssertEqual(harness.emptyRecognitionCount, 1, "空结果必须有显式回调，不能静默回 idle")
        XCTAssertTrue(harness.textInsertion.insertedTexts.isEmpty)
        XCTAssertEqual(harness.states.last, .idle)
    }

    // MARK: - 静音探测

    private static let silenceWarning = "没有检测到声音，请检查麦克风与输入设备"

    /// 麦克风被静音 / 选错输入设备时，用户会对着一个什么都没录到的会话一直说，
    /// 直到松手才发现结果是空的。电平在第一秒就已经给出信号，不该等到那时才提示。
    func testSilentRecordingWarnsAboutInputDevice() async {
        let harness = await makeHarness()
        harness.hotkey.onPress?()
        await settle()

        // 第一次探测在 1.5s；留出余量。
        try? await Task.sleep(nanoseconds: 1_800_000_000)
        XCTAssertTrue(
            harness.warnings.contains(Self.silenceWarning),
            "录音一直没有电平时必须主动提示，实际警告：\(harness.warnings)"
        )
        harness.hotkey.onRelease?()
        await settle()
    }

    func testAudibleRecordingDoesNotWarn() async {
        let harness = await makeHarness()
        harness.hotkey.onPress?()
        await settle()
        harness.audio.onLevel?(0.3)

        try? await Task.sleep(nanoseconds: 1_800_000_000)
        XCTAssertFalse(harness.warnings.contains(Self.silenceWarning))
        harness.hotkey.onRelease?()
        await settle()
    }

    /// 录音正常结束后探针必须作废：否则一次 0.4 秒的短听写会在一秒多之后
    /// 莫名其妙地弹出"没有检测到声音"。
    func testSilenceProbeIsCancelledWhenRecordingEnds() async {
        let harness = await makeHarness()
        harness.engine.script(text: "很短的一句")
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()

        try? await Task.sleep(nanoseconds: 1_600_000_000)
        XCTAssertFalse(harness.warnings.contains(Self.silenceWarning))
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["很短的一句"])
    }

    // MARK: - 门禁态：未就绪时按热键给出原因而不是毫无反应

    func testBlockedReasonRejectsRecordingAndReportsReason() async {
        let harness = await makeHarness()
        harness.controller.blockedReason = "还缺「麦克风」权限，暂时无法听写。"

        harness.hotkey.onPress?()
        await settle()

        XCTAssertEqual(harness.audio.startCallCount, 0, "门禁态不应开始录音")
        XCTAssertEqual(harness.blockedReasons, ["还缺「麦克风」权限，暂时无法听写。"])

        // 解除门禁后必须能立刻正常听写。
        harness.controller.blockedReason = nil
        harness.engine.script(text: "解除后可用")
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["解除后可用"])
    }

    /// 门禁态下 `start()` 不能发 `.idle`：那会把协调器的"待授权/下载中"覆盖成"就绪"，
    /// 菜单栏显示与真实能力脱节。
    func testStartWhileBlockedDoesNotEmitIdle() async {
        let harness = await makeHarness(blockedReasonBeforeStart: "模型还在下载")
        XCTAssertTrue(harness.controller.isStarted)
        XCTAssertFalse(harness.states.contains(.idle), "门禁态启动不应发出 .idle")
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

    // MARK: - A7：会话耗时度量

    func testNormalDictationReportsExactlyOneMetricsRecord() async {
        let fake = FakeClock()
        let harness = await makeHarness(clock: fake.clock)
        harness.engine.script(text: "你好")
        harness.textInsertion.onInsert = { fake.advance(ms: 50) }

        harness.hotkey.onPress?()
        await settle()
        fake.advance(ms: 120)
        harness.audio.onLevel?(0.2) // 首帧
        await settle()
        fake.advance(ms: 3_000)
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        XCTAssertEqual(harness.metrics.count, 1)
        let metrics = harness.metrics[0]
        XCTAssertEqual(metrics.outcome, .inserted)
        XCTAssertEqual(metrics.mode, .hold)
        XCTAssertEqual(metrics.hotkey, .fn)
        XCTAssertEqual(metrics.insertPath, .accessibility)
        XCTAssertEqual(metrics.insertNanos, 50_000_000)
        XCTAssertEqual(metrics.captureStartedAt, metrics.pressedAt)
        XCTAssertEqual(metrics.firstBufferAt.map { $0 - metrics.pressedAt }, 120_000_000)
        // 松手到上屏：假时钟只在插入期间前进了 50ms。
        XCTAssertEqual(metrics.doneAt.flatMap { done in metrics.releasedAt.map { done - $0 } }, 50_000_000)
        XCTAssertTrue(metrics.summaryLine().contains("release_to_done=50"))
        XCTAssertTrue(metrics.summaryLine().contains("first_buffer=120"))
        XCTAssertEqual(metrics.timings.llmResult, .off)
    }

    func testShortRecordingReportsDiscarded() async {
        let harness = await makeHarness()
        harness.hotkey.onPress?()
        await settle()
        harness.hotkey.onRelease?() // < 300ms，被当作误触丢弃
        await settle()

        XCTAssertEqual(harness.metrics.map(\.outcome), [.discarded])
    }

    func testEscCancelReportsCancelled() async {
        let harness = await makeHarness()
        harness.hotkey.onPress?()
        await settle()
        harness.hotkey.onCancel?()
        await settle()

        XCTAssertEqual(harness.metrics.map(\.outcome), [.cancelled])
    }

    func testEmptyRecognitionReportsEmpty() async {
        let harness = await makeHarness()
        harness.engine.script(text: "")
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        XCTAssertEqual(harness.metrics.map(\.outcome), [.empty])
    }

    func testRecognitionFailureReportsFailed() async {
        let harness = await makeHarness()
        harness.engine.script(error: StubError())
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        XCTAssertEqual(harness.metrics.map(\.outcome), [.failed])
    }

    func testStopDoesNotReportMetrics() async {
        let harness = await makeHarness()
        harness.hotkey.onPress?()
        await settle()
        harness.controller.stop()
        await settle()

        XCTAssertTrue(harness.metrics.isEmpty, "stop() 不是一次完整听写，不应上报")
    }

    func testRejectedOverlappingPressProducesNoSecondRecord() async {
        let harness = await makeHarness()
        harness.engine.script(text: "第一段", delayNanoseconds: 200_000_000)
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        await settle()
        harness.hotkey.onPress?() // 识别中再按：被拒绝
        await settle()
        try? await Task.sleep(nanoseconds: 500_000_000)

        XCTAssertEqual(harness.metrics.map(\.outcome), [.inserted])
    }

    func testStartFailureReportsStartFailed() async {
        let harness = await makeHarness(configureAudio: { $0.startError = StubError() })
        harness.hotkey.onPress?()
        await settle()

        XCTAssertEqual(harness.metrics.map(\.outcome), [.startFailed])
    }

    // MARK: - A2：输入设备策略

    func testControllerPassesConfiguredInputPolicyToCapture() async {
        var config = AppConfig()
        config.audio.inputDevice = "system"
        let harness = await makeHarness(config: config)
        harness.hotkey.onPress?()
        await settle()

        XCTAssertEqual(harness.audio.startedPolicies, [.systemDefault])
    }

    func testDefaultConfigUsesAutomaticPolicy() async {
        let harness = await makeHarness()
        harness.hotkey.onPress?()
        await settle()

        XCTAssertEqual(harness.audio.startedPolicies, [.automatic])
    }

    func testActiveInputDeviceFlowsIntoHUDNameAndMetrics() async {
        let harness = await makeHarness(configureAudio: { audio in
            audio.activeInputDevice = ActiveInputDevice(name: "MacBook Pro 麦克风", transport: .builtIn, switchedByAuto: true)
        })
        harness.hotkey.onPress?()
        await settle()
        XCTAssertEqual(harness.controller.recordingInputDeviceName, "MacBook Pro 麦克风")

        harness.hotkey.onCancel?()
        await settle()
        XCTAssertEqual(harness.metrics.first?.inputTransport, .builtIn)
        XCTAssertEqual(harness.metrics.first?.inputSwitchedByAuto, true)
        XCTAssertTrue(harness.metrics.first?.summaryLine().contains("input=builtin*") == true)
    }

    // MARK: - A3：单独修饰键手势

    private func modifierConfig(mode: HotkeyMode) -> AppConfig {
        var config = AppConfig()
        config.hotkey = HotkeyConfig(modifiers: [], key: "right_command", mode: mode)
        return config
    }

    /// hold：按住期间被用作组合快捷键 → 静默丢弃，不弹"已取消"，不插入。
    func testHoldGestureCancelSilentlyDiscardsRecording() async {
        let harness = await makeHarness(config: modifierConfig(mode: .hold))
        harness.engine.script(text: "不应上屏")

        harness.hotkey.onPress?()
        await settle()
        XCTAssertEqual(harness.states.last, .recording)

        harness.hotkey.onGestureCancelled?()
        await settle()

        XCTAssertEqual(harness.audio.stopWithoutResultCallCount, 1)
        XCTAssertEqual(harness.cancelledCount, 0, "组合快捷键不应显示\"已取消\"")
        XCTAssertTrue(harness.textInsertion.insertedTexts.isEmpty)
        XCTAssertEqual(harness.states.last, .idle)
        XCTAssertEqual(harness.metrics.map(\.outcome), [.gestureCancelled])
        XCTAssertEqual(harness.metrics.first?.hotkey, .modifier)
    }

    func testHoldModifierStillRecordsAndInsertsOnCleanTap() async {
        let harness = await makeHarness(config: modifierConfig(mode: .hold))
        harness.engine.script(text: "干净单击")

        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        XCTAssertEqual(harness.textInsertion.insertedTexts, ["干净单击"])
    }

    /// toggle：按下不动作，干净的单击（松开）才切换。
    func testToggleModifierOnlyActsOnReleaseOfACleanTap() async {
        let harness = await makeHarness(config: modifierConfig(mode: .toggle))
        harness.engine.script(text: "单击切换")

        harness.hotkey.onPress?()
        await settle()
        XCTAssertEqual(harness.audio.startCallCount, 0, "按下不应开始录音")

        harness.hotkey.onRelease?()
        await settle()
        XCTAssertEqual(harness.audio.startCallCount, 1)
        XCTAssertEqual(harness.states.last, .recording)

        await holdPastMinimumDuration()
        harness.hotkey.onRelease?() // 第二次干净单击：结束
        try? await Task.sleep(nanoseconds: 300_000_000)
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["单击切换"])
    }

    func testToggleModifierGestureCancelDoesNothing() async {
        let harness = await makeHarness(config: modifierConfig(mode: .toggle))

        harness.hotkey.onRelease?()
        await settle()
        XCTAssertEqual(harness.states.last, .recording)

        // 录音中按住右 ⌘ 再按 C：手势作废，但不影响 toggle 录音。
        harness.hotkey.onPress?()
        harness.hotkey.onGestureCancelled?()
        await settle()

        XCTAssertEqual(harness.states.last, .recording)
        XCTAssertEqual(harness.audio.stopWithoutResultCallCount, 0)
        XCTAssertEqual(harness.audio.stopCallCount, 0)
    }

    /// 门禁态下作为组合快捷键使用的修饰键不该弹提示；干净的单击才说明原因。
    func testModifierHotkeyOnlyReportsBlockedReasonOnCleanTap() async {
        let harness = await makeHarness(config: modifierConfig(mode: .hold), blockedReasonBeforeStart: "模型还在下载")

        harness.hotkey.onPress?()
        await settle()
        XCTAssertTrue(harness.blockedReasons.isEmpty)

        harness.hotkey.onRelease?()
        await settle()
        XCTAssertEqual(harness.blockedReasons, ["模型还在下载"])
    }

    // MARK: - R8-F2：上屏文本与预览统一格式化

    func testInsertedAndRecognizedTextAreFormatted() async {
        let harness = await makeHarness()
        harness.engine.script(text: "我用Python写代码")
        var recognized: [String] = []
        harness.controller.onRecognizedText = { recognized.append($0) }

        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        XCTAssertEqual(harness.textInsertion.insertedTexts, ["我用 Python 写代码"])
        XCTAssertEqual(recognized, ["我用 Python 写代码"])
        XCTAssertEqual(harness.results.map(\.text), ["我用 Python 写代码"])
        XCTAssertEqual(harness.results.map(\.kind), [.inserted])
    }

    func testPreviewCallbackReceivesFormattedText() async {
        let harness = await makeHarness()
        harness.engine.script(text: "我用Python写代码")
        harness.hotkey.onPress?()
        await settle()
        harness.audio.onChunk?([Float](repeating: 0.3, count: 9_600))
        try? await Task.sleep(nanoseconds: 300_000_000)

        XCTAssertTrue(harness.previews.contains("我用 Python 写代码"), "预览：\(harness.previews)")
        XCTAssertFalse(harness.previews.contains("我用Python写代码"))
        harness.hotkey.onCancel?()
        await settle()
    }

    // MARK: - R8-A1：看门狗时长与失败兜底

    func testFinalizeWatchdogTimeoutScalesWithAudioLength() {
        XCTAssertEqual(VoiceTyperController.finalizeWatchdogTimeout(audioSeconds: 2), 30)
        XCTAssertEqual(VoiceTyperController.finalizeWatchdogTimeout(audioSeconds: 6), 30)
        XCTAssertEqual(VoiceTyperController.finalizeWatchdogTimeout(audioSeconds: 10), 50)
        XCTAssertEqual(VoiceTyperController.finalizeWatchdogTimeout(audioSeconds: 60), 300)
        XCTAssertEqual(VoiceTyperController.finalizeWatchdogTimeout(audioSeconds: 120), 300, "封顶 300s")
    }

    /// 录音期间先让预览出一次文字，再让终稿失败 / 挂起。
    private func recordWithPreview(_ harness: Harness, previewText: String) async {
        harness.engine.script(text: previewText)
        harness.hotkey.onPress?()
        await settle()
        harness.audio.onChunk?([Float](repeating: 0.3, count: 9_600))
        try? await Task.sleep(nanoseconds: 250_000_000)
    }

    func testFinalizeTimeoutWithPreviewCopiesPartialAndRecordsHistory() async {
        let harness = await makeHarness(watchdogTimeout: { _ in 0.15 })
        await recordWithPreview(harness, previewText: "已识别Python部分")
        harness.engine.script(text: "迟到", delayNanoseconds: 700_000_000)
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 500_000_000)

        XCTAssertEqual(harness.textInsertion.copiedTexts, ["已识别 Python 部分"])
        XCTAssertEqual(harness.results.map(\.kind), [.partialAfterTimeout])
        XCTAssertEqual(harness.results.map(\.text), ["已识别 Python 部分"])
        guard case .error(let message) = harness.states.last else { return XCTFail("应以 .error 收尾") }
        XCTAssertEqual(message, "识别超时，已把识别出的部分复制到剪贴板")
        XCTAssertTrue(harness.metrics.first?.summaryLine().contains("finalize_timeout=1") == true)
        XCTAssertTrue(harness.textInsertion.insertedTexts.isEmpty)
    }

    func testOrdinaryFailureWithPreviewOnlyRecordsHistoryAndKeepsClipboard() async {
        let harness = await makeHarness()
        await recordWithPreview(harness, previewText: "已识别的部分")
        harness.engine.script(error: StubError())
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        XCTAssertTrue(harness.textInsertion.copiedTexts.isEmpty, "非超时失败不得覆盖用户剪贴板（R8-D-5）")
        XCTAssertEqual(harness.results.map(\.kind), [.partialAfterFailure])
        guard case .error(let message) = harness.states.last else { return XCTFail("应以 .error 收尾") }
        XCTAssertTrue(message.hasSuffix("可在菜单栏「最近结果」里找回已识别的部分"), message)
    }

    func testFailureWithoutPreviewRecordsNothingAndCopiesNothing() async {
        let harness = await makeHarness()
        harness.engine.script(error: StubError())
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        XCTAssertTrue(harness.results.isEmpty)
        XCTAssertTrue(harness.textInsertion.copiedTexts.isEmpty)
        guard case .error(let message) = harness.states.last else { return XCTFail("应以 .error 收尾") }
        XCTAssertFalse(message.contains("最近结果"))
    }

    // MARK: - R8-A2：各结局是否进入「最近结果」

    func testFocusChangedAndInsertFailedAreRecordedWithTheirKinds() async {
        let harness = await makeHarness()
        harness.engine.script(text: "第一句")
        harness.textInsertion.result = .focusChanged
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        harness.engine.script(text: "第二句")
        harness.textInsertion.result = .failed
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        XCTAssertEqual(harness.results.map(\.kind), [.copiedFocusChanged, .copiedInsertFailed])
        XCTAssertEqual(harness.textInsertion.copiedTexts, ["第一句", "第二句"])
    }

    func testCancelWithPreviewIsRecordedAndCancelWithoutPreviewIsNot() async {
        let withPreview = await makeHarness()
        await recordWithPreview(withPreview, previewText: "说到一半")
        withPreview.hotkey.onCancel?()
        await settle()
        XCTAssertEqual(withPreview.results.map(\.kind), [.cancelled])
        XCTAssertEqual(withPreview.results.map(\.text), ["说到一半"])

        let withoutPreview = await makeHarness()
        withoutPreview.hotkey.onPress?()
        await settle()
        withoutPreview.hotkey.onCancel?()
        await settle()
        XCTAssertTrue(withoutPreview.results.isEmpty)
    }

    func testEmptyDiscardedAndGestureResultsAreNotRecorded() async {
        let harness = await makeHarness()
        harness.engine.script(text: "")
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        harness.hotkey.onPress?()
        await settle()
        harness.hotkey.onRelease?() // 短录音丢弃
        await settle()
        XCTAssertTrue(harness.results.isEmpty)
    }

    // MARK: - R8-C1：校对回落说明

    func testInsertionNoteMapsEachFallbackReason() {
        func note(_ reason: LLMCorrector.FallbackReason?) -> VoiceTyperController.InsertionNote? {
            var timings = ASRSessionTimings()
            timings.llmResult = .fellBack
            timings.llmFallbackReason = reason
            return VoiceTyperController.insertionNote(for: timings)
        }
        XCTAssertEqual(note(.timeout)?.message, "智能校对超时，已输入识别原文。")
        XCTAssertEqual(note(.network)?.message, "无法连接智能校对服务，已输入识别原文。")
        XCTAssertEqual(note(.auth)?.message, "智能校对鉴权失败，请到「设置 → 识别」检查 API Key。已输入识别原文。")
        XCTAssertEqual(note(.rateLimited)?.message, "智能校对请求过于频繁，已输入识别原文。")
        XCTAssertEqual(note(.server)?.message, "智能校对服务出错，已输入识别原文。")
        XCTAssertEqual(note(.emptyResult)?.message, "智能校对没有返回有效结果，已输入识别原文。")
        XCTAssertEqual(note(nil)?.message, "智能校对服务出错，已输入识别原文。")

        XCTAssertEqual(note(.auth)?.showsWhenHUDHidden, true, "鉴权失败是配置问题，浮窗不显示时也要提示")
        for reason in [LLMCorrector.FallbackReason.timeout, .network, .rateLimited, .server, .emptyResult] {
            XCTAssertEqual(note(reason)?.showsWhenHUDHidden, false, "\(reason)")
        }
    }

    func testInsertionNoteIsNilForCorrectedOffAndMissingResult() {
        for result in [ASRSessionTimings.LLMResult.corrected, .off] {
            var timings = ASRSessionTimings()
            timings.llmResult = result
            XCTAssertNil(VoiceTyperController.insertionNote(for: timings))
        }
        XCTAssertNil(VoiceTyperController.insertionNote(for: ASRSessionTimings()))
    }

    func testSkippedCorrectionNoteIsNotAHUDHiddenException() {
        var timings = ASRSessionTimings()
        timings.llmResult = .skipped
        let note = VoiceTyperController.insertionNote(for: timings)
        XCTAssertEqual(note?.message, "已跳过智能校对")
        XCTAssertEqual(note?.showsWhenHUDHidden, false)
    }

    func testNormalInsertionReportsNilNote() async {
        let harness = await makeHarness()
        harness.engine.script(text: "正常")
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        XCTAssertEqual(harness.notes.count, 1)
        XCTAssertNil(harness.notes[0])
    }

    // MARK: - R8-C2：校对中再按热键跳过

    private func makeGatedCorrector(gate: DispatchSemaphore) -> LLMCorrector {
        StubURLProtocol.handler = { _ in
            _ = gate.wait(timeout: .now() + 5)
            return (200, #"{"choices":[{"message":{"content":"校对后的文本"},"finish_reason":"stop"}]}"#)
        }
        let sessionConfig = URLSessionConfiguration.ephemeral
        sessionConfig.protocolClasses = [StubURLProtocol.self]
        return LLMCorrector(
            config: LLMCorrector.Config(
                chatCompletionsURL: LLMEndpoint.chatCompletionsURL(from: "https://stub.invalid/v1")!,
                apiKey: "k", model: "m", temperature: 0, maxTokens: 800, timeout: 5
            ),
            urlSession: URLSession(configuration: sessionConfig)
        )
    }

    func testPressDuringCorrectionSkipsAndInsertsRawText() async {
        let gate = DispatchSemaphore(value: 0)
        defer { StubURLProtocol.handler = nil }
        let harness = await makeHarness(llmCorrector: makeGatedCorrector(gate: gate))
        harness.engine.script(text: "识别原文")
        var correctionStarted = 0
        harness.controller.onCorrectionStarted = { correctionStarted += 1 }

        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)
        XCTAssertEqual(correctionStarted, 1)
        XCTAssertTrue(harness.textInsertion.insertedTexts.isEmpty)

        harness.hotkey.onPress?() // 校对中再按：跳过
        await settle()
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["识别原文"])
        XCTAssertEqual(harness.audio.startCallCount, 1, "跳过校对不应开始新的录音")
        XCTAssertEqual(harness.notes.compactMap { $0?.message }, ["已跳过智能校对"])
        XCTAssertEqual(harness.metrics.first?.timings.llmResult, .skipped)

        harness.hotkey.onRelease?() // hold 模式随后到达的松开无效
        gate.signal()
        try? await Task.sleep(nanoseconds: 200_000_000)
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["识别原文"], "迟到的校对结果被忽略")
    }

    func testModifierOnlyHotkeyOnlySkipsOnCleanTapRelease() async {
        let gate = DispatchSemaphore(value: 0)
        defer { StubURLProtocol.handler = nil }
        let harness = await makeHarness(
            config: modifierConfig(mode: .hold), llmCorrector: makeGatedCorrector(gate: gate)
        )
        harness.engine.script(text: "识别原文")

        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)
        XCTAssertTrue(harness.textInsertion.insertedTexts.isEmpty, "校对中")

        harness.hotkey.onPress?() // 单独修饰键按下：可能是组合快捷键，不能跳过
        await settle()
        XCTAssertTrue(harness.textInsertion.insertedTexts.isEmpty)
        XCTAssertFalse(harness.warnings.contains("上一段听写尚未完成"), "按下时不应骚扰")

        harness.hotkey.onRelease?() // 干净单击
        await settle()
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["识别原文"])
        gate.signal()
    }

    func testPressWhileRecognizingWithoutCorrectionStillWarns() async {
        let harness = await makeHarness()
        harness.engine.script(text: "第一段", delayNanoseconds: 300_000_000)
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        await settle()
        harness.hotkey.onPress?()
        await settle()
        XCTAssertEqual(harness.warnings.last, "上一段听写尚未完成")
        try? await Task.sleep(nanoseconds: 500_000_000)
    }

    // MARK: - R8-B2：密码框

    func testSecureFieldRefusesRecordingAndReportsSecureFieldOutcome() async {
        let harness = await makeHarness()
        harness.focus.isSecure = true

        harness.hotkey.onPress?()
        await settle()

        XCTAssertEqual(harness.audio.startCallCount, 0, "密码框里不得开始录音")
        XCTAssertEqual(harness.secureRefusals, 1)
        XCTAssertEqual(harness.metrics.map(\.outcome), [.secureField])
        XCTAssertTrue(harness.metrics.first?.summaryLine().contains("outcome=secure_field") == true)

        // 离开密码框后立刻可以正常听写。
        harness.focus.isSecure = false
        harness.engine.script(text: "恢复")
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)
        XCTAssertEqual(harness.textInsertion.insertedTexts, ["恢复"])
    }

    func testModifierOnlyHoldAnnouncesSecureRefusalOnlyOnCleanTap() async {
        let harness = await makeHarness(config: modifierConfig(mode: .hold))
        harness.focus.isSecure = true

        harness.hotkey.onPress?()
        await settle()
        XCTAssertEqual(harness.secureRefusals, 0, "按下可能是组合快捷键，不提示")
        XCTAssertEqual(harness.audio.startCallCount, 0)

        harness.hotkey.onRelease?()
        await settle()
        XCTAssertEqual(harness.secureRefusals, 1)
    }

    func testFocusMovingToSecureFieldBeforeInsertionCopiesInstead() async {
        let harness = await makeHarness()
        harness.engine.script(text: "不能插进密码框")
        harness.hotkey.onPress?()
        await holdPastMinimumDuration()
        harness.focus.isSecure = true // 录音中途焦点移到了密码框
        harness.hotkey.onRelease?()
        try? await Task.sleep(nanoseconds: 300_000_000)

        XCTAssertTrue(harness.textInsertion.insertedTexts.isEmpty)
        XCTAssertEqual(harness.textInsertion.copiedTexts, ["不能插进密码框"])
        XCTAssertEqual(harness.results.map(\.kind), [.copiedFocusChanged])
        guard case .error(let message) = harness.states.last else { return XCTFail("应以 .error 收尾") }
        XCTAssertEqual(message, "目标是密码输入框，结果已复制到剪贴板")
    }
}
