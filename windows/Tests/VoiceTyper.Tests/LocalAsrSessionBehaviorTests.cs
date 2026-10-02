using System;
using System.Collections.Generic;
using System.Threading;
using VoiceTyper.Asr;
using VoiceTyper.Support;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>会话内的静音门限与打点（不需要 ONNX 与 UI 线程的部分）。</summary>
public class LocalAsrSessionBehaviorTests
{
    [Fact]
    public void ContainsSpeech_UsesRmsThreshold()
    {
        Assert.False(LocalAsrSession.ContainsSpeech(ReadOnlySpan<float>.Empty));
        Assert.False(LocalAsrSession.ContainsSpeech(new float[1600]));
        Assert.False(LocalAsrSession.ContainsSpeech(Constant(1600, AppConstants.SilenceRmsThreshold * 0.5f)));
        Assert.True(LocalAsrSession.ContainsSpeech(Constant(1600, AppConstants.SilenceRmsThreshold * 2f)));
        Assert.True(LocalAsrSession.ContainsSpeech(Constant(1600, -0.1f)));
    }

    [Fact]
    public void ContainsSpeech_AveragesOverTheWholeChunk()
    {
        // 一个孤立的尖峰不应把整段静音判成语音（RMS 而不是峰值）。
        var samples = new float[9600];
        samples[100] = 0.2f;
        Assert.False(LocalAsrSession.ContainsSpeech(samples));
    }

    [Fact]
    public void Timings_ReceivedSamples_CountsAcceptedAudioOnly()
    {
        var pump = new AsrPump("VoiceTyper.Test.Pump");
        try
        {
            var session = new LocalAsrSession(pump, () => null, null, 16_000);
            session.SendAudio(new byte[4 * 1000]);
            session.SendAudio(new byte[4 * 500]);
            session.SendAudio(new byte[3]); // 长度不是 4 的倍数：丢弃
            Assert.Equal(1500, session.Timings.ReceivedSamples);
            Assert.True(session.Timings.ColdAtStart); // 收音时引擎没有就绪
        }
        finally { pump.Dispose(); }
    }

    /// <summary>
    /// 冷恢复：引擎未就绪时缓存的有声音频，在引擎就绪、回灌 buffer 后必须恢复"有语音"标记。
    /// 否则用户恰好停顿（之后只有静音）时，预览会一直被跳过直到松键。
    /// </summary>
    [Fact]
    public void ReplayingCachedSpeech_RestoresSpeechFlag_SoPreviewRuns()
    {
        var pump = new AsrPump("VoiceTyper.Test.ReplayPump");
        try
        {
            IAsrEngine? engine = null;
            var session = new LocalAsrSession(pump, () => engine, null, 15 * AppConstants.TargetSampleRate);

            session.SendAudio(ToBytes(Constant(9600, 0.1f))); // 引擎未就绪：有声音频被缓存
            var fake = new FakeAsrEngine();
            engine = fake;
            session.SendAudio(ToBytes(new float[9600])); // 引擎就绪后只送静音

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (fake.RecognizeCalls == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
            Assert.True(fake.RecognizeCalls >= 1, "回灌缓存音频后应至少触发一次预览");
            session.Close();
        }
        finally { pump.Dispose(); }
    }

    [Fact]
    public void ReplayingCachedSilence_DoesNotTriggerPreview()
    {
        var pump = new AsrPump("VoiceTyper.Test.ReplaySilencePump");
        try
        {
            IAsrEngine? engine = null;
            var session = new LocalAsrSession(pump, () => engine, null, 15 * AppConstants.TargetSampleRate);

            session.SendAudio(ToBytes(new float[9600]));
            var fake = new FakeAsrEngine();
            engine = fake;
            session.SendAudio(ToBytes(new float[9600]));

            Thread.Sleep(200);
            Assert.Equal(0, fake.RecognizeCalls);
            session.Close();
        }
        finally { pump.Dispose(); }
    }

    [Fact]
    public void Close_ReleasesLeaseExactlyOnce()
    {
        var pump = new AsrPump("VoiceTyper.Test.LeasePump");
        try
        {
            var released = 0;
            var session = new LocalAsrSession(pump, () => null, null, 16_000, onClosed: () => released++);
            session.Close();
            session.Close();
            Assert.Equal(1, released);
        }
        finally { pump.Dispose(); }
    }

    private static byte[] ToBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] Constant(int count, float value)
    {
        var data = new float[count];
        Array.Fill(data, value);
        return data;
    }
}
