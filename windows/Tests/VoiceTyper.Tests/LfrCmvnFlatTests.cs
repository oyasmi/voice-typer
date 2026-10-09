using System;
using VoiceTyper.Asr;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>合并实现必须与"LFR → CMVN → 展平"的两步实现逐位一致（金标准夹具只覆盖整条链路，这里钉死中间步骤）。</summary>
public class LfrCmvnFlatTests
{
    private static float[][] RandomFeats(int frames, int dim, int seed)
    {
        var rng = new Random(seed);
        var feats = new float[frames][];
        for (int t = 0; t < frames; t++)
        {
            feats[t] = new float[dim];
            for (int d = 0; d < dim; d++) feats[t][d] = (float)(rng.NextDouble() * 20 - 10);
        }
        return feats;
    }

    [Theory]
    [InlineData(1, 7, 6)]
    [InlineData(2, 7, 6)]
    [InlineData(5, 7, 6)]
    [InlineData(6, 7, 6)]
    [InlineData(7, 7, 6)]
    [InlineData(100, 7, 6)]
    [InlineData(101, 7, 6)]
    [InlineData(37, 5, 2)]
    [InlineData(9, 1, 1)]
    public void Flat_EqualsTwoStepImplementation_BitForBit(int frames, int m, int n)
    {
        const int dim = 80;
        var feats = RandomFeats(frames, dim, seed: frames * 31 + m);
        var rng = new Random(7);
        var stats = new CmvnStats
        {
            Means = Array.ConvertAll(new float[m * dim], _ => (float)(rng.NextDouble() - 0.5)),
            Vars = Array.ConvertAll(new float[m * dim], _ => (float)(rng.NextDouble() + 0.5)),
        };

        var expectedRows = LfrCmvn.ApplyCmvn(LfrCmvn.ApplyLfr(feats, m, n), stats);
        var flat = LfrCmvn.ApplyLfrCmvnFlat(feats, m, n, stats, out var outFrames, out var rowDim);

        Assert.Equal(expectedRows.Length, outFrames);
        Assert.Equal(m * dim, rowDim);
        Assert.Equal(outFrames * rowDim, flat.Length);
        for (int t = 0; t < outFrames; t++)
        {
            for (int d = 0; d < rowDim; d++)
            {
                Assert.Equal(BitConverter.SingleToInt32Bits(expectedRows[t][d]), BitConverter.SingleToInt32Bits(flat[t * rowDim + d]));
            }
        }
    }

    [Fact]
    public void Flat_EmptyInput_ReturnsEmpty()
    {
        var stats = new CmvnStats { Means = new float[560], Vars = new float[560] };
        var flat = LfrCmvn.ApplyLfrCmvnFlat(Array.Empty<float[]>(), 7, 6, stats, out var frames, out var rowDim);
        Assert.Empty(flat);
        Assert.Equal(0, frames);
        Assert.Equal(0, rowDim);
    }
}
