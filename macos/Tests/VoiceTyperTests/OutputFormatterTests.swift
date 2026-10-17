import XCTest
@testable import VoiceTyper

/// R8-F2：中英文之间统一加空格。用例来自与 Windows 共享的 `text_format_cases.json`。
final class OutputFormatterTests: XCTestCase {
    private struct Case: Decodable {
        let input: String
        let expected: String
        let note: String
    }

    private struct CaseFile: Decodable {
        let version: Int
        let cjk_latin_spacing: [Case]
    }

    private func loadCases() throws -> [Case] {
        let bundle = Bundle(for: OutputFormatterTests.self)
        let url = try XCTUnwrap(bundle.url(forResource: "text_format_cases", withExtension: "json"))
        let file = try JSONDecoder().decode(CaseFile.self, from: Data(contentsOf: url))
        XCTAssertEqual(file.version, 1)
        return file.cjk_latin_spacing
    }

    func testSharedFixtureCases() throws {
        let cases = try loadCases()
        XCTAssertGreaterThanOrEqual(cases.count, 19, "规格要求的初始用例不得减少")
        for item in cases {
            XCTAssertEqual(
                OutputFormatter.applyCJKLatinSpacing(item.input), item.expected,
                "用例失败：\(item.note)"
            )
        }
    }

    /// 对每条期望值再跑一次结果不变：LLM 已经加过空格的文本不能被二次改动。
    func testFormattingIsIdempotent() throws {
        for item in try loadCases() {
            XCTAssertEqual(
                OutputFormatter.applyCJKLatinSpacing(item.expected), item.expected,
                "不幂等：\(item.note)"
            )
        }
    }

    func testFormatForOutputAppliesSpacing() {
        XCTAssertEqual(OutputFormatter.formatForOutput("我用Python写代码"), "我用 Python 写代码")
    }
}
