import XCTest
@testable import VoiceTyper

/// VOC-3：热词写法 → 原始串变体 → token 序列。合成小词表覆盖规则；有真实 tokens.json 时追加两条实测断言。
final class HotwordCompilerTests: XCTestCase {
    private static let letters = (Array("abcdefghijklmnopqrstuvwxyz") + Array("ABCDEFGHIJKLMNOPQRSTUVWXYZ")).map(String.init)
    private static let digits = (0...9).map(String.init)

    /// 合成词表：特殊 token + 每个 ASCII 字母数字 + 若干多字符片段 + 汉字。
    private func makeTokens(extra: [String] = []) -> [String] {
        ["<unk>", "<s>", "</s>", "▁", "-", "+", "."] + Self.letters + Self.digits
            + ["▁Vo", "ice", "▁Ty", "per", "▁voice", "▁type", "▁Cla", "ude", "张", "三", "丰", "李", "<|zh|>"] + extra
    }

    private func pieces(_ result: HotwordCompiler.SurfaceResult, _ tokens: [String]) -> [String] {
        guard case .sequences(let sequences) = result else { return [] }
        return sequences.map { $0.map { tokens[$0] }.joined(separator: " ") }
    }

    // MARK: - 原始串变体

    func testVariantsCoverBoundarySplitAndCase() {
        XCTAssertEqual(HotwordCompiler.rawVariants(of: "VoiceTyper"), [
            "▁VoiceTyper", "VoiceTyper",
            "▁Voice▁Typer", "Voice▁Typer",
            "▁voice▁typer", "voice▁typer",
            "▁voicetyper", "voicetyper",
            "▁Voicetyper", "Voicetyper",
        ], "原样合写 → 原样拆词 → 小写拆词 → 小写合写 → 首字母大写；每种先带 ▁ 再不带，去重")
    }

    /// 汉字之后的 ASCII 段两种 ▁ 都生成；汉字永远不带 ▁。
    func testVariantsAfterHanGenerateBothBoundaries() {
        XCTAssertEqual(HotwordCompiler.rawVariants(of: "用Claude写"), [
            "用▁Claude写", "用Claude写", "用▁claude写", "用claude写",
        ])
        XCTAssertEqual(HotwordCompiler.rawVariants(of: "张三丰"), ["张三丰"])
    }

    /// 空格之后一定带 ▁；标点之后不带；字母↔数字处可拆。
    func testVariantsAfterSpaceAndPunctuation() {
        let claudeCode = HotwordCompiler.rawVariants(of: "Claude Code")
        XCTAssertEqual(Array(claudeCode.prefix(2)), ["▁Claude▁Code", "Claude▁Code"])
        XCTAssertEqual(HotwordCompiler.rawVariants(of: "GPT-4o"), [
            "▁GPT-4o", "GPT-4o", "▁GPT-4▁o", "GPT-4▁o", "▁gpt-4▁o", "gpt-4▁o",
            "▁gpt-4o", "gpt-4o", "▁Gpt-4o", "Gpt-4o", "▁Gpt-4▁O", "Gpt-4▁O",
        ])
    }

    func testSplitPoints() {
        XCTAssertEqual(HotwordCompiler.splitPoints(Array("VoiceTyper".unicodeScalars)), [5])
        XCTAssertEqual(HotwordCompiler.splitPoints(Array("GPT4o".unicodeScalars)), [3, 4])
        XCTAssertEqual(HotwordCompiler.splitPoints(Array("API".unicodeScalars)), [], "连续大写不拆")
    }

    // MARK: - 切分

    /// token 数最少优先；同数时第一个片段更长者优先；取前 2 优。
    func testSegmentationPrefersFewestTokensThenLongerFirstPiece() {
        let tokens = ["<unk>", "<s>", "</s>", "a", "b", "c", "ab", "bc"]
        let trie = TokenTrie(tokens: tokens)
        let result = trie.bestSegmentations(of: Array("abc".unicodeScalars))
        XCTAssertEqual(result.map { $0.map { tokens[$0] } }, [["ab", "c"], ["a", "bc"]])
    }

    func testSegmentationIgnoresSpecialAndReservedTokens() {
        let tokens = ["<unk>", "<s>", "</s>", "<|zh|>", "<", "|", "z", "h", ">", "s"]
        let trie = TokenTrie(tokens: tokens)
        XCTAssertEqual(trie.bestSegmentations(of: Array("<|zh|>".unicodeScalars)).first?.count, 6, "特殊 token 不进前缀树")
        XCTAssertEqual(trie.bestSegmentations(of: Array("<s>".unicodeScalars)).first?.count, 3, "id 0–2 不进前缀树")
    }

    func testSequencesUseBestThenSecondBestSegmentations() {
        let tokens = makeTokens()
        let trie = TokenTrie(tokens: tokens)
        let result = pieces(HotwordCompiler.sequences(for: "VoiceTyper", trie: trie), tokens)
        XCTAssertEqual(result.first, "▁Vo ice T y per")
        XCTAssertTrue(result.contains("▁voice ▁type r"))
        XCTAssertLessThanOrEqual(result.count, HotwordCompiler.maxSequencesPerSurface)
        XCTAssertEqual(Set(result).count, result.count, "去重")
    }

    // MARK: - 过滤

    /// VOC-D6：原写法只有一个 token（单个汉字、单个词片）不参与偏置。
    func testSingleTokenSurfaceIsTooShort() {
        let tokens = makeTokens(extra: ["▁Voice"])
        let trie = TokenTrie(tokens: tokens)
        XCTAssertEqual(HotwordCompiler.sequences(for: "李", trie: trie), .tooShort)
        XCTAssertEqual(HotwordCompiler.sequences(for: "Voice", trie: trie), .tooShort, "其余大小写变体也不再偏置")
    }

    func testUnsupportedCharactersAreReported() {
        let trie = TokenTrie(tokens: makeTokens())
        XCTAssertEqual(HotwordCompiler.sequences(for: "😀", trie: trie), .unsupported)
        XCTAssertEqual(HotwordCompiler.sequences(for: "张😀", trie: trie), .unsupported)
    }

    func testCompileReportStatuses() {
        let tokens = makeTokens()
        let trie = TokenTrie(tokens: tokens)
        let output = HotwordCompiler.compile(VocabularyConfig(entries: [
            VocabularyEntry(term: "张三丰"),
            VocabularyEntry(term: "李"),
            VocabularyEntry(term: "😀"),
            VocabularyEntry(term: "Claude", aliases: ["李"]),
        ]), trie: trie)
        XCTAssertEqual(output.report.entries.map(\.status), [.biased, .tooShort, .unsupportedCharacters, .biased])
        XCTAssertEqual(output.report.sequenceCount, output.sequences.count)
        XCTAssertEqual(output.report.status(forTerm: "claude"), .biased, "按 term 查状态不区分大小写")
        XCTAssertEqual(output.sequences.first?.map { tokens[$0] }, ["张", "三", "丰"])
    }

    /// 序列总数上限 4000：超出部分按词条顺序截断，没能入图的词条标 truncated。
    func testTotalSequenceLimit() {
        let trie = TokenTrie(tokens: makeTokens())
        let entries = (0..<400).map { index in
            VocabularyEntry(term: "q\(index)x", aliases: (0..<10).map { "w\(index)y\($0)" })
        }
        let output = HotwordCompiler.compile(VocabularyConfig(entries: entries), trie: trie)
        XCTAssertEqual(output.sequences.count, HotwordCompiler.maxTotalSequences)
        XCTAssertEqual(output.report.entries.first?.status, .biased)
        XCTAssertEqual(output.report.entries.last?.status, .truncated)
    }

    // MARK: - 真实词表（无模型时跳过）

    func testRealTokensSplitHanPerCharacterAndEnglishIntoRealPieces() throws {
        guard let bundle = ModelLocator.locate(explicitDir: "") else {
            throw XCTSkip("本机未找到 SenseVoice 模型，跳过真实词表测试")
        }
        let tokens = try ModelLocator.loadTokens(from: bundle.tokensURL)
        let trie = TokenTrie(tokens: tokens)

        guard case .sequences(let han) = HotwordCompiler.sequences(for: "张三丰", trie: trie) else {
            return XCTFail("张三丰 应可偏置")
        }
        XCTAssertEqual(han.map { $0.map { tokens[$0] } }, [["张", "三", "丰"]])

        guard case .sequences(let english) = HotwordCompiler.sequences(for: "VoiceTyper", trie: trie) else {
            return XCTFail("VoiceTyper 应可偏置")
        }
        let rendered = english.map { $0.map { tokens[$0] } }
        XCTAssertTrue(rendered.contains { $0.starts(with: ["▁Vo", "ice"]) }, "实际：\(rendered)")
    }
}
