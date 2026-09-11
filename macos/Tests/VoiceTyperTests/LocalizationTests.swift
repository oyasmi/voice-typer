import XCTest
@testable import VoiceTyper

/// 中英双语的覆盖率闸门。
///
/// 文案表用中文原文做键，好处是漏翻不会让界面出现键名，坏处是漏翻会"静默地"退回中文——
/// 英文用户看到半个中文界面，而代码本身照常编译通过。这组测试把这件事变成可见的失败：
/// 扫描全部源码里的 `L("…")` / `LF("…")`，要求每一条都能在英文表里查到，且占位符一致。
final class LocalizationTests: XCTestCase {
    /// 源码根目录。测试宿主里拿不到仓库路径，用本文件的编译期路径回溯。
    private static func sourcesDirectory() -> URL {
        URL(fileURLWithPath: #filePath)            // …/macos/Tests/VoiceTyperTests/LocalizationTests.swift
            .deletingLastPathComponent()           // …/macos/Tests/VoiceTyperTests
            .deletingLastPathComponent()           // …/macos/Tests
            .deletingLastPathComponent()           // …/macos
            .appendingPathComponent("Sources/VoiceTyper", isDirectory: true)
    }

    /// 从源码里抓出所有 `L("…")` / `LF("…")` 的中文键。
    private func collectKeysFromSources() throws -> Set<String> {
        let root = Self.sourcesDirectory()
        let enumerator = FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil)
        var keys = Set<String>()
        // 只匹配单行字面量（本项目所有调用都是单行），转义序列按原样保留，
        // 因为文案表里的键同样是源码字面量。
        let pattern = try NSRegularExpression(pattern: #"\bLF?\(\s*"((?:[^"\\]|\\.)*)""#)

        while let url = enumerator?.nextObject() as? URL {
            guard url.pathExtension == "swift" else { continue }
            // 文案表自身不参与扫描：它的内容就是被检查的对象。
            guard url.lastPathComponent != "Strings+en.swift" else { continue }
            let content = try String(contentsOf: url, encoding: .utf8)
            let range = NSRange(content.startIndex..<content.endIndex, in: content)
            for match in pattern.matches(in: content, range: range) {
                guard let keyRange = Range(match.range(at: 1), in: content) else { continue }
                keys.insert(String(content[keyRange]))
            }
        }
        return keys
    }

    /// 把源码字面量里的转义还原成运行期真正的字符串（键在运行期就是还原后的形态）。
    private func unescape(_ literal: String) -> String {
        literal
            .replacingOccurrences(of: "\\n", with: "\n")
            .replacingOccurrences(of: "\\t", with: "\t")
            .replacingOccurrences(of: "\\\"", with: "\"")
            .replacingOccurrences(of: "\\\\", with: "\\")
    }

    /// 提取格式占位符（`%@` / `%d` / `%.2f` / `%%`）用于比对。
    private func placeholders(in text: String) throws -> [String] {
        let pattern = try NSRegularExpression(pattern: "%[0-9.]*[@dfs%]")
        let range = NSRange(text.startIndex..<text.endIndex, in: text)
        return pattern.matches(in: text, range: range).compactMap {
            Range($0.range, in: text).map { r in String(text[r]) }
        }
    }

    func testEveryLocalizedKeyHasEnglishTranslation() throws {
        let keys = try collectKeysFromSources()
        XCTAssertGreaterThan(keys.count, 100, "没有扫描到足够的文案调用，正则或路径可能失效")

        let missing = keys
            .map(unescape)
            .filter { L10n.englishTable[$0] == nil }
            .sorted()
        XCTAssertTrue(missing.isEmpty, "以下文案缺少英文翻译：\n" + missing.joined(separator: "\n"))
    }

    func testPlaceholdersMatchBetweenLanguages() throws {
        for (zh, en) in L10n.englishTable {
            let zhPlaceholders = try placeholders(in: zh)
            let enPlaceholders = try placeholders(in: en)
            XCTAssertEqual(zhPlaceholders, enPlaceholders, "占位符不一致：\(zh)")
        }
    }

    func testEnglishTableHasNoStaleEntries() throws {
        let keys = Set(try collectKeysFromSources().map(unescape))
        let stale = L10n.englishTable.keys.filter { !keys.contains($0) }.sorted()
        XCTAssertTrue(stale.isEmpty, "英文表里有源码已不再使用的条目：\n" + stale.joined(separator: "\n"))
    }

    func testLookupFallsBackToChineseWhenUntranslated() {
        L10n.setLanguage(.en)
        defer { L10n.setLanguage(.zh) }
        XCTAssertEqual(L10n.string("这条文案不存在于英文表"), "这条文案不存在于英文表")
        XCTAssertEqual(L10n.string("就绪"), "Ready")
    }

    func testChineseIsTheDefaultLanguage() {
        XCTAssertEqual(UIConfig().interfaceLanguage, .zh)
        L10n.setLanguage(.zh)
        XCTAssertEqual(L("就绪"), "就绪")
    }

    func testFormattingUsesActiveLanguage() {
        L10n.setLanguage(.en)
        defer { L10n.setLanguage(.zh) }
        XCTAssertEqual(LF("下载模型 %d%%", 42), "Downloading model 42%")
    }
}
