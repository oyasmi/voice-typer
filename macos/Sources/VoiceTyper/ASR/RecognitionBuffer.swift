import Foundation

/// 单次录音会话的音频累积与滑窗预览。移植自服务端 `recognizer.SenseVoiceSession`
/// （见 macos/DESIGN.md §5.2），逻辑原样搬运，不做"改进"：
///
/// SenseVoice 本身非流式，预览靠"对已累积音频整段重跑"实现。但对全部音频重跑的成本
/// 随录音时长线性增长而预览频率固定，于是单会话累计 CPU 会随录音时长呈平方增长。
/// 用一条滚动窗口规避：窗口左侧的音频只识别一次、固化进 `committedText`，之后的预览
/// 只对窗口右侧（最近 `previewWindowSamples` 个采样点）重跑。
///
/// 每次新建、用完即弃；非线程安全的部分（`committedText`/`committedN`）只应在
/// `ASRService.asrQueue` 上访问；`append` 允许从任意线程调用（内部用锁保护）。
final class RecognitionBuffer: @unchecked Sendable {
    /// 预览窗口（采样点，16kHz）。稳态预览 CPU ≈ RTF × 15s / 0.6s（0.6s 为客户端送帧间隔）。
    static let previewWindowSamples = 15 * 16000
    /// 窗口滚动时，在目标切点 ±100ms 内挑能量最低的 10ms 处下刀。
    private static let seamSearchRadius = 1600
    private static let seamStep = 160

    private let engine: any SenseVoiceRecognizing
    private let lock = NSLock()
    private var chunks: [[Float]] = []
    private var n = 0
    private var joined: [Float]?

    /// 窗口左侧已固化的预览文本；只在 asrQueue 上读写。
    private var committedText = ""
    private var committedN = 0

    init(engine: any SenseVoiceRecognizing) {
        self.engine = engine
    }

    /// 累计接收的样本数，供调用方做会话时长上限判断。可从任意线程调用。
    var sampleCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return n
    }

    /// 累积音频。必须足够便宜——可能跑在音频采集的回调路径上。
    func append(_ chunk: [Float]) {
        guard !chunk.isEmpty else { return }
        lock.lock()
        chunks.append(chunk)
        n += chunk.count
        joined = nil
        lock.unlock()
    }

    /// 返回全量预览文本 = 已固化前缀 + 当前窗口的识别结果。只应在 asrQueue 上调用。
    func preview() throws -> String {
        let audio = audioBuffer()
        if audio.count - committedN > Self.previewWindowSamples {
            try roll(audio)
        }
        let tail = Array(audio[committedN...])
        return committedText + (try engine.recognize(tail))
    }

    /// 松手后调用：对完整音频重新整段识别。刻意不复用 preview 的拼接结果——
    /// 窗口边界处的固化文本可能有接缝瑕疵，finalize 产出的是要上屏的最终文本，
    /// 必须整段重跑。只应在 asrQueue 上调用。
    func finalize() throws -> String {
        try engine.recognize(audioBuffer())
    }

    /// 把窗口前半段识别成文本固化，之后的预览不再重复识别这段音频。
    private func roll(_ audio: [Float]) throws {
        let target = committedN + Self.previewWindowSamples / 2
        let cut = Self.findSeam(audio, target: target)
        let segment = Array(audio[committedN..<cut])
        committedText += try engine.recognize(segment)
        committedN = cut
    }

    /// 在 target 附近找能量（RMS）最低的一小段作为切点，尽量避免把一个字切在正中间。
    private static func findSeam(_ audio: [Float], target: Int) -> Int {
        let lo = max(0, target - seamSearchRadius)
        let hi = min(audio.count, target + seamSearchRadius)
        let span = ((hi - lo) / seamStep) * seamStep
        guard span > 0 else { return target }

        var bestEnergy = Float.greatestFiniteMagnitude
        var bestOffset = 0
        var offset = 0
        while offset < span {
            var sum: Float = 0
            for i in 0..<seamStep {
                let sample = audio[lo + offset + i]
                sum += sample * sample
            }
            let energy = sum / Float(seamStep)
            if energy < bestEnergy {
                bestEnergy = energy
                bestOffset = offset
            }
            offset += seamStep
        }
        return lo + bestOffset
    }

    private func audioBuffer() -> [Float] {
        lock.lock()
        if let joined {
            lock.unlock()
            return joined
        }
        guard !chunks.isEmpty else {
            lock.unlock()
            return []
        }
        var flat = [Float]()
        flat.reserveCapacity(n)
        for chunk in chunks { flat.append(contentsOf: chunk) }
        joined = flat
        lock.unlock()
        return flat
    }
}
