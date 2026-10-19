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
    private int _abortCalls;
    private int _resetAbortCalls;
    private volatile bool _aborted;

    /// <summary>非空时 <see cref="Recognize"/> 阻塞在这里（最多 5 秒），用来让一次预览保持"在飞"状态。</summary>
    public ManualResetEventSlim? Gate { get; set; }
    public int AbortCalls => Volatile.Read(ref _abortCalls);
    public int ResetAbortCalls => Volatile.Read(ref _resetAbortCalls);
    /// <summary>为 true 时像真实引擎一样遵守中止标志：<see cref="Abort"/> 之后、<see cref="ResetAbort"/> 之前的识别抛异常。</summary>
    public bool HonorAbort { get; init; }

    public int RecognizeCalls => Volatile.Read(ref _recognizeCalls);
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public int LanguageCalls => Volatile.Read(ref _languageCalls);
    public string Result { get; set; } = "";

    public string Recognize(ReadOnlySpan<float> samples)
    {
        Interlocked.Increment(ref _recognizeCalls);
        Gate?.Wait(TimeSpan.FromSeconds(5));
        if (HonorAbort && _aborted) throw new OperationCanceledException("识别已被中止");
        return Result;
    }

    public void Abort()
    {
        Interlocked.Increment(ref _abortCalls);
        _aborted = true;
    }

    public void ResetAbort()
    {
        Interlocked.Increment(ref _resetAbortCalls);
        _aborted = false;
    }

    public void SetLanguage(AsrLanguage language) => Interlocked.Increment(ref _languageCalls);

    public void Dispose() => Interlocked.Increment(ref _disposeCount);
}
