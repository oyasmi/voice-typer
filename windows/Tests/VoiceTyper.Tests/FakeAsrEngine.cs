using System;
using System.Threading;
using VoiceTyper.Asr;
using VoiceTyper.Core;

namespace VoiceTyper.Tests;

/// <summary>生命周期 / 会话测试用的假引擎：只记录调用次数，不加载 ONNX。</summary>
internal sealed class FakeAsrEngine : IAsrEngine
{
    private int _recognizeCalls;
    private int _disposeCount;
    private int _languageCalls;

    public int RecognizeCalls => Volatile.Read(ref _recognizeCalls);
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public int LanguageCalls => Volatile.Read(ref _languageCalls);
    public string Result { get; set; } = "";

    public string Recognize(ReadOnlySpan<float> samples)
    {
        Interlocked.Increment(ref _recognizeCalls);
        return Result;
    }

    public void SetLanguage(AsrLanguage language) => Interlocked.Increment(ref _languageCalls);

    public void Dispose() => Interlocked.Increment(ref _disposeCount);
}
