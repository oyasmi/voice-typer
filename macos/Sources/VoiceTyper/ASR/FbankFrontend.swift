import Accelerate
import Foundation

/// Kaldi 兼容的 fbank 特征前端，参数对齐 `kaldi_native_fbank`（knf 1.22.3）在
/// `client-server/server/voice_typer_server/recognizer.py` 里的生效配置：
///
/// ```
/// frame_opts: samp_freq=16000  frame_length_ms=25  frame_shift_ms=10
///             dither=0  preemph_coeff=0.97  remove_dc_offset=true
///             window_type=hamming  round_to_power_of_two=true  snip_edges=true
/// mel_opts:   num_bins=80  low_freq=20  high_freq=0(→nyquist)  is_librosa=0
/// ```
///
/// 已用 `dump_reference_fixtures.py` 生成的金标准逐帧比对：max abs diff ≈ 6e-5
/// （详见 macos/DESIGN.md §4.2、FbankParityTests）。
struct FbankOptions {
    var sampleRate: Double = 16000
    var frameLengthMs: Double = 25
    var frameShiftMs: Double = 10
    var numMelBins: Int = 80
    var lowFreq: Double = 20
    /// 0 表示用 Nyquist（sampleRate/2）。
    var highFreq: Double = 0
    var preemphCoeff: Double = 0.97
    var removeDCOffset: Bool = true
}

/// 不足一帧（25ms @16kHz = 400 samples）的输入无法产出任何 fbank 帧。
/// 对应服务端 recognizer.py 的 `_MIN_SAMPLES = 400` 硬下限。
let fbankMinimumSamples = 400

final class FbankFrontend {
    private let opts: FbankOptions
    let frameLength: Int
    let frameShift: Int
    private let fftSize: Int
    private let window: [Float]
    /// 每个 mel bin 只在 [lo, hi] 范围内的 FFT bin 上有非零权重；稠密存储该子区间即可。
    private let melFilters: [[Float]]
    private let melBinRanges: [(lo: Int, hi: Int)]
    private let log2n: vDSP_Length
    private let fftSetup: FFTSetup

    init(opts: FbankOptions = FbankOptions()) {
        self.opts = opts
        self.frameLength = Int((opts.frameLengthMs * opts.sampleRate / 1000).rounded())
        self.frameShift = Int((opts.frameShiftMs * opts.sampleRate / 1000).rounded())

        var n = 1
        while n < frameLength { n <<= 1 } // round_to_power_of_two
        self.fftSize = n
        self.log2n = vDSP_Length(log2(Double(n)))
        self.fftSetup = vDSP_create_fftsetup(log2n, FFTRadix(kFFTRadix2))!

        // Hamming 窗，作用在原始 frameLength 个样本上（不是补零后的 fftSize）。
        var w = [Float](repeating: 0, count: frameLength)
        let denom = Double(frameLength - 1)
        for i in 0..<frameLength {
            w[i] = Float(0.54 - 0.46 * cos(2.0 * Double.pi * Double(i) / denom))
        }
        self.window = w

        let nyquist = opts.sampleRate / 2
        let highFreq = opts.highFreq > 0 ? opts.highFreq : nyquist
        func melScale(_ freq: Double) -> Double { 1127.0 * log(1.0 + freq / 700.0) }
        let melLow = melScale(opts.lowFreq)
        let melHigh = melScale(highFreq)
        let numBins = opts.numMelBins
        let melFreqDelta = (melHigh - melLow) / Double(numBins + 1)

        let numFFTBins = fftSize / 2
        let fftBinWidth = opts.sampleRate / Double(fftSize)

        var filters: [[Float]] = []
        var ranges: [(Int, Int)] = []
        for bin in 0..<numBins {
            let leftMel = melLow + Double(bin) * melFreqDelta
            let centerMel = melLow + Double(bin + 1) * melFreqDelta
            let rightMel = melLow + Double(bin + 2) * melFreqDelta

            var thisBin = [Float](repeating: 0, count: numFFTBins + 1)
            var firstIndex = -1
            var lastIndex = -1
            for fftBin in 0..<numFFTBins {
                let freq = Double(fftBin) * fftBinWidth
                let mel = melScale(freq)
                guard mel > leftMel, mel < rightMel else { continue }
                let weight = mel <= centerMel
                    ? (mel - leftMel) / (centerMel - leftMel)
                    : (rightMel - mel) / (rightMel - centerMel)
                thisBin[fftBin] = Float(weight)
                if firstIndex == -1 { firstIndex = fftBin }
                lastIndex = fftBin
            }
            if firstIndex == -1 { firstIndex = 0; lastIndex = -1 }
            filters.append(Array(thisBin[firstIndex..<max(firstIndex, lastIndex + 1)]))
            ranges.append((firstIndex, lastIndex))
        }
        self.melFilters = filters
        self.melBinRanges = ranges
    }

    deinit {
        vDSP_destroy_fftsetup(fftSetup)
    }

    /// - Parameter waveform: 原始 float32 采样，范围 [-1, 1]（尚未按 knf 约定乘以 32768）。
    /// - Returns: `[numFrames][numMelBins]` 的对数 mel 能量；样本不足一帧时返回空数组。
    func compute(_ waveform: [Float]) -> [[Float]] {
        let n = waveform.count
        guard n >= frameLength else { return [] }

        let scaled = waveform.map { $0 * 32768.0 }
        let numFrames = (n - frameLength) / frameShift + 1

        var result: [[Float]] = []
        result.reserveCapacity(numFrames)

        var frameBuf = [Float](repeating: 0, count: fftSize)
        for f in 0..<numFrames {
            let start = f * frameShift
            for i in 0..<frameLength { frameBuf[i] = scaled[start + i] }
            for i in frameLength..<fftSize { frameBuf[i] = 0 }

            if opts.removeDCOffset {
                var mean: Float = 0
                vDSP_meanv(frameBuf, 1, &mean, vDSP_Length(frameLength))
                var negMean = -mean
                vDSP_vsadd(frameBuf, 1, &negMean, &frameBuf, 1, vDSP_Length(frameLength))
            }

            if opts.preemphCoeff != 0 {
                // Kaldi 的预加重按倒序原地修改：x[i] -= coeff*x[i-1]（i 从高到低），
                // 最后单独处理 x[0] -= coeff*x[0]。顺序不可颠倒，否则会用到"未来"的已修改值。
                let coeff = Float(opts.preemphCoeff)
                var i = frameLength - 1
                while i > 0 {
                    frameBuf[i] -= coeff * frameBuf[i - 1]
                    i -= 1
                }
                frameBuf[0] -= coeff * frameBuf[0]
            }

            for i in 0..<frameLength { frameBuf[i] *= window[i] }

            let power = powerSpectrum(frameBuf)
            // 用 vDSP_dotpr 做每个 mel bin 的加权求和（替代标量循环），并把 80 个 bin
            // 的 log 一次性交给 vvlogf 批量处理（替代逐个调用 logf），减少每帧的标量
            // 循环与函数调用开销——120s 音频对应约 12000 帧，累计可观（R3-08）。
            var melEnergies = [Float](repeating: 0, count: opts.numMelBins)
            power.withUnsafeBufferPointer { powerPtr in
                for bin in 0..<opts.numMelBins {
                    let (lo, hi) = melBinRanges[bin]
                    guard hi >= lo else { continue }
                    let weights = melFilters[bin]
                    var sum: Float = 0
                    weights.withUnsafeBufferPointer { weightPtr in
                        vDSP_dotpr(powerPtr.baseAddress! + lo, 1, weightPtr.baseAddress!, 1, &sum, vDSP_Length(hi - lo + 1))
                    }
                    melEnergies[bin] = max(sum, Float.leastNormalMagnitude)
                }
            }
            var logged = [Float](repeating: 0, count: opts.numMelBins)
            melEnergies.withUnsafeBufferPointer { inPtr in
                logged.withUnsafeMutableBufferPointer { outPtr in
                    var count = Int32(opts.numMelBins)
                    vvlogf(outPtr.baseAddress!, inPtr.baseAddress!, &count)
                }
            }
            result.append(logged)
        }
        return result
    }

    /// 实数输入的功率谱，vDSP 实 FFT，返回 bin 0…fftSize/2（含 Nyquist）。
    private func powerSpectrum(_ frame: [Float]) -> [Float] {
        let half = fftSize / 2
        var realp = [Float](repeating: 0, count: half)
        var imagp = [Float](repeating: 0, count: half)
        var power = [Float](repeating: 0, count: half + 1)

        realp.withUnsafeMutableBufferPointer { realPtr in
            imagp.withUnsafeMutableBufferPointer { imagPtr in
                var split = DSPSplitComplex(realp: realPtr.baseAddress!, imagp: imagPtr.baseAddress!)
                frame.withUnsafeBufferPointer { framePtr in
                    framePtr.baseAddress!.withMemoryRebound(to: DSPComplex.self, capacity: half) { complexPtr in
                        vDSP_ctoz(complexPtr, 2, &split, 1, vDSP_Length(half))
                    }
                }
                vDSP_fft_zrip(fftSetup, &split, 1, log2n, FFTDirection(kFFTDirection_Forward))

                // vDSP_fft_zrip 的输出是"标准"正向 DFT 结果的 2 倍；实数 FFT 打包约定下
                // realp[0] 存 DC 分量（bin 0），imagp[0] 存 Nyquist 分量（bin fftSize/2）。
                let scale: Float = 0.5
                let dc = split.realp[0] * scale
                let nyq = split.imagp[0] * scale
                power[0] = dc * dc
                power[half] = nyq * nyq
                for k in 1..<half {
                    let re = split.realp[k] * scale
                    let im = split.imagp[k] * scale
                    power[k] = re * re + im * im
                }
            }
        }
        return power
    }
}
