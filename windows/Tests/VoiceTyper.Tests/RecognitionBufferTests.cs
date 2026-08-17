using System;
using System.Reflection;
using VoiceTyper.Asr;
using Xunit;

namespace VoiceTyper.Tests;

public class RecognitionBufferTests
{
    private sealed class FakeEngine : ISenseVoiceRecognizing
    {
        public int CallCount;

        public string Recognize(ReadOnlySpan<float> samples)
        {
            CallCount++;
            // 用样本长度做一个确定性、可区分的"文本"，足以验证滑窗/finalize 行为，
            // 不需要真实模型。
            return $"[{samples.Length}]";
        }
    }

    [Fact]
    public void Preview_DoesNotRoll_BeforeExceedingWindow()
    {
        var engine = new FakeEngine();
        var buffer = new RecognitionBuffer(engine, previewWindowSamples: 16000);

        buffer.Append(new float[8000]);
        Assert.False(string.IsNullOrEmpty(buffer.Preview()));

        buffer.Append(new float[8000]);
        Assert.False(string.IsNullOrEmpty(buffer.Preview()));

        Assert.Equal(0, GetCommittedN(buffer));
    }

    [Fact]
    public void Preview_RollsWindow_WhenExceedingPreviewWindow()
    {
        var engine = new FakeEngine();
        var buffer = new RecognitionBuffer(engine, previewWindowSamples: 16000);

        buffer.Append(new float[32000]);
        var preview = buffer.Preview();

        Assert.False(string.IsNullOrEmpty(preview));
        Assert.True(GetCommittedN(buffer) > 0, "超过窗口后应发生滚动固化");
    }

    [Fact]
    public void Finalize_RunsOverFullAudio_NotWindowedPreview()
    {
        var engine = new FakeEngine();
        var buffer = new RecognitionBuffer(engine, previewWindowSamples: 16000);

        buffer.Append(new float[40000]);
        _ = buffer.Preview(); // 触发一次滚动，制造 committed 前缀
        var final = buffer.Finalize();

        // finalize 对完整 40000 样本整段重跑，不受 preview 滚动固化影响。
        Assert.Contains("[40000]", final);
    }

    [Fact]
    public void SampleCount_ReflectsAppendedTotal()
    {
        var engine = new FakeEngine();
        var buffer = new RecognitionBuffer(engine);
        buffer.Append(new float[100]);
        buffer.Append(new float[50]);
        Assert.Equal(150, buffer.SampleCount);
    }

    [Fact]
    public void Append_IgnoresEmptyChunk()
    {
        var engine = new FakeEngine();
        var buffer = new RecognitionBuffer(engine);
        buffer.Append(Array.Empty<float>());
        Assert.Equal(0, buffer.SampleCount);
    }

    private static int GetCommittedN(RecognitionBuffer buffer)
    {
        var field = typeof(RecognitionBuffer).GetField("_committedN", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (int)field.GetValue(buffer)!;
    }
}
