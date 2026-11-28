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

        var ids = GreedyTokens(logits, numFrames, vocabSize);
        var raw = new StringBuilder();
        foreach (var id in ids)
        {
            if (id >= 0 && id < tokens.Count) raw.Append(tokens[id]);
        }
        return TextPostprocessor.Process(raw.ToString());
    }

    /// <summary>束搜索回退时可复用非 blank 峰值，跳过 blank 占优的帧。</summary>
    public static int[] GreedyTokens(ReadOnlySpan<float> logits, int numFrames, int vocabSize,
        ReadOnlySpan<float> nonBlankMaxima = default)
    {
        var ids = new List<int>(numFrames);
        int previous = -1;
        for (int t = 0; t < numFrames; t++)
        {
            var row = logits.Slice(t * vocabSize, vocabSize);
            int id = nonBlankMaxima.IsEmpty ? RowArgmax(row) : RowArgmax(row, nonBlankMaxima[t]);
            // 先合并连续重复，再丢弃 blank。blank 仍会将两个相同 token 隔开。
            if (id != previous && id != 0) ids.Add(id);
            previous = id;
        }
        return ids.ToArray();
    }

    /// <summary>
    /// 贪心解码并同时记录每个非特殊 token 的非 blank 峰值核心帧（HINT-1）。
    /// 开头的 <c>&lt;|…|&gt;</c> 特殊标签仍保留在 <c>Ids</c>（供 <see cref="TextPostprocessor"/> 走统一路径），
    /// 但不进入 <c>Aligned</c>。
    /// </summary>
    public static (int[] Ids, List<AlignedToken> Aligned) GreedyAlignedTokens(
        ReadOnlySpan<float> logits,
        int numFrames,
        int vocabSize,
        IReadOnlyList<string> tokens,
        ReadOnlySpan<float> nonBlankMaxima = default)
    {
        if (numFrames <= 0 || vocabSize <= 0) return (Array.Empty<int>(), new List<AlignedToken>());

        var ids = new List<int>(numFrames);
        var aligned = new List<AlignedToken>(numFrames);

        void FlushRun(int id, int start, int end, ReadOnlySpan<float> data)
        {
            ids.Add(id);
            if (aligned.Count == 0 && id >= 0 && id < tokens.Count && Hotword.TokenTrie.IsSpecial(tokens[id]))
            {
                return;
            }
            int bestFrame = start;
            float bestAdv = float.NegativeInfinity;
            for (int f = start; f < end; f++)
            {
                int baseOffset = f * vocabSize;
                float adv = data[baseOffset + id] - data[baseOffset];
                if (adv > bestAdv)
                {
                    bestAdv = adv;
                    bestFrame = f;
                }
            }
            aligned.Add(new AlignedToken(id, bestFrame, ProtectedByHotword: false));
        }

        int previous = -1;
        int runStart = 0;
        for (int t = 0; t < numFrames; t++)
        {
            var row = logits.Slice(t * vocabSize, vocabSize);
            int id = nonBlankMaxima.IsEmpty ? RowArgmax(row) : RowArgmax(row, nonBlankMaxima[t]);
            if (id != previous)
            {
                if (previous > 0) FlushRun(previous, runStart, t, logits);
                previous = id;
                runStart = t;
            }
        }
        if (previous > 0) FlushRun(previous, runStart, numFrames, logits);
        return (ids.ToArray(), aligned);
    }

    private static int RowArgmax(ReadOnlySpan<float> row, float maxNonBlank)
    {
        if (!float.IsFinite(maxNonBlank) || !float.IsFinite(row[0])) return RowArgmax(row);
        // 分数相同取 id 较小者，与普通贪心的严格大于比较一致。
        return row[0] >= maxNonBlank ? 0 : row.IndexOf(maxNonBlank);
    }

    internal static int RowArgmax(ReadOnlySpan<float> row)
    {
        int maxIndex = 0;
        float maxValue = row[0];
        for (int i = 1; i < row.Length; i++)
        {
            if (row[i] > maxValue)
            {
                maxValue = row[i];
                maxIndex = i;
            }
        }
        return maxIndex;
    }
}
