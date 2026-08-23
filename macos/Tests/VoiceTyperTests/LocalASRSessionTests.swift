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

    override func tearDown() {
        StubURLProtocol.handler = nil
        super.tearDown()
    }

    // MARK: - 预览 in-flight 跳过

    func testPreviewInFlightSkipsReentrantScheduling() async {
        let engine = GatedFakeEngine()
        engine.isGated = true
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil)

        session.sendAudio([Float](repeating: 0, count: 20_000))
        await poll { engine.callCount == 1 }
        XCTAssertEqual(engine.callCount, 1)

        // 预览仍在执行（阻塞在 gate 上）时再次 sendAudio：不应发起第二次并发预览。
        session.sendAudio([Float](repeating: 0, count: 20_000))
        try? await Task.sleep(nanoseconds: 30_000_000)
        XCTAssertEqual(engine.callCount, 1, "previewInFlight 时不应发起第二次并发预览")

        engine.release()

        // previewInFlight 复位是异步落地的（asrQueue 回到 MainActor 有一跳），
        // 用重试而不是单次盲发 sendAudio，避免踩中复位前的窄窗口。
        for _ in 0..<50 where engine.callCount < 2 {
            session.sendAudio([Float](repeating: 0, count: 1_000))
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTAssertEqual(engine.callCount, 2, "previewInFlight 复位后，下一次 sendAudio 应能重新调度预览")
    }

    // MARK: - finalize 看门狗

    func testFinalizeWatchdogFiresOnErrorWhenRecognitionHangs() async {
        let engine = GatedFakeEngine()
        engine.isGated = true
        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: nil)
        session.sendAudio([Float](repeating: 0, count: 20_000))

        var receivedError: String?
        session.onError = { receivedError = $0 }
        session.finalize(timeout: 0.05)

        await poll(timeout: 1) { receivedError != nil }
        XCTAssertEqual(receivedError, "识别超时")

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
        session.sendAudio([Float](repeating: 0, count: 5_000))

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
        session.sendAudio([Float](repeating: 0, count: 120 * 16_000))
        XCTAssertEqual(cappedCount, 0)

        session.sendAudio([Float](repeating: 0, count: 1_000))
        XCTAssertEqual(cappedCount, 1)
        XCTAssertEqual(warnings.count, 1)

        // 再来一次不应重复触发。
        session.sendAudio([Float](repeating: 0, count: 1_000))
        XCTAssertEqual(cappedCount, 1, "上限回调只应触发一次")
        XCTAssertEqual(warnings.count, 1)

        var finalText: String?
        session.onFinal = { finalText = $0 }
        session.finalize(timeout: 5)
        await poll(timeout: 2) { finalText != nil }
        XCTAssertEqual(engine.callSampleCounts.last, 120 * 16_000, "超过上限后的样本不应被计入 buffer")
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

        session.sendAudio([Float](repeating: 0, count: 20_000))
        await poll { engine.callCount >= 1 } // 确认已经进入（阻塞的）asrQueue 调用

        session.close()
        engine.release() // 放行被阻塞的调用，让它"迟到"完成

        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(partialCalls, 0, "close() 之后不应再转发预览结果")
        XCTAssertEqual(errorCalls, 0)

        // close() 之后 sendAudio/finalize 应该都是 no-op，不应该崩溃或产生新副作用。
        session.sendAudio([Float](repeating: 0, count: 1_000))
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
        let sessionConfig = URLSessionConfiguration.ephemeral
        sessionConfig.protocolClasses = [StubURLProtocol.self]
        let corrector = LLMCorrector(
            config: LLMCorrector.Config(
                chatCompletionsURL: LLMEndpoint.chatCompletionsURL(from: "https://stub.invalid/v1")!,
                apiKey: "test-key",
                model: "gpt-4o-mini",
                temperature: 0,
                maxTokens: 800,
                timeout: 5
            ),
            urlSession: URLSession(configuration: sessionConfig)
        )

        let session = LocalASRSession(asrQueue: makeQueue(), engineAccessor: { engine }, llmCorrector: corrector)

        var partialTexts: [String] = []
        var finalCalls = 0
        session.onPartial = { partialTexts.append($0) }
        session.onFinal = { _ in finalCalls += 1 }

        session.sendAudio([Float](repeating: 0, count: 20_000))
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
}
