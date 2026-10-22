import XCTest
@testable import VoiceTyper

/// VOC-7：确定性替换。规则用例来自与 Windows 共享的 `vocabulary_cases.json`。
final class VocabularyReplacerTests: XCTestCase {
    private struct Case: Decodable {
        let input: String
        let expected: String
        let formatted: String?
        let note: String
    }

    private struct Group: Decodable {
        let name: String
        let vocabulary: [VocabularyEntry]
        let cases: [Case]
    }

    private struct CaseFile: Decodable {
        let version: Int
        let groups: [Group]
    }

    private func loadGroups() throws -> [Group] {
        let bundle = Bundle(for: VocabularyReplacerTests.self)
        let url = try XCTUnwrap(bundle.url(forResource: "vocabulary_cases", withExtension: "json"))
        let file = try JSONDecoder().decode(CaseFile.self, from: Data(contentsOf: url))
        XCTAssertEqual(file.version, 1)
        return file.groups
    }

    private func replacer(_ entries: [VocabularyEntry]) -> VocabularyReplacer {
        VocabularyReplacer(VocabularyConfig(entries: entries).validated())
    }

    func testSharedFixtureCases() throws {
        let groups = try loadGroups()
        XCTAssertGreaterThanOrEqual(groups.flatMap(\.cases).count, 27, "规格要求的初始用例不得减少")
        for group in groups {
            let replacer = replacer(group.vocabulary)
            for item in group.cases {
                XCTAssertEqual(replacer.apply(item.input), item.expected, "\(group.name)：\(item.note)")
                if let formatted = item.formatted {
                    XCTAssertEqual(
                        OutputFormatter.formatForOutput(item.input, vocabulary: replacer), formatted,
                        "\(group.name)（formatForOutput）：\(item.note)"
                    )
                }
            }
        }
    }

    /// 幂等是硬性要求：处理链里替换会跑两次（LLM 之前、上屏之前）。
    func testApplyIsIdempotentOnFixtures() throws {
        for group in try loadGroups() {
            let replacer = replacer(group.vocabulary)
            for item in group.cases {
                let once = replacer.apply(item.input)
                XCTAssertEqual(replacer.apply(once), once, "\(group.name)：\(item.note)")
            }
        }
    }

    /// 性质测试：term、alias、它们的小写形式与常见填充片段随机拼接，`apply` 都幂等。
    func testApplyIsIdempotentOnRandomConcatenations() throws {
        var state: UInt64 = 7
        func next() -> Int {
            state = state &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
            return Int(state >> 33)
        }
        for group in try loadGroups() {
            let replacer = replacer(group.vocabulary)
            var pieces = ["的", "用", " ", "-", "a", "x1", "，", "code", "Code", "voice", "typer", "4o", "gpt"]
            for entry in group.vocabulary {
                pieces.append(entry.term)
                pieces.append(entry.term.lowercased())
                pieces.append(contentsOf: entry.aliases)
            }
            for _ in 0..<2000 {
                let text = (0..<(1 + next() % 8)).map { _ in pieces[next() % pieces.count] }.joined()
                let once = replacer.apply(text)
                XCTAssertEqual(replacer.apply(once), once, "\(group.name)：「\(text)」")
            }
        }
    }

    func testEmptyVocabularyIsNoOp() {
        XCTAssertTrue(VocabularyReplacer.empty.isEmpty)
        XCTAssertEqual(VocabularyReplacer.empty.apply("用克劳德写 voice typer"), "用克劳德写 voice typer")
        XCTAssertTrue(VocabularyReplacer.empty.terms.isEmpty)
    }

    /// LLM 词表只放 term，按词汇表顺序。
    func testTermsKeepVocabularyOrder() {
        let replacer = replacer([
            VocabularyEntry(term: "Claude", aliases: ["克劳德"]),
            VocabularyEntry(term: "张三丰"),
        ])
        XCTAssertEqual(replacer.terms, ["Claude", "张三丰"])
    }
}
