using System;
using System.IO;
using VoiceTyper.Support;
using YamlDotNet.Serialization;

namespace VoiceTyper.Core;

/// <summary>
/// 首次启动一次性迁移：若新配置路径（%APPDATA%\VoiceTyper）尚无配置文件，
/// 而老客户端（client_windows_native / VoiceTyperClient）的 <c>%APPDATA%\voice_typer\config.yaml</c>
/// 存在，则继承其中的 hotkey 与 ui.opacity —— 老用户换过来热键不用重设。
///
/// server 段（地址/端口/API Key/streaming）在新 App 里没有对应概念，直接丢弃。
/// </summary>
internal static class ConfigMigrator
{
    private sealed class LegacyHotkey
    {
        public System.Collections.Generic.List<string>? Modifiers { get; set; }
        public string? Key { get; set; }
    }

    private sealed class LegacyUI
    {
        public double? Opacity { get; set; }
    }

    private sealed class LegacyConfig
    {
        public LegacyHotkey? Hotkey { get; set; }
        public LegacyUI? Ui { get; set; }
    }

    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    public static AppConfig? MigrateFromLegacyClientConfig()
    {
        var legacyPath = AppConstants.LegacyConfigFilePath;
        if (!File.Exists(legacyPath)) return null;

        LegacyConfig? legacy;
        try
        {
            var content = File.ReadAllText(legacyPath);
            legacy = _deserializer.Deserialize<LegacyConfig>(content);
        }
        catch (Exception ex)
        {
            AppLog.Warn("config", $"解析旧客户端配置失败，跳过迁移: {ex.Message}");
            return null;
        }

        if (legacy is null) return null;

        var config = new AppConfig();
        if (legacy.Hotkey is not null)
        {
            config.Hotkey = new HotkeyConfig
            {
                Modifiers = legacy.Hotkey.Modifiers ?? new System.Collections.Generic.List<string>(),
                Key = legacy.Hotkey.Key ?? "f2",
            };
        }
        if (legacy.Ui?.Opacity is { } opacity)
        {
            config.UI.Opacity = opacity;
        }

        AppLog.Info("config", $"已从旧客户端配置迁移热键与 HUD 透明度: {legacyPath}");
        return config;
    }
}
