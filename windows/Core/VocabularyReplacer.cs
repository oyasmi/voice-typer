using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VoiceTyper.Asr.Hotword;

namespace VoiceTyper.Core;

/// <summary>
/// 自定义词汇的确定性替换（VOC-7）：把识别结果里的 alias 换成 term，并把 term 的大小写、空格、连字符变体
/// 统一成 term 的写法（<c>voice typer</c> → <c>VoiceTyper</c>）。由 <see cref="VocabularyConfig"/> 编译出的不可变对象，
/// <see cref="Apply"/> 是纯函数。C# 直译自 <c>macos/Sources/VoiceTyper/Text/VocabularyReplacer.swift</c>，
/// 两边读同一份 <c>vocabulary_cases.json</c>；按 Unicode 标量匹配，与 Swift 一致。
/// <list type="bullet">
/// <item>汉字 / 假名 / 谚文等非 ASCII 字符逐字匹配；与 ASCII 段交界处允许 0–1 个空白。</item>
/// <item>ASCII 字母不区分大小写；写法首尾若是 ASCII 字母数字，要求原文对应位置外侧不是 [A-Za-z0-9]（词边界）。</item>
/// <item>ASCII 段内「小写→大写」「字母↔数字」处、写法里 ASCII 段之间的空格或连字符处，允许 0–1 个空白或连字符。</item>
/// </list>
/// 一轮内按起点升序、同起点取最长、不重叠贪心选取，替换产物不参与本轮匹配；同起点同长度时 term 自身的模式优先于
/// alias。替换会产生新的相邻关系，所以整轮重复到结果不再变化（最多 <see cref="MaxRounds"/> 轮），保证幂等。
/// </summary>
internal sealed class VocabularyReplacer
{
    public static readonly VocabularyReplacer Empty = new(new VocabularyConfig());
    public const int MaxRounds = 4;

    /// <summary>全部 term，按词汇表顺序；供 LLM 词表（VOC-8）使用。</summary>
    public IReadOnlyList<string> Terms { get; }
    private readonly List<Pattern> _patterns = new();
    /// <summary>模式首字符（ASCII 已转小写）→ 以它开头的模式下标，按优先级排列。</summary>
    private readonly Dictionary<int, List<int>> _patternsByFirstScalar = new();

    public bool IsEmpty => _patterns.Count == 0;

    public VocabularyReplacer(VocabularyConfig vocabulary)
    {
        foreach (var entry in vocabulary.Entries)
        {
            var target = HotwordCompiler.Scalars(entry.Term);
            if (target.Length == 0) continue;
            var surfaces = new[] { entry.Term }.Concat(entry.Aliases).ToList();
            for (int index = 0; index < surfaces.Count; index++)
            {
                if (Pattern.Create(surfaces[index], target, isTerm: index == 0) is { } pattern) _patterns.Add(pattern);
            }
        }
        for (int index = 0; index < _patterns.Count; index++)
        {
            if (_patterns[index].Units[0] is not { IsSeparator: false } first) continue;
            if (!_patternsByFirstScalar.TryGetValue(first.Scalar, out var list))
            {
                list = new List<int>();
                _patternsByFirstScalar[first.Scalar] = list;
            }
            list.Add(index);
        }
        Terms = vocabulary.Entries.Select(e => e.Term).Where(t => t.Length > 0).ToList();
    }

    public string Apply(string text)
    {
        if (IsEmpty || string.IsNullOrEmpty(text)) return text ?? "";
        var current = HotwordCompiler.Scalars(text);
        for (int round = 0; round < MaxRounds; round++)
        {
            var next = ApplyOnce(current);
            if (next.AsSpan().SequenceEqual(current)) break;
            current = next;
        }
        var builder = new StringBuilder(text.Length);
        foreach (var scalar in current) builder.Append(new Rune(scalar).ToString());
        return builder.ToString();
    }

    private int[] ApplyOnce(int[] text)
    {
        var output = new List<int>(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            if (LongestMatch(text, index) is not { } match)
            {
                output.Add(text[index]);
                index++;
                continue;
            }
            var target = _patterns[match.Pattern].Target;
            // CJK 写法换成英文 term 时不能和相邻的英文粘在一起（克劳德code → Claude code）。
            if (output.Count > 0 && HotwordCompiler.IsAsciiAlphanumeric(output[^1]) && HotwordCompiler.IsAsciiAlphanumeric(target[0]))
            {
                output.Add(' ');
            }
            output.AddRange(target);
            if (match.End < text.Length && HotwordCompiler.IsAsciiAlphanumeric(text[match.End])
                && HotwordCompiler.IsAsciiAlphanumeric(target[^1]))
            {
                output.Add(' ');
            }
            index = match.End;
        }
        return output.ToArray();
    }

    /// <summary>同起点取最长；同长度时 term 模式优先于 alias 模式，其余按词汇表顺序。</summary>
    private (int Pattern, int End)? LongestMatch(int[] text, int start)
    {
        if (!_patternsByFirstScalar.TryGetValue(HotwordCompiler.AsciiLower(text[start]), out var candidates)) return null;
        (int Pattern, int End)? best = null;
        foreach (var index in candidates)
        {
            if (_patterns[index].Match(text, start) is not { } end) continue;
            if (best is not { } current)
            {
                best = (index, end);
                continue;
            }
            if (end > current.End || (end == current.End && _patterns[index].IsTerm && !_patterns[current.Pattern].IsTerm))
            {
                best = (index, end);
            }
        }
        return best;
    }

    /// <summary>模式单元：一个字符（ASCII 字母已转小写），或 0–1 个空白（<c>AllowsHyphen</c> 时也可以是连字符）。</summary>
    private readonly record struct Unit(bool IsSeparator, int Scalar, bool AllowsHyphen)
    {
        public static Unit Literal(int scalar) => new(false, scalar, false);
        public static Unit Separator(bool allowsHyphen) => new(true, 0, allowsHyphen);
    }

    private sealed class Pattern
    {
        public required Unit[] Units { get; init; }
        public required int[] Target { get; init; }
        public required bool IsTerm { get; init; }
        private bool NeedsLeadingBoundary { get; init; }
        private bool NeedsTrailingBoundary { get; init; }

        public static Pattern? Create(string surface, int[] target, bool isTerm)
        {
            var scalars = HotwordCompiler.Scalars(surface);
            var units = new List<Unit>();
            // 上一个非空白字符（原样，未转小写），用来决定两字符之间允许什么分隔。
            int? previous = null;
            bool pendingSpace = false;
            for (int index = 0; index < scalars.Length; index++)
            {
                int scalar = scalars[index];
                if (HotwordCompiler.IsWhitespace(scalar))
                {
                    pendingSpace = true;
                    continue;
                }
                bool isAscii = scalar <= 0x7F;
                if (previous is { } prev && units.Count > 0 && !units[^1].IsSeparator)
                {
                    bool previousWasAscii = prev <= 0x7F;
                    if (scalar == '-' && index + 1 < scalars.Length
                        && HotwordCompiler.IsAsciiAlphanumeric(prev) && HotwordCompiler.IsAsciiAlphanumeric(scalars[index + 1]))
                    {
                        // 两个 ASCII 字母数字之间的连字符是分隔符，不是字面量：GPT-4o 也匹配 GPT 4o、GPT4o。
                        units.Add(Unit.Separator(allowsHyphen: true));
                        pendingSpace = false;
                        continue;
                    }
                    if (pendingSpace)
                    {
                        units.Add(Unit.Separator(allowsHyphen: previousWasAscii && isAscii));
                    }
                    else if (previousWasAscii != isAscii)
                    {
                        units.Add(Unit.Separator(allowsHyphen: false));
                    }
                    else if (HotwordCompiler.SplitPoints(new[] { prev, scalar }).Count == 1)
                    {
                        units.Add(Unit.Separator(allowsHyphen: true));
                    }
                }
                pendingSpace = false;
                units.Add(Unit.Literal(HotwordCompiler.AsciiLower(scalar)));
                previous = scalar;
            }
            if (units.Count == 0 || units[0].IsSeparator || units[^1].IsSeparator) return null;
            return new Pattern
            {
                Units = units.ToArray(),
                Target = target,
                IsTerm = isTerm,
                NeedsLeadingBoundary = HotwordCompiler.IsAsciiAlphanumeric(units[0].Scalar),
                NeedsTrailingBoundary = HotwordCompiler.IsAsciiAlphanumeric(units[^1].Scalar),
            };
        }

        /// <summary>在 <paramref name="start"/> 处尝试匹配，返回匹配终点（不含）；多种走法时取最长。</summary>
        public int? Match(int[] text, int start)
        {
            if (NeedsLeadingBoundary && start > 0 && HotwordCompiler.IsAsciiAlphanumeric(text[start - 1])) return null;
            return MatchUnits(0, text, start);
        }

        private int? MatchUnits(int unitIndex, int[] text, int position)
        {
            if (unitIndex >= Units.Length)
            {
                if (NeedsTrailingBoundary && position < text.Length && HotwordCompiler.IsAsciiAlphanumeric(text[position])) return null;
                return position;
            }
            var unit = Units[unitIndex];
            if (!unit.IsSeparator)
            {
                if (position >= text.Length || HotwordCompiler.AsciiLower(text[position]) != unit.Scalar) return null;
                return MatchUnits(unitIndex + 1, text, position + 1);
            }
            var best = MatchUnits(unitIndex + 1, text, position);
            if (position < text.Length
                && (HotwordCompiler.IsWhitespace(text[position]) || (unit.AllowsHyphen && text[position] == '-'))
                && MatchUnits(unitIndex + 1, text, position + 1) is { } consumed)
            {
                best = Math.Max(best ?? consumed, consumed);
            }
            return best;
        }
    }
}
