import XCTest
@testable import VoiceTyper

/// API Base URL 是用户手输的，且 API Key 会随请求发往这个地址：
/// 归一化要稳，明文 HTTP 只允许回环 / 私网（R2-07），其余一律拒绝。
final class LLMEndpointTests: XCTestCase {
    private func resolved(_ input: String) -> String? {
        LLMEndpoint.chatCompletionsURL(from: input)?.absoluteString
    }

    func testNormalizesToChatCompletionsURL() {
        let expected = "https://api.openai.com/v1/chat/completions"
        XCTAssertEqual(resolved("https://api.openai.com/v1"), expected)
        XCTAssertEqual(resolved("https://api.openai.com/v1///"), expected, "尾部斜杠")
        XCTAssertEqual(resolved("https://api.openai.com/v1/chat/completions"), expected, "不重复追加后缀")
    }

    func testRejectsMalformedInput() {
        for input in ["   ", "api.openai.com/v1", "ftp://api.openai.com/v1", "这不是一个网址", "https://api.openai.com/v1 extra"] {
            XCTAssertNil(resolved(input), input)
        }
    }

    func testPlainHTTPIsAllowedOnlyForLoopbackAndPrivateHosts() {
        let allowed = [
            "http://localhost:1234/v1", "http://127.0.0.1:8080/v1", "http://[::1]:8080/v1",
            "http://10.0.1.5:11434/v1", "http://192.168.1.20/v1",
            "http://172.16.0.1/v1", "http://172.31.255.255/v1", "http://ollama.local:11434/v1",
        ]
        for input in allowed { XCTAssertNotNil(resolved(input), input) }

        XCTAssertNil(resolved("http://172.32.0.1/v1"), "172.16/12 之外不属于私网")
        XCTAssertNotNil(resolved("https://example.com/v1"), "HTTPS 不限制 host")
        switch LLMEndpoint.resolve("http://example.com/v1") {
        case .failure(.insecurePlaintextHost(let host)):
            XCTAssertEqual(host, "example.com")
        default:
            XCTFail("公网 host 的明文 HTTP 必须被拒绝")
        }
    }
}
