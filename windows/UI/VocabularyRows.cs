using System;
using System.Collections.Generic;
using System.Linq;
using VoiceTyper.Asr.Hotword;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

/// <summary>「词汇」页表格的一行草稿（VOC-9）。<see cref="AliasesText"/> 是逗号分隔的「听起来像」。</summary>
internal sealed record VocabularyRow(string Term, string AliasesText)
{
    /// <summary>半角 / 全角逗号与顿号都算分隔符（与 macOS <c>VocabularyRow.entry</c> 一致）。</summary>
    private static readonly char[] AliasSeparators = { ',', '，', '、' };

    public static VocabularyRow FromEntry(VocabularyEntry entry) =>
        new(entry.Term, string.Join(L10n.T("，"), entry.Aliases));

    public VocabularyEntry ToEntry() => new()
    {
        Term = Term,
        Aliases = AliasesText
            .Split(AliasSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList(),
    };
}

/// <summary>状态列的显示：<see cref="Symbol"/> 进单元格，<see cref="Reason"/> 进悬停提示。</summary>
internal sealed record VocabularyRowStatus(VocabularyRowStatus.Kind Status, string Reason)
{
    internal enum Kind { Empty, Unknown, Active, Warning }

    public string Symbol => Status switch
    {
        Kind.Active => "✓",
        Kind.Warning => "⚠",
        Kind.Unknown => "—",
        _ => "",
    };
}

/// <summary>
/// 「词汇」页不依赖 WinForms 的逻辑：行 ↔ 配置、状态列、「从最近结果添加」的合并。
/// 与 macOS <c>SettingsViewModel</c> 的词汇段同一语义，便于在没有界面线程的测试里验证。
/// </summary>
internal static class VocabularyRows
{
    /// <summary>表格草稿 → 规整后的配置（与落盘时的规整一致，用来做脏检查与「试一试」）。
    /// 每次按键都会调用，不写规整日志。</summary>
    public static VocabularyConfig BuildConfig(VocabularyBias bias, IEnumerable<VocabularyRow> rows) =>
        new VocabularyConfig { BiasValue = bias, Entries = rows.Select(r => r.ToEntry()).ToList() }
            .Validated(logWarnings: false);

    public static List<VocabularyRow> FromConfig(VocabularyConfig vocabulary) =>
        vocabulary.Entries.Select(VocabularyRow.FromEntry).ToList();

    /// <param name="report">引擎的编译报告；模型从未加载过为 null。</param>
    /// <param name="savedBias">已保存的识别偏向：报告按它编译，草稿里改了强度要保存后才生效。</param>
    public static VocabularyRowStatus Status(string term, HotwordCompileReport? report, VocabularyBias savedBias)
    {
        var key = NormalizedTerm(term);
        if (key.Length == 0) return new(VocabularyRowStatus.Kind.Empty, "");
        if (savedBias == VocabularyBias.Off)
            return new(VocabularyRowStatus.Kind.Unknown, L10n.T("识别偏向已关闭，仍会做替换"));
        if (report is null)
            return new(VocabularyRowStatus.Kind.Unknown, L10n.T("模型加载后检查"));
        // 报告只覆盖已保存的词条：刚输入、还没保存的行查不到。
        return report.StatusForTerm(key) switch
        {
            HotwordCompileReport.Status.Biased => new(VocabularyRowStatus.Kind.Active, L10n.T("已生效")),
            HotwordCompileReport.Status.TooShort => new(VocabularyRowStatus.Kind.Warning,
                L10n.T("只有一个字 / 一个词片，不参与识别偏向，仍会做替换")),
            HotwordCompileReport.Status.UnsupportedCharacters => new(VocabularyRowStatus.Kind.Warning,
                L10n.T("含无法识别的字符，不参与识别偏向，仍会做替换")),
            HotwordCompileReport.Status.Truncated => new(VocabularyRowStatus.Kind.Warning,
                L10n.T("词汇过多，本条未参与识别偏向，仍会做替换")),
            _ => new(VocabularyRowStatus.Kind.Unknown, L10n.T("保存后检查")),
        };
    }

    /// <summary>
    /// 「从最近结果添加」（§8.2）：<paramref name="heard"/> 是用户在识别结果里选中的、被认错的片段，
    /// <paramref name="term"/> 是正确写法。已有同名词条（不区分大小写）时把片段并入它的「听起来像」；
    /// 片段与正确写法只差大小写时不作为 alias（替换层本来就不区分大小写）。
    /// </summary>
    /// <returns>新增或被并入的行号；正确写法为空或已达上限时为 null，<paramref name="rows"/> 不变。</returns>
    public static int? AddFromHistory(List<VocabularyRow> rows, string term, string heard)
    {
        term = term.Trim();
        heard = heard.Trim();
        if (term.Length == 0) return null;
        var alias = string.Equals(heard, term, StringComparison.OrdinalIgnoreCase) ? "" : heard;
        var index = rows.FindIndex(r => string.Equals(NormalizedTerm(r.Term), NormalizedTerm(term), StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            if (alias.Length > 0)
            {
                var existing = rows[index].AliasesText.Trim();
                rows[index] = rows[index] with
                {
                    AliasesText = existing.Length == 0 ? alias : existing + L10n.T("，") + alias,
                };
            }
            return index;
        }
        if (rows.Count >= VocabularyConfig.MaxEntries) return null;
        rows.Add(new VocabularyRow(term, alias));
        return rows.Count - 1;
    }

    /// <summary>与 <see cref="VocabularyConfig.Validated"/> 相同的空白规整，用来在报告里查找本行。</summary>
    private static string NormalizedTerm(string term) =>
        string.Join(" ", term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
