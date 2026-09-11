using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VoiceTyper.Support;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 中英双语的覆盖率闸门（与 macOS <c>LocalizationTests</c> 同构）。
///
/// 文案表用中文原文做键，漏翻会"静默地"退回中文——英文用户看到半个中文界面，代码却照常
/// 编译通过。这组测试把它变成可见的失败：扫描全部源码里的 <c>L10n.T(...)</c> /
/// <c>L10n.F(...)</c>，要求每条都能在英文表里查到，且占位符一致。
/// </summary>
public class LocalizationTests
{
    /// <summary>从测试输出目录向上找到 windows/ 源码根（含 Support/Localization）。</summary>
    private static string? FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Support", "Localization"))
                && File.Exists(Path.Combine(dir.FullName, "VoiceTyper.csproj")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static readonly Regex CallPattern = new(@"\bL10n\.[TF]\(\s*""((?:[^""\\]|\\.)*)""");

    private static HashSet<string> CollectKeysFromSources(string root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            // 构建中间产物与测试自身不参与扫描；文案表本身就是被检查的对象。
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("bin/") || relative.StartsWith("obj/")
                || relative.Contains("/bin/") || relative.Contains("/obj/")
                || relative.StartsWith("Tests/")
                || relative.EndsWith("Strings.en.cs", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in CallPattern.Matches(File.ReadAllText(path)))
            {
                keys.Add(Unescape(match.Groups[1].Value));
            }
        }
        return keys;
    }

    /// <summary>把源码字面量里的转义还原成运行期真正的字符串。</summary>
    private static string Unescape(string literal) => literal
        .Replace("\\n", "\n")
        .Replace("\\t", "\t")
        .Replace("\\\"", "\"")
        .Replace("\\\\", "\\");

    private static List<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+(?::[^}]*)?\}").Select(m => m.Value).ToList();

    [Fact]
    public void EveryLocalizedKeyHasEnglishTranslation()
    {
        var root = FindSourceRoot();
        if (root is null) return; // 源码树不可达（例如只拿到编译产物）时跳过，不误报失败。

        var keys = CollectKeysFromSources(root);
        Assert.True(keys.Count > 100, "没有扫描到足够的文案调用，正则或路径可能失效");

        var missing = keys.Where(k => !L10n.Table.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "以下文案缺少英文翻译：\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EnglishTableHasNoStaleEntries()
    {
        var root = FindSourceRoot();
        if (root is null) return;

        var keys = CollectKeysFromSources(root);
        var stale = L10n.Table.Keys.Where(k => !keys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "英文表里有源码已不再使用的条目：\n" + string.Join("\n", stale));
    }

    [Fact]
    public void PlaceholdersMatchBetweenLanguages()
    {
        foreach (var (zh, en) in L10n.Table)
        {
            Assert.Equal(Placeholders(zh), Placeholders(en));
        }
    }

    [Fact]
    public void LookupFallsBackToChineseWhenUntranslated()
    {
        L10n.SetLanguage(AppLanguage.En);
        try
        {
            Assert.Equal("这条文案不存在于英文表", L10n.T("这条文案不存在于英文表"));
            Assert.Equal("Ready", L10n.T("就绪"));
            Assert.Equal("Downloading model 42%", L10n.F("下载模型 {0}%", 42));
        }
        finally
        {
            L10n.SetLanguage(AppLanguage.Zh);
        }
    }

    [Fact]
    public void ChineseIsTheDefaultLanguage()
    {
        L10n.SetLanguage(AppLanguage.Zh);
        Assert.Equal(AppLanguage.Zh, new Core.UIConfig().InterfaceLanguageValue);
        Assert.Equal("就绪", L10n.T("就绪"));
    }

    [Fact]
    public void BootstrapReadsInterfaceLanguageFromConfigFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voicetyper-l10n-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, "ui:\n  opacity: 0.85\n  interface_language: \"en\"\n");
        try
        {
            L10n.SetLanguage(AppLanguage.Zh);
            L10n.Bootstrap(path);
            Assert.Equal(AppLanguage.En, L10n.Current);
        }
        finally
        {
            L10n.SetLanguage(AppLanguage.Zh);
            File.Delete(path);
        }
    }

    [Fact]
    public void BootstrapFallsBackToChineseWhenConfigMissing()
    {
        L10n.SetLanguage(AppLanguage.Zh);
        L10n.Bootstrap(Path.Combine(Path.GetTempPath(), $"voicetyper-missing-{Guid.NewGuid():N}.yaml"));
        Assert.Equal(AppLanguage.Zh, L10n.Current);
    }
}
