using System;
using System.IO;
using System.Text.Json;
using VoiceTyper.Asr;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// fbank / LFR+CMVN 逐帧比对服务端（Python <c>WavFrontend</c>）产出的金标准特征。
/// 夹具复用 <c>macos/Tests/VoiceTyperTests/Fixtures/</c>（由
/// <c>macos/scripts/dump_reference_fixtures.py</c> 生成）——两个平台的 fbank 实现共用同一份
/// Python 参考输出，不需要各自造一遍。
///
/// xUnit 2.x 没有干净的运行期动态跳过 API（不同于 macOS 侧的 <c>XCTSkip</c>），
/// 缺夹具/缺模型时用提前 return 代替"跳过"，测试仍会显示为通过——这是本文件的已知局限，
/// 效果上等价于 CI/无模型环境下不阻塞其余测试。
/// </summary>
public class FbankParityTests
{
    private sealed class Shapes
    {
        public int n_samples { get; set; }
        public int fbank_frames { get; set; }
        public int fbank_dim { get; set; }
        public int lfr_frames { get; set; }
        public int lfr_dim { get; set; }
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Fbank_MatchesPythonReference()
    {
        var inputPath = FixturePath("fbank_input.f32");
        var refPath = FixturePath("fbank_reference.f32");
        var shapesPath = FixturePath("fbank_parity_shapes.json");
        if (!File.Exists(inputPath) || !File.Exists(refPath) || !File.Exists(shapesPath)) return;

        var shapes = JsonSerializer.Deserialize<Shapes>(File.ReadAllText(shapesPath))!;
        var waveform = LoadFloats(inputPath);
        var reference = LoadFloats(refPath);
        Assert.Equal(reference.Length, shapes.fbank_frames * shapes.fbank_dim);

        var frontend = new FbankFrontend();
        var feats = frontend.Compute(waveform);
        Assert.Equal(shapes.fbank_frames, feats.Length);

        float maxDiff = 0;
        for (int t = 0; t < feats.Length; t++)
        {
            Assert.Equal(shapes.fbank_dim, feats[t].Length);
            for (int d = 0; d < feats[t].Length; d++)
            {
                var diff = Math.Abs(feats[t][d] - reference[t * shapes.fbank_dim + d]);
                if (diff > maxDiff) maxDiff = diff;
            }
        }
        Assert.True(maxDiff < 1e-3f, $"fbank 最大逐点误差应 < 1e-3，实际 {maxDiff}");
    }

    /// <summary>
    /// 全零输入的对数下限回归（W-03）：两个平台共用同一份金标准夹具，但夹具的合成信号
    /// （正弦+噪声）能量恒不为零，永远走不到下限分支，两边都测不到这个数量级写错的问题。
    /// 用真实全零输入直接实测：全零帧的对数能量应为 log(FLT_EPSILON) ≈ -15.942385，
    /// 而不是 log(FLT_MIN) ≈ -87.3（对齐 macOS bb25282 的
    /// testAllZeroInputHitsDocumentedLogFloor）。
    /// </summary>
    [Fact]
    public void Fbank_AllZeroInputHitsDocumentedLogFloor()
    {
        var frontend = new FbankFrontend();
        var silence = new float[400]; // 恰好一帧（25ms @ 16kHz），全零
        var feats = frontend.Compute(silence);

        Assert.Single(feats);
        const float expected = -15.942385f;
        foreach (var value in feats[0])
        {
            Assert.True(Math.Abs(value - expected) < 1e-4f, $"期望 {expected}，实际 {value}");
        }
    }

    [Fact]
    public void LfrCmvn_MatchesPythonReference()
    {
        var inputPath = FixturePath("fbank_input.f32");
        var refPath = FixturePath("lfrcmvn_reference.f32");
        var shapesPath = FixturePath("fbank_parity_shapes.json");
        if (!File.Exists(inputPath) || !File.Exists(refPath) || !File.Exists(shapesPath)) return;

        // 需要本机已有 am.mvn（下载过模型，或跑过 client-server/server/ 留下 ModelScope 缓存）。
        var bundle = ModelLocator.Locate("");
        if (bundle is null) return;

        var shapes = JsonSerializer.Deserialize<Shapes>(File.ReadAllText(shapesPath))!;
        var waveform = LoadFloats(inputPath);
        var reference = LoadFloats(refPath);

        var frontend = new FbankFrontend();
        var feats = frontend.Compute(waveform);
        var lfr = LfrCmvn.ApplyLfr(feats, 7, 6);
        var stats = CmvnStats.Parse(bundle.CmvnPath);
        var normalized = LfrCmvn.ApplyCmvn(lfr, stats);

        Assert.Equal(shapes.lfr_frames, normalized.Length);

        float maxDiff = 0;
        for (int t = 0; t < normalized.Length; t++)
        {
            for (int d = 0; d < normalized[t].Length; d++)
            {
                var diff = Math.Abs(normalized[t][d] - reference[t * shapes.lfr_dim + d]);
                if (diff > maxDiff) maxDiff = diff;
            }
        }
        Assert.True(maxDiff < 1e-3f, $"LFR+CMVN 最大逐点误差应 < 1e-3，实际 {maxDiff}");
    }

    private static float[] LoadFloats(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
