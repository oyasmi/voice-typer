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
}
