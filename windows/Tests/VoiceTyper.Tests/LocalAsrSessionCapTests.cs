using System;
using VoiceTyper.Asr;
using VoiceTyper.Support;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 单段录音上限的裁剪语义（对齐 macOS <c>LocalASRSessionTests</c> 的
/// <c>acceptWithinCap</c> 系列）。这里只覆盖"引擎尚未就绪、音频攒在 _pendingAudio"的路径——
/// 该路径全程同步、不经过 <see cref="UiDispatcher"/>，无需真实模型即可断言。
/// </summary>
public class LocalAsrSessionCapTests
{
    private const int Max = 120 * AppConstants.TargetSampleRate;

    private static byte[] Silence(int sampleCount) => new byte[sampleCount * 4];

    private static LocalAsrSession MakeSession(out AsrPump pump)
    {
        pump = new AsrPump("VoiceTyper.Test.AsrPump");
        return new LocalAsrSession(pump, engineAccessor: () => null, llmCorrector: null, previewWindowSamples: 16_000);
    }

    /// <summary>引擎长时间未就绪时，_pendingAudio 也必须受单段上限约束：恰好喂满不触发，
    /// 再来一点点才触发一次 warning + OnSessionCapped，之后静默丢弃。</summary>
    [Fact]
    public void PendingAudio_RespectsSessionCap_AndTriggersOnce()
    {
        var session = MakeSession(out var pump);
        try
        {
            var warnings = 0;
            var capped = 0;
            session.OnWarning = _ => warnings++;
            session.OnSessionCapped = () => capped++;

            // 恰好喂满上限：这一次本身不触发（检查在下一次入口）。
            session.SendAudio(Silence(Max));
            Assert.Equal(0, capped);

            session.SendAudio(Silence(1_000));
            Assert.Equal(1, capped);
            Assert.Equal(1, warnings);

            session.SendAudio(Silence(1_000));
            Assert.Equal(1, capped);
            Assert.Equal(1, warnings);
        }
        finally { pump.Dispose(); }
    }

    /// <summary>引擎未就绪时首个 chunk 本身就超过上限：只攒到上限、裁掉多余部分，触发一次 cap。</summary>
    [Fact]
    public void PendingAudio_OversizedFirstChunk_IsClippedAndTriggersOnce()
    {
        var session = MakeSession(out var pump);
        try
        {
            var warnings = 0;
            var capped = 0;
            session.OnWarning = _ => warnings++;
            session.OnSessionCapped = () => capped++;

            session.SendAudio(Silence(Max + 5_000));
            Assert.Equal(1, capped);
            Assert.Equal(1, warnings);

            session.SendAudio(Silence(1_000));
            Assert.Equal(1, capped);
        }
        finally { pump.Dispose(); }
    }
}
