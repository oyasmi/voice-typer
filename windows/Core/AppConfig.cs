using System.Collections.Generic;
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

    /// <summary>0 = 常驻不卸载。</summary>
    [YamlMember(Alias = "idle_unload_minutes")]
    public int IdleUnloadMinutes { get; set; } = 5;

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
