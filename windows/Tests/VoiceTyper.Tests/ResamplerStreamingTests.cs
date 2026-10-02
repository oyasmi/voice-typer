using System;
using System.Collections.Generic;
using NAudio.Wave;
using VoiceTyper.Services;
using Xunit;
using Xunit.Abstractions;

namespace VoiceTyper.Tests;

/// <summary>
/// 采集链路重采样（BufferedWaveProvider → [ToMono] → WdlResamplingSampleProvider）的流式供给正确性。
/// 生产里每次 WASAPI 回调只给约 10ms 数据，读取循环必然以"短读"收尾，而 WDL 在拉取模式下遇到短读
/// 会进入 flush 分支（临时补零并丢弃部分滤波历史）。这组测试把"流式分块"与"一次性整段"的输出
/// 做对比，量化该分支的实际影响，用来决定是否需要换成输入驱动的重采样（见 TRIAGE T3-1）。
///
/// 实现用的是生产同一条链（<see cref="AudioCaptureService.BuildResamplingChain"/> 与
/// <see cref="AudioCaptureService.DrainResampled"/>）。指标总是写入测试输出；
/// 严格门限（SNR ≥ 40dB）只在设置环境变量 <c>VOICETYPER_RESAMPLER_STRICT=1</c> 时断言，
/// 因为这是"先测量、再决定"的验证项，不应在测量结果出来前让常规 CI 变红。
/// </summary>
public class ResamplerStreamingTests
{
    private readonly ITestOutputHelper _output;

    public ResamplerStreamingTests(ITestOutputHelper output) => _output = output;

    private const int OutRate = 16_000;
    private const double Seconds = 2.0; // 一次性基线单次回调最多消费约 3s 输出（DrainResampled 的保险上限）

    /// <summary>440Hz + 1kHz 混合正弦，幅度 0.3，交错多声道（各声道相同）。</summary>
    private static float[] MakeSignal(int sampleRate, int channels)
    {
        int frames = (int)(sampleRate * Seconds);
        var data = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)sampleRate;
            float v = (float)(0.15 * Math.Sin(2 * Math.PI * 440 * t) + 0.15 * Math.Sin(2 * Math.PI * 1000 * t));
            for (int c = 0; c < channels; c++) data[i * channels + c] = v;
        }
        return data;
    }

    private static byte[] ToBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static List<float> Run(float[] signal, int sampleRate, int channels, Func<int, int> nextBlockFrames)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        var input = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(10),
            DiscardOnBufferOverflow = true,
            ReadFully = false, // 与生产一致（R1-1）
        };
        var chain = AudioCaptureService.BuildResamplingChain(input, channels);
        var output = new List<float>();
        var bytes = ToBytes(signal);
        int frameBytes = 4 * channels;
        int offset = 0;
        int block = 0;
        while (offset < bytes.Length)
        {
            int frames = Math.Max(1, nextBlockFrames(block++));
            int count = Math.Min(frames * frameBytes, bytes.Length - offset);
            input.AddSamples(bytes, offset, count);
            offset += count;
            AudioCaptureService.DrainResampled(chain, (buffer, n) =>
            {
                for (int i = 0; i < n; i++) output.Add(buffer[i]);
            });
        }
        return output;
    }

    private static (double SnrDb, double MaxJump) Compare(List<float> reference, List<float> streamed)
    {
        // 跳过两端各 50ms：滤波器起始 / 收尾的瞬态与分块方式无关。
        int skip = OutRate / 20;
        int n = Math.Min(reference.Count, streamed.Count) - skip;
        double signal = 0, error = 0, maxJump = 0;
        for (int i = skip; i < n; i++)
        {
            double r = reference[i], s = streamed[i];
            signal += r * r;
            error += (s - r) * (s - r);
            if (i > skip) maxJump = Math.Max(maxJump, Math.Abs(s - streamed[i - 1]));
        }
        double snr = error <= 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / error);
        return (snr, maxJump);
    }

    public static IEnumerable<object[]> Formats() => new[]
    {
        new object[] { 48_000, 1 },
        new object[] { 48_000, 2 },
        new object[] { 44_100, 1 },
        new object[] { 44_100, 2 },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void FixedTenMillisecondBlocks_MatchOneShotOutput(int sampleRate, int channels)
    {
        var signal = MakeSignal(sampleRate, channels);
        var oneShot = Run(signal, sampleRate, channels, _ => 1_000_000);
        var streamed = Run(signal, sampleRate, channels, _ => sampleRate / 100);
        Report("10ms", sampleRate, channels, oneShot, streamed);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void RandomSevenToThirteenMillisecondBlocks_MatchOneShotOutput(int sampleRate, int channels)
    {
        var signal = MakeSignal(sampleRate, channels);
        var oneShot = Run(signal, sampleRate, channels, _ => 1_000_000);
        var random = new Random(12345);
        var streamed = Run(signal, sampleRate, channels, _ => sampleRate * random.Next(7, 14) / 1000);
        Report("7-13ms", sampleRate, channels, oneShot, streamed);
    }

    private void Report(string label, int sampleRate, int channels, List<float> oneShot, List<float> streamed)
    {
        double expected = Seconds * OutRate;
        double deviation = Math.Abs(streamed.Count - expected) / expected;
        var (snr, jump) = Compare(oneShot, streamed);
        _output.WriteLine(
            $"[{label}] {sampleRate}Hz x{channels}: 一次性={oneShot.Count} 流式={streamed.Count} 期望≈{expected:F0} "
            + $"偏差={deviation:P2} SNR={snr:F1}dB 最大相邻跳变={jump:F4}");

        Assert.True(streamed.Count > 0);
        Assert.True(deviation < 0.01, $"流式输出样本数偏离期望 {deviation:P2}，超过 1%");

        if (Environment.GetEnvironmentVariable("VOICETYPER_RESAMPLER_STRICT") == "1")
        {
            Assert.True(snr >= 40, $"流式与一次性输出的 SNR={snr:F1}dB，低于 40dB：应改为输入驱动的重采样（TRIAGE T3-1 第二步）");
        }
    }

    [Fact]
    public void SilenceInput_ProducesOnlySilence()
    {
        var signal = new float[48_000 * 2]; // 2 秒静音
        var streamed = Run(signal, 48_000, 1, _ => 480);
        Assert.NotEmpty(streamed);
        Assert.All(streamed, v => Assert.Equal(0f, v));
    }
}
