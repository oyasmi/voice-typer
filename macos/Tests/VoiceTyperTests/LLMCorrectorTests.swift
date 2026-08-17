import XCTest
@testable import VoiceTyper

/// 用 URLProtocol 打桩验证 LLMCorrector 的失败兜底：网络/超时/截断/格式错误
/// 都必须原样返回输入文本，绝不让纠错失败丢掉已经识别出的文本。
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

    func testSuccessfulCorrectionReturnsCorrectedText() async {
        StubURLProtocol.handler = { _ in
            let body = #"{"choices":[{"message":{"content":"这个服务器的告警规则配置好了吗"},"finish_reason":"stop"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector(session: makeSession())
        let result = await corrector.correct("这个服物器的告警规则配置好了吗")
        XCTAssertEqual(result, "这个服务器的告警规则配置好了吗")
    }

    func testTruncatedResponseFallsBackToOriginalText() async {
        StubURLProtocol.handler = { _ in
            let body = #"{"choices":[{"message":{"content":"被截断的部分文本"},"finish_reason":"length"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector(session: makeSession())
        let original = "一段很长的听写内容"
        let result = await corrector.correct(original)
        XCTAssertEqual(result, original, "finish_reason=length 时必须放弃修正，返回原文")
    }

    func testHTTPErrorFallsBackToOriginalText() async {
        StubURLProtocol.handler = { _ in (500, "internal error") }
        let corrector = makeCorrector(session: makeSession())
        let original = "原始文本"
        let result = await corrector.correct(original)
        XCTAssertEqual(result, original)
    }

    func testMalformedJSONFallsBackToOriginalText() async {
        StubURLProtocol.handler = { _ in (200, "not json at all") }
        let corrector = makeCorrector(session: makeSession())
        let original = "原始文本"
        let result = await corrector.correct(original)
        XCTAssertEqual(result, original)
    }

    func testEmptyContentFallsBackToOriginalText() async {
        StubURLProtocol.handler = { _ in
            let body = #"{"choices":[{"message":{"content":"   "},"finish_reason":"stop"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector(session: makeSession())
        let original = "已识别的原文"
        let result = await corrector.correct(original)
        XCTAssertEqual(result, original, "2xx 空 content 不应丢弃已识别文本（F-03）")
    }

    func testEchoedTagsAreStripped() async {
        StubURLProtocol.handler = { _ in
            let body = #"{"choices":[{"message":{"content":"<asr_text>\n修正后的文本\n</asr_text>"},"finish_reason":"stop"}]}"#
            return (200, body)
        }
        let corrector = makeCorrector(session: makeSession())
        let result = await corrector.correct("原文")
        XCTAssertEqual(result, "修正后的文本")
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
