using System;
using System.Collections.Generic;
using VoiceTyper.Services;
using VoiceTyper.Support;
using YamlDotNet.Serialization;

namespace VoiceTyper.Core;

internal sealed class AppConfig
{
    [YamlMember(Alias = "asr")]
    public AsrConfig Asr { get; set; } = new();

    [YamlMember(Alias = "llm")]
    public LlmConfig Llm { get; set; } = new();

    [YamlMember(Alias = "hotkey")]
    public HotkeyConfig Hotkey { get; set; } = new();

    [YamlMember(Alias = "ui")]
    public UIConfig UI { get; set; } = new();

    public AppConfig Clone() => new()
    {
        Asr = Asr.Clone(),
        Llm = Llm.Clone(),
        Hotkey = Hotkey.Clone(),
        UI = UI.Clone(),
    };

    /// <summary>
    /// 把手改 YAML 或历史脏数据里越界的值夹逼回合法范围，避免 <c>timeout: -1</c>、
    /// <c>opacity: 5.0</c>、<c>threads: 9999</c>、<c>idle_unload_minutes: -5</c>、
    /// 裸键热键（无修饰键，正常打字就会触发录音）之类的值进入运行态（F-13 / R2-12 / R4-05）。
    /// 越界时记一条 warning，不静默——但也不因此拒绝整份配置。<see cref="Core.ConfigStore"/>
    /// 的 load 与 save 两侧都应调用。
    /// </summary>
    public AppConfig Validated()
    {
        var config = Clone();
        config.Asr.Threads = ClampInt(config.Asr.Threads, 0, 32, "asr.threads");
        config.Asr.IdleUnloadMinutes = ClampInt(config.Asr.IdleUnloadMinutes, 0, 24 * 60, "asr.idle_unload_minutes");
        config.Asr.PreviewWindowSeconds = ClampInt(config.Asr.PreviewWindowSeconds, 0, 30, "asr.preview_window");
        config.Llm.Temperature = ClampDouble(config.Llm.Temperature, 0, 2, "llm.temperature");
        config.Llm.MaxTokens = ClampInt(config.Llm.MaxTokens, 64, 8192, "llm.max_tokens");
        config.Llm.Timeout = ClampDouble(config.Llm.Timeout, 1, 120, "llm.timeout");
        config.UI.Opacity = ClampDouble(config.UI.Opacity, 0.1, 1.0, "ui.opacity");
        config.Hotkey = ValidatedHotkey(config.Hotkey);
        return config;
    }

    /// <summary>
    /// 设置窗口的热键 Tab 已经在 UI 路径校验过"主键是否支持"与"必须搭配至少一个修饰键"，
    /// 但设置窗口不是改配置的唯一入口——托盘菜单里就有"打开配置目录"，README 也鼓励手改
    /// YAML。不经 UI 直接写一个 <c>key: "d"</c> 且不带修饰键的配置完全可达：
    /// <see cref="Services.HotkeyService"/> 只检查键名是否受支持，不要求修饰键非空，
    /// 结果是在任何应用里正常打字敲字母 d 都会触发一次录音（R4-05）。这里补上手改配置文件
    /// 绕过 UI 校验的第二道防线，越界回落到默认热键 Ctrl+F2，不静默接受。
    /// </summary>
    private static HotkeyConfig ValidatedHotkey(HotkeyConfig hotkey)
    {
        var key = (hotkey.Key ?? "").Trim().ToLowerInvariant();
        if (!HotkeyService.IsSupportedKey(key))
        {
            AppLog.Warn("config", $"配置字段 hotkey.key 不支持({hotkey.Key})，已回落为默认热键 Ctrl+F2");
            return new HotkeyConfig();
        }
        if (hotkey.Modifiers is null || hotkey.Modifiers.Count == 0)
        {
            AppLog.Warn("config", $"配置字段 hotkey 未搭配修饰键({hotkey.Key})，已回落为默认热键 Ctrl+F2");
            return new HotkeyConfig();
        }
        return hotkey;
    }

    private static int ClampInt(int value, int lower, int upper, string field)
    {
        if (value >= lower && value <= upper) return value;
        var clamped = Math.Clamp(value, lower, upper);
        AppLog.Warn("config", $"配置字段 {field} 越界({value})，已夹逼为 {clamped}");
        return clamped;
    }

    /// <summary>非有限数值（NaN/Infinity）直接重置为下限：<c>temperature: .nan</c> 会让
    /// JSON 序列化产出非法字面量，每次纠错静默失败；<c>opacity: .nan</c> 会污染窗口 alpha（R2-12）。</summary>
    private static double ClampDouble(double value, double lower, double upper, string field)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            AppLog.Warn("config", $"配置字段 {field} 不是有限数值({value})，已重置为 {lower}");
            return lower;
        }
        if (value >= lower && value <= upper) return value;
        var clamped = Math.Clamp(value, lower, upper);
        AppLog.Warn("config", $"配置字段 {field} 越界({value})，已夹逼为 {clamped}");
        return clamped;
    }
}

/// <summary>支持的 SenseVoice 识别语言。与 client-server/server/voice_typer_server/recognizer.py 的 _SENSEVOICE_LID 表一一对应。</summary>
internal enum AsrLanguage
{
    Auto,
    Zh,
    En,
    Yue,
    Ja,
    Ko,
}

internal static class AsrLanguageExtensions
{
    public static string ToYamlValue(this AsrLanguage lang) => lang switch
    {
        AsrLanguage.Auto => "auto",
        AsrLanguage.Zh => "zh",
        AsrLanguage.En => "en",
        AsrLanguage.Yue => "yue",
        AsrLanguage.Ja => "ja",
        AsrLanguage.Ko => "ko",
        _ => "auto",
    };

    public static string DisplayName(this AsrLanguage lang) => lang switch
    {
        AsrLanguage.Auto => "自动",
        AsrLanguage.Zh => "中文",
        AsrLanguage.En => "英文",
        AsrLanguage.Yue => "粤语",
        AsrLanguage.Ja => "日语",
        AsrLanguage.Ko => "韩语",
        _ => lang.ToString(),
    };

    /// <summary>SenseVoice 词表里的语言 id，与 client-server/server/voice_typer_server/recognizer.py:_SENSEVOICE_LID 保持一致。</summary>
    public static int TokenId(this AsrLanguage lang) => lang switch
    {
        AsrLanguage.Auto => 0,
        AsrLanguage.Zh => 3,
        AsrLanguage.En => 4,
        AsrLanguage.Yue => 7,
        AsrLanguage.Ja => 11,
        AsrLanguage.Ko => 12,
        _ => 0,
    };

    public static AsrLanguage Parse(string? value) => (value ?? "auto").Trim().ToLowerInvariant() switch
    {
        "zh" => AsrLanguage.Zh,
        "en" => AsrLanguage.En,
        "yue" => AsrLanguage.Yue,
        "ja" => AsrLanguage.Ja,
        "ko" => AsrLanguage.Ko,
        _ => AsrLanguage.Auto,
    };
}

internal sealed class AsrConfig
{
    [YamlMember(Alias = "language")]
    public string Language { get; set; } = "auto";

    /// <summary>0 = 自动（min(4, 核数)）。</summary>
    [YamlMember(Alias = "threads")]
    public int Threads { get; set; } = 0;

    /// <summary>留空 = 按 ModelLocator 优先级自动定位。</summary>
    [YamlMember(Alias = "model_dir")]
    public string ModelDir { get; set; } = "";

    /// <summary>秒；0 = 首次加载后按实测 RTF 自动校准。</summary>
    [YamlMember(Alias = "preview_window")]
    public int PreviewWindowSeconds { get; set; } = 0;

    /// <summary>0 = 常驻不卸载。默认值与 macOS 保持一致（W-30：此前 Windows 默认 5 分钟，
    /// macOS 默认 10 分钟，属无理由分歧）。</summary>
    [YamlMember(Alias = "idle_unload_minutes")]
    public int IdleUnloadMinutes { get; set; } = 10;

    public AsrLanguage LanguageValue
    {
        get => AsrLanguageExtensions.Parse(Language);
        set => Language = value.ToYamlValue();
    }

    public AsrConfig Clone() => new()
    {
        Language = Language,
        Threads = Threads,
        ModelDir = ModelDir,
        PreviewWindowSeconds = PreviewWindowSeconds,
        IdleUnloadMinutes = IdleUnloadMinutes,
    };
}

/// <summary>LLM 纠错配置。api_key 不落此结构 —— 存 <see cref="Core.SecretStore"/>。</summary>
internal sealed class LlmConfig
{
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = false;

    [YamlMember(Alias = "base_url")]
    public string BaseUrl { get; set; } = "";

    [YamlMember(Alias = "model")]
    public string Model { get; set; } = "gpt-4o-mini";

    [YamlMember(Alias = "temperature")]
    public double Temperature { get; set; } = 0.0;

    [YamlMember(Alias = "max_tokens")]
    public int MaxTokens { get; set; } = 800;

    [YamlMember(Alias = "timeout")]
    public double Timeout { get; set; } = 5.0;

    public LlmConfig Clone() => new()
    {
        Enabled = Enabled,
        BaseUrl = BaseUrl,
        Model = Model,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        Timeout = Timeout,
    };
}

internal sealed class HotkeyConfig
{
    [YamlMember(Alias = "modifiers")]
    public List<string> Modifiers { get; set; } = new() { "ctrl" };

    [YamlMember(Alias = "key")]
    public string Key { get; set; } = "f2";

    public HotkeyConfig Clone() => new()
    {
        Modifiers = new List<string>(Modifiers),
        Key = Key,
    };

    public string DisplayString
    {
        get
        {
            var parts = new List<string>();
            foreach (var m in Modifiers)
            {
                parts.Add(NormalizeModifierDisplay(m));
            }
            parts.Add(Key.ToUpperInvariant());
            return string.Join("+", parts);
        }
    }

    private static string NormalizeModifierDisplay(string m) => m.ToLowerInvariant() switch
    {
        "ctrl" or "control" => "Ctrl",
        "alt" or "option" => "Alt",
        "shift" => "Shift",
        "win" or "win_l" or "win_r" or "super" or "command" or "cmd" => "Win",
        _ => m,
    };
}

internal sealed class UIConfig
{
    [YamlMember(Alias = "opacity")]
    public double Opacity { get; set; } = 0.85;

    public UIConfig Clone() => new()
    {
        Opacity = Opacity,
    };
}
