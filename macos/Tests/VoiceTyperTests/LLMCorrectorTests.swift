import XCTest
@testable import VoiceTyper

/// 用 URLProtocol 打桩验证 LLMCorrector 的失败兜底：网络/超时/截断/格式错误
/// 都必须原样返回输入文本，绝不让校对失败丢掉已经识别出的文本。
final class LLMCorrectorTests: XCTestCase {
    private func makeSession() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: config)
    }

    /// 内存版能力缓存：绝不能让单测写到真实的 UserDefaults。
    private final class MemoryCapabilityStore: LLMCapabilityStoring, @unchecked Sendable {
        private let lock = NSLock()
        private var fingerprints = Set<String>()
        var all: Set<String> { lock.lock(); defer { lock.unlock() }; return fingerprints }

        func isThinkingParameterUnsupported(fingerprint: String) -> Bool {
            lock.lock(); defer { lock.unlock() }
            return fingerprints.contains(fingerprint)
        }

        func setThinkingParameterUnsupported(_ unsupported: Bool, fingerprint: String) {
            lock.lock(); defer { lock.unlock() }
            if unsupported { fingerprints.insert(fingerprint) } else { fingerprints.remove(fingerprint) }
        }
    }

    /// 记录每次请求的请求体（URLProtocol 里 `httpBody` 常为 nil，需读 `httpBodyStream`）。
    private final class RequestRecorder: @unchecked Sendable {
        private let lock = NSLock()
        private var bodies: [[String: Any]] = []
        var requests: [[String: Any]] { lock.lock(); defer { lock.unlock() }; return bodies }

        func record(_ request: URLRequest) {
            lock.lock(); bodies.append(Self.parseBody(of: request)); lock.unlock()
        }

        static func parseBody(of request: URLRequest) -> [String: Any] {
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
            return data.flatMap { try? JSONSerialization.jsonObject(with: $0) } as? [String: Any] ?? [:]
        }
    }

    private static let okBody = #"{"choices":[{"message":{"content":"修正后的文本"},"finish_reason":"stop"}]}"#
    private let modelURL = LLMEndpoint.chatCompletionsURL(from: "https://stub.invalid/v1")!

    private func makeCorrector(
        session: URLSession,
        apiKey: String = "test-key",
        store: (any LLMCapabilityStoring)? = nil
    ) -> LLMCorrector {
        LLMCorrector(
            config: LLMCorrector.Config(
                chatCompletionsURL: modelURL,
                apiKey: apiKey,
                model: "gpt-4o-mini",
                temperature: 0,
                maxTokens: 800,
                timeout: 5
            ),
            urlSession: session,
            capabilityStore: store ?? MemoryCapabilityStore()
        )
    }

    private var fingerprint: String { LLMCapabilityFingerprint.make(chatURL: modelURL, model: "gpt-4o-mini") }

    override func tearDown() {
        StubURLProtocol.handler = nil
        StubURLProtocol.failure = nil
        super.tearDown()
    }

    // MARK: - R8-C1：回落原因分类

    private func fallbackReason(forStatus status: Int, body: String = "oops") async -> LLMCorrector.FallbackReason? {
        StubURLProtocol.handler = { _ in (status, body) }
        let report = await makeCorrector(session: makeSession()).correctWithReport("原文")
        return report.fallbackReason
    }

    /// 所有失败路径都必须原样交回输入文本——绝不能因为校对失败丢掉已识别的文字。
    private func assertOriginalKept(
        status: Int, reason: LLMCorrector.FallbackReason, body: String,
        file: StaticString = #filePath, line: UInt = #line
    ) async {
        StubURLProtocol.handler = { _ in (status, body) }
        let report = await makeCorrector(session: makeSession()).correctWithReport("原文")
        assertFellBack(report.outcome, "原文", "HTTP \(status) \(body)", file: file, line: line)
        XCTAssertEqual(report.fallbackReason, reason, file: file, line: line)
    }

    func testEveryFailureKeepsOriginalTextWithClassifiedReason() async {
        await assertOriginalKept(status: 500, reason: .server, body: "internal error")
        await assertOriginalKept(status: 200, reason: .server, body: "not json at all")
        await assertOriginalKept(status: 200, reason: .emptyResult, body:
            #"{"choices":[{"message":{"content":"半截"},"finish_reason":"length"}]}"#)
        await assertOriginalKept(status: 200, reason: .emptyResult, body:
            #"{"choices":[{"message":{"content":"   "},"finish_reason":"stop"}]}"#)
        // R2-08：剥标签之后才变空的 tags-only 响应同样要回落，而不是返回空串把原文丢掉。
        await assertOriginalKept(status: 200, reason: .emptyResult, body:
            #"{"choices":[{"message":{"content":"<asr_text>\n  \n</asr_text>"},"finish_reason":"stop"}]}"#)
    }

    func testFallbackReasonMapping() async {
        let authCodes = [401, 403]
        for code in authCodes {
            let reason = await fallbackReason(forStatus: code)
            XCTAssertEqual(reason, .auth, "HTTP \(code)")
        }
        let rateLimited = await fallbackReason(forStatus: 429)
        XCTAssertEqual(rateLimited, .rateLimited)
        let server = await fallbackReason(forStatus: 503)
        XCTAssertEqual(server, .server)
        let other = await fallbackReason(forStatus: 404)
        XCTAssertEqual(other, .server, "其他 HTTP 错误归入「服务出错」")
    }

    func testFallbackReasonForTimeoutAndNetworkErrors() async {
        StubURLProtocol.failure = URLError(.timedOut)
        let timedOut = await makeCorrector(session: makeSession()).correctWithReport("原文")
        XCTAssertEqual(timedOut.fallbackReason, .timeout)

        StubURLProtocol.failure = URLError(.notConnectedToInternet)
        let offline = await makeCorrector(session: makeSession()).correctWithReport("原文")
        XCTAssertEqual(offline.fallbackReason, .network)
    }

    private func assertFellBack(
        _ outcome: LLMCorrector.CorrectionOutcome, _ expected: String,
        _ message: String = "", file: StaticString = #filePath, line: UInt = #line
    ) {
        guard case .fellBack(let text) = outcome else {
            return XCTFail("期望 .fellBack，实际: \(outcome). \(message)", file: file, line: line)
        }
        XCTAssertEqual(text, expected, message, file: file, line: line)
    }

    private func assertCorrected(
        _ outcome: LLMCorrector.CorrectionOutcome, _ expected: String,
        file: StaticString = #filePath, line: UInt = #line
    ) {
        guard case .corrected(let text) = outcome else {
            return XCTFail("期望 .corrected，实际: \(outcome)", file: file, line: line)
        }
        XCTAssertEqual(text, expected, file: file, line: line)
    }

    func testEchoedTagsAreStripped() async {
        StubURLProtocol.handler = { _ in
            let body = #"{"choices":[{"message":{"content":"<asr_text>\n修正后的文本\n</asr_text>"},"finish_reason":"stop"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector(session: makeSession())
        let result = await corrector.correct("原文")
        assertCorrected(result, "修正后的文本")
    }

    // MARK: - test()：契约不变（成功返回文本，失败抛出）

    func testTestThrowsOnHTTPError() async {
        StubURLProtocol.handler = { _ in (401, "unauthorized") }
        let corrector = makeCorrector(session: makeSession())
        do {
            _ = try await corrector.test("原文")
            XCTFail("test() 在 HTTP 错误时必须抛出，而不是回落原文")
        } catch {
            XCTAssertTrue("\(error)".contains("401") || error is LLMCorrector.LLMError)
        }
    }

    // MARK: - A1：关闭深度思考与能力缓存

    func testDefaultRequestDisablesThinking() async throws {
        let recorder = RequestRecorder()
        StubURLProtocol.handler = { request in
            recorder.record(request)
            return (200, Self.okBody)
        }
        let report = await makeCorrector(session: makeSession()).correctWithReport("原文")

        assertCorrected(report.outcome, "修正后的文本")
        XCTAssertNil(report.fallbackReason)
        XCTAssertEqual(report.thinkingParameter, .sent)
        let thinking = try XCTUnwrap(recorder.requests.first?["thinking"] as? [String: String])
        XCTAssertEqual(thinking, ["type": "disabled"])
    }

    func testRejectedThinkingParameterRetriesOnceWithoutItAndCachesTheAnswer() async {
        let recorder = RequestRecorder()
        let store = MemoryCapabilityStore()
        StubURLProtocol.handler = { request in
            recorder.record(request)
            if recorder.requests.last?["thinking"] != nil {
                return (400, #"{"error":{"message":"Unrecognized request argument supplied: thinking"}}"#)
            }
            return (200, Self.okBody)
        }
        let report = await makeCorrector(session: makeSession(), store: store).correctWithReport("原文")

        assertCorrected(report.outcome, "修正后的文本")
        XCTAssertEqual(report.thinkingParameter, .rejectedThenOmitted)
        XCTAssertEqual(recorder.requests.count, 2)
        XCTAssertNotNil(recorder.requests[0]["thinking"])
        XCTAssertNil(recorder.requests[1]["thinking"])
        XCTAssertTrue(store.isThinkingParameterUnsupported(fingerprint: fingerprint))
    }

    func testCachedUnsupportedSkipsTheParameterWithASingleRequest() async {
        let recorder = RequestRecorder()
        let store = MemoryCapabilityStore()
        store.setThinkingParameterUnsupported(true, fingerprint: fingerprint)
        StubURLProtocol.handler = { request in
            recorder.record(request)
            return (200, Self.okBody)
        }
        let report = await makeCorrector(session: makeSession(), store: store).correctWithReport("原文")

        XCTAssertEqual(report.thinkingParameter, .omittedByCache)
        XCTAssertEqual(recorder.requests.count, 1)
        XCTAssertNil(recorder.requests[0]["thinking"])
    }

    /// 换 Key 不改变接口能力：缓存与 API Key 无关。
    func testCapabilityCacheIsSharedAcrossAPIKeys() async {
        let store = MemoryCapabilityStore()
        StubURLProtocol.handler = { _ in (400, "thinking is not supported") }
        _ = await makeCorrector(session: makeSession(), apiKey: "key-A", store: store).correctWithReport("原文")
        XCTAssertTrue(store.isThinkingParameterUnsupported(fingerprint: fingerprint))

        let recorder = RequestRecorder()
        StubURLProtocol.handler = { request in
            recorder.record(request)
            return (200, Self.okBody)
        }
        let report = await makeCorrector(session: makeSession(), apiKey: "key-B", store: store).correctWithReport("原文")
        XCTAssertEqual(report.thinkingParameter, .omittedByCache)
        XCTAssertEqual(recorder.requests.count, 1)
    }

    func testUnrelated400DoesNotRetry() async {
        let recorder = RequestRecorder()
        let store = MemoryCapabilityStore()
        StubURLProtocol.handler = { request in
            recorder.record(request)
            return (400, #"{"error":{"message":"model not found"}}"#)
        }
        let report = await makeCorrector(session: makeSession(), store: store).correctWithReport("原文")

        assertFellBack(report.outcome, "原文")
        XCTAssertEqual(recorder.requests.count, 1, "与 thinking 无关的 400 不应重发")
        XCTAssertTrue(store.all.isEmpty)
    }

    func testFailureAfterRetryFallsBackWithAtMostTwoRequests() async {
        let recorder = RequestRecorder()
        StubURLProtocol.handler = { request in
            recorder.record(request)
            if recorder.requests.last?["thinking"] != nil { return (422, "unknown field: thinking") }
            return (500, "internal error")
        }
        let report = await makeCorrector(session: makeSession()).correctWithReport("原文")

        assertFellBack(report.outcome, "原文")
        XCTAssertEqual(report.thinkingParameter, .rejectedThenOmitted)
        XCTAssertEqual(recorder.requests.count, 2)
    }

    func testTestAlwaysProbesWithParameterAndClearsCacheWhenAccepted() async throws {
        let recorder = RequestRecorder()
        let store = MemoryCapabilityStore()
        store.setThinkingParameterUnsupported(true, fingerprint: fingerprint)
        StubURLProtocol.handler = { request in
            recorder.record(request)
            return (200, Self.okBody)
        }
        let result = try await makeCorrector(session: makeSession(), store: store).test("原文")

        XCTAssertEqual(result.thinkingParameter, .sent)
        XCTAssertNotNil(recorder.requests.first?["thinking"], "测试校对应忽略缓存重新探测")
        XCTAssertFalse(store.isThinkingParameterUnsupported(fingerprint: fingerprint))
    }

    func testTestRecordsRejectionAndReportsFallbackRequest() async throws {
        let store = MemoryCapabilityStore()
        StubURLProtocol.handler = { request in
            RequestRecorder.parseBody(of: request)["thinking"] != nil
                ? (400, "thinking not allowed") : (200, Self.okBody)
        }
        let result = try await makeCorrector(session: makeSession(), store: store).test("原文")
        XCTAssertEqual(result.thinkingParameter, .rejectedThenOmitted)
        XCTAssertTrue(store.isThinkingParameterUnsupported(fingerprint: fingerprint))
    }

    func testFingerprintDependsOnURLAndModelOnly() {
        let a = LLMCapabilityFingerprint.make(chatURL: modelURL, model: "m1")
        XCTAssertEqual(a, LLMCapabilityFingerprint.make(chatURL: modelURL, model: "m1"))
        XCTAssertNotEqual(a, LLMCapabilityFingerprint.make(chatURL: modelURL, model: "m2"))
        XCTAssertNotEqual(a, LLMCapabilityFingerprint.make(chatURL: URL(string: "https://other.invalid/v1/chat/completions")!, model: "m1"))
        XCTAssertEqual(a.count, 16)
        XCTAssertTrue(a.allSatisfy { $0.isHexDigit })
    }

    func testUserDefaultsStoreRoundTripAndCapsEntries() throws {
        let suite = "com.voicetyper.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = UserDefaultsLLMCapabilityStore(defaults: defaults)

        store.setThinkingParameterUnsupported(true, fingerprint: "a")
        XCTAssertTrue(store.isThinkingParameterUnsupported(fingerprint: "a"))
        store.setThinkingParameterUnsupported(false, fingerprint: "a")
        XCTAssertFalse(store.isThinkingParameterUnsupported(fingerprint: "a"))

        for index in 0..<40 { store.setThinkingParameterUnsupported(true, fingerprint: "f\(index)") }
        XCTAssertFalse(store.isThinkingParameterUnsupported(fingerprint: "f0"), "超出 32 个时应丢最旧的")
        XCTAssertTrue(store.isThinkingParameterUnsupported(fingerprint: "f39"))
    }

    // MARK: - VOC-8：词表提示

    private var payloadConfig: LLMCorrector.Config {
        LLMCorrector.Config(
            chatCompletionsURL: modelURL, apiKey: "k", model: "gpt-4o-mini", temperature: 0, maxTokens: 800, timeout: 5
        )
    }

    private func messages(_ payload: [String: Any]) -> [[String: String]] {
        payload["messages"] as? [[String: String]] ?? []
    }

    /// 带词表时 system 与 few-shot 逐条不变（保住前缀缓存），只有末条 user 消息多出 `<glossary>`。
    func testGlossaryOnlyChangesLastUserMessage() {
        let plain = messages(LLMCorrector.buildPayload(
            config: payloadConfig, systemPrompt: "SYS", text: "用克劳德写", includeThinking: true
        ))
        let withGlossary = messages(LLMCorrector.buildPayload(
            config: payloadConfig, systemPrompt: "SYS", text: "用克劳德写", includeThinking: true,
            glossary: ["Claude", "VoiceTyper", "张三丰"]
        ))
        XCTAssertEqual(plain.count, withGlossary.count)
        XCTAssertEqual(Array(plain.dropLast()), Array(withGlossary.dropLast()))
        XCTAssertEqual(
            withGlossary.last?["content"],
            "<glossary>Claude、VoiceTyper、张三丰</glossary>\n<asr_text>\n用克劳德写\n</asr_text>"
        )
    }

    /// 空词表时请求与没有这项功能时逐字节相同。
    func testEmptyGlossaryKeepsMessagesByteIdentical() throws {
        let legacy = LLMCorrector.buildPayload(config: payloadConfig, systemPrompt: "SYS", text: "原文", includeThinking: false)
        let empty = LLMCorrector.buildPayload(
            config: payloadConfig, systemPrompt: "SYS", text: "原文", includeThinking: false, glossary: []
        )
        let encode = { (payload: [String: Any]) in
            try JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])
        }
        XCTAssertEqual(try encode(legacy), try encode(empty))
        XCTAssertEqual(messages(empty).last?["content"], "<asr_text>\n原文\n</asr_text>")
    }

    /// 词表经 `correctWithReport` 真正发到服务端。
    func testCorrectWithReportSendsGlossary() async {
        let recorder = RequestRecorder()
        StubURLProtocol.handler = { request in
            recorder.record(request)
            return (200, Self.okBody)
        }
        _ = await makeCorrector(session: makeSession()).correctWithReport("原文", glossary: ["Claude"])
        let sent = (recorder.requests.first?["messages"] as? [[String: Any]])?.last?["content"] as? String
        XCTAssertEqual(sent, "<glossary>Claude</glossary>\n<asr_text>\n原文\n</asr_text>")
    }

    /// test() 遇到语义回落（如 finish_reason=length）仍返回文本、不抛错——契约不变。
    func testTestReturnsOriginalTextOnSemanticFallback() async throws {
        StubURLProtocol.handler = { _ in
            let body = #"{"choices":[{"message":{"content":"截断"},"finish_reason":"length"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector(session: makeSession())
        let result = try await corrector.test("原始长文本")
        XCTAssertEqual(result.text, "原始长文本")
    }
}

@MainActor
final class SettingsViewModelTests: XCTestCase {
    func testCorrectionTestMessageIncludesElapsedTime() async {
        let vm = SettingsViewModel()
        vm.llmEnabled = true
        vm.llmBaseURL = "https://stub.invalid/v1"
        vm.onTestLLMCorrection = { _, _ in
            try? await Task.sleep(nanoseconds: 20_000_000)
            return .success(LLMCorrector.TestResult(text: "修正后的文本", thinkingParameter: .sent))
        }

        vm.testLLMCorrection()
        let deadline = Date().addingTimeInterval(1)
        while vm.recognitionBusy && Date() < deadline {
            try? await Task.sleep(nanoseconds: 1_000_000)
        }

        XCTAssertFalse(vm.recognitionBusy, "测试校对异步任务未在预期时间内完成")
        XCTAssertTrue(vm.recognitionMessage.hasPrefix("校对测试成功"))
        XCTAssertTrue(vm.recognitionMessage.contains("耗时 "))
    }

    /// R3-13 回归：失败必须展示真实错误原因，而不是笼统的"未通过"——网络不通与
    /// "模型认为无需修改"此前会被误判成同一个结果。
    func testCorrectionTestFailureShowsRealErrorMessage() async {
        let vm = SettingsViewModel()
        vm.llmEnabled = true
        vm.llmBaseURL = "https://stub.invalid/v1"
        vm.onTestLLMCorrection = { _, _ in
            .failure(SimpleMessageError(message: "LLM API 错误 (401)"))
        }

        vm.testLLMCorrection()
        let deadline = Date().addingTimeInterval(1)
        while vm.recognitionBusy && Date() < deadline {
            try? await Task.sleep(nanoseconds: 1_000_000)
        }

        XCTAssertTrue(vm.recognitionMessage.hasPrefix("校对测试失败"))
        XCTAssertTrue(vm.recognitionMessage.contains("LLM API 错误 (401)"))
        XCTAssertEqual(vm.recognitionMessageKind, .error)
    }
}

/// 拦截所有请求，交给测试用例设置的 handler 决定响应。
final class StubURLProtocol: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var handler: ((URLRequest) -> (Int, String))?
    /// 非 nil 时请求直接以该网络错误失败（验证超时 / 网络不通的回落分类）。
    nonisolated(unsafe) static var failure: URLError?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        if let failure = Self.failure {
            client?.urlProtocol(self, didFailWithError: failure)
            return
        }
        guard let handler = Self.handler else {
            client?.urlProtocol(self, didFailWithError: URLError(.unknown))
            return
        }
        let (status, body) = handler(request)
        let response = HTTPURLResponse(
            url: request.url!, statusCode: status, httpVersion: "HTTP/1.1", headerFields: nil
        )!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(body.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}
