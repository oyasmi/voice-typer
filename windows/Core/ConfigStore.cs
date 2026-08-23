using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using VoiceTyper.Support;
using YamlDotNet.Serialization;

namespace VoiceTyper.Core;

internal sealed class ConfigStore
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    public string ConfigDirectory => AppConstants.ConfigDirectory;
    public string ConfigPath => AppConstants.ConfigFilePath;

    /// <summary>
    /// 不存在时：若能从老客户端（voice_typer）迁移热键/透明度则据此生成，否则写入默认配置。
    /// 存在时按现状解析。
    /// </summary>
    public AppConfig LoadOrCreate()
    {
        Directory.CreateDirectory(ConfigDirectory);

        if (!File.Exists(ConfigPath))
        {
            var migrated = (ConfigMigrator.MigrateFromLegacyClientConfig() ?? new AppConfig()).Validated();
            WriteAtomically(ConfigPath, SerializeYaml(migrated));
            return migrated;
        }

        var content = File.ReadAllText(ConfigPath);
        try
        {
            var cfg = _deserializer.Deserialize<AppConfig>(content) ?? new AppConfig();
            cfg.Asr ??= new AsrConfig();
            cfg.Llm ??= new LlmConfig();
            cfg.Hotkey ??= new HotkeyConfig();
            cfg.UI ??= new UIConfig();
            return cfg.Validated();
        }
        catch (Exception ex)
        {
            AppLog.Error("config", "配置解析失败，回落为默认值", ex);
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);
        WriteAtomically(ConfigPath, SerializeYaml(config.Validated()));
    }

    public void OpenConfigDirectory()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ConfigDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("config", $"打开配置目录失败: {ex.Message}");
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new System.Text.UTF8Encoding(false));

        if (File.Exists(path))
        {
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private static string SerializeYaml(AppConfig config)
    {
        var modifiersBlock = config.Hotkey.Modifiers.Count == 0
            ? "  modifiers: []"
            : "  modifiers:\n" + string.Join("\n", config.Hotkey.Modifiers.Select(m => $"    - {YamlString(m)}"));

        return string.Join("\n",
            "asr:",
            $"  language: {YamlString(config.Asr.Language)}",
            $"  threads: {config.Asr.Threads}",
            $"  model_dir: {YamlString(config.Asr.ModelDir)}",
            $"  preview_window: {config.Asr.PreviewWindowSeconds}",
            $"  idle_unload_minutes: {config.Asr.IdleUnloadMinutes}",
            "llm:",
            $"  enabled: {YamlBool(config.Llm.Enabled)}",
            $"  base_url: {YamlString(config.Llm.BaseUrl)}",
            $"  model: {YamlString(config.Llm.Model)}",
            $"  temperature: {YamlNumber(config.Llm.Temperature)}",
            $"  max_tokens: {config.Llm.MaxTokens}",
            $"  timeout: {YamlNumber(config.Llm.Timeout)}",
            "hotkey:",
            modifiersBlock,
            $"  key: {YamlString(config.Hotkey.Key)}",
            "ui:",
            $"  opacity: {YamlNumber(config.UI.Opacity)}",
            ""
        );
    }

    private static string YamlString(string value)
    {
        var escaped = (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private static string YamlBool(bool value) => value ? "true" : "false";

    private static string YamlNumber(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 1e-7)
        {
            return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        }
        return value.ToString("G", CultureInfo.InvariantCulture);
    }
}
