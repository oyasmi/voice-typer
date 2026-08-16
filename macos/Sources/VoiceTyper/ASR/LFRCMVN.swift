import Foundation

/// `am.mvn` 里的均值/方差，用于 CMVN 归一化。文件格式（Kaldi nnet1 风格）：
/// `<AddShift> ... \n <LearnRateCoef> 0 [ mean0 mean1 ... ] \n <Rescale> ... \n <LearnRateCoef> 0 [ var0 var1 ... ]`
/// 均值/方差数组各自单独占一整行。解析逻辑对齐
/// `funasr_onnx.utils.frontend.WavFrontend.load_cmvn`。
struct CMVNStats {
    let means: [Float]
    let vars: [Float]

    static func parse(contentsOf url: URL) throws -> CMVNStats {
        let text = try String(contentsOf: url, encoding: .utf8)
        let lines = text.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        var means: [Float] = []
        var vars: [Float] = []

        for i in 0..<lines.count {
            let tokens = lines[i].split(separator: " ").map(String.init)
            guard let first = tokens.first, i + 1 < lines.count else { continue }
            let next = lines[i + 1].split(separator: " ").map(String.init)
            guard next.first == "<LearnRateCoef>", next.count > 4 else { continue }
            let values = next[3..<(next.count - 1)].compactMap { Float($0) }
            if first == "<AddShift>" {
                means = values
            } else if first == "<Rescale>" {
                vars = values
            }
        }

        guard !means.isEmpty, means.count == vars.count else {
            throw NSError(
                domain: AppConstants.bundleIdentifier,
                code: 1101,
                userInfo: [NSLocalizedDescriptionKey: "am.mvn 解析失败或均值/方差维度不一致: \(url.path)"]
            )
        }
        return CMVNStats(means: means, vars: vars)
    }
}

/// LFR（Low Frame Rate）拼接：每 `n` 帧滑动取 `m` 帧原始特征拼接为一帧。
/// 对齐 `WavFrontend.apply_lfr`（本项目固定 m=7, n=6）。
///
/// 左侧用第一帧复制 `(m-1)/2` 份补齐；尾帧不足 m 帧时用最后一帧重复补齐。
func applyLFR(_ feats: [[Float]], m: Int, n: Int) -> [[Float]] {
    guard !feats.isEmpty else { return [] }

    let leftPad = (m - 1) / 2
    var padded = [[Float]](repeating: feats[0], count: leftPad)
    padded.append(contentsOf: feats)

    let originalT = feats.count
    let paddedT = originalT + leftPad
    let framesLFR = Int((Double(originalT) / Double(n)).rounded(.up))

    var out: [[Float]] = []
    out.reserveCapacity(framesLFR)

    for i in 0..<framesLFR {
        let start = i * n
        if m <= paddedT - start {
            var row = [Float]()
            row.reserveCapacity(m * feats[0].count)
            for k in 0..<m { row.append(contentsOf: padded[start + k]) }
            out.append(row)
        } else {
            var row = [Float]()
            for frame in padded[start...] { row.append(contentsOf: frame) }
            let numPadding = m - (paddedT - start)
            let lastFrame = padded[padded.count - 1]
            for _ in 0..<numPadding { row.append(contentsOf: lastFrame) }
            out.append(row)
        }
    }
    return out
}

/// `out = (in + mean) * var`，逐维度。对齐 `WavFrontend.apply_cmvn`。
func applyCMVN(_ feats: [[Float]], stats: CMVNStats) -> [[Float]] {
    feats.map { row in
        var out = [Float](repeating: 0, count: row.count)
        for d in 0..<row.count {
            out[d] = (row[d] + stats.means[d]) * stats.vars[d]
        }
        return out
    }
}
