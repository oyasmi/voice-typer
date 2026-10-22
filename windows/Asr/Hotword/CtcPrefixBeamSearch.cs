using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using VoiceTyper.Core;

namespace VoiceTyper.Asr.Hotword;

/// <summary>一次解码的统计（VOC-10），只含数字与枚举，可进入度量日志。</summary>
internal readonly record struct DecodeStats(DecodeStats.DecodeMode Mode, int HotwordHits, long DecodeTicks)
{
    internal enum DecodeMode { Greedy, Beam }

    public static readonly DecodeStats Greedy = new(DecodeMode.Greedy, 0, 0);
}

/// <summary>
/// 带热词偏置的 CTC 前缀束搜索（VOC-5）。C# 直译自 <c>macos/Sources/VoiceTyper/ASR/Hotword/CTCPrefixBeamSearch.swift</c>，
/// 两边读同一份 <c>ctc_hotword_cases.json</c>。
///
/// 不做 log-softmax：每个假设每帧恰好消费一个 token 或 blank，对整帧减同一个常数不改变任何假设之间的排名，
/// 因此只减去帧最大值（<c>ℓ_t[k] = x_t[k] − max_k x_t[k]</c>）。偏置 b 与门限 G 都是同一帧内的分差。累加器用 double。
///
/// 迭代顺序（假设按排名、候选按 token id 升序、新前缀按首次出现）全部固定，不依赖哈希表的遍历顺序。
/// 同一平台上结果可复现；跨平台 exp/log 的末位差异只影响分数完全相等的极端情况，共享夹具的分差都远大于它。
/// </summary>
internal static class CtcPrefixBeamSearch
{
    internal sealed record Parameters(double PerTokenBias, double AcousticGate, int BeamWidth = 8, int TopK = 10, int MaxScoredDepth = 4)
    {
        /// <summary>三档初值（VOC-6）。<b>全部是估算</b>，需用评估工具和真实录音校准，见 macOS <c>DESIGN.md</c> §11.7。</summary>
        public static Parameters? Preset(VocabularyBias bias) => bias switch
        {
            VocabularyBias.Light => new Parameters(1.0, 4.0),
            VocabularyBias.Standard => new Parameters(1.5, 5.0),
            VocabularyBias.Strong => new Parameters(2.5, 7.0),
            _ => null,
        };
    }

    /// <summary>词表里哪些 token 可以出现在候选里。与具体热词无关，一个引擎算一次。</summary>
    internal sealed class TokenClasses
    {
        /// <summary>普通 token：不是 blank / <c>&lt;s&gt;</c> / <c>&lt;/s&gt;</c>，也不是 <c>&lt;|…|&gt;</c>。</summary>
        public bool[] IsRegular { get; }
        /// <summary><c>&lt;|…|&gt;</c> 特殊 token（开头的语言 / 情感 / 事件 / ITN 标签）。</summary>
        public bool[] IsSpecial { get; }

        public TokenClasses(IReadOnlyList<string> tokens)
        {
            IsRegular = new bool[tokens.Count];
            IsSpecial = new bool[tokens.Count];
            for (int i = 0; i < tokens.Count; i++)
            {
                IsSpecial[i] = TokenTrie.IsSpecial(tokens[i]);
                IsRegular[i] = !TokenTrie.IsExcluded(i, tokens[i]);
            }
        }
    }

    internal sealed class DecodeException : Exception
    {
        public DecodeException(string message) : base(message) { }
    }

    internal sealed record Result(int[] Tokens, int HotwordHits);

    private const int ScanBlock = 256;

    /// <param name="logits"><c>[numFrames * vocabSize]</c> 行主序展平数据（与 <see cref="CtcDecoder"/> 相同的零拷贝入口）。</param>
    public static Result Decode(ReadOnlySpan<float> logits, int numFrames, int vocabSize, ContextGraph graph,
        TokenClasses classes, Parameters parameters)
    {
        var search = new Search(vocabSize, graph, classes, parameters);
        int frame = 0;

        // 开头的标签帧：只要 argmax 是 <|…|> 就按贪心取值、不展开，标签 token 作为初始前缀。
        int prefix = 0;
        int lastTag = -1;
        while (frame < numFrames)
        {
            int argmax = RowArgmax(logits.Slice(frame * vocabSize, vocabSize));
            if (!classes.IsSpecial[argmax]) break;
            if (argmax != lastTag)
            {
                prefix = search.Child(prefix, argmax);
                lastTag = argmax;
            }
            frame++;
        }
        var beam = new List<Hypothesis>
        {
            lastTag >= 0 ? new Hypothesis(prefix, double.NegativeInfinity, 0) : new Hypothesis(prefix, 0, double.NegativeInfinity),
        };

        for (; frame < numFrames; frame++)
        {
            beam = search.Advance(beam, logits.Slice(frame * vocabSize, vocabSize));
        }
        return search.Best(beam);
    }

    private readonly record struct Hypothesis(int Prefix, double Pb, double Pnb)
    {
        public double Total => LogAddExp(Pb, Pnb);
    }

    /// <summary>前缀以「节点表 + (父节点, token) → 子节点」表示，扩展时不复制 token 数组。</summary>
    private sealed class Search
    {
        private readonly int _vocabSize;
        private readonly ContextGraph _graph;
        private readonly TokenClasses _classes;
        private readonly Parameters _parameters;

        private readonly List<int> _parent = new() { -1 };
        private readonly List<int> _token = new() { -1 };
        private readonly List<ContextGraph.State> _state = new() { ContextGraph.Root };
        private readonly Dictionary<long, int> _childIndex = new();

        // 每帧复用的缓冲。
        private readonly Dictionary<int, int> _slotByPrefix = new();
        private readonly List<Hypothesis> _slots = new();
        private readonly List<int> _frameCandidates = new();
        private readonly List<int> _hypothesisCandidates = new();
        private readonly List<(int Token, float Score)> _topCandidates = new();

        public Search(int vocabSize, ContextGraph graph, TokenClasses classes, Parameters parameters)
        {
            _vocabSize = vocabSize;
            _graph = graph;
            _classes = classes;
            _parameters = parameters;
        }

        public int Child(int prefix, int k)
        {
            long key = (long)prefix * _vocabSize + k;
            if (_childIndex.TryGetValue(key, out var existing)) return existing;
            int index = _parent.Count;
            _parent.Add(prefix);
            _token.Add(k);
            _state.Add(_graph.Step(_state[prefix], k));
            _childIndex[key] = index;
            return index;
        }

        private double Rank(Hypothesis hypothesis) => hypothesis.Total + _graph.Bias(_state[hypothesis.Prefix]);

        private bool Admits(ReadOnlySpan<float> row, int k, double peak) =>
            _classes.IsRegular[k] && (double)row[k] - peak >= -_parameters.AcousticGate;

        public List<Hypothesis> Advance(List<Hypothesis> beam, ReadOnlySpan<float> row)
        {
            double gate = _parameters.AcousticGate;
            float blankScore = row[0];
            float maxNonBlank = RowMax(row[1..]);
            float frameMax = Math.Max(blankScore, maxNonBlank);

            // 跳帧：没有任何非 blank token 落在门限内，所有假设只做 blank 扩展。与候选集规则完全一致，不是近似。
            if ((double)blankScore - maxNonBlank > gate)
            {
                var skipped = new List<Hypothesis>(beam.Count);
                foreach (var h in beam) skipped.Add(new Hypothesis(h.Prefix, h.Total, double.NegativeInfinity));
                return skipped;
            }

            double peak = frameMax;
            CollectFrameCandidates(row, peak);

            _slotByPrefix.Clear();
            _slots.Clear();
            double blankDelta = blankScore - peak;

            foreach (var hypothesis in beam)
            {
                double total = hypothesis.Total;
                Accumulate(hypothesis.Prefix, total + blankDelta, double.NegativeInfinity);

                // 候选 = 本帧公共候选 ∪ 正在匹配的热词的下一个 token ∪ 前缀末 token（停留），都受门限约束。
                _hypothesisCandidates.Clear();
                _hypothesisCandidates.AddRange(_frameCandidates);
                var graphState = _state[hypothesis.Prefix];
                // 停在根上的假设，「下一个 token」就是全部热词首 token，本帧公共候选里已经有了。
                if (graphState.Node != ContextGraph.Root.Node)
                {
                    foreach (var next in _graph.ChildrenOf(graphState))
                    {
                        if (Admits(row, next, peak)) _hypothesisCandidates.Add(next);
                    }
                }
                int last = _token[hypothesis.Prefix];
                if (last >= 0 && Admits(row, last, peak)) _hypothesisCandidates.Add(last);
                _hypothesisCandidates.Sort();

                int previous = -1;
                foreach (var k in _hypothesisCandidates)
                {
                    if (k == previous) continue;
                    previous = k;
                    double delta = row[k] - peak;
                    if (k == last)
                    {
                        // 停留在同一个 token；重复 token 之间必须隔 blank 才算新发射。
                        Accumulate(hypothesis.Prefix, double.NegativeInfinity, hypothesis.Pnb + delta);
                        Accumulate(Child(hypothesis.Prefix, k), double.NegativeInfinity, hypothesis.Pb + delta);
                    }
                    else
                    {
                        Accumulate(Child(hypothesis.Prefix, k), double.NegativeInfinity, total + delta);
                    }
                }
            }

            var ranked = new List<(double Rank, Hypothesis Hypothesis)>(_slots.Count);
            foreach (var slot in _slots)
            {
                if (slot.Total > double.NegativeInfinity) ranked.Add((Rank(slot), slot));
            }
            if (ranked.Count == 0) throw new DecodeException("某一帧之后束为空（logits 含 NaN 等）");
            ranked.Sort((a, b) =>
            {
                if (a.Rank != b.Rank) return b.Rank.CompareTo(a.Rank);
                return CompareLexicographically(a.Hypothesis.Prefix, b.Hypothesis.Prefix);
            });
            var result = new List<Hypothesis>(Math.Min(_parameters.BeamWidth, ranked.Count));
            for (int i = 0; i < ranked.Count && i < _parameters.BeamWidth; i++) result.Add(ranked[i].Hypothesis);
            return result;
        }

        /// <summary>本帧与假设无关的候选：门限内分数最高的 K 个普通 token ∪ 门限内的热词首 token。升序、去重。</summary>
        private void CollectFrameCandidates(ReadOnlySpan<float> row, double peak)
        {
            int limit = _parameters.TopK;
            // 先用单精度粗筛（向下取整保证不漏），再交给 Admits 按双精度精确判定。
            float floor = MathF.BitDecrement((float)(peak - _parameters.AcousticGate));
            _topCandidates.Clear();
            // 分块扫描：整块最大值都在门限外就跳过。门限内的 token 每帧通常只有个位数。
            for (int blockStart = 1; blockStart < _vocabSize; blockStart += ScanBlock)
            {
                int blockEnd = Math.Min(blockStart + ScanBlock, _vocabSize);
                if (RowMax(row[blockStart..blockEnd]) < floor) continue;
                for (int k = blockStart; k < blockEnd; k++)
                {
                    float score = row[k];
                    if (score < floor || !Admits(row, k, peak)) continue;
                    if (_topCandidates.Count == limit)
                    {
                        // 同分时保留 id 较小者，与「按分数降序、稳定」的选取一致。
                        if (!(score > _topCandidates[limit - 1].Score)) continue;
                        _topCandidates.RemoveAt(limit - 1);
                    }
                    int position = _topCandidates.Count;
                    while (position > 0 && _topCandidates[position - 1].Score < score) position--;
                    _topCandidates.Insert(position, (k, score));
                }
            }

            _frameCandidates.Clear();
            foreach (var candidate in _topCandidates) _frameCandidates.Add(candidate.Token);
            foreach (var first in _graph.RootTokens)
            {
                if (Admits(row, first, peak)) _frameCandidates.Add(first);
            }
            _frameCandidates.Sort();
            int unique = 0;
            for (int i = 0; i < _frameCandidates.Count; i++)
            {
                if (i == 0 || _frameCandidates[i] != _frameCandidates[i - 1]) _frameCandidates[unique++] = _frameCandidates[i];
            }
            _frameCandidates.RemoveRange(unique, _frameCandidates.Count - unique);
        }

        private void Accumulate(int prefix, double pb, double pnb)
        {
            if (_slotByPrefix.TryGetValue(prefix, out var slot))
            {
                var existing = _slots[slot];
                _slots[slot] = new Hypothesis(prefix, LogAddExp(existing.Pb, pb), LogAddExp(existing.Pnb, pnb));
            }
            else
            {
                _slotByPrefix[prefix] = _slots.Count;
                _slots.Add(new Hypothesis(prefix, pb, pnb));
            }
        }

        private int[] TokensOf(int prefix)
        {
            var result = new List<int>();
            int node = prefix;
            while (node > 0)
            {
                if (result.Count >= _parent.Count) throw new DecodeException("前缀回溯不到根");
                result.Add(_token[node]);
                node = _parent[node];
            }
            if (node != 0) throw new DecodeException("前缀回溯不到根");
            result.Reverse();
            return result.ToArray();
        }

        /// <summary>同分时按前缀 token id 字典序（短前缀在前），保证可复现。只在分数完全相等时调用。</summary>
        private int CompareLexicographically(int a, int b)
        {
            if (a == b) return 0;
            var lhs = TokensOf(a);
            var rhs = TokensOf(b);
            for (int i = 0; i < Math.Min(lhs.Length, rhs.Length); i++)
            {
                if (lhs[i] != rhs[i]) return lhs[i].CompareTo(rhs[i]);
            }
            return lhs.Length.CompareTo(rhs.Length);
        }

        /// <summary>结束时撤回暂存分：<c>final = 声学分 + committed</c>。</summary>
        public Result Best(List<Hypothesis> beam)
        {
            Hypothesis? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var hypothesis in beam)
            {
                double score = hypothesis.Total + _state[hypothesis.Prefix].Committed;
                if (best is null || score > bestScore
                    || (score == bestScore && CompareLexicographically(hypothesis.Prefix, best.Value.Prefix) < 0))
                {
                    best = hypothesis;
                    bestScore = score;
                }
            }
            if (best is not { } winner) throw new DecodeException("束为空");
            return new Result(TokensOf(winner.Prefix), _state[winner.Prefix].Hits);
        }
    }

    /// <summary>首个最大值的下标（与 <see cref="CtcDecoder"/> 的严格大于比较一致）。</summary>
    private static int RowArgmax(ReadOnlySpan<float> row)
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

    /// <summary>向量化求最大值（相当于 macOS 的 <c>vDSP_maxv</c>）。</summary>
    private static float RowMax(ReadOnlySpan<float> span)
    {
        float max = float.NegativeInfinity;
        int i = 0;
        if (Vector.IsHardwareAccelerated && span.Length >= Vector<float>.Count)
        {
            var vectors = MemoryMarshal.Cast<float, Vector<float>>(span);
            var acc = new Vector<float>(float.NegativeInfinity);
            foreach (var v in vectors) acc = Vector.Max(acc, v);
            for (int lane = 0; lane < Vector<float>.Count; lane++) max = Math.Max(max, acc[lane]);
            i = vectors.Length * Vector<float>.Count;
        }
        for (; i < span.Length; i++)
        {
            if (span[i] > max) max = span[i];
        }
        return max;
    }

    /// <summary>.NET 没有精确的 log1p（<c>double.LogP1</c> 即 <c>Log(x + 1)</c>），这里的 x ≤ 1，误差远小于任何有意义的分差。</summary>
    internal static double LogAddExp(double a, double b)
    {
        if (double.IsNegativeInfinity(a)) return b;
        if (double.IsNegativeInfinity(b)) return a;
        return Math.Max(a, b) + Math.Log(1.0 + Math.Exp(-Math.Abs(a - b)));
    }
}
