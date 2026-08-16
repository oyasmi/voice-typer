import XCTest
@testable import VoiceTyper

final class TextPostprocessorTests: XCTestCase {
    func testStripsRichTags() {
        let raw = "<|zh|><|NEUTRAL|><|Speech|><|withitn|>你好世界"
        XCTAssertEqual(TextPostprocessor.process(raw), "你好世界")
    }

    func testSentencePieceSpaceMarkerBecomesSpace() {
        XCTAssertEqual(TextPostprocessor.process("▁hello▁world"), "hello world")
    }

    func testPureSubstantivelessResultIsDropped() {
        XCTAssertEqual(TextPostprocessor.process("。"), "")
        XCTAssertEqual(TextPostprocessor.process("<|zh|><|NEUTRAL|><|Speech|><|withitn|>。"), "")
    }

    func testRemovesSpaceAfterCJKBeforeLatin() {
        XCTAssertEqual(TextPostprocessor.process("你好▁issue"), "你好issue")
    }

    func testRemovesSpaceBeforeCJKAfterLatin() {
        XCTAssertEqual(TextPostprocessor.process("issue▁你好"), "issue你好")
    }

    func testKeepsSpaceBetweenLatinWords() {
        XCTAssertEqual(TextPostprocessor.process("audio▁driven▁text"), "audio driven text")
    }

    func testCollapsesRepeatedWhitespace() {
        XCTAssertEqual(TextPostprocessor.process("hello   world"), "hello world")
    }

    func testKeepsJapaneseAndKoreanAsSubstantive() {
        XCTAssertEqual(TextPostprocessor.process("こんにちは"), "こんにちは")
        XCTAssertEqual(TextPostprocessor.process("안녕하세요"), "안녕하세요")
    }
}
