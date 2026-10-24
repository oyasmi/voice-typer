import XCTest
@testable import VoiceTyper

/// VOC-5：带偏置的 CTC 前缀束搜索。合成 logits 的规则用例来自与 Windows 共享的 `ctc_hotword_cases.json`。
final class CTCPrefixBeamSearchTests: XCTestCase {
    private struct CaseFile: Decodable {
        let version: Int
        let tokens: [String]
        let floor: Float
        let cases: [Case]
    }

    private struct Case: Decodable {
        let name: String
        let note: String
        let preset: String
        let hotwords: [[String]]
        let frames: [[String: Float]]
        let expected: [String]
        let expected_hits: Int
    }

    private func loadCases() throws -> CaseFile {
        let bundle = Bundle(for: CTCPrefixBeamSearchTests.self)
        let url = try XCTUnwrap(bundle.url(forResource: "ctc_hotword_cases", withExtension: "json"))
        let file = try JSONDecoder().decode(CaseFile.self, from: Data(contentsOf: url))
        XCTAssertEqual(file.version, 1)
        return file
    }

    private func decode(
        _ logits: [Float], frames: Int, vocabSize: Int, sequences: [[Int]], tokens: [String], bias: VocabularyBias
    ) throws -> CTCPrefixBeamSearch.Result {
        let parameters = try XCTUnwrap(CTCPrefixBeamSearch.Parameters.preset(bias))
        let graph = ContextGraph(
            sequences: sequences, perTokenBias: parameters.perTokenBias, maxScoredDepth: parameters.maxScoredDepth
        )
        let classes = CTCPrefixBeamSearch.TokenClasses(tokens: tokens)
        return try logits.withUnsafeBufferPointer { buffer in
            try CTCPrefixBeamSearch.decode(
                logits: buffer.baseAddress!, numFrames: frames, vocabSize: vocabSize,
                graph: graph, classes: classes, parameters: parameters
            )
        }
    }

    func testSharedFixtureCases() throws {
        let file = try loadCases()
        XCTAssertGreaterThanOrEqual(file.cases.count, 23, "初始用例与保守偏置回归用例不得减少")
        let id = Dictionary(uniqueKeysWithValues: file.tokens.enumerated().map { ($1, $0) })
        let vocabSize = file.tokens.count
        for item in file.cases {
            var logits = [Float](repeating: file.floor, count: item.frames.count * vocabSize)
            for (t, frame) in item.frames.enumerated() {
                for (token, score) in frame {
                    logits[t * vocabSize + id[token]!] = score
                }
            }
            let sequences = item.hotwords.map { $0.map { id[$0]! } }
            let bias = try XCTUnwrap(VocabularyBias(rawValue: item.preset))
            let result = try decode(
                logits, frames: item.frames.count, vocabSize: vocabSize, sequences: sequences, tokens: file.tokens, bias: bias
            )
            XCTAssertEqual(result.tokens.map { file.tokens[$0] }, item.expected, "\(item.name)：\(item.note)")
            XCTAssertEqual(result.hotwordHits, item.expected_hits, "\(item.name) 命中次数")
        }
    }

    // MARK: - 性质测试

    /// 可复现的伪随机数（LCG），不依赖系统随机源。
    private struct LCG {
        var state: UInt64
        mutating func next() -> UInt64 {
            state = state &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
            return state >> 33
        }
        mutating func unit() -> Float { Float(next() % 1_000_000) / 1_000_000 }
    }

    private func greedy(_ logits: [Float], frames: Int, vocabSize: Int) -> [Int] {
        var result: [Int] = []
        var previous = -1
        for t in 0..<frames {
            let row = logits[(t * vocabSize)..<((t + 1) * vocabSize)]
            let argmax = row.indices.max { row[$0] < row[$1] || (row[$0] == row[$1] && $0 > $1) }! - t * vocabSize
            if argmax != previous, argmax != 0 { result.append(argmax) }
            previous = argmax
        }
        return result
    }

    /// ①：尖锐分布（第二名都在门限外）下，带无关热词、带与原文重合的热词，结果都与贪心逐 token 相同。
    func testMatchesGreedyOnSharpDistributions() throws {
        let tokens = ["<unk>", "<s>", "</s>"] + (3..<40).map { "t\($0)" } + ["<|zh|>"]
        let vocabSize = tokens.count
        var rng = LCG(state: 2024)
        for trial in 0..<200 {
            let frames = 20 + Int(rng.next() % 60)
            var logits = [Float](repeating: 0, count: frames * vocabSize)
            for t in 0..<frames {
                for k in 0..<vocabSize { logits[t * vocabSize + k] = -8 - 12 * rng.unit() }
                let top = rng.next() % 5 < 3 ? 0 : 3 + Int(rng.next() % 37)
                logits[t * vocabSize + top] = 0
            }
            let reference = greedy(logits, frames: frames, vocabSize: vocabSize)
            // 一条与原文重合的热词（取贪心结果里的一段）与一条无关热词。
            var sequences: [[Int]] = [[38, 39, 37]]
            if reference.count >= 3 { sequences.append(Array(reference[1...2])) }
            for bias in [VocabularyBias.light, .standard, .strong] {
                let result = try decode(
                    logits, frames: frames, vocabSize: vocabSize, sequences: sequences, tokens: tokens, bias: bias
                )
                XCTAssertEqual(result.tokens, reference, "trial \(trial) \(bias)")
            }
        }
    }

    /// ⑧：同一输入重复解码，结果逐 token 相同（不依赖哈希表遍历顺序）。
    func testDecodingIsReproducible() throws {
        let tokens = ["<unk>", "<s>", "</s>", "A", "B", "C", "D"]
        var rng = LCG(state: 99)
        let frames = 120
        var logits = [Float](repeating: 0, count: frames * tokens.count)
        for index in logits.indices { logits[index] = -4 * rng.unit() }
        let first = try decode(logits, frames: frames, vocabSize: tokens.count, sequences: [[3, 4], [5, 6, 3]], tokens: tokens, bias: .strong)
        for _ in 0..<5 {
            let again = try decode(logits, frames: frames, vocabSize: tokens.count, sequences: [[3, 4], [5, 6, 3]], tokens: tokens, bias: .strong)
            XCTAssertEqual(again, first)
        }
    }

    /// §5.8：logits 含 NaN 时抛错（由引擎回退贪心），不返回垃圾结果、不崩溃。
    func testNonFiniteLogitsThrow() {
        let tokens = ["<unk>", "<s>", "</s>", "A", "B"]
        let logits = [Float](repeating: .nan, count: 3 * tokens.count)
        XCTAssertThrowsError(try decode(logits, frames: 3, vocabSize: tokens.count, sequences: [[3, 4]], tokens: tokens, bias: .standard))
    }

    func testOffPresetHasNoParameters() {
        XCTAssertNil(CTCPrefixBeamSearch.Parameters.preset(.off))
        let light = CTCPrefixBeamSearch.Parameters.preset(.light)!
        let strong = CTCPrefixBeamSearch.Parameters.preset(.strong)!
        XCTAssertLessThan(light.perTokenBias, strong.perTokenBias)
        XCTAssertLessThan(light.acousticGate, strong.acousticGate)
    }

    // MARK: - 性能（§10.4）

    /// 2000 帧（≈120s）× 25055、约 75% blank 帧、200 条词汇。只断言宽松上限防数量级退化：
    /// 测试按 Debug 构建运行，比 Release 慢约 10 倍；真实数值看真机度量日志的 `decode_ms`。
    func testDecodeTimeStaysWithinLooseBound() throws {
        let vocabSize = 25_055
        let tokens: [String] = (0..<vocabSize).map { index in
            switch index {
            case 0: return "<unk>"
            case 1, 2: return "<s>"
            case 24_884...: return "<|t\(index)|>"
            case ..<10_000: return String(UnicodeScalar(0x4E00 + index)!)
            default: return "w\(index)"
            }
        }
        var rng = LCG(state: 42)
        let frames = 2000
        // 底分用常数填充（Debug 构建下逐个生成 5000 万个随机数太慢），门限外的分数取什么值不影响结果。
        var logits = [Float](repeating: -20, count: frames * vocabSize)
        for t in 0..<frames {
            let base = t * vocabSize
            if t < 4 {
                logits[base + 24_884 + t] = 0
            } else if rng.next() % 4 != 0 {
                logits[base] = 0
            } else {
                logits[base + 3 + Int(rng.next() % 24_000)] = 0
                logits[base] = -3
                for _ in 0..<4 { logits[base + 3 + Int(rng.next() % 24_000)] = -2 - 4 * rng.unit() }
            }
        }
        let entries = (0..<200).map { _ in
            VocabularyEntry(term: (0..<3).map { _ in tokens[3 + Int(rng.next() % 9_990)] }.joined())
        }
        let compiled = HotwordCompiler.compile(VocabularyConfig(bias: .strong, entries: entries), trie: TokenTrie(tokens: tokens))
        XCTAssertEqual(compiled.report.count(of: .biased), 200)

        let startedAt = DispatchTime.now().uptimeNanoseconds
        let result = try decode(logits, frames: frames, vocabSize: vocabSize, sequences: compiled.sequences, tokens: tokens, bias: .strong)
        let elapsedMs = Double(DispatchTime.now().uptimeNanoseconds - startedAt) / 1_000_000
        XCTAssertFalse(result.tokens.isEmpty)
        XCTAssertLessThan(elapsedMs, 2_000, "解码 \(elapsedMs)ms")
    }
}
