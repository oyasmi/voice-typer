import XCTest
@testable import VoiceTyper

/// HINT-8：局部声学低置信度备选提取、词汇表一致性过滤、Prompt 拼装与标签防御、度量字段测试。
final class AcousticHintExtractorTests: XCTestCase {
    private struct CaseFile: Decodable {
        let version: Int
        let tokens: [String]
        let floor: Float
        let margin_threshold: Double
        let cases: [CaseItem]
    }

    private struct ExpectedHint: Decodable, Equatable {
        let surface: String
        let alternatives: [String]
    }

    private struct CaseItem: Decodable {
        let name: String
        let note: String
        let mode: String
        let preset: String?
        let hotwords: [[String]]?
        let frames: [[String: Float]]
        let expected_text: String
        let expected_hints: [ExpectedHint]
    }

    private func loadCases() throws -> CaseFile {
        let bundle = Bundle(for: AcousticHintExtractorTests.self)
        let url = try XCTUnwrap(bundle.url(forResource: "asr_hints_cases", withExtension: "json"))
        let file = try JSONDecoder().decode(CaseFile.self, from: Data(contentsOf: url))
        XCTAssertEqual(file.version, 1)
        return file
    }

    func testSharedFixtureCases() throws {
        let file = try loadCases()
        XCTAssertGreaterThanOrEqual(file.cases.count, 11)
        let id = Dictionary(uniqueKeysWithValues: file.tokens.enumerated().map { ($1, $0) })
        let vocabSize = file.tokens.count
        let meta = AcousticHintExtractor.TokenMetadata(tokens: file.tokens)
        var params = AcousticHintExtractor.Parameters.default
        params.marginThreshold = file.margin_threshold

        for item in file.cases {
            var logits = [Float](repeating: file.floor, count: item.frames.count * vocabSize)
            for (t, frame) in item.frames.enumerated() {
                for (token, score) in frame {
                    logits[t * vocabSize + id[token]!] = score
                }
            }

            let text: String
            let hints: [AcousticHint]

            if item.mode == "beam" {
                let bias = try XCTUnwrap(VocabularyBias(rawValue: item.preset ?? "standard"))
                let beamParams = try XCTUnwrap(CTCPrefixBeamSearch.Parameters.preset(bias))
                let sequences = (item.hotwords ?? []).map { $0.map { id[$0]! } }
                let graph = ContextGraph(
                    sequences: sequences,
                    perTokenBias: beamParams.perTokenBias,
                    maxScoredDepth: beamParams.maxScoredDepth
                )
                let classes = CTCPrefixBeamSearch.TokenClasses(tokens: file.tokens)
                let decoded = try logits.withUnsafeBufferPointer { buf in
                    try CTCPrefixBeamSearch.decode(
                        logits: buf.baseAddress!, numFrames: item.frames.count, vocabSize: vocabSize,
                        graph: graph, classes: classes, parameters: beamParams
                    )
                }
                let raw = decoded.tokens.map { file.tokens[$0] }.joined()
                text = TextPostprocessor.process(raw)
                let aligned = try XCTUnwrap(logits.withUnsafeBufferPointer { buf in
                    AcousticHintExtractor.forcedAlign(
                        logits: buf.baseAddress!, numFrames: item.frames.count, vocabSize: vocabSize,
                        tokens: decoded.tokens, tokenStrings: file.tokens, graph: graph
                    )
                })
                hints = AcousticHintExtractor.extract(
                    logits: logits, numFrames: item.frames.count, vocabSize: vocabSize,
                    aligned: aligned, fullText: text, metadata: meta, parameters: params
                )
            } else {
                let (ids, aligned) = logits.withUnsafeBufferPointer { buf in
                    CTCDecoder.greedyAlignedTokens(
                        logits: buf.baseAddress!, numFrames: item.frames.count, vocabSize: vocabSize, tokens: file.tokens
                    )
                }
                let raw = ids.map { file.tokens[$0] }.joined()
                text = TextPostprocessor.process(raw)
                hints = AcousticHintExtractor.extract(
                    logits: logits, numFrames: item.frames.count, vocabSize: vocabSize,
                    aligned: aligned, fullText: text, metadata: meta, parameters: params
                )
            }

            XCTAssertEqual(text, item.expected_text, "\(item.name)：\(item.note)")
            let got = hints.map { ExpectedHint(surface: $0.surface, alternatives: $0.alternatives) }
            XCTAssertEqual(got, item.expected_hints, "\(item.name)：\(item.note)")
        }
    }

    func testNaNLogitsSafelyFallBackToEmptyHints() {
        let tokens = ["<unk>", "<s>", "</s>", "密", "幂", "等"]
        let meta = AcousticHintExtractor.TokenMetadata(tokens: tokens)
        var logits = [Float](repeating: -20, count: 2 * tokens.count)
        logits[3] = 0.0
        logits[4] = Float.nan
        logits[tokens.count + 5] = 0.0
        let aligned = [
            AlignedToken(id: 3, peakFrame: 0, protectedByHotword: false),
            AlignedToken(id: 5, peakFrame: 1, protectedByHotword: false),
        ]
        let hints = AcousticHintExtractor.extract(
            logits: logits, numFrames: 2, vocabSize: tokens.count,
            aligned: aligned, fullText: "密等", metadata: meta
        )
        XCTAssertTrue(hints.isEmpty)
    }

    func testReconcileHintsDropsReplacedAndExactTermsAndNormalizesAliases() {
        let vocab = VocabularyConfig(entries: [
            VocabularyEntry(term: "Claude", aliases: ["克劳德"]),
            VocabularyEntry(term: "VoiceTyper", aliases: ["语音打字"]),
        ])
        let replacer = VocabularyReplacer(vocab)
        let rawHints = [
            // 已被替换消失
            AcousticHint(surface: "克劳德", alternatives: ["可劳德"], bestMargin: 0.4),
            // 精确等于 term（忽略大小写）
            AcousticHint(surface: "claude", alternatives: ["cloud"], bestMargin: 0.5),
            // 正常保留，且备选里的 alias 规整为 term 并去重
            AcousticHint(surface: "密等", alternatives: ["克劳德", "Claude", "幂等"], bestMargin: 0.6),
            // 含非法标签字符被防御过滤
            AcousticHint(surface: "坏<tag>", alternatives: ["好"], bestMargin: 0.2),
        ]
        let replacedText = "用Claude检查密等性"
        let (active, dropped) = replacer.reconcileHints(rawHints, inReplacedText: replacedText)
        XCTAssertEqual(dropped, 2)
        XCTAssertEqual(active.count, 1)
        XCTAssertEqual(active[0].surface, "密等")
        XCTAssertEqual(active[0].alternatives, ["Claude", "幂等"])
    }

    func testLLMUserMessageFormattingAndEmptyHintsByteIdentical() {
        XCTAssertEqual(
            LLMCorrector.userMessage("你好", glossary: [], hints: []),
            "<asr_text>\n你好\n</asr_text>"
        )
        let hints = [
            AcousticHint(surface: "密等", alternatives: ["幂等", "密度"], bestMargin: 0.4),
            AcousticHint(surface: "将近", alternatives: ["降级"], bestMargin: 0.8),
        ]
        let msg = LLMCorrector.userMessage("测试文本", glossary: ["VoiceTyper", "SenseVoice"], hints: hints)
        XCTAssertEqual(AcousticHintExtractor.Parameters.default.marginThreshold, 1.15, accuracy: 1e-9)
        XCTAssertEqual(
            msg,
            """
            <glossary>VoiceTyper、SenseVoice</glossary>
            <asr_hints>
            「密等」备选：幂等 / 密度
            「将近」备选：降级
            </asr_hints>
            <asr_text>
            测试文本
            </asr_text>
            """
        )
    }

    func testDictationMetricsSummaryLineIncludesAsrHints() {
        var m = DictationMetrics(sessionID: 0x1a2b, mode: .hold, hotkey: .fn, pressedAt: 0)
        m.timings.llmResult = .off
        XCTAssertTrue(m.summaryLine().hasSuffix(" asr_hints=-"))

        m.timings.llmResult = .corrected
        m.timings.asrHintCount = 3
        XCTAssertTrue(m.summaryLine().hasSuffix(" asr_hints=3"))
    }
}
