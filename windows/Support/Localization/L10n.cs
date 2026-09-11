using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace VoiceTyper.Support;

/// <summary>
/// 界面语言。中文是默认语言，英文为对等的第二语言（与 macOS <c>AppLanguage</c> 同构）。
/// </summary>
internal enum AppLanguage
{
    Zh,
    En,
}

internal static class AppLanguageExtensions
{
    public static string ToYamlValue(this AppLanguage language) => language switch
    {
        AppLanguage.En => "en",
        _ => "zh",
    };

    /// <summary>语言名用它自己的语言书写，不随当前界面语言变化。</summary>
    public static string DisplayName(this AppLanguage language) => language switch
    {
        AppLanguage.En => "English",
        _ => "中文",
    };

    public static AppLanguage Parse(string? value) => (value ?? "zh").Trim().ToLowerInvariant() switch
    {
        "en" => AppLanguage.En,
        _ => AppLanguage.Zh,
    };
}

/// <summary>
/// 运行期界面语言与中英文案表（与 macOS <c>L10n</c> 同构，键即中文原文）。
///
/// 键用中文原文而不是 <c>hud.recording</c> 这样的符号名，理由有三：中文是默认语言，
/// 查不到翻译时回落到键本身仍是正确的中文界面；新增文案只需补一处英文；覆盖率可测
/// —— <c>LocalizationTests</c> 扫描源码里的 <c>T(...)</c> / <c>F(...)</c>，漏翻直接让测试失败。
///
/// 语言在进程启动早期由 <see cref="Bootstrap"/> 定下，运行期改设置需要重启才全面生效
/// （设置页有明确提示）。
/// </summary>
internal static partial class L10n
{
    private static readonly object _gate = new();
    private static AppLanguage _current = AppLanguage.Zh;

    public static AppLanguage Current
    {
        get { lock (_gate) { return _current; } }
    }

    public static void SetLanguage(AppLanguage language)
    {
        lock (_gate) { _current = language; }
    }

    /// <summary>
    /// 进程启动早期调用：托盘菜单与设置窗口都是带着文案构造出来的，语言必须在那之前定下。
    ///
    /// 这里刻意不走 <c>ConfigStore.LoadOrCreate()</c>：那条路径会创建目录、写默认配置、
    /// 触发老配置迁移，只为读一个字段而在启动最早期产生这些副作用并不合适。读不到、
    /// 读出无法识别的值，一律回落中文。
    /// </summary>
    public static void Bootstrap(string? configPath = null)
    {
        try
        {
            var path = configPath ?? AppConstants.ConfigFilePath;
            if (!File.Exists(path)) return;

            var match = Regex.Match(
                File.ReadAllText(path),
                @"^\s*interface_language\s*:\s*[""']?(?<value>[A-Za-z_-]+)[""']?\s*$",
                RegexOptions.Multiline
            );
            if (match.Success)
            {
                SetLanguage(AppLanguageExtensions.Parse(match.Groups["value"].Value));
            }
        }
        catch (Exception ex)
        {
            // 语言读取失败绝不该阻断启动：回落中文，留一条记录即可。
            Console.Error.WriteLine($"[VoiceTyper] 读取界面语言失败，回落中文: {ex.Message}");
        }
    }

    /// <summary>配置加载/保存后调用，以 config.yaml 为准。</summary>
    public static void Apply(AppLanguage language) => SetLanguage(language);

    /// <summary>查表。查不到（尚未翻译）时回落到中文原文，界面不会出现空白或键名。</summary>
    public static string T(string zh)
    {
        if (Current == AppLanguage.Zh) return zh;
        return EnglishTable.TryGetValue(zh, out var translated) ? translated : zh;
    }

    /// <summary>带占位符的文案。中文原文即格式串，例如 <c>F("下载模型 {0}%", percent)</c>。</summary>
    public static string F(string zh, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, T(zh), arguments);

    internal static IReadOnlyDictionary<string, string> Table => EnglishTable;
}
