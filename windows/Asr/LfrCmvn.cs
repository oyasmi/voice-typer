using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VoiceTyper.Asr;

/// <summary>
/// <c>am.mvn</c> 里的均值/方差，用于 CMVN 归一化。文件格式（Kaldi nnet1 风格）：
/// <c>&lt;AddShift&gt; ... \n &lt;LearnRateCoef&gt; 0 [ mean0 mean1 ... ] \n &lt;Rescale&gt; ... \n &lt;LearnRateCoef&gt; 0 [ var0 var1 ... ]</c>
/// 均值/方差数组各自单独占一整行。解析逻辑对齐 <c>funasr_onnx.utils.frontend.WavFrontend.load_cmvn</c>。
/// </summary>
internal sealed class CmvnStats
{
    public required float[] Means { get; init; }
    public required float[] Vars { get; init; }

    public static CmvnStats Parse(string path)
    {
        var lines = File.ReadAllLines(path);
        float[]? means = null;
        float[]? vars = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || i + 1 >= lines.Length) continue;
            var first = tokens[0];
            var next = lines[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (next.Length <= 4 || next[0] != "<LearnRateCoef>") continue;

            var values = next[3..^1]
                .Select(s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (float?)v : null)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToArray();

            if (first == "<AddShift>") means = values;
            else if (first == "<Rescale>") vars = values;
        }

        if (means is null || vars is null || means.Length == 0 || means.Length != vars.Length)
        {
            throw new InvalidDataException($"am.mvn 解析失败或均值/方差维度不一致: {path}");
        }
        return new CmvnStats { Means = means, Vars = vars };
    }
}

internal static class LfrCmvn
{
    /// <summary>
    /// LFR（Low Frame Rate）拼接：每 <c>n</c> 帧滑动取 <c>m</c> 帧原始特征拼接为一帧。
    /// 对齐 <c>WavFrontend.apply_lfr</c>（本项目固定 m=7, n=6）。
    ///
    /// 左侧用第一帧复制 (m-1)/2 份补齐；尾帧不足 m 帧时用最后一帧重复补齐。
    /// </summary>
    public static float[][] ApplyLfr(float[][] feats, int m, int n)
    {
        if (feats.Length == 0) return Array.Empty<float[]>();

        int leftPad = (m - 1) / 2;
        var padded = new float[leftPad + feats.Length][];
        for (int i = 0; i < leftPad; i++) padded[i] = feats[0];
        Array.Copy(feats, 0, padded, leftPad, feats.Length);

        int originalT = feats.Length;
        int paddedT = originalT + leftPad;
        int dim = feats[0].Length;
        int framesLfr = (int)Math.Ceiling((double)originalT / n);

        var outArr = new float[framesLfr][];
        for (int i = 0; i < framesLfr; i++)
        {
            int start = i * n;
            var row = new float[m * dim];
            if (m <= paddedT - start)
            {
                for (int k = 0; k < m; k++)
                {
                    Array.Copy(padded[start + k], 0, row, k * dim, dim);
                }
            }
            else
            {
                int available = paddedT - start;
                for (int k = 0; k < available; k++)
                {
                    Array.Copy(padded[start + k], 0, row, k * dim, dim);
                }
                var lastFrame = padded[paddedT - 1];
                for (int k = available; k < m; k++)
                {
                    Array.Copy(lastFrame, 0, row, k * dim, dim);
                }
            }
            outArr[i] = row;
        }
        return outArr;
    }

    /// <summary><c>out = (in + mean) * var</c>，逐维度。对齐 <c>WavFrontend.apply_cmvn</c>。</summary>
    public static float[][] ApplyCmvn(float[][] feats, CmvnStats stats)
    {
        var outArr = new float[feats.Length][];
        for (int t = 0; t < feats.Length; t++)
        {
            var row = feats[t];
            var outRow = new float[row.Length];
            for (int d = 0; d < row.Length; d++)
            {
                outRow[d] = (row[d] + stats.Means[d]) * stats.Vars[d];
            }
            outArr[t] = outRow;
        }
        return outArr;
    }
}
