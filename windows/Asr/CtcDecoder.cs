using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceTyper.Asr;

/// <summary>
/// CTC 贪心解码：逐帧 argmax → 合并连续重复 → 去 blank(id=0) → 拼 token → 文本后处理。
/// 对齐 <c>SenseVoiceRecognizer._ctc_greedy_decode</c>。C# 直译自
/// <c>macos/Sources/VoiceTyper/ASR/CTCDecoder.swift</c>。
/// </summary>
internal static class CtcDecoder
{
    /// <param name="logits"><c>[numFrames * vocabSize]</c> 行主序展平的 CTC logits。</param>
    /// <param name="tokens">词表，下标即 token id。</param>
    public static string Decode(ReadOnlySpan<float> logits, int numFrames, int vocabSize, IReadOnlyList<string> tokens)
    {
        if (numFrames <= 0 || vocabSize <= 0) return "";

        var ids = new int[numFrames];
        for (int t = 0; t < numFrames; t++)
        {
            var row = logits.Slice(t * vocabSize, vocabSize);
            int maxIdx = 0;
            float maxVal = row[0];
            for (int v = 1; v < vocabSize; v++)
            {
                if (row[v] > maxVal)
                {
                    maxVal = row[v];
                    maxIdx = v;
                }
            }
            ids[t] = maxIdx;
        }

        // torch.unique_consecutive 的等价实现：仅保留与前一帧不同的位置。
        var collapsed = new List<int>(ids.Length);
        int prev = -1;
        foreach (var id in ids)
        {
            if (id != prev) collapsed.Add(id);
            prev = id;
        }

        var raw = new StringBuilder();
        foreach (var id in collapsed)
        {
            if (id == 0) continue; // blank_id = 0
            if (id >= 0 && id < tokens.Count) raw.Append(tokens[id]);
        }
        return TextPostprocessor.Process(raw.ToString());
    }
}
