using System;
using VoiceTyper.Asr;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 自写 512 点 radix-2 FFT 与朴素 O(n^2) DFT 的比对——固定尺寸、算法确定，
/// 这是它在没有金标准模型的情况下唯一需要的正确性验证（见 windows/DESIGN.md §4.3 D6）。
/// </summary>
public class FftTests
{
    [Fact]
    public void Forward_MatchesNaiveDft_ForRandomSignal()
    {
        const int n = 512;
        var rng = new Random(42);
        var real = new float[n];
        var imag = new float[n];
        for (int i = 0; i < n; i++) real[i] = (float)(rng.NextDouble() * 2 - 1);

        var expectedReal = new double[n];
        var expectedImag = new double[n];
        for (int k = 0; k < n; k++)
        {
            double sumRe = 0, sumIm = 0;
            for (int t = 0; t < n; t++)
            {
                double angle = -2.0 * Math.PI * k * t / n;
                sumRe += real[t] * Math.Cos(angle);
                sumIm += real[t] * Math.Sin(angle);
            }
            expectedReal[k] = sumRe;
            expectedImag[k] = sumIm;
        }

        var fft = new Rfft(n);
        fft.Forward(real, imag);

        double maxDiff = 0;
        for (int k = 0; k < n; k++)
        {
            maxDiff = Math.Max(maxDiff, Math.Abs(real[k] - expectedReal[k]));
            maxDiff = Math.Max(maxDiff, Math.Abs(imag[k] - expectedImag[k]));
        }
        Assert.True(maxDiff < 1e-2, $"FFT 与朴素 DFT 最大误差 {maxDiff} 超出阈值");
    }

    [Fact]
    public void Forward_OfImpulse_ProducesFlatSpectrum()
    {
        const int n = 512;
        var real = new float[n];
        var imag = new float[n];
        real[0] = 1f;

        var fft = new Rfft(n);
        fft.Forward(real, imag);

        for (int k = 0; k < n; k++)
        {
            Assert.True(Math.Abs(real[k] - 1f) < 1e-4f, $"bin {k}: real={real[k]}");
            Assert.True(Math.Abs(imag[k]) < 1e-4f, $"bin {k}: imag={imag[k]}");
        }
    }

    [Fact]
    public void Constructor_RejectsNonPowerOfTwo()
    {
        Assert.Throws<ArgumentException>(() => new Rfft(500));
    }
}
