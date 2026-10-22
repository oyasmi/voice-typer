import XCTest
import Yams
@testable import VoiceTyper

/// §10.5 离线评估工具（调参用，默认跳过）。
///
/// 环境变量 `VT_HOTWORD_EVAL_DIR` 指向一个目录（不入库），内含若干 `*.wav`（16kHz 单声道）与同名 `*.txt`
/// （期望文本），以及一份 `vocabulary.yaml`：顶层写 `entries:`，内容与 config.yaml 里 `vocabulary` 段相同（`bias` 忽略）。
/// 对关 / 轻 / 标准 / 强四档分别输出召回、误触发、字错误率与平均解码耗时。输出只含数字与文件名，不打印识别文本。
///
/// ```bash
/// VT_HOTWORD_EVAL_DIR=~/vt-eval xcodebuild -project VoiceTyper.xcodeproj -scheme VoiceTyper \
///   -destination 'platform=macOS' test -only-testing:VoiceTyperTests/HotwordEvaluationTests
/// ```
///
/// 「关」档只关识别偏置，替换层照常生效，所以它与其余三档的差就是识别偏置本身的贡献。
/// 校准目标：标准档误触发为 0、召回尽量高（`DESIGN.md` §11.7）。
final class HotwordEvaluationTests: XCTestCase {
    private struct Sample {
        let name: String
        let samples: [Float]
        let expected: String
    }

    private struct Score {
        var recallHits = 0
        var recallTotal = 0
        var falseTriggers = 0
        var editDistance = 0
        var referenceLength = 0
        var decodeNanos: UInt64 = 0
        var files = 0
        var falseTriggerFiles: [String] = []
        var missedFiles: [String] = []
    }

    func testEvaluateBiasPresets() throws {
        guard let directory = ProcessInfo.processInfo.environment["VT_HOTWORD_EVAL_DIR"], !directory.isEmpty else {
            throw XCTSkip("未设置 VT_HOTWORD_EVAL_DIR，跳过热词离线评估")
        }
        guard let bundle = ModelLocator.locate(explicitDir: "") else {
            throw XCTSkip("本机未找到 SenseVoice 模型，跳过热词离线评估")
        }
        let root = URL(fileURLWithPath: (directory as NSString).expandingTildeInPath, isDirectory: true)
        let vocabularyYAML = try String(contentsOf: root.appendingPathComponent("vocabulary.yaml"), encoding: .utf8)
        let entries = try YAMLDecoder().decode(VocabularyConfig.self, from: vocabularyYAML).validated().entries
        XCTAssertFalse(entries.isEmpty, "vocabulary.yaml 没有词条")
        let samples = try loadSamples(root)
        XCTAssertFalse(samples.isEmpty, "目录里没有成对的 *.wav / *.txt")

        let engine = try SenseVoiceEngine(bundle: bundle, language: .auto, threads: 4)
        var lines = ["热词离线评估：\(samples.count) 个文件、\(entries.count) 条词汇",
                     "档位      召回          误触发  字错误率  平均解码"]
        for bias in VocabularyBias.allCases {
            let vocabulary = VocabularyConfig(bias: bias, entries: entries)
            _ = engine.setVocabulary(vocabulary)
            let replacer = VocabularyReplacer(vocabulary)
            var score = Score()
            for sample in samples {
                let output = OutputFormatter.formatForOutput(try engine.recognize(sample.samples), vocabulary: replacer)
                score.decodeNanos += engine.lastDecodeStats.decodeNanos
                score.files += 1
                accumulate(&score, output: output, sample: sample, entries: entries)
            }
            let recall = score.recallTotal == 0 ? 0 : Double(score.recallHits) / Double(score.recallTotal)
            let cer = score.referenceLength == 0 ? 0 : Double(score.editDistance) / Double(score.referenceLength)
            let decodeMs = Double(score.decodeNanos) / Double(max(score.files, 1)) / 1_000_000
            lines.append(String(
                format: "%@  %3d/%-3d %5.1f%%  %6d  %7.2f%%  %7.2fms",
                bias.rawValue.padding(toLength: 8, withPad: " ", startingAt: 0),
                score.recallHits, score.recallTotal, recall * 100, score.falseTriggers, cer * 100, decodeMs
            ))
            if !score.falseTriggerFiles.isEmpty {
                lines.append("          误触发文件：" + score.falseTriggerFiles.joined(separator: ", "))
            }
            if !score.missedFiles.isEmpty {
                lines.append("          未召回文件：" + score.missedFiles.joined(separator: ", "))
            }
        }
        print(lines.joined(separator: "\n"))
    }

    private func loadSamples(_ root: URL) throws -> [Sample] {
        let files = try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)
        return try files
            .filter { $0.pathExtension.lowercased() == "wav" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
            .compactMap { wav in
                let textURL = wav.deletingPathExtension().appendingPathExtension("txt")
                guard FileManager.default.fileExists(atPath: textURL.path) else { return nil }
                let (samples, sampleRate) = try WavLoader.loadMonoFloat32(wav)
                XCTAssertEqual(sampleRate, 16000, wav.lastPathComponent)
                let expected = try String(contentsOf: textURL, encoding: .utf8)
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                return Sample(name: wav.lastPathComponent, samples: samples, expected: expected)
            }
    }

    /// 召回：期望文本含某词条、输出也含它；误触发：期望文本不含某词条、输出却含它。都不区分大小写。
    private func accumulate(_ score: inout Score, output: String, sample: Sample, entries: [VocabularyEntry]) {
        let normalizedOutput = output.lowercased()
        let normalizedExpected = sample.expected.lowercased()
        var missed = false
        var triggered = false
        for entry in entries {
            let term = entry.term.lowercased()
            if normalizedExpected.contains(term) {
                score.recallTotal += 1
                if normalizedOutput.contains(term) {
                    score.recallHits += 1
                } else {
                    missed = true
                }
            } else if normalizedOutput.contains(term) {
                score.falseTriggers += 1
                triggered = true
            }
        }
        if missed { score.missedFiles.append(sample.name) }
        if triggered { score.falseTriggerFiles.append(sample.name) }

        // 字错误率不计空白：中英文空格是格式化层的事，不是识别错误。
        let hypothesis = Array(output.filter { !$0.isWhitespace })
        let reference = Array(sample.expected.filter { !$0.isWhitespace })
        score.editDistance += levenshtein(hypothesis, reference)
        score.referenceLength += reference.count
    }

    private func levenshtein(_ a: [Character], _ b: [Character]) -> Int {
        guard !a.isEmpty else { return b.count }
        guard !b.isEmpty else { return a.count }
        var previous = Array(0...b.count)
        var current = [Int](repeating: 0, count: b.count + 1)
        for i in 1...a.count {
            current[0] = i
            for j in 1...b.count {
                let cost = a[i - 1] == b[j - 1] ? 0 : 1
                current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + cost)
            }
            previous = current
        }
        return previous[b.count]
    }
}
