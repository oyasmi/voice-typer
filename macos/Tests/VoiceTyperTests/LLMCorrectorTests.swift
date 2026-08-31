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

    private func makeCorrector(session: URLSession) -> LLMCorrector {
        LLMCorrector(
            config: LLMCorrector.Config(
                chatCompletionsURL: LLMEndpoint.chatCompletionsURL(from: "https://stub.invalid/v1")!,
                apiKey: "test-key",
                model: "gpt-4o-mini",
                temperature: 0,
                maxTokens: 800,
                timeout: 5
            ),
            urlSession: session
        )
    }

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
        let text = try await corrector.test("原文")
        XCTAssertEqual(text, "修正后的文本")
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

    /// test() 遇到语义回落（如 finish_reason=length）仍返回文本、不抛错——契约不变。
    func testTestReturnsOriginalTextOnSemanticFallback() async throws {
        StubURLProtocol.handler = { _ in
            let body = #"{"choices":[{"message":{"content":"截断"},"finish_reason":"length"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector(session: makeSession())
        let text = try await corrector.test("原始长文本")
        XCTAssertEqual(text, "原始长文本")
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
            return .success("修正后的文本")
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
