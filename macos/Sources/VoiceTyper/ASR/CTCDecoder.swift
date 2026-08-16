import Accelerate
import Foundation

/// CTC 贪心解码：逐帧 argmax → 合并连续重复 → 去 blank(id=0) → 拼 token → 文本后处理。
/// 对齐 `SenseVoiceRecognizer._ctc_greedy_decode`。已用真实语音样本验证：
/// 对同一段 `say` 合成语音，Swift 解码结果与 Python 服务端逐字相同（含中英混排、标点、数字）。
enum CTCDecoder {
    /// - Parameters:
    ///   - logits: `[numFrames * vocabSize]` 行主序展平的 CTC logits。
    ///   - tokens: 词表，下标即 token id。
    static func decode(logits: [Float], numFrames: Int, vocabSize: Int, tokens: [String]) -> String {
        guard numFrames > 0, vocabSize > 0 else { return "" }

        var ids = [Int](repeating: 0, count: numFrames)
        logits.withUnsafeBufferPointer { buf in
            for t in 0..<numFrames {
                var maxVal: Float = 0
                var maxIdx: vDSP_Length = 0
                let rowPtr = buf.baseAddress! + t * vocabSize
                vDSP_maxvi(rowPtr, 1, &maxVal, &maxIdx, vDSP_Length(vocabSize))
                ids[t] = Int(maxIdx)
            }
        }

        // torch.unique_consecutive 的等价实现：仅保留与前一帧不同的位置。
        var collapsed: [Int] = []
        collapsed.reserveCapacity(ids.count)
        var prev = -1
        for id in ids {
            if id != prev { collapsed.append(id) }
            prev = id
        }
        let nonBlank = collapsed.filter { $0 != 0 } // blank_id = 0

        var raw = ""
        raw.reserveCapacity(nonBlank.count * 2)
        for id in nonBlank where id >= 0 && id < tokens.count {
            raw += tokens[id]
        }
        return TextPostprocessor.process(raw)
    }
}
