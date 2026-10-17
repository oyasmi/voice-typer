import XCTest
@testable import VoiceTyper

@MainActor
final class DictationHistoryTests: XCTestCase {
    func testNewestFirstAndCapacityEviction() {
        let history = DictationHistory()
        for index in 0..<(DictationHistory.capacity + 3) {
            history.record("第\(index)条", kind: .inserted)
        }
        XCTAssertEqual(history.entries.count, DictationHistory.capacity)
        XCTAssertEqual(history.entries.first?.text, "第12条")
        XCTAssertEqual(history.entries.last?.text, "第3条", "最旧的三条被淘汰")
    }

    func testBlankTextIsIgnored() {
        let history = DictationHistory()
        history.record("", kind: .inserted)
        history.record("  \n ", kind: .cancelled)
        XCTAssertTrue(history.entries.isEmpty)
    }

    func testSameTextAsLatestOnlyUpdatesKindAndTime() {
        let history = DictationHistory()
        let first = Date(timeIntervalSince1970: 100)
        let second = Date(timeIntervalSince1970: 200)
        history.record("同一句", kind: .cancelled, now: first)
        let id = history.entries[0].id
        history.record("同一句", kind: .inserted, now: second)

        XCTAssertEqual(history.entries.count, 1)
        XCTAssertEqual(history.entries[0].id, id)
        XCTAssertEqual(history.entries[0].kind, .inserted)
        XCTAssertEqual(history.entries[0].createdAt, second)
    }

    func testSameTextNotAtTopIsStillANewEntry() {
        let history = DictationHistory()
        history.record("甲", kind: .inserted)
        history.record("乙", kind: .inserted)
        history.record("甲", kind: .inserted)
        XCTAssertEqual(history.entries.map(\.text), ["甲", "乙", "甲"])
    }

    func testClearAndOnChange() {
        let history = DictationHistory()
        var changes = 0
        history.onChange = { changes += 1 }
        history.record("内容", kind: .inserted)
        XCTAssertEqual(changes, 1)
        history.record(" ", kind: .inserted)
        XCTAssertEqual(changes, 1, "被忽略的空白不触发变更")
        history.clear()
        XCTAssertTrue(history.entries.isEmpty)
        XCTAssertEqual(changes, 2)
        history.clear()
        XCTAssertEqual(changes, 2, "已经为空时清空不触发变更")
    }

    // MARK: - 菜单标题

    private func entry(_ text: String, _ kind: DictationHistory.Kind) -> DictationHistory.Entry {
        DictationHistory.Entry(id: UUID(), text: text, createdAt: Date(), kind: kind)
    }

    func testMenuTitleFlattensNewlinesAndTruncatesTo30Characters() {
        let long = String(repeating: "字", count: 45)
        XCTAssertEqual(
            DictationHistory.menuTitle(for: entry(long, .inserted)),
            String(repeating: "字", count: 30) + "…"
        )
        XCTAssertEqual(DictationHistory.menuTitle(for: entry("第一行\n第二行", .inserted)), "第一行 第二行")
        XCTAssertEqual(
            DictationHistory.menuTitle(for: entry(String(repeating: "字", count: 30), .inserted)),
            String(repeating: "字", count: 30),
            "恰好 30 个字符不加省略号"
        )
    }

    func testMenuTitleSuffixPerKind() {
        XCTAssertEqual(DictationHistory.menuTitle(for: entry("文本", .inserted)), "文本")
        XCTAssertEqual(DictationHistory.menuTitle(for: entry("文本", .copiedFocusChanged)), "文本（未输入）")
        XCTAssertEqual(DictationHistory.menuTitle(for: entry("文本", .copiedInsertFailed)), "文本（未输入）")
        XCTAssertEqual(DictationHistory.menuTitle(for: entry("文本", .cancelled)), "文本（已取消）")
        XCTAssertEqual(DictationHistory.menuTitle(for: entry("文本", .partialAfterTimeout)), "文本（部分）")
        XCTAssertEqual(DictationHistory.menuTitle(for: entry("文本", .partialAfterFailure)), "文本（部分）")
    }

    func testMenuTitleSuffixInEnglishHasLeadingSpace() {
        L10n.setLanguage(.en)
        defer { L10n.setLanguage(.zh) }
        XCTAssertEqual(DictationHistory.menuTitle(for: entry("hello", .cancelled)), "hello (cancelled)")
    }
}
