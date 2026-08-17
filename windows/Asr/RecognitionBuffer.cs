using System;
using System.Collections.Generic;

namespace VoiceTyper.Asr;

/// <summary>
/// 单次录音会话的音频累积与滑窗预览。C# 直译自
/// <c>macos/Sources/VoiceTyper/ASR/RecognitionBuffer.swift</c>（移植自服务端
/// <c>recognizer.SenseVoiceSession</c>），逻辑原样搬运，不做"改进"：
///
/// SenseVoice 本身非流式，预览靠"对已累积音频整段重跑"实现。用一条滚动窗口规避成本随
/// 录音时长平方增长：窗口左侧的音频只识别一次、固化进 <see cref="_committedText"/>，
/// 之后的预览只对窗口右侧（最近 <c>previewWindowSamples</c> 个采样点）重跑。
///
/// 每次新建、用完即弃；<c>_committedText</c>/<c>_committedN</c> 只应在 <see cref="AsrPump"/> 线程上
/// 访问；<see cref="Append"/> 允许从任意线程调用（内部用 <c>lock</c> 保护）。
/// </summary>
internal sealed class RecognitionBuffer
{
    /// <summary>窗口滚动时，在目标切点 ±100ms 内挑能量最低的 10ms 处下刀。</summary>
    private const int SeamSearchRadius = 1600;
    private const int SeamStep = 160;

    private readonly ISenseVoiceRecognizing _engine;
    private readonly int _previewWindowSamples;
    private readonly object _lock = new();
    private readonly List<float[]> _chunks = new();
    private int _n;
    private float[]? _joined;

    /// <summary>窗口左侧已固化的预览文本；只在 AsrPump 线程上读写。</summary>
    private string _committedText = "";
    private int _committedN;

    /// <param name="previewWindowSamples">
    /// 预览窗口（采样点，16kHz）。默认 15 秒，与服务端/macOS 一致；
    /// Windows 侧允许按本机实测 RTF 调小（见 windows/DESIGN.md §4.5 的自校准）。
    /// </param>
    public RecognitionBuffer(ISenseVoiceRecognizing engine, int previewWindowSamples = 15 * 16000)
    {
        _engine = engine;
        _previewWindowSamples = previewWindowSamples;
    }

    /// <summary>累计接收的样本数，供调用方做会话时长上限判断。可从任意线程调用。</summary>
    public int SampleCount
    {
        get { lock (_lock) return _n; }
    }

    /// <summary>累积音频。必须足够便宜——可能跑在音频采集的回调路径上。</summary>
    public void Append(float[] chunk)
    {
        if (chunk.Length == 0) return;
        lock (_lock)
        {
            _chunks.Add(chunk);
            _n += chunk.Length;
            _joined = null;
        }
    }

    /// <summary>返回全量预览文本 = 已固化前缀 + 当前窗口的识别结果。只应在 AsrPump 线程上调用。</summary>
    public string Preview()
    {
        var audio = AudioBuffer();
        if (audio.Length - _committedN > _previewWindowSamples)
        {
            Roll(audio);
        }
        var tail = audio.AsSpan(_committedN);
        return _committedText + _engine.Recognize(tail);
    }

    /// <summary>
    /// 松手后调用：对完整音频重新整段识别。刻意不复用 Preview 的拼接结果——窗口边界处的固化
    /// 文本可能有接缝瑕疵，Finalize 产出的是要上屏的最终文本，必须整段重跑。只应在 AsrPump 线程上调用。
    /// </summary>
    public string Finalize()
    {
        return _engine.Recognize(AudioBuffer());
    }

    /// <summary>把窗口前半段识别成文本固化，之后的预览不再重复识别这段音频。</summary>
    private void Roll(float[] audio)
    {
        int target = _committedN + _previewWindowSamples / 2;
        int cut = FindSeam(audio, target);
        var segment = audio.AsSpan(_committedN, cut - _committedN);
        _committedText += _engine.Recognize(segment);
        _committedN = cut;
    }

    /// <summary>在 target 附近找能量（RMS）最低的一小段作为切点，尽量避免把一个字切在正中间。</summary>
    private static int FindSeam(float[] audio, int target)
    {
        int lo = Math.Max(0, target - SeamSearchRadius);
        int hi = Math.Min(audio.Length, target + SeamSearchRadius);
        int span = (hi - lo) / SeamStep * SeamStep;
        if (span <= 0) return target;

        float bestEnergy = float.MaxValue;
        int bestOffset = 0;
        for (int offset = 0; offset < span; offset += SeamStep)
        {
            float sum = 0;
            for (int i = 0; i < SeamStep; i++)
            {
                float sample = audio[lo + offset + i];
                sum += sample * sample;
            }
            float energy = sum / SeamStep;
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                bestOffset = offset;
            }
        }
        return lo + bestOffset;
    }

    private float[] AudioBuffer()
    {
        lock (_lock)
        {
            if (_joined is not null) return _joined;
            if (_chunks.Count == 0) return Array.Empty<float>();

            var flat = new float[_n];
            int offset = 0;
            foreach (var chunk in _chunks)
            {
                Array.Copy(chunk, 0, flat, offset, chunk.Length);
                offset += chunk.Length;
            }
            _joined = flat;
            return flat;
        }
    }
}
