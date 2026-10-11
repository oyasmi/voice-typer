using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Core;
using VoiceTyper.Llm;
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

    /// <summary>永不返回的 LLM 服务，只在请求被取消时结束。</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static LlmCorrector HangingCorrector() => new(new LlmCorrector.Config
    {
        ChatCompletionsUrl = new Uri("https://example.invalid/v1/chat/completions"),
        ApiKey = "k", Model = "m", Temperature = 0, MaxTokens = 800, Timeout = 60,
    }, new HttpClient(new HangingHandler()));

    [Fact]
    public async Task SkipCorrection_FinishesWithRawText_AndMarksSkipped()
    {
        var pump = new AsrPump("VoiceTyper.Test.SkipPump");
        try
        {
            using var corrector = HangingCorrector();
            var session = new LocalAsrSession(pump, () => null, corrector, 16_000);
            var final = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OnFinal = text => final.TrySetResult(text);
            var started = 0;
            session.OnCorrectionStarted = () => started++;

            session.CompleteWithAsrText("原始识别文本");
            Assert.Equal(1, started);
            Assert.False(final.Task.IsCompleted); // 纠错请求挂着，还没有结果

            session.SkipCorrection();
            Assert.Equal("原始识别文本", await final.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(AsrSessionTimings.LlmOutcome.Skipped, session.Timings.LlmResult);
            session.Close();
        }
        finally { pump.Dispose(); }
    }

    [Fact]
    public async Task CloseDuringCorrection_DiscardsResult_AndSkipAfterwardsIsNoOp()
    {
        var pump = new AsrPump("VoiceTyper.Test.CloseCorrectionPump");
        try
        {
            using var corrector = HangingCorrector();
            var session = new LocalAsrSession(pump, () => null, corrector, 16_000);
            var finals = 0;
            session.OnFinal = _ => Interlocked.Increment(ref finals);

            session.CompleteWithAsrText("原始识别文本");
            session.Close();
            session.SkipCorrection(); // 已关闭：不应再产生结果
            await Task.Delay(200);
            Assert.Equal(0, Volatile.Read(ref finals));
        }
        finally { pump.Dispose(); }
    }

    [Fact]
    public void SkipCorrection_WithoutCorrectionInFlight_DoesNothing()
    {
        var pump = new AsrPump("VoiceTyper.Test.NoSkipPump");
        try
        {
            var session = new LocalAsrSession(pump, () => null, null, 16_000);
            session.SkipCorrection();
            Assert.Null(session.Timings.LlmResult);
        }
        finally { pump.Dispose(); }
    }

    // ─── 录音阶段的"在等模型"提示（REVIEW_UX F-02）──────────────

    [Fact]
    public void EngineWaitDuringRecording_NotifiedAfterOneSecondOfPendingAudio()
    {
        var pump = new AsrPump("VoiceTyper.Test.RecordingWaitPump");
        try
        {
            IAsrEngine? engine = null;
            var session = new LocalAsrSession(pump, () => engine, null, 15 * AppConstants.TargetSampleRate);
            var waits = new List<bool>();
            session.OnEngineWait = waiting => waits.Add(waiting);

            // 攒了 0.6 秒：还不到提示阈值（多数冷恢复几百毫秒内就绪，不值得闪一下）。
            session.SendAudio(ToBytes(Constant(AppConstants.TargetSampleRate * 6 / 10, 0.1f)));
            Assert.Empty(waits);

            session.SendAudio(ToBytes(Constant(AppConstants.TargetSampleRate * 6 / 10, 0.1f))); // 累计 1.2s
            Assert.Equal(new[] { true }, waits);

            var fake = new FakeAsrEngine();
            engine = fake;
            session.SendAudio(ToBytes(Constant(3200, 0.1f))); // 引擎就绪：配对地通知结束
            Assert.Equal(new[] { true, false }, waits);
            session.Close();
        }
        finally { pump.Dispose(); }
    }

    [Fact]
    public void EngineWaitDuringRecording_NotNotifiedWhenEngineReadyAtOnce()
    {
        var pump = new AsrPump("VoiceTyper.Test.NoWaitPump");
        try
        {
            var fake = new FakeAsrEngine();
            var session = new LocalAsrSession(pump, () => fake, null, 16_000);
            var waits = new List<bool>();
            session.OnEngineWait = waiting => waits.Add(waiting);

            session.SendAudio(ToBytes(Constant(AppConstants.TargetSampleRate * 2, 0.1f)));
            Assert.Empty(waits); // 引擎一直在：录音中不该出现"在等模型"
            session.Close();
        }
        finally { pump.Dispose(); }
    }

    // ─── 纠错结果在上屏前经 OnPartial 预览（REVIEW_UX F-06）──────

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    [Fact]
    public async Task CorrectionSuccess_PreviewsCorrectedText_BeforeFinal()
    {
        var pump = new AsrPump("VoiceTyper.Test.CorrectPreviewPump");
        try
        {
            using var corrector = new LlmCorrector(new LlmCorrector.Config
            {
                ChatCompletionsUrl = new Uri("https://example.invalid/v1/chat/completions"),
                ApiKey = "k", Model = "m", Temperature = 0, MaxTokens = 800, Timeout = 5,
            }, new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"修正后的文本"},"finish_reason":"stop"}]}"""),
            })));
            var session = new LocalAsrSession(pump, () => null, corrector, 16_000);
            var partials = new List<string>();
            var final = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OnPartial = t => partials.Add(t);
            session.OnFinal = t => final.TrySetResult(t);

            session.CompleteWithAsrText("原始识别文本");
            Assert.Equal("修正后的文本", await final.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            // 先看到 ASR 原文、再看到纠错后的文本：插入被推迟 / 降级为复制时，这是用户校对的唯一窗口。
            Assert.Equal(new[] { "原始识别文本", "修正后的文本" }, partials);
            session.Close();
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
