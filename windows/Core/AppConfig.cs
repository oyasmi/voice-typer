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

    [YamlMember(Alias = "audio")]
    public AudioConfig Audio { get; set; } = new();

    [YamlMember(Alias = "ui")]
    public UIConfig UI { get; set; } = new();

    public AppConfig Clone() => new()
    {
        Asr = Asr.Clone(),
        Llm = Llm.Clone(),
        Hotkey = Hotkey.Clone(),
        Audio = Audio.Clone(),
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
        config.Asr.Threads = ClampInt(config.Asr.Threads, ConfigLimits.ThreadsMin, ConfigLimits.ThreadsMax, "asr.threads");
        config.Asr.IdleUnloadMinutes = ClampInt(config.Asr.IdleUnloadMinutes, ConfigLimits.IdleUnloadMinutesMin, ConfigLimits.IdleUnloadMinutesMax, "asr.idle_unload_minutes");
        config.Asr.PreviewWindowSeconds = ClampInt(config.Asr.PreviewWindowSeconds, ConfigLimits.PreviewWindowSecondsMin, ConfigLimits.PreviewWindowSecondsMax, "asr.preview_window");
        config.Llm.Temperature = ClampDouble(config.Llm.Temperature, ConfigLimits.TemperatureMin, ConfigLimits.TemperatureMax, "llm.temperature");
        config.Llm.MaxTokens = ClampInt(config.Llm.MaxTokens, ConfigLimits.MaxTokensMin, ConfigLimits.MaxTokensMax, "llm.max_tokens");
        config.Llm.Timeout = ClampDouble(config.Llm.Timeout, ConfigLimits.TimeoutSecondsMin, ConfigLimits.TimeoutSecondsMax, "llm.timeout");
        config.UI.Opacity = ClampDouble(config.UI.Opacity, ConfigLimits.OpacityMin, ConfigLimits.OpacityMax, "ui.opacity");
        config.Hotkey = ValidatedHotkey(config.Hotkey);
        config.Audio ??= new AudioConfig();
        config.Audio.InputDevice = string.IsNullOrWhiteSpace(config.Audio.InputDevice)
            ? AudioConfig.Auto
            : config.Audio.InputDevice.Trim();
        // 无法识别的 hud_position 回落默认，与 mode 的容错一致；不打 warning：手改成未知值不是错误。
        config.UI.HudPositionValue = HudPlacementExtensions.Parse(config.UI.HudPosition);
        return config;
    }

    /// <summary>
    /// 设置窗口的热键 Tab 已经在 UI 路径校验过"主键是否支持"与"必须搭配至少一个修饰键"，
    /// 但用户仍可能直接编辑配置文件。不经 UI 直接写一个 <c>key: "d"</c> 且不带修饰键的配置完全可达：
    /// <see cref="Services.HotkeyService"/> 只检查键名是否受支持，不要求修饰键非空，
    /// 结果是在任何应用里正常打字敲字母 d 都会触发一次录音（R4-05）。这里补上手改配置文件
    /// 绕过 UI 校验的第二道防线，越界回落到默认热键 Ctrl+F2，不静默接受。
    /// </summary>
    private static HotkeyConfig ValidatedHotkey(HotkeyConfig hotkey)
    {
        // 回落只针对"按哪个键"这件事；mode（按住 / 切换）是独立且始终合法的用户选择，
        // 不应被一个非法键名连坐重置（对齐 macOS validatedHotkey）。
        var mode = HotkeyModeExtensions.Parse(hotkey.Mode);
        var key = (hotkey.Key ?? "").Trim().ToLowerInvariant();
        if (!HotkeyService.IsSupportedKey(key))
        {
            AppLog.Warn("config", $"配置字段 hotkey.key 不支持({hotkey.Key})，已回落为默认热键 Ctrl+F2");
            return new HotkeyConfig { ModeValue = mode };
        }
        // 单独修饰键（右 Ctrl / 右 Alt）本身就是完整热键；手改配置里多写的 modifiers 会让识别器永远
        // 对不上，忽略并记 warning。
        if (ModifierHotkeys.IsModifierOnlyKey(key))
        {
            if (hotkey.Modifiers is { Count: > 0 })
            {
                AppLog.Warn("config", $"配置字段 hotkey.modifiers 与单独修饰键({key})同时出现，已忽略 modifiers");
            }
            return new HotkeyConfig { Modifiers = new List<string>(), Key = key, ModeValue = mode };
        }
        var modifiers = NormalizeModifiers(hotkey.Modifiers);
        if (modifiers.Count == 0)
        {
            // 含"写了修饰键但全是未知值"：HotkeyService 会忽略未知值，放行就会退化成裸主键全局热键。
            AppLog.Warn("config", $"配置字段 hotkey 未搭配有效修饰键({hotkey.Key})，已回落为默认热键 Ctrl+F2");
            return new HotkeyConfig { ModeValue = mode };
        }
        return new HotkeyConfig { Modifiers = modifiers, Key = key, ModeValue = mode };
    }

    /// <summary>
    /// 把修饰键规范化为 <c>ctrl/alt/shift/win</c> 并去重（保持首次出现的顺序）；别名与
    /// <see cref="Services.HotkeyService"/> 的识别范围一致。未知值记 warning 后丢弃。
    /// </summary>
    internal static List<string> NormalizeModifiers(IEnumerable<string>? modifiers)
    {
        var result = new List<string>();
        foreach (var raw in modifiers ?? Array.Empty<string>())
        {
            var canonical = (raw ?? "").Trim().ToLowerInvariant() switch
            {
                "ctrl" or "control" => "ctrl",
                "alt" or "option" => "alt",
                "shift" => "shift",
                "win" or "win_l" or "win_r" or "super" or "command" or "cmd" => "win",
                _ => null,
            };
            if (canonical is null)
            {
                AppLog.Warn("config", $"配置字段 hotkey.modifiers 含未知修饰键({raw})，已丢弃");
                continue;
            }
            if (!result.Contains(canonical)) result.Add(canonical);
        }
        return result;
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

/// <summary>
/// 配置数值范围的唯一来源：<see cref="AppConfig.Validated"/> 的夹逼与设置页控件的范围共用，
/// 避免控件比配置更窄，打开合法配置即被夹逼并误判为"已修改"。
/// </summary>
internal static class ConfigLimits
{
    public const int ThreadsMin = 0;
    public const int ThreadsMax = 32;
    public const int IdleUnloadMinutesMin = 0;
    public const int IdleUnloadMinutesMax = 24 * 60;
    public const int PreviewWindowSecondsMin = 0;
    public const int PreviewWindowSecondsMax = 30;
    public const double TemperatureMin = 0;
    public const double TemperatureMax = 2;
    public const int MaxTokensMin = 64;
    public const int MaxTokensMax = 8192;
    public const double TimeoutSecondsMin = 1;
    public const double TimeoutSecondsMax = 120;
    public const double OpacityMin = 0.1;
    public const double OpacityMax = 1.0;
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
        AsrLanguage.Auto => L10n.T("自动"),
        AsrLanguage.Zh => L10n.T("中文"),
        AsrLanguage.En => L10n.T("英文"),
        AsrLanguage.Yue => L10n.T("粤语"),
        AsrLanguage.Ja => L10n.T("日语"),
        AsrLanguage.Ko => L10n.T("韩语"),
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

    /// <summary>0 = 常驻不卸载。默认值与 macOS「预加载默认开启 + 空闲默认从不卸载」的配套决策
    /// 保持一致——按热键始终零等待。内存紧张的机器可改回定时卸载。</summary>
    [YamlMember(Alias = "idle_unload_minutes")]
    public int IdleUnloadMinutes { get; set; } = 0;

    /// <summary>启动时就把模型载入内存（默认开启，对齐 macOS 3.3.1）：多数用户是"每天高频使用"，
    /// 启动即加载让首次按热键零等待。关掉之后首次按热键才加载，且加载与录音并行
    /// （<see cref="Asr.AsrService.MakeSession"/>），用户通常正在说第一句话，感知延迟也接近于零；
    /// 适合"今天可能一次都不用听写、且在意常驻内存"的场景。</summary>
    [YamlMember(Alias = "preload_on_launch")]
    public bool PreloadOnLaunch { get; set; } = true;

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
        PreloadOnLaunch = PreloadOnLaunch,
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

/// <summary>
/// 热键的触发语义。默认 <see cref="Hold"/>（按住说话）；<see cref="Toggle"/> 为长听写准备：
/// 按一次开始、再按一次结束。刻意不做"短按 toggle / 长按 hold"的自动判别——那会把被最短录音
/// 时长当成误触丢弃的一次轻碰，变成用户毫无察觉就开始的持续录音（对齐 macOS HotkeyMode）。
/// </summary>
internal enum HotkeyMode { Hold, Toggle }

internal static class HotkeyModeExtensions
{
    public static string ToYamlValue(this HotkeyMode mode) => mode == HotkeyMode.Toggle ? "toggle" : "hold";

    /// <summary>无法识别的取值回落 Hold，而不是解析失败——与本文件其余字段的容错一致。</summary>
    public static HotkeyMode Parse(string? value) =>
        (value ?? "hold").Trim().ToLowerInvariant() == "toggle" ? HotkeyMode.Toggle : HotkeyMode.Hold;

    public static string DisplayName(this HotkeyMode mode) => mode == HotkeyMode.Toggle
        ? L10n.T("按一次开始，再按一次结束")
        : L10n.T("按住说话");
}

internal sealed class HotkeyConfig
{
    [YamlMember(Alias = "modifiers")]
    public List<string> Modifiers { get; set; } = new() { "ctrl" };

    [YamlMember(Alias = "key")]
    public string Key { get; set; } = "f2";

    [YamlMember(Alias = "mode")]
    public string Mode { get; set; } = "hold";

    public HotkeyMode ModeValue
    {
        get => HotkeyModeExtensions.Parse(Mode);
        set => Mode = value.ToYamlValue();
    }

    /// <summary>热键是单独的修饰键（右 Ctrl / 右 Alt）：干净单击即触发，按住它再按别的键或点鼠标则
    /// 作为普通快捷键使用（本次录音被静默丢弃）。</summary>
    public bool IsModifierOnly => ModifierHotkeys.IsModifierOnlyKey(Key);

    public HotkeyConfig Clone() => new()
    {
        Modifiers = new List<string>(Modifiers),
        Key = Key,
        Mode = Mode,
    };

    public string DisplayString
    {
        get
        {
            if (ModifierHotkeys.DisplayName(Key) is { } modifierName) return modifierName;
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

/// <summary>录音输入设备。</summary>
internal sealed class AudioConfig
{
    /// <summary>默认：跟随系统默认输入，仅在「蓝牙耳机通话模式」场景改用非蓝牙麦克风。</summary>
    public const string Auto = "auto";
    /// <summary>严格跟随系统默认输入。</summary>
    public const string System = "system";

    /// <summary><c>auto</c> / <c>system</c> / 音频端点 ID（设置页里手选的设备；ID 不存在时回落系统默认）。</summary>
    [YamlMember(Alias = "input_device")]
    public string InputDevice { get; set; } = Auto;

    public AudioConfig Clone() => new() { InputDevice = InputDevice };
}

/// <summary>HUD 浮窗的落点。</summary>
internal enum HudPlacement { BottomCenter, BottomRight, NearCursor, Hidden }

internal static class HudPlacementExtensions
{
    public static string ToYamlValue(this HudPlacement position) => position switch
    {
        HudPlacement.BottomRight => "bottom_right",
        HudPlacement.NearCursor => "near_cursor",
        HudPlacement.Hidden => "hidden",
        _ => "bottom_center",
    };

    public static HudPlacement Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "bottom_right" => HudPlacement.BottomRight,
        "near_cursor" => HudPlacement.NearCursor,
        "hidden" => HudPlacement.Hidden,
        _ => HudPlacement.BottomCenter,
    };

    public static string DisplayName(this HudPlacement position) => position switch
    {
        HudPlacement.BottomRight => L10n.T("右下角"),
        HudPlacement.NearCursor => L10n.T("跟随光标"),
        HudPlacement.Hidden => L10n.T("不显示（仅出错时提示）"),
        _ => L10n.T("底部居中"),
    };
}

internal sealed class UIConfig
{
    [YamlMember(Alias = "opacity")]
    public double Opacity { get; set; } = 0.85;

    /// <summary>浮窗落点。"不显示"的语义是不显示<b>过程</b>，不是什么都不显示：错误与"没有识别到内容"
    /// 这类提示仍会浮出来——那些信息在别处拿不到，一并静音会把可配置项变成静默失败的陷阱。</summary>
    [YamlMember(Alias = "hud_position")]
    public string HudPosition { get; set; } = "bottom_center";

    public HudPlacement HudPositionValue
    {
        get => HudPlacementExtensions.Parse(HudPosition);
        set => HudPosition = value.ToYamlValue();
    }

    /// <summary>界面语言（zh / en）。默认中文；改动需要重启应用才全面生效（设置页已明示）。</summary>
    [YamlMember(Alias = "interface_language")]
    public string InterfaceLanguage { get; set; } = "zh";

    public AppLanguage InterfaceLanguageValue
    {
        get => AppLanguageExtensions.Parse(InterfaceLanguage);
        set => InterfaceLanguage = value.ToYamlValue();
    }

    public UIConfig Clone() => new()
    {
        Opacity = Opacity,
        HudPosition = HudPosition,
        InterfaceLanguage = InterfaceLanguage,
    };
}
