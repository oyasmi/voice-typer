using System;

namespace VoiceTyper.Asr;

/// <summary>
/// 固定长度（2 的幂）的 radix-2 复数 FFT，原地、迭代实现（bit-reversal 置换 + 蝶形运算）。
///
/// .NET 没有内置 FFT（macOS 侧用 Accelerate 的 <c>vDSP_fft_zrip</c>）。这里直接对复数做变换
/// （实数输入时虚部置零）比 macOS 那套"打包实数 FFT"更简单——不需要 macOS 代码里那段
/// "vDSP_fft_zrip 输出是标准 DFT 的 2 倍，DC/Nyquist 分量打包在 realp[0]/imagp[0]，
/// 需要 0.5 缩放并特判"的处理；本实现的输出就是标准 DFT 结果，直接取 |X[k]|^2 即可。
///
/// 帧长固定为 400（25ms@16kHz），`round_to_power_of_two` 后固定 512 点，
/// 因此本类只需支持一个尺寸，构造时预计算一次旋转因子表与位反转表。
/// </summary>
internal sealed class Rfft
{
    private readonly int _n;
    private readonly int _log2n;
    private readonly float[] _cosTable;
    private readonly float[] _sinTable;
    private readonly int[] _bitReverse;

    public Rfft(int n)
    {
        if (n <= 1 || (n & (n - 1)) != 0)
        {
            throw new ArgumentException("n 必须是大于 1 的 2 的幂", nameof(n));
        }
        _n = n;
        _log2n = (int)Math.Round(Math.Log2(n));

        _cosTable = new float[n / 2];
        _sinTable = new float[n / 2];
        for (int k = 0; k < n / 2; k++)
        {
            double angle = -2.0 * Math.PI * k / n;
            _cosTable[k] = (float)Math.Cos(angle);
            _sinTable[k] = (float)Math.Sin(angle);
        }

        _bitReverse = new int[n];
        for (int i = 0; i < n; i++)
        {
            _bitReverse[i] = ReverseBits(i, _log2n);
        }
    }

    public int Length => _n;

    /// <summary>
    /// 原地正向 DFT。<paramref name="real"/>/<paramref name="imag"/> 长度必须等于构造时的 n。
    /// 纯实信号时调用方需先把 <paramref name="imag"/> 清零。
    /// </summary>
    public void Forward(Span<float> real, Span<float> imag)
    {
        int n = _n;
        if (real.Length != n || imag.Length != n)
        {
            throw new ArgumentException($"real/imag 长度必须等于 {n}");
        }

        for (int i = 0; i < n; i++)
        {
            int j = _bitReverse[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size / 2;
            int tableStep = n / size;
            for (int start = 0; start < n; start += size)
            {
                int tableIndex = 0;
                for (int k = 0; k < half; k++)
                {
                    float cos = _cosTable[tableIndex];
                    float sin = _sinTable[tableIndex];
                    tableIndex += tableStep;

                    int evenIdx = start + k;
                    int oddIdx = start + k + half;

                    float oddReal = real[oddIdx] * cos - imag[oddIdx] * sin;
                    float oddImag = real[oddIdx] * sin + imag[oddIdx] * cos;

                    float evenReal = real[evenIdx];
                    float evenImag = imag[evenIdx];

                    real[evenIdx] = evenReal + oddReal;
                    imag[evenIdx] = evenImag + oddImag;
                    real[oddIdx] = evenReal - oddReal;
                    imag[oddIdx] = evenImag - oddImag;
                }
            }
        }
    }

    private static int ReverseBits(int value, int bits)
    {
        int result = 0;
        for (int i = 0; i < bits; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }
}
