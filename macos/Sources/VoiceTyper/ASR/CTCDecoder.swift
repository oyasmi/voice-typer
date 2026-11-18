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
        logits.withUnsafeBufferPointer { buf in
            decode(logits: buf.baseAddress, numFrames: numFrames, vocabSize: vocabSize, tokens: tokens)
        }
    }

    /// 零拷贝入口：直接在 ORT 输出的底层内存上解码，不构造中间 `[Float]` 副本。
    /// 300 秒音频场景下可省去约 500MB 的 logits 拷贝峰值（F-07a）。
    /// - Parameter logits: 指向 `[numFrames * vocabSize]` 行主序展平数据的指针。
    static func decode(logits: UnsafePointer<Float>?, numFrames: Int, vocabSize: Int, tokens: [String]) -> String {
        guard numFrames > 0, vocabSize > 0, let logits else { return "" }
        let ids = greedyTokens(logits: logits, numFrames: numFrames, vocabSize: vocabSize)
        var raw = ""
        raw.reserveCapacity(ids.count * 2)
        for id in ids where id >= 0 && id < tokens.count {
            raw += tokens[id]
        }
        return TextPostprocessor.process(raw)
    }

    /// 束搜索回退时可复用各帧的非 blank 峰值，只在 blank 不是明确首选时重新求 argmax。
    static func greedyTokens(
        logits: UnsafePointer<Float>, numFrames: Int, vocabSize: Int, nonBlankMaxima: [Float]? = nil
    ) -> [Int] {
        var ids: [Int] = []
        ids.reserveCapacity(numFrames)
        var previous = -1
        for t in 0..<numFrames {
            let rowPtr = logits + t * vocabSize
            var maxIdx: vDSP_Length = 0
            if !(rowPtr[0] > (nonBlankMaxima?[t] ?? .infinity)) {
                var maxVal: Float = 0
                vDSP_maxvi(rowPtr, 1, &maxVal, &maxIdx, vDSP_Length(vocabSize))
            }
            let id = Int(maxIdx)
            // 先合并连续重复，再丢弃 blank。blank 仍会将两个相同 token 隔开。
            if id != previous, id != 0 { ids.append(id) }
            previous = id
        }
        return ids
    }
}
