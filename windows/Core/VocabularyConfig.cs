using System;
using System.Collections.Generic;
using System.Linq;
using VoiceTyper.Support;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace VoiceTyper.Core;

/// <summary>识别偏置强度（VOC-D5）。只影响识别层偏置；替换层与 LLM 词表不受它控制。</summary>
internal enum VocabularyBias { Off, Light, Standard, Strong }

internal static class VocabularyBiasExtensions
{
    public static string ToYamlValue(this VocabularyBias bias) => bias switch
    {
        VocabularyBias.Off => "off",
        VocabularyBias.Light => "light",
        VocabularyBias.Strong => "strong",
        _ => "standard",
    };

    /// <summary>未知值回落 standard，与本文件其余枚举字段的容错一致。</summary>
    public static VocabularyBias Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "off" => VocabularyBias.Off,
        "light" => VocabularyBias.Light,
        "strong" => VocabularyBias.Strong,
        _ => VocabularyBias.Standard,
    };
}

/// <summary>一条自定义词汇（VOC-D2）：<see cref="Term"/> 是正确写法，<see cref="Aliases"/> 是「听起来像 / 常被识别成」的写法。</summary>
internal sealed class VocabularyEntry : IEquatable<VocabularyEntry>
{
    [YamlMember(Alias = "term")]
    public string Term { get; set; } = "";

    [YamlMember(Alias = "aliases")]
    public List<string> Aliases { get; set; } = new();

    public VocabularyEntry() { }

    public VocabularyEntry(string term, params string[] aliases)
    {
        Term = term;
        Aliases = aliases.ToList();
    }

    public VocabularyEntry Clone() => new() { Term = Term, Aliases = new List<string>(Aliases) };

    public bool Equals(VocabularyEntry? other) =>
        other is not null && Term == other.Term && Aliases.SequenceEqual(other.Aliases);

    public override bool Equals(object? obj) => Equals(obj as VocabularyEntry);

    public override int GetHashCode() => HashCode.Combine(Term, Aliases.Count);

    public override string ToString() => $"{Term} [{string.Join(", ", Aliases)}]";
}

/// <summary>
/// 自定义词汇表（VOC-1），与 macOS 同一 schema（<c>macos/docs/VOCABULARY_SPEC.md</c> §4）。
/// 词条文本只存在 config.yaml，日志里只记条数（VOC-D9）。
/// </summary>
internal sealed class VocabularyConfig
{
    public const int MaxEntries = 200;
    public const int MaxAliasesPerEntry = 10;
    public const int MaxSurfaceLength = 50;

    [YamlMember(Alias = "bias")]
    public string Bias { get; set; } = "standard";

    [YamlMember(Alias = "entries")]
    public List<VocabularyEntry> Entries { get; set; } = new();

    [YamlIgnore]
    public VocabularyBias BiasValue
    {
        get => VocabularyBiasExtensions.Parse(Bias);
        set => Bias = value.ToYamlValue();
    }

    /// <summary>识别层是否需要编译上下文图：有词条且强度不是「关」。</summary>
    [YamlIgnore]
    public bool BiasesRecognition => BiasValue != VocabularyBias.Off && Entries.Count > 0;

    public VocabularyConfig Clone() => new()
    {
        Bias = Bias,
        Entries = Entries.Select(e => e.Clone()).ToList(),
    };

    public bool ContentEquals(VocabularyConfig other) =>
        BiasValue == other.BiasValue && Entries.SequenceEqual(other.Entries);

    /// <summary>§4.2 的逐条校验：去空白、去空、去重、合并同名 term、截断超限部分。越界只截断，warning 只记条数。</summary>
    public VocabularyConfig Validated()
    {
        var merged = new List<VocabularyEntry>();
        var indexByKey = new Dictionary<string, int>();
        int truncatedSurfaces = 0, droppedEntries = 0, mergedEntries = 0;

        foreach (var entry in Entries ?? new List<VocabularyEntry>())
        {
            if (entry is null) continue;
            var term = NormalizedSurface(entry.Term, ref truncatedSurfaces);
            if (term.Length == 0)
            {
                droppedEntries++;
                continue;
            }
            var aliases = new List<string>();
            foreach (var alias in entry.Aliases ?? new List<string>())
            {
                aliases.Add(NormalizedSurface(alias, ref truncatedSurfaces));
            }
            var key = term.ToLowerInvariant();
            if (indexByKey.TryGetValue(key, out var index))
            {
                mergedEntries++;
                merged[index].Aliases.AddRange(aliases);
            }
            else
            {
                indexByKey[key] = merged.Count;
                merged.Add(new VocabularyEntry { Term = term, Aliases = aliases });
            }
        }

        int truncatedAliases = 0;
        foreach (var entry in merged)
        {
            var seen = new HashSet<string> { entry.Term.ToLowerInvariant() };
            var kept = new List<string>();
            foreach (var alias in entry.Aliases)
            {
                if (alias.Length == 0 || !seen.Add(alias.ToLowerInvariant())) continue;
                kept.Add(alias);
            }
            if (kept.Count > MaxAliasesPerEntry)
            {
                truncatedAliases += kept.Count - MaxAliasesPerEntry;
                kept.RemoveRange(MaxAliasesPerEntry, kept.Count - MaxAliasesPerEntry);
            }
            entry.Aliases = kept;
        }

        int overflow = Math.Max(0, merged.Count - MaxEntries);
        if (overflow > 0) merged.RemoveRange(MaxEntries, overflow);

        if (droppedEntries + mergedEntries + truncatedSurfaces + truncatedAliases + overflow > 0)
        {
            AppLog.Warn("config",
                $"词汇表已规整：空词条 {droppedEntries} 条、合并重复 {mergedEntries} 条、截断过长写法 {truncatedSurfaces} 个、"
                + $"截断多余 alias {truncatedAliases} 个、超出上限 {overflow} 条");
        }
        return new VocabularyConfig { Bias = BiasValue.ToYamlValue(), Entries = merged };
    }

    /// <summary>
    /// 去首尾空白；内部的换行、制表等空白收敛成一个半角空格；超长截断。长度按 Unicode 标量计
    /// （与 Swift 的 Character 计数在汉字、英文上一致；组合字符序列极少出现在词条里）。
    /// </summary>
    private static string NormalizedSurface(string? raw, ref int truncatedCount)
    {
        var collapsed = string.Join(" ", (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var runes = collapsed.EnumerateRunes().ToArray();
        if (runes.Length <= MaxSurfaceLength) return collapsed;
        truncatedCount++;
        return string.Concat(runes.Take(MaxSurfaceLength).Select(r => r.ToString())).Trim();
    }
}

/// <summary>
/// 词条的 YAML 容错读取：手写配置时允许 <c>- 张三丰</c> 这种只有 term 的简写，<c>aliases</c> 也允许写成单个字符串
/// （与 macOS <c>VocabularyEntry.init(from:)</c> 一致）。写回由 <see cref="ConfigStore"/> 手工序列化，不走这里。
/// </summary>
internal sealed class VocabularyEntryYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(VocabularyEntry);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return new VocabularyEntry { Term = scalar.Value };
        }
        var entry = new VocabularyEntry();
        parser.Consume<MappingStart>();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;
            switch (key)
            {
                case "term" when parser.TryConsume<Scalar>(out var term):
                    entry.Term = term.Value;
                    break;
                case "aliases" when parser.TryConsume<Scalar>(out var single):
                    if (!IsNull(single)) entry.Aliases.Add(single.Value);
                    break;
                case "aliases" when parser.TryConsume<SequenceStart>(out _):
                    while (!parser.TryConsume<SequenceEnd>(out _))
                    {
                        if (parser.TryConsume<Scalar>(out var alias))
                        {
                            if (!IsNull(alias)) entry.Aliases.Add(alias.Value);
                        }
                        else
                        {
                            parser.SkipThisAndNestedEvents();
                        }
                    }
                    break;
                default:
                    parser.SkipThisAndNestedEvents();
                    break;
            }
        }
        return entry;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        throw new NotSupportedException("config.yaml 由 ConfigStore 手工序列化");

    private static bool IsNull(Scalar scalar) =>
        scalar.Style == ScalarStyle.Plain && scalar.Value is "" or "~" or "null" or "Null" or "NULL";
}
