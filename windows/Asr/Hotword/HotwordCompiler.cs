using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using VoiceTyper.Core;

namespace VoiceTyper.Asr.Hotword;

/// <summary>
/// 词汇编译报告（VOC-3）：逐条给出识别层的生效状态。报告里有词条文本，只在内存里使用，
/// <b>不得写入日志</b>（VOC-D9）。C# 直译自 <c>macos/Sources/VoiceTyper/ASR/Hotword/HotwordCompiler.swift</c>。
/// </summary>
internal sealed class HotwordCompileReport
{
    internal enum Status
    {
        /// <summary>至少一条 token 序列进入上下文图。</summary>
        Biased,
        /// <summary>写法只有 1 个 token（单个汉字、单个词片），不参与偏置，只做替换（VOC-D6）。</summary>
        TooShort,
        /// <summary>含词表里没有的字符（生僻扩展区汉字、emoji、全角符号等）。</summary>
        UnsupportedCharacters,
        /// <summary>序列总数已达上限，本条没能入图。</summary>
        Truncated,
    }

    public IReadOnlyList<(string Term, Status Status)> Entries { get; init; } = Array.Empty<(string, Status)>();
    /// <summary>入图的 token 序列总数。</summary>
    public int SequenceCount { get; init; }
    public long CompileTicks { get; init; }

    public Status? StatusForTerm(string term)
    {
        var key = term.ToLowerInvariant();
        foreach (var entry in Entries)
        {
            if (entry.Term.ToLowerInvariant() == key) return entry.Status;
        }
        return null;
    }

    public int CountOf(Status status) => Entries.Count(e => e.Status == status);
}

/// <summary>
/// 模型词表的字符级前缀树，用来把热词写法切成 token（VOC-3 步骤 2）。只收普通 token：排除 id 0–2
/// （<c>&lt;unk&gt;</c> <c>&lt;s&gt;</c> <c>&lt;/s&gt;</c>）与 <c>&lt;|…|&gt;</c> 特殊 token。一个引擎只建一次，之后不可变。
/// 按 Unicode 标量（<see cref="Rune"/>）切分，与 Swift 的 <c>unicodeScalars</c> 一致。
/// </summary>
internal sealed class TokenTrie
{
    /// <summary><c>(节点 &lt;&lt; 21) | 标量值</c> → 子节点。Unicode 标量最大 0x10FFFF，21 位放得下。</summary>
    private readonly Dictionary<long, int> _edges = new();
    /// <summary>节点 → 以该节点结尾的 token id；不是 token 结尾为 -1。</summary>
    private readonly List<int> _tokenAtNode = new() { -1 };

    public TokenTrie(IReadOnlyList<string> tokens)
    {
        for (int id = 0; id < tokens.Count; id++)
        {
            var token = tokens[id];
            if (IsExcluded(id, token)) continue;
            int node = 0;
            foreach (var rune in token.EnumerateRunes())
            {
                long key = ((long)node << 21) | (uint)rune.Value;
                if (_edges.TryGetValue(key, out var next))
                {
                    node = next;
                }
                else
                {
                    next = _tokenAtNode.Count;
                    _tokenAtNode.Add(-1);
                    _edges[key] = next;
                    node = next;
                }
            }
            // 词表里同一字符串出现两次时保留第一个 id。
            if (_tokenAtNode[node] < 0) _tokenAtNode[node] = id;
        }
    }

    public static bool IsSpecial(string token) => token.StartsWith("<|", StringComparison.Ordinal) && token.EndsWith("|>", StringComparison.Ordinal);

    public static bool IsExcluded(int id, string token) => id <= 2 || token.Length == 0 || IsSpecial(token);

    /// <summary>
    /// token 数最少的前 <paramref name="limit"/> 优切分；token 数相同时，逐段比较片段长度，先出现更长片段者优先
    /// （近似 BPE 的贪心合并）。有字符在词表里找不到时返回空列表。
    /// </summary>
    public List<int[]> BestSegmentations(int[] text, int limit = 2)
    {
        var result = new List<int[]>();
        if (text.Length == 0) return result;
        int n = text.Length;
        // best[i] 是 text[i..] 的前 limit 优切分。
        var best = new List<Segmentation>[n + 1];
        best[n] = new List<Segmentation> { new(Array.Empty<int>(), Array.Empty<int>()) };
        for (int start = n - 1; start >= 0; start--)
        {
            var candidates = new List<Segmentation>();
            int node = 0;
            int end = start;
            while (end < n && _edges.TryGetValue(((long)node << 21) | (uint)text[end], out var next))
            {
                node = next;
                end++;
                int id = _tokenAtNode[node];
                if (id < 0) continue;
                foreach (var rest in best[end])
                {
                    candidates.Add(new Segmentation(Prepend(id, rest.Ids), Prepend(end - start, rest.Lengths)));
                }
            }
            // 稳定排序：同一排名键保持生成顺序，与 Swift 的 sort 在键唯一时结果一致。
            best[start] = candidates.OrderBy(c => c, SegmentationComparer.Instance).Take(limit).ToList();
        }
        foreach (var segmentation in best[0]) result.Add(segmentation.Ids);
        return result;
    }

    private static int[] Prepend(int head, int[] rest)
    {
        var array = new int[rest.Length + 1];
        array[0] = head;
        Array.Copy(rest, 0, array, 1, rest.Length);
        return array;
    }

    private readonly record struct Segmentation(int[] Ids, int[] Lengths);

    private sealed class SegmentationComparer : IComparer<Segmentation>
    {
        public static readonly SegmentationComparer Instance = new();

        public int Compare(Segmentation a, Segmentation b)
        {
            if (a.Ids.Length != b.Ids.Length) return a.Ids.Length.CompareTo(b.Ids.Length);
            for (int i = 0; i < a.Lengths.Length; i++)
            {
                if (a.Lengths[i] != b.Lengths[i]) return b.Lengths[i].CompareTo(a.Lengths[i]);
            }
            return 0;
        }
    }
}

/// <summary>把词汇表编译成上下文图要用的 token 序列（VOC-3）。纯函数，可在任意线程调用。</summary>
internal static class HotwordCompiler
{
    /// <summary>SentencePiece 的词首标记 ▁。</summary>
    public const int WordBoundary = 0x2581;
    public const int MaxSequencesPerSurface = 8;
    public const int MaxTotalSequences = 4000;

    internal sealed record Output(List<int[]> Sequences, HotwordCompileReport Report);

    internal enum SurfaceKind { Sequences, TooShort, Unsupported }

    internal readonly record struct SurfaceResult(SurfaceKind Kind, List<int[]> Sequences);

    public static Output Compile(VocabularyConfig vocabulary, TokenTrie trie)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var collected = new List<int[]>();
        var entries = new List<(string, HotwordCompileReport.Status)>();

        foreach (var entry in vocabulary.Entries)
        {
            bool added = false, hitLimit = false, sawTooShort = false;
            foreach (var surface in new[] { entry.Term }.Concat(entry.Aliases))
            {
                var result = SequencesFor(surface, trie);
                switch (result.Kind)
                {
                    case SurfaceKind.TooShort:
                        sawTooShort = true;
                        break;
                    case SurfaceKind.Sequences:
                        foreach (var sequence in result.Sequences)
                        {
                            if (collected.Count >= MaxTotalSequences)
                            {
                                hitLimit = true;
                                break;
                            }
                            collected.Add(sequence);
                            added = true;
                        }
                        break;
                }
            }
            var status = added ? HotwordCompileReport.Status.Biased
                : hitLimit ? HotwordCompileReport.Status.Truncated
                : sawTooShort ? HotwordCompileReport.Status.TooShort
                : HotwordCompileReport.Status.UnsupportedCharacters;
            entries.Add((entry.Term, status));
        }

        var report = new HotwordCompileReport
        {
            Entries = entries,
            SequenceCount = collected.Count,
            CompileTicks = Stopwatch.GetTimestamp() - startedAt,
        };
        return new Output(collected, report);
    }

    /// <summary>
    /// 一个写法的 token 序列：变体按 <see cref="RawVariants"/> 的优先级逐个切分，先取各自的最优切分，再补次优切分；
    /// 去重、丢弃单 token 序列后最多取 8 条。
    /// </summary>
    public static SurfaceResult SequencesFor(string surface, TokenTrie trie)
    {
        var best = new List<int[]>();
        var secondBest = new List<int[]>();
        var variants = RawVariants(surface);
        for (int index = 0; index < variants.Count; index++)
        {
            var found = trie.BestSegmentations(Scalars(variants[index]));
            if (found.Count == 0) continue;
            // 原写法本身就是一个 token：模型能直接输出它，其余大小写变体的偏置只会把结果推向别的写法（VOC-D6）。
            if (index == 0 && found[0].Length < 2) return new SurfaceResult(SurfaceKind.TooShort, new List<int[]>());
            best.Add(found[0]);
            secondBest.AddRange(found.Skip(1));
        }
        if (best.Count == 0) return new SurfaceResult(SurfaceKind.Unsupported, new List<int[]>());

        var result = new List<int[]>();
        foreach (var sequence in best.Concat(secondBest))
        {
            if (sequence.Length < 2 || result.Any(s => s.AsSpan().SequenceEqual(sequence))) continue;
            result.Add(sequence);
            if (result.Count == MaxSequencesPerSurface) break;
        }
        return result.Count == 0
            ? new SurfaceResult(SurfaceKind.TooShort, result)
            : new SurfaceResult(SurfaceKind.Sequences, result);
    }

    // ─── 原始串变体（VOC-3 步骤 1）──────────────────────────────

    private enum SegmentKind { Alphanumeric, Punctuation, Wide }

    private readonly record struct Segment(SegmentKind Kind, int[] Scalars, bool SpaceBefore);

    private enum LetterCase { Original, Lower, Capitalized }

    /// <summary>
    /// 写法 → 模型可能输出的原始 token 串，按可能性从高到低排列、已去重。规则与 Swift 版逐条一致：
    /// 空格之后的 ASCII 段一定带 ▁；串首、汉字之后的 ASCII 段两种都生成；ASCII 段在「小写→大写」「字母↔数字」处可拆；
    /// 大小写取原样、全小写、首字母大写；顺序为原样合写 → 原样拆词 → 小写拆词 → 小写合写 → 首字母大写（合写、拆词），
    /// 每一种先带 ▁ 再不带。
    /// </summary>
    public static List<string> RawVariants(string surface)
    {
        var segments = SegmentSurface(surface);
        var variants = new List<string>();
        if (segments.Count == 0) return variants;
        var forms = new (bool Split, LetterCase Case)[]
        {
            (false, LetterCase.Original), (true, LetterCase.Original), (true, LetterCase.Lower),
            (false, LetterCase.Lower), (false, LetterCase.Capitalized), (true, LetterCase.Capitalized),
        };
        foreach (var form in forms)
        {
            foreach (var leadingBoundary in new[] { true, false })
            {
                var raw = Render(segments, form.Split, form.Case, leadingBoundary);
                if (!variants.Contains(raw)) variants.Add(raw);
            }
        }
        return variants;
    }

    internal static int[] Scalars(string text) => text.EnumerateRunes().Select(r => r.Value).ToArray();

    private static List<Segment> SegmentSurface(string surface)
    {
        var segments = new List<Segment>();
        var scalars = Scalars(surface);
        int index = 0;
        bool spaceBefore = false;
        while (index < scalars.Length)
        {
            var scalar = scalars[index];
            if (IsWhitespace(scalar))
            {
                spaceBefore = true;
                index++;
                continue;
            }
            var kind = KindOf(scalar);
            int end = index + 1;
            if (kind != SegmentKind.Wide)
            {
                while (end < scalars.Length && !IsWhitespace(scalars[end]) && KindOf(scalars[end]) == kind) end++;
            }
            segments.Add(new Segment(kind, scalars[index..end], spaceBefore));
            spaceBefore = false;
            index = end;
        }
        return segments;
    }

    private static SegmentKind KindOf(int scalar)
    {
        if (scalar > 0x7F) return SegmentKind.Wide;
        return IsAsciiAlphanumeric(scalar) ? SegmentKind.Alphanumeric : SegmentKind.Punctuation;
    }

    private static string Render(List<Segment> segments, bool split, LetterCase letterCase, bool ambiguousBoundary)
    {
        var output = new StringBuilder();
        SegmentKind? previous = null;
        foreach (var segment in segments)
        {
            if (segment.Kind == SegmentKind.Wide)
            {
                Append(output, segment.Scalars);
                previous = SegmentKind.Wide;
                continue;
            }
            bool ambiguous = previous is null || (previous == SegmentKind.Wide && !segment.SpaceBefore);
            if (segment.SpaceBefore || (ambiguous && ambiguousBoundary)) output.Append((char)WordBoundary);
            if (segment.Kind == SegmentKind.Alphanumeric)
            {
                var pieces = CasedPieces(segment.Scalars, split, letterCase);
                for (int i = 0; i < pieces.Count; i++)
                {
                    if (i > 0) output.Append((char)WordBoundary);
                    Append(output, pieces[i]);
                }
            }
            else
            {
                Append(output, segment.Scalars);
            }
            previous = segment.Kind;
        }
        return output.ToString();
    }

    private static void Append(StringBuilder builder, int[] scalars)
    {
        foreach (var scalar in scalars) builder.Append(new Rune(scalar).ToString());
    }

    /// <summary>ASCII 段内部可以拆开的位置：小写→大写、字母↔数字。</summary>
    public static List<int> SplitPoints(int[] scalars)
    {
        var points = new List<int>();
        for (int index = 1; index < scalars.Length; index++)
        {
            int a = scalars[index - 1], b = scalars[index];
            if ((IsAsciiLower(a) && IsAsciiUpper(b)) || (IsAsciiLetter(a) && IsAsciiDigit(b)) || (IsAsciiDigit(a) && IsAsciiLetter(b)))
            {
                points.Add(index);
            }
        }
        return points;
    }

    private static List<int[]> CasedPieces(int[] scalars, bool split, LetterCase letterCase)
    {
        var pieces = new List<int[]> { scalars };
        if (split)
        {
            var bounds = new List<int> { 0 };
            bounds.AddRange(SplitPoints(scalars));
            bounds.Add(scalars.Length);
            pieces = new List<int[]>();
            for (int i = 0; i < bounds.Count - 1; i++) pieces.Add(scalars[bounds[i]..bounds[i + 1]]);
        }
        return letterCase switch
        {
            LetterCase.Lower => pieces.Select(p => p.Select(AsciiLower).ToArray()).ToList(),
            LetterCase.Capitalized => pieces.Select(p => p.Select((c, i) => i == 0 ? AsciiUpper(c) : AsciiLower(c)).ToArray()).ToList(),
            _ => pieces,
        };
    }

    // ─── ASCII 字符分类（不受区域设置影响）──────────────────────

    /// <summary>与 Swift <c>Unicode.Scalar.Properties.isWhitespace</c> 同一集合（Unicode White_Space）。</summary>
    internal static bool IsWhitespace(int scalar) => Rune.IsValid(scalar) && Rune.IsWhiteSpace(new Rune(scalar));
    internal static bool IsAsciiUpper(int c) => c is >= 0x41 and <= 0x5A;
    internal static bool IsAsciiLower(int c) => c is >= 0x61 and <= 0x7A;
    internal static bool IsAsciiDigit(int c) => c is >= 0x30 and <= 0x39;
    internal static bool IsAsciiLetter(int c) => IsAsciiUpper(c) || IsAsciiLower(c);
    internal static bool IsAsciiAlphanumeric(int c) => IsAsciiLetter(c) || IsAsciiDigit(c);
    internal static int AsciiLower(int c) => IsAsciiUpper(c) ? c + 0x20 : c;
    internal static int AsciiUpper(int c) => IsAsciiLower(c) ? c - 0x20 : c;
}
