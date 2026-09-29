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
        super.tearDown()
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

    func testSuccessfulCorrectionReturnsCorrectedText() async {
        StubURLProtocol.handler = { _ in
            let body = #"{"choices":[{"message":{"content":"这个服务器的告警规则配置好了吗"},"finish_reason":"stop"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector(session: makeSession())
        let result = await corrector.correct("这个服物器的告警规则配置好了吗")
        guard case .corrected(let text) = result else {
            return XCTFail("成功响应应返回 .corrected，实际: \(result)")
        }
        XCTAssertEqual(text, "这个服务器的告警规则配置好了吗")
    }

    func testTruncatedResponseFallsBackToOriginalText() async {
        StubURLProtocol.handler = { _ in
            let body = #"{"choices":[{"message":{"content":"被截断的部分文本"},"finish_reason":"length"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector(session: makeSession())
        let original = "一段很长的听写内容"
        let result = await corrector.correct(original)
        assertFellBack(result, original, "finish_reason=length 是语义回落，必须是 .fellBack")
    }

    func testHTTPErrorFallsBackToOriginalText() async {
        StubURLProtocol.handler = { _ in (500, "internal error") }
        let corrector = makeCorrector(session: makeSession())
        let original = "原始文本"
        let result = await corrector.correct(original)
        assertFellBack(result, original, "HTTP 错误必须回落 .fellBack")
    }

    func testMalformedJSONFallsBackToOriginalText() async {
        StubURLProtocol.handler = { _ in (200, "not json at all") }
        let corrector = makeCorrector(session: makeSession())
        let original = "原始文本"
        let result = await corrector.correct(original)
        assertFellBack(result, original, "解析异常必须回落 .fellBack")
    }

    func testEmptyContentFallsBackToOriginalText() async {
        StubURLProtocol.handler = { _ in
            let body = #"{"choices":[{"message":{"content":"   "},"finish_reason":"stop"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector(session: makeSession())
        let original = "已识别的原文"
        let result = await corrector.correct(original)
        assertFellBack(result, original, "2xx 空 content 是语义回落（F-03），必须是 .fellBack")
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

    /// R2-08 回归：剥标签之后才变空的 tags-only 响应，此前会被当作"有效修正"返回空串，
    /// 把已识别出的文本直接丢掉；必须和剥标签前就是空的情况一样回落原文。
    func testTagsOnlyResponseFallsBackToOriginalText() async {
        StubURLProtocol.handler = { _ in
            let body = #"{"choices":[{"message":{"content":"<asr_text>\n</asr_text>"},"finish_reason":"stop"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector(session: makeSession())
        let original = "已识别的原文"
        let result = await corrector.correct(original)
        assertFellBack(result, original, "tags-only 响应是语义回落，必须是 .fellBack")
    }

    func testTagsWrappingOnlyWhitespaceFallsBackToOriginalText() async {
        StubURLProtocol.handler = { _ in
            let body = #"{"choices":[{"message":{"content":"<asr_text>\n   \n</asr_text>"},"finish_reason":"stop"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector(session: makeSession())
        let original = "已识别的原文"
        let result = await corrector.correct(original)
        assertFellBack(result, original, "tags 内仅空白是语义回落，必须是 .fellBack")
    }

    // MARK: - test()：契约不变（成功返回文本，失败抛出）

    func testTestReturnsCorrectedTextOnSuccess() async throws {
        StubURLProtocol.handler = { _ in
            let body = #"{"choices":[{"message":{"content":"修正后的文本"},"finish_reason":"stop"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector(session: makeSession())
        let result = try await corrector.test("原文")
        XCTAssertEqual(result.text, "修正后的文本")
    }

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

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
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
