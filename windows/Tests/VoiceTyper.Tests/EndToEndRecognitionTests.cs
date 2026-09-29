using System;
using System.IO;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 完整识别链路（fbank → LFR/CMVN → 真实 ONNX 推理 → CTC 解码 → 文本后处理）对真实语音样本的校验，
/// 对应 macOS 的 <c>EndToEndRecognitionTests</c>——这是「Windows 识别结果与 Python 参考一致」的正式验收。
/// 两个平台共用同一份夹具（<c>macos/Tests/VoiceTyperTests/Fixtures/</c>）。
///
/// 需要本机已有 SenseVoice 模型（首启下载过，或跑过 client-server/server/ 留下 ModelScope 缓存，
/// 或运行 <c>windows/scripts/fetch_model.ps1</c>）；缺失时明确跳过并写清原因，不会变成假通过。
/// </summary>
public class EndToEndRecognitionTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>极简 WAV 读取：只支持夹具用到的形态（PCM16 / float32 单声道），纯按 RIFF chunk 解析，
    /// 不依赖系统解码器，避免隐式重采样掩盖问题。</summary>
    internal static (float[] Samples, int SampleRate) LoadMonoWav(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 44 || System.Text.Encoding.ASCII.GetString(data, 0, 4) != "RIFF"
            || System.Text.Encoding.ASCII.GetString(data, 8, 4) != "WAVE")
        {
            throw new InvalidDataException($"不是合法的 RIFF/WAVE 文件: {path}");
        }

        int offset = 12;
        ushort audioFormat = 0, channels = 0, bitsPerSample = 0;
        uint sampleRate = 0;
        (int Start, int End)? dataRange = null;
        while (offset + 8 <= data.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(data, offset, 4);
            int size = (int)BitConverter.ToUInt32(data, offset + 4);
            int bodyStart = offset + 8;
            int bodyEnd = Math.Min(bodyStart + size, data.Length);
            if (id == "fmt ")
            {
                audioFormat = BitConverter.ToUInt16(data, bodyStart);
                channels = BitConverter.ToUInt16(data, bodyStart + 2);
                sampleRate = BitConverter.ToUInt32(data, bodyStart + 4);
                bitsPerSample = BitConverter.ToUInt16(data, bodyStart + 14);
            }
            else if (id == "data")
            {
                dataRange = (bodyStart, bodyEnd);
            }
            offset = bodyEnd + (size % 2); // chunk 按偶数对齐
        }

        if (dataRange is not { } range) throw new InvalidDataException("未找到 data chunk");
        if (channels != 1) throw new InvalidDataException($"仅支持单声道，实际 {channels} 声道");

        var length = range.End - range.Start;
        float[] samples;
        if (audioFormat == 3 && bitsPerSample == 32)
        {
            samples = new float[length / 4];
            Buffer.BlockCopy(data, range.Start, samples, 0, samples.Length * 4);
        }
        else if (audioFormat == 1 && bitsPerSample == 16)
        {
            samples = new float[length / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(data, range.Start + i * 2) / 32768f;
        }
        else
        {
            throw new InvalidDataException($"不支持的格式: format={audioFormat} bits={bitsPerSample}");
        }
        return (samples, (int)sampleRate);
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    [SkippableFact]
    public void MatchesPythonReferenceOnRealSpeech()
    {
        var bundle = ModelLocator.Locate("");
        Skip.If(bundle is null, "本机未找到 SenseVoice 模型，跳过端到端识别测试（运行 windows/scripts/fetch_model.ps1 后重试）");

        var wavPath = FixturePath("speech_zh_en_mixed.wav");
        var referencePath = FixturePath("speech_zh_en_mixed.reference.txt");
        Skip.IfNot(File.Exists(wavPath) && File.Exists(referencePath), "缺少语音夹具");

        var (samples, sampleRate) = LoadMonoWav(wavPath);
        Assert.Equal(16000, sampleRate);
        var reference = File.ReadAllText(referencePath).Trim();

        using var engine = new SenseVoiceEngine(bundle!, AsrLanguage.Auto, threads: 4);
        var decoded = engine.Recognize(samples);

        // 不要求逐字节相等：Python wheel 与 .NET 的 ORT 是两套独立编译的二进制，50 层 transformer 内部的
        // 浮点求和顺序不保证跨构建一致，个别模棱两可的 token（如单词大小写）可能翻转 argmax。
        // 这是良性的跨平台浮点不确定性，因此用编辑距离容忍度验收（与 macOS 侧同一标准）。
        var distance = Levenshtein(decoded, reference);
        Assert.True(distance <= 2, $"编辑距离应 ≤ 2，实际 {distance}；windows=「{decoded}」 python=「{reference}」");
    }

    [SkippableFact]
    public void ChunkedPreviewFlow_ConvergesToTheSameFinalText()
    {
        var bundle = ModelLocator.Locate("");
        Skip.If(bundle is null, "本机未找到 SenseVoice 模型");
        var wavPath = FixturePath("speech_zh_en_mixed.wav");
        Skip.IfNot(File.Exists(wavPath), "缺少语音夹具");

        var (samples, _) = LoadMonoWav(wavPath);
        using var engine = new SenseVoiceEngine(bundle!, AsrLanguage.Auto, threads: 4);
        var whole = engine.Recognize(samples);

        // 模拟录音：每 600ms 一块送进 RecognitionBuffer，中途做预览，最后 Finalize——
        // 预览（含窗口滚动固化）不能影响最终文本，Finalize 永远对完整音频整段重跑。
        var buffer = new RecognitionBuffer(engine, previewWindowSamples: 6 * 16000, reservedSampleCapacity: 120 * 16000);
        for (int i = 0; i < samples.Length; i += AppConstantsChunk)
        {
            var chunk = samples.AsSpan(i, Math.Min(AppConstantsChunk, samples.Length - i)).ToArray();
            buffer.Append(chunk);
            if ((i / AppConstantsChunk) % 3 == 2) _ = buffer.Preview();
        }
        var final = buffer.Finalize();

        Assert.Equal(whole, final);
        Assert.False(string.IsNullOrWhiteSpace(final));
    }

    private const int AppConstantsChunk = VoiceTyper.Support.AppConstants.ChunkSamples;
}
