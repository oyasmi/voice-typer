using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VoiceTyper.Support;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VoiceTyper.Core;

internal sealed class ConfigStore
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    public string ConfigDirectory => AppConstants.ConfigDirectory;
    public string ConfigPath => AppConstants.ConfigFilePath;

    /// <summary>不存在时写入默认配置；否则按现状解析。</summary>
    public AppConfig LoadOrCreate()
    {
        EnsureDefaultFiles();

        var content = File.ReadAllText(ConfigPath);
        try
        {
            var cfg = _deserializer.Deserialize<AppConfig>(content) ?? new AppConfig();
            cfg.Server ??= new ServerConfig();
            cfg.Hotkey ??= new HotkeyConfig();
            cfg.UI ??= new UIConfig();
            return cfg;
        }
        catch (Exception ex)
        {
            AppLog.Error("config", "配置解析失败，回落为默认值", ex);
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        EnsureDefaultFiles();
        WriteAtomically(ConfigPath, SerializeYaml(config.Clone()));
    }

    public void EnsureDefaultFiles()
    {
        Directory.CreateDirectory(ConfigDirectory);

        if (!File.Exists(ConfigPath))
        {
            WriteAtomically(ConfigPath, DefaultConfigYaml);
        }
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
            // File.Replace 在跨卷场景或目标不存在时失败，前面已 check
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private static string SerializeYaml(AppConfig config)
    {
        // 自己拼字符串，控制注释/字段顺序、与 macOS 版风格一致。
        var modifiersBlock = config.Hotkey.Modifiers.Count == 0
            ? "  modifiers: []"
            : "  modifiers:\n" + string.Join("\n", config.Hotkey.Modifiers.Select(m => $"    - {YamlString(m)}"));

        return string.Join("\n",
            $"server:",
            $"  host: {YamlString(config.Server.Host)}",
            $"  port: {config.Server.Port}",
            $"  timeout: {YamlNumber(config.Server.Timeout)}",
            $"  api_key: {YamlString(config.Server.ApiKey)}",
            $"  llm_recorrect: {YamlBool(config.Server.LlmRecorrect)}",
            $"  streaming: {YamlBool(config.Server.Streaming)}",
            $"hotkey:",
            modifiersBlock,
            $"  key: {YamlString(config.Hotkey.Key)}",
            $"ui:",
            $"  opacity: {YamlNumber(config.UI.Opacity)}",
            $"  width: {YamlNumber(config.UI.Width)}",
            $"  height: {YamlNumber(config.UI.Height)}",
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
            return ((long)Math.Round(value)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
    }

    private const string DefaultConfigYaml = """
        # VoiceTyper 客户端配置（Windows 原生客户端默认值）
        server:
          host: "127.0.0.1"
          port: 6008
          timeout: 60
          api_key: ""
          llm_recorrect: true
          streaming: true
        hotkey:
          modifiers:
            - "ctrl"
          key: "f2"
        ui:
          opacity: 0.85
          width: 320
          height: 90
        """;
}
