import XCTest
@testable import VoiceTyper

final class LLMEndpointTests: XCTestCase {
    func testAppendsChatCompletionsPath() {
        let url = LLMEndpoint.chatCompletionsURL(from: "https://api.openai.com/v1")
        XCTAssertEqual(url?.absoluteString, "https://api.openai.com/v1/chat/completions")
    }

    func testDoesNotDuplicateExistingSuffix() {
        let url = LLMEndpoint.chatCompletionsURL(from: "https://api.openai.com/v1/chat/completions")
        XCTAssertEqual(url?.absoluteString, "https://api.openai.com/v1/chat/completions")
    }

    func testTrimsTrailingSlashes() {
        let url = LLMEndpoint.chatCompletionsURL(from: "https://api.openai.com/v1///")
        XCTAssertEqual(url?.absoluteString, "https://api.openai.com/v1/chat/completions")
    }

    func testAllowsHTTPLoopbackForLocalModels() {
        let url = LLMEndpoint.chatCompletionsURL(from: "http://localhost:1234/v1")
        XCTAssertEqual(url?.absoluteString, "http://localhost:1234/v1/chat/completions")
    }

    func testRejectsInputWithInternalWhitespace() {
        XCTAssertNil(LLMEndpoint.chatCompletionsURL(from: "https://api.openai.com/v1 extra"))
    }

    func testRejectsMissingScheme() {
        XCTAssertNil(LLMEndpoint.chatCompletionsURL(from: "api.openai.com/v1"))
    }

    func testRejectsNonHTTPScheme() {
        XCTAssertNil(LLMEndpoint.chatCompletionsURL(from: "ftp://api.openai.com/v1"))
    }

    func testRejectsPureChineseInput() {
        XCTAssertNil(LLMEndpoint.chatCompletionsURL(from: "这不是一个网址"))
    }

    func testRejectsEmptyInput() {
        XCTAssertNil(LLMEndpoint.chatCompletionsURL(from: "   "))
    }

    // MARK: - R2-07：明文 HTTP 限回环/私网

    func testAllowsHTTPIPv4Loopback() {
        XCTAssertNotNil(LLMEndpoint.chatCompletionsURL(from: "http://127.0.0.1:8080/v1"))
    }

    func testAllowsHTTPIPv6Loopback() {
        XCTAssertNotNil(LLMEndpoint.chatCompletionsURL(from: "http://[::1]:8080/v1"))
    }

    func testAllowsHTTPPrivateNetwork10() {
        XCTAssertNotNil(LLMEndpoint.chatCompletionsURL(from: "http://10.0.1.5:11434/v1"))
    }

    func testAllowsHTTPPrivateNetwork192168() {
        XCTAssertNotNil(LLMEndpoint.chatCompletionsURL(from: "http://192.168.1.20/v1"))
    }

    func testAllowsHTTPPrivateNetwork172Range() {
        XCTAssertNotNil(LLMEndpoint.chatCompletionsURL(from: "http://172.16.0.1/v1"))
        XCTAssertNotNil(LLMEndpoint.chatCompletionsURL(from: "http://172.31.255.255/v1"))
        XCTAssertNil(LLMEndpoint.chatCompletionsURL(from: "http://172.32.0.1/v1"), "172.32/12 之外不属于私网")
    }

    func testAllowsHTTPDotLocalHost() {
        XCTAssertNotNil(LLMEndpoint.chatCompletionsURL(from: "http://ollama.local:11434/v1"))
    }

    func testRejectsHTTPPublicHost() {
        switch LLMEndpoint.resolve("http://example.com/v1") {
        case .failure(.insecurePlaintextHost(let host)):
            XCTAssertEqual(host, "example.com")
        default:
            XCTFail("公网 host 的明文 HTTP 必须被拒绝")
        }
    }

    func testAllowsHTTPSForPublicHost() {
        XCTAssertNotNil(LLMEndpoint.chatCompletionsURL(from: "https://example.com/v1"))
    }
}
