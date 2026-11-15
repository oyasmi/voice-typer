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
        let flat = computeFlat(waveform)
        guard flat.frameCount > 0 else { return [] }
        var result: [[Float]] = []
        result.reserveCapacity(flat.frameCount)
        let dim = flat.melDim
        for f in 0..<flat.frameCount {
            let start = f * dim
            result.append(Array(flat.features[start..<(start + dim)]))
        }
        return result
    }

    /// 扁平化零帧内堆分配的 fbank 计算（S-5）：
    /// - 不拷贝整段 `waveform * 32768`（120s 录音省去 7.7MB 临时数组），改为按帧原地缩放；
    /// - FFT / 功率谱 / mel 工作缓冲在帧循环外预分配一次并复用（保持 `FbankFrontend` 本身无可变状态）；
    /// - `vvlogf` 直接写入行优先连续输出 `[frameCount × numMelBins]`。
    func computeFlat(_ waveform: [Float]) -> (features: [Float], frameCount: Int, melDim: Int) {
        let n = waveform.count
        let numMelBins = opts.numMelBins
        guard n >= frameLength else { return ([], 0, numMelBins) }

        let numFrames = (n - frameLength) / frameShift + 1
        var output = [Float](repeating: 0, count: numFrames * numMelBins)

        let half = fftSize / 2
        var frameBuf = [Float](repeating: 0, count: fftSize)
        var realp = [Float](repeating: 0, count: half)
        var imagp = [Float](repeating: 0, count: half)
        var power = [Float](repeating: 0, count: half + 1)
        var melEnergies = [Float](repeating: 0, count: numMelBins)
        var pcmScale: Float = 32768.0
        var logCount = Int32(numMelBins)

        waveform.withUnsafeBufferPointer { wavePtr in
            window.withUnsafeBufferPointer { winPtr in
                output.withUnsafeMutableBufferPointer { outPtr in
                    let waveBase = wavePtr.baseAddress!
                    let winBase = winPtr.baseAddress!
                    let outBase = outPtr.baseAddress!

                    for f in 0..<numFrames {
                        let start = f * frameShift
                        frameBuf.withUnsafeMutableBufferPointer { framePtr in
                            let fBase = framePtr.baseAddress!
                            vDSP_vsmul(waveBase + start, 1, &pcmScale, fBase, 1, vDSP_Length(frameLength))
                            if fftSize > frameLength {
                                vDSP_vclr(fBase + frameLength, 1, vDSP_Length(fftSize - frameLength))
                            }

                            if opts.removeDCOffset {
                                var mean: Float = 0
                                vDSP_meanv(fBase, 1, &mean, vDSP_Length(frameLength))
                                var negMean = -mean
                                vDSP_vsadd(fBase, 1, &negMean, fBase, 1, vDSP_Length(frameLength))
                            }

                            if opts.preemphCoeff != 0 {
                                // Kaldi 的预加重按倒序原地修改：x[i] -= coeff*x[i-1]（i 从高到低），
                                // 最后单独处理 x[0] -= coeff*x[0]。顺序不可颠倒，否则会用到"未来"的已修改值。
                                let coeff = Float(opts.preemphCoeff)
                                var i = frameLength - 1
                                while i > 0 {
                                    fBase[i] -= coeff * fBase[i - 1]
                                    i -= 1
                                }
                                fBase[0] -= coeff * fBase[0]
                            }

                            vDSP_vmul(fBase, 1, winBase, 1, fBase, 1, vDSP_Length(frameLength))
                            computePowerSpectrum(frameBase: fBase, realp: &realp, imagp: &imagp, power: &power)
                        }

                        // 用 vDSP_dotpr 做每个 mel bin 的加权求和，并把 80 个 bin 的 log
                        // 一次性交给 vvlogf 直接写进连续输出切片（R3-08 / S-5）。
                        power.withUnsafeBufferPointer { powerPtr in
                            let pBase = powerPtr.baseAddress!
                            for bin in 0..<numMelBins {
                                let (lo, hi) = melBinRanges[bin]
                                guard hi >= lo else {
                                    melEnergies[bin] = Float.ulpOfOne
                                    continue
                                }
                                let weights = melFilters[bin]
                                var sum: Float = 0
                                weights.withUnsafeBufferPointer { weightPtr in
                                    vDSP_dotpr(pBase + lo, 1, weightPtr.baseAddress!, 1, &sum, vDSP_Length(hi - lo + 1))
                                }
                                // 对数下限对齐 kaldi_native_fbank 的 std::numeric_limits<float>::epsilon()
                                // （Float.ulpOfOne ≈ 1.19e-7，而非 Float.leastNormalMagnitude，R4-10）。
                                melEnergies[bin] = max(sum, Float.ulpOfOne)
                            }
                        }

                        melEnergies.withUnsafeBufferPointer { inPtr in
                            vvlogf(outBase + f * numMelBins, inPtr.baseAddress!, &logCount)
                        }
                    }
                }
            }
        }
        return (output, numFrames, numMelBins)
    }

    /// 实数输入的功率谱，vDSP 实 FFT，写入预分配的 `power[0…fftSize/2]`（含 Nyquist）。
    private func computePowerSpectrum(
        frameBase: UnsafePointer<Float>,
        realp: inout [Float],
        imagp: inout [Float],
        power: inout [Float]
    ) {
        let half = fftSize / 2
        realp.withUnsafeMutableBufferPointer { realPtr in
            imagp.withUnsafeMutableBufferPointer { imagPtr in
                var split = DSPSplitComplex(realp: realPtr.baseAddress!, imagp: imagPtr.baseAddress!)
                frameBase.withMemoryRebound(to: DSPComplex.self, capacity: half) { complexPtr in
                    vDSP_ctoz(complexPtr, 2, &split, 1, vDSP_Length(half))
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
    }
}
