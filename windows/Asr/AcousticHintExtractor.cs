using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using VoiceTyper.Asr.Hotword;

namespace VoiceTyper.Asr;

/// <summary>统一对齐后的发射 token（HINT-1），供 <see cref="AcousticHintExtractor"/> 消费。</summary>
internal readonly record struct AlignedToken(int Id, int PeakFrame, bool ProtectedByHotword);

/// <summary>
/// 局部声学低置信度备选（HINT-5）。
/// <b>隐私红线</b>：不得写入主系统日志（<c>app.log</c>）或 <see cref="Core.DictationMetrics"/>。
/// </summary>
internal sealed record AcousticHint(string Surface, IReadOnlyList<string> Alternatives, double BestMargin);

/// <summary>带局部声学备选的识别结果（HINT-5）。</summary>
internal readonly record struct RecognitionResult(string Text, IReadOnlyList<AcousticHint> Hints);

/// <summary>
/// 局部低置信度声学备选提取器（HINT-1 – HINT-4）。C# 直译自
/// <c>macos/Sources/VoiceTyper/ASR/AcousticHintExtractor.swift</c>，两平台共用同一份
/// <c>asr_hints_cases.json</c> 共享夹具，同分排序完全确定。
/// </summary>
internal static class AcousticHintExtractor
{
    /// <summary>提取算法参数（HINT-4.5）；不进入 <c>config.yaml</c>。</summary>
    internal sealed record Parameters(
        double MarginThreshold = 1.15,
        int MaxHintsPerUtterance = 5,
        int MaxAltsPerHint = 3,
        int MaxMergedCjkLength = 3,
        double JointMarginMultiplier = 1.5,
        int FrameTopK = 4)
    {
        public static readonly Parameters Default = new();
    }

    /// <summary>词表静态元数据（HINT-2），引擎初始化后一次性预计算，与具体音频无关。</summary>
    internal sealed class TokenMetadata
    {
        private static readonly HashSet<string> PureFillers = new(StringComparer.Ordinal)
        {
            "呃", "嗯", "啊", "额", "哦", "噢", "唉", "哎",
        };

        private static readonly HashSet<string> ThirdPersonPronouns = new(StringComparer.Ordinal)
        {
            "他", "她", "它",
        };

        private static readonly HashSet<int> NumeralRunes = new("0123456789零〇一二两三四五六七八九十百千万亿".EnumerateRunes().Select(r => r.Value));

        public int Count { get; }
        public bool[] IsExcluded { get; }
        public bool[] HasLeadingSpace { get; }
        public string[] Stripped { get; }
        public bool[] IsSubstantive { get; }
        public bool[] IsCjkChar { get; }
        public bool[] IsAsciiWordPart { get; }
        public bool[] IsPureFiller { get; }
        public bool[] IsSingleAsciiLetter { get; }
        public bool[] IsPureAscii { get; }
        public bool[] IsThirdPersonPronoun { get; }
        public bool[] IsPureNumeral { get; }

        public TokenMetadata(IReadOnlyList<string> tokens)
        {
            int n = tokens.Count;
            Count = n;
            IsExcluded = new bool[n];
            HasLeadingSpace = new bool[n];
            Stripped = new string[n];
            IsSubstantive = new bool[n];
            IsCjkChar = new bool[n];
            IsAsciiWordPart = new bool[n];
            IsPureFiller = new bool[n];
            IsSingleAsciiLetter = new bool[n];
            IsPureAscii = new bool[n];
            IsThirdPersonPronoun = new bool[n];
            IsPureNumeral = new bool[n];

            for (int i = 0; i < n; i++)
            {
                var tok = tokens[i];
                IsExcluded[i] = TokenTrie.IsExcluded(i, tok);
                bool leading = tok.StartsWith("▁", StringComparison.Ordinal);
                HasLeadingSpace[i] = leading;
                string s = leading ? tok[1..] : tok;
                Stripped[i] = s;

                int scalarCount = 0;
                int firstScalar = 0;
                bool hasSub = false;
                bool allAscii = s.Length > 0;
                bool allAsciiAlnum = s.Length > 0;
                bool allNumeral = s.Length > 0;
                foreach (var rune in s.EnumerateRunes())
                {
                    int v = rune.Value;
                    if (scalarCount == 0) firstScalar = v;
                    scalarCount++;
                    if (v > 0x7F) allAscii = false;
                    if (!NumeralRunes.Contains(v)) allNumeral = false;
                    bool isAsciiAlnum = (v >= '0' && v <= '9') || (v >= 'A' && v <= 'Z') || (v >= 'a' && v <= 'z');
                    if (!isAsciiAlnum) allAsciiAlnum = false;
                    if (isAsciiAlnum
                        || (v >= 0x4E00 && v <= 0x9FFF)
                        || (v >= 0x3040 && v <= 0x30FF)
                        || (v >= 0xAC00 && v <= 0xD7A3))
                    {
                        hasSub = true;
                    }
                }
                IsSubstantive[i] = hasSub;
                IsPureAscii[i] = allAscii;
                IsAsciiWordPart[i] = allAsciiAlnum;
                IsPureNumeral[i] = allNumeral;
                if (scalarCount == 1)
                {
                    IsCjkChar[i] = firstScalar >= 0x4E00 && firstScalar <= 0x9FFF;
                    IsSingleAsciiLetter[i] = (firstScalar >= 'A' && firstScalar <= 'Z')
                        || (firstScalar >= 'a' && firstScalar <= 'z');
                }
                IsPureFiller[i] = PureFillers.Contains(s);
                IsThirdPersonPronoun[i] = ThirdPersonPronouns.Contains(s);
            }
        }
    }

    private const int ScanBlock = 256;

    /// <summary>
    /// 单路径 CTC Viterbi 强制对齐（HINT-1）：用于热词束搜索胜出路径。
    /// 数值异常（NaN / 路径不可达）时返回 null，调用方按安全阀回落空 hints。
    /// </summary>
    public static List<AlignedToken>? ForcedAlign(
        ReadOnlySpan<float> logits,
        int numFrames,
        int vocabSize,
        IReadOnlyList<int> tokens,
        IReadOnlyList<string> tokenStrings,
        ContextGraph graph)
    {
        if (numFrames <= 0 || vocabSize <= 0) return new List<AlignedToken>();

        int tokenStart = 0;
        while (tokenStart < tokens.Count)
        {
            int id = tokens[tokenStart];
            if (id >= 0 && id < tokenStrings.Count && TokenTrie.IsSpecial(tokenStrings[id]))
            {
                tokenStart++;
            }
            else break;
        }
        int L = tokens.Count - tokenStart;
        if (L <= 0) return new List<AlignedToken>();

        var contentTokens = new int[L];
        for (int i = 0; i < L; i++) contentTokens[i] = tokens[tokenStart + i];

        int startFrame = 0;
        while (startFrame < numFrames)
        {
            var row = logits.Slice(startFrame * vocabSize, vocabSize);
            int argmax = CtcDecoder.RowArgmax(row);
            if (!float.IsFinite(row[argmax])) return null;
            if (argmax >= 0 && argmax < tokenStrings.Count && TokenTrie.IsSpecial(tokenStrings[argmax]))
            {
                startFrame++;
            }
            else break;
        }
        int T = numFrames - startFrame;
        if (T <= 0) return null;

        int S = 2 * L + 1;
        var ext = new int[S];
        for (int i = 0; i < L; i++)
        {
            int id = contentTokens[i];
            if (id < 0 || id >= vocabSize) return null;
            ext[2 * i + 1] = id;
        }

        var prevDp = new double[S];
        var currDp = new double[S];
        Array.Fill(prevDp, double.NegativeInfinity);
        var backptr = new int[T * S];
        Array.Fill(backptr, -1);

        var row0 = logits.Slice(startFrame * vocabSize, vocabSize);
        double b0 = row0[0];
        double y0 = row0[ext[1]];
        if (!double.IsFinite(b0) || !double.IsFinite(y0)) return null;
        prevDp[0] = b0;
        prevDp[1] = y0;

        for (int t = 1; t < T; t++)
        {
            var row = logits.Slice((startFrame + t) * vocabSize, vocabSize);
            double blankScore = row[0];
            if (!double.IsFinite(blankScore)) return null;
            for (int u = 0; u < S; u++)
            {
                double emit = (u & 1) == 0 ? blankScore : row[ext[u]];
                if (!double.IsFinite(emit)) return null;
                double bestVal = prevDp[u];
                int bestPrev = u;
                if (u >= 1 && prevDp[u - 1] > bestVal)
                {
                    bestVal = prevDp[u - 1];
                    bestPrev = u - 1;
                }
                if (u >= 2 && (u & 1) == 1 && ext[u] != ext[u - 2] && prevDp[u - 2] > bestVal)
                {
                    bestVal = prevDp[u - 2];
                    bestPrev = u - 2;
                }
                currDp[u] = bestVal + emit;
                backptr[t * S + u] = bestPrev;
            }
            (prevDp, currDp) = (currDp, prevDp);
        }

        int endState = S - 2;
        if (prevDp[S - 1] > prevDp[endState]) endState = S - 1;
        if (!double.IsFinite(prevDp[endState])) return null;

        var bestFrameForToken = new int[L];
        var bestAdvForToken = new float[L];
        Array.Fill(bestFrameForToken, -1);
        Array.Fill(bestAdvForToken, float.NegativeInfinity);

        int state = endState;
        for (int t = T - 1; t >= 0; t--)
        {
            if ((state & 1) == 1)
            {
                int tokenIdx = state / 2;
                int absFrame = startFrame + t;
                int baseOffset = absFrame * vocabSize;
                float adv = logits[baseOffset + ext[state]] - logits[baseOffset];
                if (adv >= bestAdvForToken[tokenIdx])
                {
                    bestAdvForToken[tokenIdx] = adv;
                    bestFrameForToken[tokenIdx] = absFrame;
                }
            }
            if (t > 0)
            {
                int prev = backptr[t * S + state];
                if (prev < 0) return null;
                state = prev;
            }
        }

        var protectedMask = graph.ProtectedMask(contentTokens);
        var aligned = new List<AlignedToken>(L);
        for (int i = 0; i < L; i++)
        {
            if (bestFrameForToken[i] < 0) return null;
            aligned.Add(new AlignedToken(contentTokens[i], bestFrameForToken[i], protectedMask[i]));
        }
        return aligned;
    }

    /// <summary>零拷贝主入口：在 <paramref name="logits"/> 底层 Span 上提取最多 5 处局部低置信度备选（HINT-3 / HINT-4）。</summary>
    public static IReadOnlyList<AcousticHint> Extract(
        ReadOnlySpan<float> logits,
        int numFrames,
        int vocabSize,
        IReadOnlyList<AlignedToken> aligned,
        string fullText,
        TokenMetadata metadata,
        Parameters? parameters = null)
    {
        parameters ??= Parameters.Default;
        int n = aligned.Count;
        if (n == 0 || numFrames <= 0 || vocabSize <= 0 || vocabSize != metadata.Count || string.IsNullOrEmpty(fullText))
        {
            return Array.Empty<AcousticHint>();
        }

        var frameAlts = new List<(string Text, double Delta)>[n];
        for (int i = 0; i < n; i++) frameAlts[i] = new List<(string, double)>();

        for (int i = 0; i < n; i++)
        {
            var tok = aligned[i];
            int y = tok.Id;
            if (tok.ProtectedByHotword || y < 0 || y >= vocabSize
                || tok.PeakFrame < 0 || tok.PeakFrame >= numFrames
                || metadata.IsExcluded[y] || !metadata.IsSubstantive[y] || metadata.IsPureFiller[y])
            {
                continue;
            }

            var row = logits.Slice(tok.PeakFrame * vocabSize, vocabSize);
            float sy = row[y];
            if (!float.IsFinite(sy)) return Array.Empty<AcousticHint>();

            double syDouble = sy;
            float cutoff = MathF.BitDecrement((float)(syDouble - parameters.MarginThreshold));
            string yStripped = metadata.Stripped[y];
            bool yPureAscii = metadata.IsPureAscii[y];
            string yLower = yPureAscii ? yStripped.ToLowerInvariant() : yStripped;
            bool yIsCjk = metadata.IsCjkChar[y];
            bool yIsPronoun = metadata.IsThirdPersonPronoun[y];
            bool yIsNumeral = metadata.IsPureNumeral[y];

            var rawCands = new List<(double Delta, int Id, string Text)>(parameters.FrameTopK + 2);
            for (int blockStart = 0; blockStart < vocabSize; blockStart += ScanBlock)
            {
                int blockLen = Math.Min(ScanBlock, vocabSize - blockStart);
                var slice = row.Slice(blockStart, blockLen);
                float blockMax = BlockMax(slice);
                if (float.IsNaN(blockMax)) return Array.Empty<AcousticHint>();
                if (blockMax < cutoff) continue;

                int blockEnd = blockStart + blockLen;
                for (int k = blockStart; k < blockEnd; k++)
                {
                    float v = row[k];
                    if (float.IsNaN(v)) return Array.Empty<AcousticHint>();
                    if (k == y || metadata.IsExcluded[k] || !metadata.IsSubstantive[k]) continue;
                    double delta = syDouble - v;
                    if (delta > parameters.MarginThreshold) continue;
                    string kStripped = metadata.Stripped[k];
                    if (string.Equals(kStripped, yStripped, StringComparison.Ordinal)) continue;
                    if (yPureAscii && metadata.IsPureAscii[k]
                        && string.Equals(kStripped.ToLowerInvariant(), yLower, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (yIsCjk && metadata.IsSingleAsciiLetter[k]) continue;
                    // 同音无声学区分度等价类过滤：「他/她/它」互换、数字写法互换。
                    if (yIsPronoun && metadata.IsThirdPersonPronoun[k]) continue;
                    if (yIsNumeral && metadata.IsPureNumeral[k]) continue;
                    rawCands.Add((delta, k, kStripped));
                }
            }

            if (rawCands.Count == 0) continue;
            rawCands.Sort(static (a, b) =>
            {
                int cmp = a.Delta.CompareTo(b.Delta);
                return cmp != 0 ? cmp : a.Id.CompareTo(b.Id);
            });

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int limit = Math.Min(parameters.FrameTopK, rawCands.Count);
            for (int idx = 0; idx < limit; idx++)
            {
                var item = rawCands[idx];
                string norm = metadata.IsPureAscii[item.Id] ? item.Text.ToLowerInvariant() : item.Text;
                if (seen.Add(norm))
                {
                    frameAlts[i].Add((item.Text, item.Delta));
                    if (frameAlts[i].Count >= parameters.MaxAltsPerHint) break;
                }
            }
        }

        // 步骤 1：词/段分组（HINT-4）。
        var candidates = new List<Candidate>();
        var singleCjkIndices = new List<int>();
        int pos = 0;
        while (pos < n)
        {
            int yi = aligned[pos].Id;
            if (metadata.IsAsciiWordPart[yi])
            {
                int j = pos + 1;
                while (j < n && metadata.IsAsciiWordPart[aligned[j].Id] && !metadata.HasLeadingSpace[aligned[j].Id])
                {
                    j++;
                }
                bool hasProtected = false;
                for (int k = pos; k < j; k++)
                {
                    if (aligned[k].ProtectedByHotword) { hasProtected = true; break; }
                }
                if (!hasProtected)
                {
                    var lowPositions = new List<int>();
                    for (int k = pos; k < j; k++)
                    {
                        if (frameAlts[k].Count > 0) lowPositions.Add(k);
                    }
                    if (lowPositions.Count > 0)
                    {
                        lowPositions.Sort((a, b) =>
                        {
                            int cmp = frameAlts[a][0].Delta.CompareTo(frameAlts[b][0].Delta);
                            return cmp != 0 ? cmp : a.CompareTo(b);
                        });
                        var chosen = new HashSet<int>(lowPositions.Take(2));
                        var baseWordSb = new StringBuilder();
                        for (int k = pos; k < j; k++) baseWordSb.Append(metadata.Stripped[aligned[k].Id]);
                        string baseWord = baseWordSb.ToString();
                        string baseLower = baseWord.ToLowerInvariant();

                        var combos = new List<(string Word, double Delta)> { ("", 0.0) };
                        for (int k = pos; k < j; k++)
                        {
                            string basePart = metadata.Stripped[aligned[k].Id];
                            var opts = new List<(string Text, double Delta)> { (basePart, 0.0) };
                            if (chosen.Contains(k)) opts.AddRange(frameAlts[k]);
                            var next = new List<(string Word, double Delta)>(combos.Count * opts.Count);
                            foreach (var c in combos)
                            {
                                foreach (var o in opts) next.Add((c.Word + o.Text, c.Delta + o.Delta));
                            }
                            combos = next;
                        }

                        var bestByNorm = new Dictionary<string, (string Word, double Delta)>(StringComparer.Ordinal);
                        foreach (var combo in combos)
                        {
                            string key = combo.Word.ToLowerInvariant();
                            if (string.Equals(key, baseLower, StringComparison.Ordinal)) continue;
                            if (IsMalformedAsciiAlt(key, baseLower)) continue;
                            if (!bestByNorm.TryGetValue(key, out var prev)
                                || combo.Delta < prev.Delta
                                || (combo.Delta == prev.Delta && string.CompareOrdinal(combo.Word, prev.Word) < 0))
                            {
                                bestByNorm[key] = combo;
                            }
                        }
                        var ranked = bestByNorm.Values
                            .OrderBy(x => x.Delta)
                            .ThenBy(x => x.Word, StringComparer.Ordinal)
                            .Take(parameters.MaxAltsPerHint)
                            .ToList();
                        if (ranked.Count > 0)
                        {
                            candidates.Add(new Candidate(pos, baseWord, ranked.Select(x => x.Word).ToList(), ranked[0].Delta));
                        }
                    }
                }
                pos = j;
            }
            else if (metadata.IsCjkChar[yi] && !aligned[pos].ProtectedByHotword && frameAlts[pos].Count > 0)
            {
                int j = pos + 1;
                while (j < n && (j - pos) < parameters.MaxMergedCjkLength
                    && metadata.IsCjkChar[aligned[j].Id]
                    && !aligned[j].ProtectedByHotword
                    && frameAlts[j].Count > 0)
                {
                    j++;
                }
                if (j - pos >= 2)
                {
                    var baseSb = new StringBuilder();
                    for (int k = pos; k < j; k++) baseSb.Append(metadata.Stripped[aligned[k].Id]);
                    string baseStr = baseSb.ToString();
                    double jointLimit = parameters.JointMarginMultiplier * parameters.MarginThreshold + 1e-9;

                    var combos = new List<(string Word, double Delta)> { ("", 0.0) };
                    for (int k = pos; k < j; k++)
                    {
                        string basePart = metadata.Stripped[aligned[k].Id];
                        var opts = new List<(string Text, double Delta)> { (basePart, 0.0) };
                        for (int a = 0; a < Math.Min(2, frameAlts[k].Count); a++) opts.Add(frameAlts[k][a]);
                        var next = new List<(string Word, double Delta)>();
                        foreach (var c in combos)
                        {
                            foreach (var o in opts)
                            {
                                double sum = c.Delta + o.Delta;
                                if (sum <= jointLimit) next.Add((c.Word + o.Text, sum));
                            }
                        }
                        combos = next;
                    }
                    var bestByStr = new Dictionary<string, double>(StringComparer.Ordinal);
                    foreach (var combo in combos)
                    {
                        if (string.Equals(combo.Word, baseStr, StringComparison.Ordinal)) continue;
                        if (!bestByStr.TryGetValue(combo.Word, out var prev) || combo.Delta < prev)
                        {
                            bestByStr[combo.Word] = combo.Delta;
                        }
                    }
                    var ranked = bestByStr
                        .OrderBy(kv => kv.Value)
                        .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                        .Take(parameters.MaxAltsPerHint)
                        .ToList();
                    if (ranked.Count > 0)
                    {
                        candidates.Add(new Candidate(pos, baseStr, ranked.Select(x => x.Key).ToList(), ranked[0].Value));
                    }
                }
                else
                {
                    singleCjkIndices.Add(pos);
                }
                pos = j;
            }
            else
            {
                if (!aligned[pos].ProtectedByHotword && frameAlts[pos].Count > 0)
                {
                    var alts = frameAlts[pos].Take(parameters.MaxAltsPerHint).Select(x => x.Text).ToList();
                    candidates.Add(new Candidate(pos, metadata.Stripped[yi], alts, frameAlts[pos][0].Delta));
                }
                pos++;
            }
        }

        // 步骤 2：孤立单汉字上下文锚点扩展（HINT-4）。
        singleCjkIndices.Sort((a, b) =>
        {
            int cmp = frameAlts[a][0].Delta.CompareTo(frameAlts[b][0].Delta);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });
        var claimedAnchors = new HashSet<int>();
        foreach (int idx in singleCjkIndices)
        {
            string baseChar = metadata.Stripped[aligned[idx].Id];
            var alts = frameAlts[idx].Take(parameters.MaxAltsPerHint).Select(x => x.Text).ToList();
            double margin = frameAlts[idx][0].Delta;
            bool canRight = idx + 1 < n && metadata.IsCjkChar[aligned[idx + 1].Id];
            bool canLeft = idx - 1 >= 0 && metadata.IsCjkChar[aligned[idx - 1].Id];

            bool useLeft = false;
            bool useRight = false;
            if (canRight && !claimedAnchors.Contains(idx + 1))
            {
                useRight = true;
                claimedAnchors.Add(idx + 1);
            }
            else if (canLeft && !claimedAnchors.Contains(idx - 1))
            {
                useLeft = true;
                claimedAnchors.Add(idx - 1);
            }
            else if (canRight)
            {
                useRight = true;
            }
            else if (canLeft)
            {
                useLeft = true;
            }

            string BuildSurface(bool left, bool right, string center)
            {
                string lch = left ? metadata.Stripped[aligned[idx - 1].Id] : "";
                string rch = right ? metadata.Stripped[aligned[idx + 1].Id] : "";
                return lch + center + rch;
            }

            string surface = BuildSurface(useLeft, useRight, baseChar);
            if (CountOccurrences(surface, fullText) >= 2)
            {
                if (useRight && canLeft)
                {
                    useLeft = true;
                    surface = BuildSurface(useLeft, useRight, baseChar);
                }
                else if (useLeft && canRight)
                {
                    useRight = true;
                    surface = BuildSurface(useLeft, useRight, baseChar);
                }
            }

            int startIdx = useLeft ? idx - 1 : idx;
            var expandedAlts = alts.Select(alt => BuildSurface(useLeft, useRight, alt)).ToList();
            candidates.Add(new Candidate(startIdx, surface, expandedAlts, margin));
        }

        // 步骤 3：去重、按 bestMargin 截断前 maxHintsPerUtterance 条、按原文顺序重排（HINT-4）。
        var dedupBySurface = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        foreach (var cand in candidates)
        {
            if (string.IsNullOrEmpty(cand.Surface) || cand.Alternatives.Count == 0) continue;
            if (!dedupBySurface.TryGetValue(cand.Surface, out var prev)
                || cand.BestMargin < prev.BestMargin
                || (cand.BestMargin == prev.BestMargin && cand.StartIndex < prev.StartIndex))
            {
                dedupBySurface[cand.Surface] = cand;
            }
        }

        var winners = dedupBySurface.Values
            .OrderBy(c => c.BestMargin)
            .ThenBy(c => c.StartIndex)
            .Take(parameters.MaxHintsPerUtterance)
            .OrderBy(c => c.StartIndex)
            .Select(c => new AcousticHint(c.Surface, c.Alternatives, c.BestMargin))
            .ToList();

        return winners;
    }

    private static float BlockMax(ReadOnlySpan<float> slice)
    {
        float max = float.NegativeInfinity;
        int i = 0;
        if (Vector.IsHardwareAccelerated && slice.Length >= Vector<float>.Count)
        {
            int vecLen = Vector<float>.Count;
            var vmax = new Vector<float>(float.NegativeInfinity);
            for (; i <= slice.Length - vecLen; i += vecLen)
            {
                var v = new Vector<float>(slice.Slice(i, vecLen));
                // Vector.Max 对 NaN 行为随平台而异，用 Equals 检测 NaN 立即返回。
                if (!Vector.EqualsAll(v, v)) return float.NaN;
                vmax = Vector.Max(vmax, v);
            }
            for (int lane = 0; lane < vecLen; lane++)
            {
                if (vmax[lane] > max) max = vmax[lane];
            }
        }
        for (; i < slice.Length; i++)
        {
            float v = slice[i];
            if (float.IsNaN(v)) return float.NaN;
            if (v > max) max = v;
        }
        return max;
    }

    /// <summary>过滤英文 BPE 子词笛卡尔积产生的畸形叠词（如 antigravityvity）。</summary>
    private static bool IsMalformedAsciiAlt(string candidateLower, string baseLower)
    {
        int cLen = candidateLower.Length;
        int bLen = baseLower.Length;
        if (cLen > bLen + 2) return true;
        if (bLen >= 3 && cLen < Math.Max(2, bLen - 2)) return true;
        for (int i = 2; i < cLen; i++)
        {
            if (candidateLower[i] == candidateLower[i - 1] && candidateLower[i] == candidateLower[i - 2])
                return true;
        }
        for (int subLen = 3; subLen <= 5; subLen++)
        {
            if (cLen >= subLen * 2)
            {
                int tailStart = cLen - subLen;
                int prevStart = tailStart - subLen;
                if (candidateLower.AsSpan(prevStart, subLen).SequenceEqual(candidateLower.AsSpan(tailStart, subLen)))
                    return true;
            }
        }
        return false;
    }

    private static int CountOccurrences(string needle, string haystack)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private sealed record Candidate(int StartIndex, string Surface, IReadOnlyList<string> Alternatives, double BestMargin);
}
