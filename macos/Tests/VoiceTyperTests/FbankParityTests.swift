import XCTest
@testable import VoiceTyper

/// fbank / LFR+CMVN 逐帧比对服务端（Python `WavFrontend`）产出的金标准特征。
/// 夹具由 `macos/scripts/dump_reference_fixtures.py` 生成，需要本机已下载
/// SenseVoice 模型（含 am.mvn）——CI/无模型环境下用 XCTSkip 跳过。
final class FbankParityTests: XCTestCase {
    func testFbankMatchesPythonReference() throws {
        let bundle = Bundle(for: Self.self)
        guard let inputURL = bundle.url(forResource: "fbank_input", withExtension: "f32"),
              let refURL = bundle.url(forResource: "fbank_reference", withExtension: "f32"),
              let shapesURL = bundle.url(forResource: "fbank_parity_shapes", withExtension: "json") else {
            throw XCTSkip("缺少 fbank 金标准夹具，先运行 macos/scripts/dump_reference_fixtures.py")
        }

        let waveform = try loadFloats(inputURL)
        let shapes = try JSONDecoder().decode(Shapes.self, from: Data(contentsOf: shapesURL))
        let reference = try loadFloats(refURL)
        XCTAssertEqual(reference.count, shapes.fbank_frames * shapes.fbank_dim)

        let frontend = FbankFrontend()
        let feats = frontend.compute(waveform)
        XCTAssertEqual(feats.count, shapes.fbank_frames, "帧数应完全相同")

        var maxDiff: Float = 0
        for (t, row) in feats.enumerated() {
            XCTAssertEqual(row.count, shapes.fbank_dim)
            for d in 0..<row.count {
                let diff = abs(row[d] - reference[t * shapes.fbank_dim + d])
                maxDiff = max(maxDiff, diff)
            }
        }
        XCTAssertLessThan(maxDiff, 1e-3, "fbank 最大逐点误差应 < 1e-3，实际 \(maxDiff)")
    }

    func testLFRCMVNMatchesPythonReference() throws {
        let bundle = Bundle(for: Self.self)
        guard let inputURL = bundle.url(forResource: "fbank_input", withExtension: "f32"),
              let refURL = bundle.url(forResource: "lfrcmvn_reference", withExtension: "f32"),
              let shapesURL = bundle.url(forResource: "fbank_parity_shapes", withExtension: "json"),
              let cmvnURL = ModelLocator.locate(explicitDir: "")?.cmvnURL else {
            throw XCTSkip("缺少 LFR/CMVN 金标准夹具或本机模型（am.mvn），先运行 dump_reference_fixtures.py / 确保模型已下载")
        }

        let waveform = try loadFloats(inputURL)
        let shapes = try JSONDecoder().decode(Shapes.self, from: Data(contentsOf: shapesURL))
        let reference = try loadFloats(refURL)

        let frontend = FbankFrontend()
        let feats = frontend.compute(waveform)
        let lfr = applyLFR(feats, m: 7, n: 6)
        let stats = try CMVNStats.parse(contentsOf: cmvnURL)
        let normalized = applyCMVN(lfr, stats: stats)

        XCTAssertEqual(normalized.count, shapes.lfr_frames)

        var maxDiff: Float = 0
        for (t, row) in normalized.enumerated() {
            for d in 0..<row.count {
                let diff = abs(row[d] - reference[t * shapes.lfr_dim + d])
                maxDiff = max(maxDiff, diff)
            }
        }
        XCTAssertLessThan(maxDiff, 1e-3, "LFR+CMVN 最大逐点误差应 < 1e-3，实际 \(maxDiff)")
    }

    private struct Shapes: Decodable {
        let n_samples: Int
        let fbank_frames: Int
        let fbank_dim: Int
        let lfr_frames: Int
        let lfr_dim: Int
    }

    private func loadFloats(_ url: URL) throws -> [Float] {
        let data = try Data(contentsOf: url)
        return data.withUnsafeBytes { Array($0.bindMemory(to: Float.self)) }
    }
}
