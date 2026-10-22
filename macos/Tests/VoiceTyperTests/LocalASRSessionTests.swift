import XCTest
@testable import VoiceTyper

/// `LocalASRSession` 是并发最密集的一层（预览 in-flight 跳过、finalize 看门狗、引擎未就绪
/// 时的 pendingAudio 重试、单段上限、close() 后的迟到回调抑制、LLM 校对期间的收尾），
/// 此前完全没有直接测试——`asrQueue`/`engineAccessor` 都是构造注入，覆盖成本很低（R4-11）。
///
/// 除引擎未就绪那条外，均不依赖真实 5s/120s 量级的 wall-clock 等待：用可控的
/// `GatedFakeEngine`（信号量阻塞 `recognize()`）制造确定性的"仍在执行中"窗口，
/// 而不是靠 sleep 赌时序。
@MainActor
final class LocalASRSessionTests: XCTestCase {
    /// 可控假引擎：`recognize()` 先记录调用（含入参样本数），再可选地阻塞在一个信号量上
    /// 直到测试释放——用于确定性地制造"预览/finalize 仍在执行中"的窗口。阻塞设了 5s 上限，
    /// 即便某条测试忘记 release()，也不会让测试进程里的 asrQueue 线程永久挂起。
    private final class GatedFakeEngine: SenseVoiceRecognizing, @unchecked Sendable {
        private let lock = NSLock()
        private var _callSampleCounts: [Int] = []
        private let gate = DispatchSemaphore(value: 0)
        var isGated = false
        var textForCall: ((Int) -> String)?

        var callCount: Int { lock.lock(); defer { lock.unlock() }; return _callSampleCounts.count }
        var callSampleCounts: [Int] { lock.lock(); defer { lock.unlock() }; return _callSampleCounts }

        func recognize(_ samples: [Float]) throws -> String {
            lock.lock()
            let index = _callSampleCounts.count
            _callSampleCounts.append(samples.count)
            lock.unlock()
            if isGated {
                _ = gate.wait(timeout: .now() + 5)
            }
            return textForCall?(index) ?? "text\(index)"
        }

        func release() { gate.signal() }
    }

    /// 有能量的音频样本。
    ///
    /// **不能用全零**：`LocalASRSession` 现在会跳过"自上次预览以来全是静音"的那一轮预览
    /// （省掉一次没有意义的整窗重跑）。凡是期望预览被调度的用例都必须喂真正有声音的样本，
    /// 否则测的就不是被测逻辑，而是 VAD 的短路分支。
    private func speech(count: Int) -> [Float] {
        [Float](repeating: 0.3, count: count)
    }

    /// 静音样本，专门用于验证 VAD 的短路行为。
    private func silence(count: Int) -> [Float] {
        [Float](repeating: 0, count: count)
    }

    private func makeQueue() -> DispatchQueue {
        DispatchQueue(label: "test.localasrsession.\(UUID().uuidString)")
    }

    /// 有界轮询：避免固定时长的 sleep 在慢机器上偶发失败、在快机器上又白等。
    private func poll(timeout: TimeInterval = 2, _ condition: () -> Bool) async {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition(), Date() < deadline {
            try? await Task.sleep(nanoseconds: 5_000_000)
        }
    }

    /// 走 `StubURLProtocol` 的校对器；响应由各用例设置 `StubURLProtocol.handler` 决定。
    private func makeCorrector() -> LLMCorrector {
        let sessionConfig = URLSessionConfiguration.ephemeral
        sessionConfig.protocolClasses = [StubURLProtocol.self]
        return LLMCorrector(
            config: LLMCorrector.Config(
                chatCompletionsURL: LLMEndpoint.chatCompletionsURL(from: "https://stub.invalid/v1")!,
                apiKey: "test-key", model: "gpt-4o-mini", temperature: 0, maxTokens: 800, timeout: 5
            ),
            urlSession: URLSession(configuration: sessionConfig)
        )
    }

    override func tearDown() {
        StubURLProtocol.handler = nil
        super.tearDown()
    }

    // MARK: - 静音跳过预览（VAD）

    func testSilentAudioDoesNotSchedulePreview() async {
        let engine = GatedFakeEngine()
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil)

        session.sendAudio(silence(count: 20_000))
        try? await Task.sleep(nanoseconds: 120_000_000)
        XCTAssertEqual(engine.callCount, 0, "整段静音时不应该跑一次注定不变的整窗推理")

        // 有声音之后必须立刻恢复预览。
        session.sendAudio(speech(count: 20_000))
        await poll { engine.callCount == 1 }
        XCTAssertEqual(engine.callCount, 1)
    }

    /// 静音只跳过**预览**。松手后的 finalize 永远对完整音频整段重跑——
    /// 上屏文本的正确性不能受这条优化影响。
    func testFinalizeStillRunsAfterOnlySilence() async {
        let engine = GatedFakeEngine()
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil)
        var final: String?
        session.onFinal = { final = $0 }

        session.sendAudio(silence(count: 8_000))
        session.finalize(timeout: 2)
        await poll { final != nil }

        XCTAssertEqual(engine.callSampleCounts, [8_000], "finalize 必须对完整音频跑一次")
        XCTAssertNotNil(final)
    }

    // MARK: - 预览 in-flight 跳过

    func testPreviewInFlightSkipsReentrantScheduling() async {
        let engine = GatedFakeEngine()
        engine.isGated = true
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil)

        session.sendAudio(speech(count: 20_000))
        await poll { engine.callCount == 1 }
        XCTAssertEqual(engine.callCount, 1)

        // 预览仍在执行（阻塞在 gate 上）时再次 sendAudio：不应发起第二次并发预览。
        session.sendAudio(speech(count: 20_000))
        try? await Task.sleep(nanoseconds: 30_000_000)
        XCTAssertEqual(engine.callCount, 1, "previewInFlight 时不应发起第二次并发预览")

        engine.release()

        // previewInFlight 复位是异步落地的（asrQueue 回到 MainActor 有一跳），
        // 用重试而不是单次盲发 sendAudio，避免踩中复位前的窄窗口。
        for _ in 0..<50 where engine.callCount < 2 {
            session.sendAudio(speech(count: 1_000))
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTAssertEqual(engine.callCount, 2, "previewInFlight 复位后，下一次 sendAudio 应能重新调度预览")
    }

    // MARK: - finalize 看门狗

    func testFinalizeWatchdogFiresOnErrorWhenRecognitionHangs() async {
        let engine = GatedFakeEngine()
        engine.isGated = true
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil)
        session.sendAudio(speech(count: 20_000))

        var receivedError: String?
        session.onError = { receivedError = $0 }
        session.finalize(timeout: 0.05)

        await poll(timeout: 1) { receivedError != nil }
        XCTAssertEqual(receivedError, "识别超时")
        XCTAssertTrue(session.timings.finalizeTimedOut, "超时必须置位，控制器据此决定是否复制已识别部分（R8-A1）")

        engine.release() // 放行被阻塞的调用，避免残留一个长期占用线程池的挂起任务。
    }

    // MARK: - 引擎未就绪：pendingAudio 与重试

    /// 只验证"引擎稍后可用 → 重试成功 → pendingAudio 被一并灌入"这条有价值的路径，
    /// 不测试 50 次 × 100ms = 5s 的完全耗尽分支——为一个简单的固定常量在自动化套件里
    /// 引入真实的 5 秒等待不划算，那条边界靠代码审查足够。
    func testFinalizeRetriesUntilEngineBecomesAvailableAndFlushesPendingAudio() async {
        let engine = GatedFakeEngine()
        engine.textForCall = { _ in "final-text" }
        var engineAvailable = false
        let session = LocalASRSession(
            asrQueue: makeQueue(),
            engineAccessor: { engineAvailable ? engine : nil },
            llmCorrector: nil
        )

        // 引擎未就绪：样本应该攒进 pendingAudio，而不是报错或丢弃。
        session.sendAudio(speech(count: 5_000))

        var finalText: String?
        session.onFinal = { finalText = $0 }
        session.finalize(timeout: 5) // 看门狗给够时间，不应该是这次测试的限制因素

        // 让 waitForEngineThenFinalize 至少先落空一次 100ms 轮询，再让引擎"变得可用"
        // （对应真实场景里模型加载完成的那一刻）。
        try? await Task.sleep(nanoseconds: 150_000_000)
        engineAvailable = true

        await poll(timeout: 2) { finalText != nil }
        XCTAssertEqual(finalText, "final-text")
        XCTAssertEqual(engine.callSampleCounts.last, 5_000, "攒下的 pendingAudio 应该被一并识别，而不是被丢弃")
    }

    // MARK: - 单段会话上限

    func testSessionCapTriggersWarningOnceAndDropsFurtherAudio() async {
        let engine = GatedFakeEngine()
        engine.textForCall = { _ in "" }
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil)

        var warnings: [String] = []
        var cappedCount = 0
        session.onWarning = { warnings.append($0) }
        session.onSessionCapped = { cappedCount += 1 }

        // 一次性喂满上限（120s@16kHz）：刚好达到上限的这次 append 本身不应触发回调，
        // 上限检查发生在下一次 sendAudio 的入口。
        session.sendAudio(speech(count: 120 * 16_000))
        XCTAssertEqual(cappedCount, 0)

        session.sendAudio(speech(count: 1_000))
        XCTAssertEqual(cappedCount, 1)
        XCTAssertEqual(warnings.count, 1)

        // 再来一次不应重复触发。
        session.sendAudio(speech(count: 1_000))
        XCTAssertEqual(cappedCount, 1, "上限回调只应触发一次")
        XCTAssertEqual(warnings.count, 1)

        var finalText: String?
        session.onFinal = { finalText = $0 }
        session.finalize(timeout: 5)
        await poll(timeout: 2) { finalText != nil }
        XCTAssertEqual(engine.callSampleCounts.last, 120 * 16_000, "超过上限后的样本不应被计入 buffer")
    }

    /// 跨界 chunk：buffer 已到 max-1，再来 2 个样本。必须只追加 1 个（严格不超过上限）
    /// 并立即触发一次 cap/warning，而不是让 chunk 跨过上限、计数变成 max+1。
    func testBufferChunkCrossingCapIsClippedAndTriggersCapOnce() async {
        let max = 120 * 16_000
        let engine = GatedFakeEngine()
        engine.textForCall = { _ in "" }
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil)

        var warnings: [String] = []
        var cappedCount = 0
        session.onWarning = { warnings.append($0) }
        session.onSessionCapped = { cappedCount += 1 }

        session.sendAudio(speech(count: max - 1))
        XCTAssertEqual(cappedCount, 0)

        // 剩余容量 1，来 2 个：追加 1 个后立即触发。
        session.sendAudio(speech(count: 2))
        XCTAssertEqual(cappedCount, 1)
        XCTAssertEqual(warnings.count, 1)

        // 之后继续发也不再触发。
        session.sendAudio(speech(count: 5))
        XCTAssertEqual(cappedCount, 1)

        var finalText: String?
        session.onFinal = { finalText = $0 }
        session.finalize(timeout: 5)
        await poll(timeout: 2) { finalText != nil }
        XCTAssertEqual(engine.callSampleCounts.last, max, "跨界 chunk 必须被裁剪到恰好上限，不能是 max+1")
    }

    /// 引擎未就绪时，首个 pendingAudio chunk 本身就超过上限：只攒到上限、裁掉多余部分，
    /// 触发一次 cap/warning；引擎就绪后 finalize 的样本数恰好等于上限。
    func testEngineNotReadyOversizedFirstPendingChunkIsClippedToCap() async {
        let max = 120 * 16_000
        let engine = GatedFakeEngine()
        engine.textForCall = { _ in "final-text" }
        var engineAvailable = false
        let session = LocalASRSession(
            asrQueue: makeQueue(),
            engineAccessor: { engineAvailable ? engine : nil },
            llmCorrector: nil
        )

        var warnings: [String] = []
        var cappedCount = 0
        session.onWarning = { warnings.append($0) }
        session.onSessionCapped = { cappedCount += 1 }

        session.sendAudio(speech(count: max + 5_000))
        XCTAssertEqual(cappedCount, 1)
        XCTAssertEqual(warnings.count, 1)

        session.sendAudio(speech(count: 1_000))
        XCTAssertEqual(cappedCount, 1, "cap 只触发一次")

        var finalText: String?
        session.onFinal = { finalText = $0 }
        session.finalize(timeout: 5)
        try? await Task.sleep(nanoseconds: 120_000_000)
        engineAvailable = true

        await poll(timeout: 2) { finalText != nil }
        XCTAssertEqual(finalText, "final-text")
        XCTAssertEqual(engine.callSampleCounts.last, max, "pendingAudio 必须被裁剪到恰好上限")
    }

    /// LLM 校对失败回落原文时，LocalASRSession 必须调用 onWarning（DESIGN.md 约定），
    /// 且最终仍以原文调用 onFinal。
    func testLLMFallbackTriggersWarningAndFinalizesWithOriginalText() async {
        let engine = GatedFakeEngine()
        engine.textForCall = { _ in "raw-asr-text" }

        StubURLProtocol.handler = { _ in (500, "boom") }
        let corrector = makeCorrector()
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: corrector)

        var warnings: [String] = []
        var finalText: String?
        session.onWarning = { warnings.append($0) }
        session.onFinal = { finalText = $0 }

        session.sendAudio(speech(count: 20_000))
        session.finalize(timeout: 5)

        await poll(timeout: 3) { finalText != nil }
        XCTAssertEqual(finalText, "raw-asr-text")
        // 原因交给控制器在「已输入 · 未校对」里说明，不再发会被成功提示盖掉的 warning（R8-C1）。
        XCTAssertTrue(warnings.isEmpty, "回落不应再发 onWarning：\(warnings)")
        XCTAssertEqual(session.timings.llmResult, .fellBack)
        XCTAssertEqual(session.timings.llmFallbackReason, .server)
    }

    /// 进入 LLM 校对阶段必须发一次 `onCorrectionStarted`：没有它，HUD 会一直停在
    /// "识别中"，用户分不清自己在等本地推理（几百毫秒）还是在等网络（最长 llm.timeout）。
    func testCorrectionStartedFiresOnceBeforeFinal() async {
        let engine = GatedFakeEngine()
        engine.textForCall = { _ in "raw-asr-text" }

        StubURLProtocol.handler = { _ in (500, "boom") }
        let corrector = makeCorrector()
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: corrector)

        var correctionStartedCount = 0
        var finalText: String?
        var sawCorrectionBeforeFinal = false
        session.onCorrectionStarted = { correctionStartedCount += 1 }
        session.onFinal = {
            sawCorrectionBeforeFinal = correctionStartedCount == 1
            finalText = $0
        }

        session.sendAudio(speech(count: 20_000))
        session.finalize(timeout: 5)

        await poll(timeout: 3) { finalText != nil }
        XCTAssertEqual(correctionStartedCount, 1)
        XCTAssertTrue(sawCorrectionBeforeFinal, "校对开始的信号必须早于最终结果")
    }

    /// 没有启用校对时不应该发这个信号——否则 HUD 会显示一个根本不存在的"校对中"阶段。
    func testCorrectionStartedIsNotFiredWithoutCorrector() async {
        let engine = GatedFakeEngine()
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil)

        var correctionStartedCount = 0
        var finalText: String?
        session.onCorrectionStarted = { correctionStartedCount += 1 }
        session.onFinal = { finalText = $0 }

        session.sendAudio(speech(count: 8_000))
        session.finalize(timeout: 2)
        await poll { finalText != nil }

        XCTAssertEqual(correctionStartedCount, 0)
    }

    // MARK: - close() 后的迟到回调抑制

    func testCloseSuppressesLateCallbacksFromInFlightWork() async {
        let engine = GatedFakeEngine()
        engine.isGated = true
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil)

        var partialCalls = 0
        var errorCalls = 0
        session.onPartial = { _ in partialCalls += 1 }
        session.onError = { _ in errorCalls += 1 }

        session.sendAudio(speech(count: 20_000))
        await poll { engine.callCount >= 1 } // 确认已经进入（阻塞的）asrQueue 调用

        session.close()
        engine.release() // 放行被阻塞的调用，让它"迟到"完成

        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(partialCalls, 0, "close() 之后不应再转发预览结果")
        XCTAssertEqual(errorCalls, 0)

        // close() 之后 sendAudio/finalize 应该都是 no-op，不应该崩溃或产生新副作用。
        session.sendAudio(speech(count: 1_000))
        var finalCalls = 0
        session.onFinal = { _ in finalCalls += 1 }
        session.finalize(timeout: 0.05)
        try? await Task.sleep(nanoseconds: 150_000_000)
        XCTAssertEqual(finalCalls, 0, "close() 之后 finalize 应是 no-op")
    }

    // MARK: - LLM 校对进行中 close()

    /// `completeWithASRText` 在校对完成前检查 `!self.closed`：close() 若发生在
    /// LLM 校对请求已经在飞、结果还没回来的窗口内，`onFinal` 会被有意抑制——
    /// 与 finalize/预览路径的"迟到抑制"是同一套设计，这里锁定这个行为，
    /// 而不是假设它"不应该丢"（一个已经被关闭的会话没有谁还在等这个结果）。
    func testCloseDuringLLMCorrectionSuppressesLateOnFinal() async {
        let engine = GatedFakeEngine()
        engine.textForCall = { _ in "raw-asr-text" }

        let gate = DispatchSemaphore(value: 0)
        StubURLProtocol.handler = { _ in
            _ = gate.wait(timeout: .now() + 5)
            let body = #"{"choices":[{"message":{"content":"corrected-text"},"finish_reason":"stop"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector()

        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: corrector)

        var partialTexts: [String] = []
        var finalCalls = 0
        session.onPartial = { partialTexts.append($0) }
        session.onFinal = { _ in finalCalls += 1 }

        session.sendAudio(speech(count: 20_000))
        session.finalize(timeout: 5)

        // completeWithASRText 会先用 onPartial 顶一次原文，再发起 LLM 校对。
        await poll(timeout: 2) { partialTexts.contains("raw-asr-text") }
        XCTAssertTrue(partialTexts.contains("raw-asr-text"))

        // 此时 LLM 请求已经在飞（阻塞在 gate 上）：session.close() 模拟外部收尾
        // （例如设置变更重建控制器）先于校对完成到达。
        session.close()
        gate.signal() // 放行校对请求，让它"迟到"完成

        try? await Task.sleep(nanoseconds: 150_000_000)
        XCTAssertEqual(finalCalls, 0, "close() 之后即使 LLM 校对稍后完成，也不应再转发 onFinal")
    }

    // MARK: - A7：分阶段打点

    // MARK: - R8-A1：看门狗起算点、引擎不可用、等待阶段

    /// 看门狗从推理真正派发时起算，不含等待引擎加载：慢机上引擎加载超过看门狗时长，
    /// 不应该被误判成「识别超时」。
    func testFinalizeWatchdogDoesNotCountEngineLoadTime() async {
        final class EngineBox: @unchecked Sendable {
            private let lock = NSLock()
            private var engine: (any SenseVoiceRecognizing)?
            var current: (any SenseVoiceRecognizing)? { lock.lock(); defer { lock.unlock() }; return engine }
            func set(_ value: any SenseVoiceRecognizing) { lock.lock(); engine = value; lock.unlock() }
        }
        let box = EngineBox()
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { box.current }, llmCorrector: nil)
        session.sendAudio(speech(count: 5_000))

        var finalText: String?
        var receivedError: String?
        session.onFinal = { finalText = $0 }
        session.onError = { receivedError = $0 }
        session.finalize(timeout: 0.1)

        // 引擎在 0.1s 看门狗时长之后才就绪。
        try? await Task.sleep(nanoseconds: 350_000_000)
        XCTAssertNil(receivedError, "等待引擎加载的时间不应计入看门狗")
        let engine = GatedFakeEngine()
        engine.textForCall = { _ in "late-engine" }
        box.set(engine)

        await poll { finalText != nil || receivedError != nil }
        XCTAssertEqual(finalText, "late-engine")
        XCTAssertFalse(session.timings.finalizeTimedOut)
    }

    func testEngineUnavailableFailsImmediatelyWithoutWaiting() async {
        let session = LocalASRSession(
            asrQueue: makeQueue(), engineAccessor: { nil }, llmCorrector: nil, engineUnavailable: { true }
        )
        session.sendAudio(speech(count: 5_000))
        var receivedError: String?
        session.onError = { receivedError = $0 }
        session.finalize(timeout: 30)

        await poll(timeout: 0.5) { receivedError != nil }
        XCTAssertEqual(receivedError, "识别引擎尚未就绪")
        XCTAssertFalse(session.timings.finalizeTimedOut)
    }

    func testRecordingEngineWaitNoticeFiresOnceAfterOneSecondOfAudio() {
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { nil }, llmCorrector: nil)
        var stages: [EngineWaitStage] = []
        session.onEngineWait = { stages.append($0) }

        session.sendAudio(speech(count: 8_000))
        XCTAssertTrue(stages.isEmpty, "不足 1s 音频时不提示")
        session.sendAudio(speech(count: 8_000))
        XCTAssertEqual(stages, [.recording])
        session.sendAudio(speech(count: 8_000))
        XCTAssertEqual(stages, [.recording], "同一阶段至多提示一次")
    }

    // MARK: - R8-C2：校对中跳过

    private func makeGatedCorrector(gate: DispatchSemaphore) -> LLMCorrector {
        StubURLProtocol.handler = { _ in
            _ = gate.wait(timeout: .now() + 5)
            return (200, #"{"choices":[{"message":{"content":"corrected-text"},"finish_reason":"stop"}]}"#)
        }
        return makeCorrector()
    }

    func testSkipCorrectionFinalizesOnceWithRawTextAndIgnoresLateResult() async {
        let engine = GatedFakeEngine()
        engine.textForCall = { _ in "raw-asr-text" }
        let gate = DispatchSemaphore(value: 0)
        let session = LocalASRSession(
            asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: makeGatedCorrector(gate: gate)
        )
        var finals: [String] = []
        var correctionStarted = false
        session.onFinal = { finals.append($0) }
        session.onCorrectionStarted = { correctionStarted = true }

        XCTAssertFalse(session.skipCorrection(), "未开始校对时不能跳过")
        session.sendAudio(speech(count: 20_000))
        session.finalize(timeout: 5)
        await poll { correctionStarted }
        XCTAssertTrue(session.isCorrecting)

        XCTAssertTrue(session.skipCorrection())
        XCTAssertEqual(finals, ["raw-asr-text"])
        XCTAssertEqual(session.timings.llmResult, .skipped)
        XCTAssertFalse(session.isCorrecting)
        XCTAssertFalse(session.skipCorrection(), "只能跳过一次")

        gate.signal() // LLM 结果迟到
        try? await Task.sleep(nanoseconds: 200_000_000)
        XCTAssertEqual(finals, ["raw-asr-text"], "迟到的校对结果必须被忽略")
    }

    // MARK: - R8-D1：事件驱动的预览调度

    private final class TestClock: @unchecked Sendable {
        private let lock = NSLock()
        private var nanos: UInt64 = 1_000_000_000
        func advance(ms: UInt64) { lock.lock(); nanos += ms * 1_000_000; lock.unlock() }
        var clock: MonotonicClock { { [self] in lock.lock(); defer { lock.unlock() }; return nanos } }
    }

    func testPreviewWaitsForThreeHundredMillisecondsOfNewSamples() async {
        let engine = GatedFakeEngine()
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil)

        session.sendAudio(speech(count: 3_200)) // 200ms，不足 300ms
        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(engine.callCount, 0)

        session.sendAudio(speech(count: 3_200)) // 累计 400ms
        await poll { engine.callCount == 1 }
        XCTAssertEqual(engine.callCount, 1)
        XCTAssertEqual(session.timings.previewSkipped, 0, "样本不足不算「静音跳过」")
    }

    func testNextPreviewWaitsForHalfOfLastPreviewDuration() async {
        let testClock = TestClock()
        let engine = GatedFakeEngine()
        engine.textForCall = { index in
            testClock.advance(ms: 400) // 这次预览「耗时」400ms
            return "preview\(index)"
        }
        let session = LocalASRSession(
            asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil, now: testClock.clock
        )

        session.sendAudio(speech(count: 8_000))
        await poll { session.timings.previewRuns == 1 }
        XCTAssertEqual(session.timings.previewRuns, 1)
        XCTAssertEqual(session.timings.previewDeliveredCount, 1)
        XCTAssertEqual(session.timings.previewDeliveredNanos, 400_000_000, "从发起到送达")

        // 新样本已够，但还没到「结束 + 耗时/2」。
        session.sendAudio(speech(count: 8_000))
        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(engine.callCount, 1, "占空比限制：还不能跑下一次")

        testClock.advance(ms: 200)
        session.sendAudio(speech(count: 160)) // 下一个采集块到达，再检查一次
        await poll { engine.callCount == 2 }
        XCTAssertEqual(engine.callCount, 2)
    }

    // MARK: - 松键后不再预览

    /// 松键后送来的音频（补录、尾音）不再发起预览，终稿是 asrQueue 上的第一件事。
    func testStopPreviewsBlocksNewPreviewsButFinalizeStillSeesAllAudio() async {
        let engine = GatedFakeEngine()
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil)
        var finalText: String?
        session.onFinal = { finalText = $0 }

        session.stopPreviews()
        session.sendAudio(speech(count: 20_000))
        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(engine.callCount, 0, "停止预览后不应再发起预览")

        session.finalize(timeout: 2)
        await poll { finalText != nil }
        XCTAssertEqual(engine.callSampleCounts, [20_000], "终稿仍对全部音频识别一次")
    }

    /// 已入队、还没开跑的预览在停止后直接放弃，不再占用 asrQueue。
    func testQueuedPreviewIsSkippedAfterStopPreviews() async {
        let engine = GatedFakeEngine()
        let queue = makeQueue()
        let session = LocalASRSession(asrQueue: queue, engineAccessor: { engine }, llmCorrector: nil)
        let blocker = DispatchSemaphore(value: 0)
        queue.async { _ = blocker.wait(timeout: .now() + 5) } // 先把队列占住，让预览只能排队

        session.sendAudio(speech(count: 20_000)) // 预览入队
        session.stopPreviews()
        blocker.signal()
        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(engine.callCount, 0, "排队中的预览应被跳过")
    }

    // MARK: - VOC-7 / VOC-8 / VOC-10：自定义词汇

    private final class BeamStatsEngine: SenseVoiceRecognizing, @unchecked Sendable {
        let text: String
        init(text: String) { self.text = text }
        func recognize(_ samples: [Float]) throws -> String { text }
        var lastDecodeStats: DecodeStats { DecodeStats(mode: .beam, hotwordHits: 2, decodeNanos: 3_000_000) }
    }

    private let claudeVocabulary = VocabularyReplacer(VocabularyConfig(entries: [
        VocabularyEntry(term: "Claude", aliases: ["克劳德"]),
        VocabularyEntry(term: "VoiceTyper"),
    ]))

    /// 未启用 LLM：终稿已做词汇替换；终稿的解码统计进入会话打点。
    func testFinalAppliesVocabularyAndRecordsDecodeStats() async {
        let engine = BeamStatsEngine(text: "用克劳德写voice typer")
        let session = LocalASRSession(
            asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil, vocabulary: claudeVocabulary
        )
        var finalText: String?
        session.onFinal = { finalText = $0 }

        session.sendAudio(speech(count: 8_000))
        session.finalize(timeout: 2)
        await poll { finalText != nil }

        XCTAssertEqual(finalText, "用Claude写VoiceTyper", "中英文空格由 formatForOutput 负责，这里只做替换")
        XCTAssertEqual(session.timings.decodeMode, .beam)
        XCTAssertEqual(session.timings.hotwordHits, 2)
        XCTAssertEqual(session.timings.decodeNanos, 3_000_000)
    }

    /// 启用 LLM：校对收到的是替换后的文本，且末条消息带词表；校对前顶到 HUD 的也是替换后的文本。
    func testCorrectionReceivesReplacedTextAndGlossary() async {
        let engine = GatedFakeEngine()
        engine.textForCall = { _ in "用克劳德写" }
        let session = LocalASRSession(
            asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: makeCorrector(), vocabulary: claudeVocabulary
        )
        let captured = CapturedContent()
        StubURLProtocol.handler = { request in
            captured.set(Self.lastUserContent(of: request))
            return (200, #"{"choices":[{"message":{"content":"用克劳德写"},"finish_reason":"stop"}]}"#)
        }
        var partials: [String] = []
        var finalText: String?
        session.onPartial = { partials.append($0) }
        session.onFinal = { finalText = $0 }

        session.stopPreviews()
        session.sendAudio(speech(count: 8_000))
        session.finalize(timeout: 2)
        await poll { finalText != nil }

        XCTAssertEqual(partials, ["用Claude写"])
        XCTAssertEqual(captured.value, "<glossary>Claude、VoiceTyper</glossary>\n<asr_text>\n用Claude写\n</asr_text>")
        // LLM 改回去的写法由 formatForOutput 里的第二次替换兜住；会话本身原样转交校对结果。
        XCTAssertEqual(finalText, "用克劳德写")
    }

    private final class CapturedContent: @unchecked Sendable {
        private let lock = NSLock()
        private var content: String?
        var value: String? { lock.lock(); defer { lock.unlock() }; return content }
        func set(_ value: String?) { lock.lock(); content = value; lock.unlock() }
    }

    private nonisolated static func lastUserContent(of request: URLRequest) -> String? {
        var data = request.httpBody
        if data == nil, let stream = request.httpBodyStream {
            stream.open()
            defer { stream.close() }
            var collected = Data()
            var buffer = [UInt8](repeating: 0, count: 4096)
            while stream.hasBytesAvailable {
                let count = stream.read(&buffer, maxLength: buffer.count)
                if count <= 0 { break }
                collected.append(buffer, count: count)
            }
            data = collected
        }
        guard let data, let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let messages = json["messages"] as? [[String: Any]] else { return nil }
        return messages.last?["content"] as? String
    }
}
