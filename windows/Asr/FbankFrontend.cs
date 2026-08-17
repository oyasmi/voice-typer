using System;
using System.Collections.Generic;

namespace VoiceTyper.Asr;

/// <summary>
/// Kaldi 兼容的 fbank 特征前端。C# 直译自 <c>macos/Sources/VoiceTyper/ASR/FbankFrontend.swift</c>，
/// 参数对齐 <c>kaldi_native_fbank</c>（knf 1.22.3）在 <c>client-server/server/voice_typer_server/recognizer.py</c>
/// 里的生效配置：
///
/// <code>
/// frame_opts: samp_freq=16000  frame_length_ms=25  frame_shift_ms=10
///             dither=0  preemph_coeff=0.97  remove_dc_offset=true
///             window_type=hamming  round_to_power_of_two=true  snip_edges=true
/// mel_opts:   num_bins=80  low_freq=20  high_freq=0(→nyquist)  is_librosa=0
/// </code>
///
/// 用 <c>macos/Tests/VoiceTyperTests/Fixtures/</c> 下的金标准夹具逐帧比对验证（阈值 1e-3），
/// 与 macOS 侧共用同一份 Python 参考输出。
/// </summary>
internal sealed class FbankOptions
{
    public double SampleRate { get; init; } = 16000;
    public double FrameLengthMs { get; init; } = 25;
    public double FrameShiftMs { get; init; } = 10;
    public int NumMelBins { get; init; } = 80;
    public double LowFreq { get; init; } = 20;
    /// <summary>0 表示用 Nyquist（sampleRate/2）。</summary>
    public double HighFreq { get; init; } = 0;
    public double PreemphCoeff { get; init; } = 0.97;
    public bool RemoveDCOffset { get; init; } = true;
}

internal sealed class FbankFrontend
{
    /// <summary>
    /// 不足一帧（25ms @16kHz = 400 samples）的输入无法产出任何 fbank 帧。
    /// 对应服务端 recognizer.py 的 <c>_MIN_SAMPLES = 400</c> 硬下限。
    /// </summary>
    public const int MinimumSamples = 400;

    /// <summary>
    /// Kaldi 的能量下限用最小正规格化 float，不是 <see cref="float.Epsilon"/>（次正规数，
    /// 差 7 个数量级）。写错不会崩，只会让静音帧的 log 能量偏低几十——这是最难在测试里
    /// 发现的一类 bug，金标准测试的静音段能抓到。
    /// </summary>
    private const float FloatMin = 1.17549435E-38f;

    private readonly FbankOptions _opts;
    public int FrameLength { get; }
    public int FrameShift { get; }
    private readonly int _fftSize;
    private readonly float[] _window;
    private readonly float[][] _melFilters;
    private readonly (int lo, int hi)[] _melBinRanges;
    private readonly Rfft _fft;

    public FbankFrontend(FbankOptions? opts = null)
    {
        _opts = opts ?? new FbankOptions();

        FrameLength = (int)Math.Round(_opts.FrameLengthMs * _opts.SampleRate / 1000);
        FrameShift = (int)Math.Round(_opts.FrameShiftMs * _opts.SampleRate / 1000);

        int n = 1;
        while (n < FrameLength) n <<= 1; // round_to_power_of_two
        _fftSize = n;
        _fft = new Rfft(n);

        // Hamming 窗，作用在原始 FrameLength 个样本上（不是补零后的 fftSize）。
        _window = new float[FrameLength];
        double denom = FrameLength - 1;
        for (int i = 0; i < FrameLength; i++)
        {
            _window[i] = (float)(0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / denom));
        }

        double nyquist = _opts.SampleRate / 2;
        double highFreq = _opts.HighFreq > 0 ? _opts.HighFreq : nyquist;
        double MelScale(double freq) => 1127.0 * Math.Log(1.0 + freq / 700.0);
        double melLow = MelScale(_opts.LowFreq);
        double melHigh = MelScale(highFreq);
        int numBins = _opts.NumMelBins;
        double melFreqDelta = (melHigh - melLow) / (numBins + 1);

        int numFFTBins = _fftSize / 2;
        double fftBinWidth = _opts.SampleRate / _fftSize;

        var filters = new List<float[]>(numBins);
        var ranges = new (int, int)[numBins];
        for (int bin = 0; bin < numBins; bin++)
        {
            double leftMel = melLow + bin * melFreqDelta;
            double centerMel = melLow + (bin + 1) * melFreqDelta;
            double rightMel = melLow + (bin + 2) * melFreqDelta;

            var thisBin = new float[numFFTBins];
            int firstIndex = -1;
            int lastIndex = -1;
            for (int fftBin = 0; fftBin < numFFTBins; fftBin++)
            {
                double freq = fftBin * fftBinWidth;
                double mel = MelScale(freq);
                if (!(mel > leftMel && mel < rightMel)) continue;
                double weight = mel <= centerMel
                    ? (mel - leftMel) / (centerMel - leftMel)
                    : (rightMel - mel) / (rightMel - centerMel);
                thisBin[fftBin] = (float)weight;
                if (firstIndex == -1) firstIndex = fftBin;
                lastIndex = fftBin;
            }
            if (firstIndex == -1) { firstIndex = 0; lastIndex = -1; }

            int span = Math.Max(0, lastIndex + 1 - firstIndex);
            var dense = new float[span];
            Array.Copy(thisBin, firstIndex, dense, 0, span);
            filters.Add(dense);
            ranges[bin] = (firstIndex, lastIndex);
        }
        _melFilters = filters.ToArray();
        _melBinRanges = ranges;
    }

    /// <param name="waveform">原始 float32 采样，范围 [-1, 1]（尚未按 knf 约定乘以 32768）。</param>
    /// <returns><c>[numFrames][numMelBins]</c> 的对数 mel 能量；样本不足一帧时返回空数组。</returns>
    public float[][] Compute(ReadOnlySpan<float> waveform)
    {
        int n = waveform.Length;
        if (n < FrameLength) return Array.Empty<float[]>();

        var scaled = new float[n];
        for (int i = 0; i < n; i++) scaled[i] = waveform[i] * 32768.0f;

        int numFrames = (n - FrameLength) / FrameShift + 1;
        var result = new float[numFrames][];

        var real = new float[_fftSize];
        var imag = new float[_fftSize];

        for (int f = 0; f < numFrames; f++)
        {
            int start = f * FrameShift;
            for (int i = 0; i < FrameLength; i++) real[i] = scaled[start + i];
            for (int i = FrameLength; i < _fftSize; i++) real[i] = 0f;
            Array.Clear(imag, 0, _fftSize);

            if (_opts.RemoveDCOffset)
            {
                float mean = 0;
                for (int i = 0; i < FrameLength; i++) mean += real[i];
                mean /= FrameLength;
                for (int i = 0; i < FrameLength; i++) real[i] -= mean;
            }

            if (_opts.PreemphCoeff != 0)
            {
                // Kaldi 的预加重按倒序原地修改：x[i] -= coeff*x[i-1]（i 从高到低），
                // 最后单独处理 x[0] -= coeff*x[0]。顺序不可颠倒，否则会用到"未来"的已修改值。
                float coeff = (float)_opts.PreemphCoeff;
                for (int i = FrameLength - 1; i > 0; i--)
                {
                    real[i] -= coeff * real[i - 1];
                }
                real[0] -= coeff * real[0];
            }

            for (int i = 0; i < FrameLength; i++) real[i] *= _window[i];

            _fft.Forward(real, imag);

            var melEnergies = new float[_opts.NumMelBins];
            int numFFTBins = _fftSize / 2;
            for (int bin = 0; bin < _opts.NumMelBins; bin++)
            {
                var (lo, hi) = _melBinRanges[bin];
                if (hi < lo) continue;
                var weights = _melFilters[bin];
                float sum = 0;
                for (int k = lo; k <= hi; k++)
                {
                    float power = real[k] * real[k] + imag[k] * imag[k];
                    sum += power * weights[k - lo];
                }
                melEnergies[bin] = MathF.Log(MathF.Max(sum, FloatMin));
            }
            result[f] = melEnergies;
        }
        return result;
    }
}
