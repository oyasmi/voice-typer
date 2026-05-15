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
    public string DefaultHotwordsPath => AppConstants.DefaultHotwordsPath;

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
            cfg.HotwordFiles ??= new List<string> { AppConstants.DefaultHotwordsFileName };
            if (cfg.HotwordFiles.Count == 0)
            {
                cfg.HotwordFiles.Add(AppConstants.DefaultHotwordsFileName);
            }
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
        var normalized = config.Clone();
        normalized.HotwordFiles = NormalizeHotwordFiles(normalized.HotwordFiles);
        WriteAtomically(ConfigPath, SerializeYaml(normalized));
    }

    public void EnsureDefaultFiles()
    {
        Directory.CreateDirectory(ConfigDirectory);

        if (!File.Exists(ConfigPath))
        {
            WriteAtomically(ConfigPath, DefaultConfigYaml);
        }

        if (!File.Exists(DefaultHotwordsPath))
        {
            WriteAtomically(DefaultHotwordsPath, DefaultHotwordsContent);
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

    public IReadOnlyList<string> LoadHotwords(AppConfig config)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var words = new List<string>();

        foreach (var rel in config.HotwordFiles)
        {
            var path = ResolveHotwordPath(rel);
            if (!File.Exists(path)) continue;

            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex)
            {
                AppLog.Warn("config", $"读取热词文件失败 {path}: {ex.Message}");
                continue;
            }

            foreach (var line in lines)
            {
                var word = line.Trim();
                if (string.IsNullOrEmpty(word) || word.StartsWith("#")) continue;
                if (seen.Add(word)) words.Add(word);
            }
        }

        return words;
    }

    public string LoadManagedHotwordsText(AppConfig config)
    {
        EnsureDefaultFiles();
        var path = ManagedHotwordsPath(config);
        if (!File.Exists(path))
        {
            WriteAtomically(path, DefaultHotwordsContent);
        }
        return File.ReadAllText(path);
    }

    public void SaveManagedHotwordsText(string text, AppConfig config)
    {
        EnsureDefaultFiles();
        var path = ManagedHotwordsPath(config);
        var normalized = NormalizeHotwordsText(text);
        WriteAtomically(path, normalized);
    }

    public string ManagedHotwordsPath(AppConfig config)
    {
        var files = NormalizeHotwordFiles(config.HotwordFiles);
        var first = files.FirstOrDefault() ?? AppConstants.DefaultHotwordsFileName;
        return ResolveHotwordPath(first);
    }

    public int AdditionalHotwordFileCount(AppConfig config)
    {
        var managed = Path.GetFullPath(ManagedHotwordsPath(config));
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in NormalizeHotwordFiles(config.HotwordFiles))
        {
            unique.Add(Path.GetFullPath(ResolveHotwordPath(rel)));
        }
        unique.Remove(managed);
        return unique.Count;
    }

    private string ResolveHotwordPath(string relativeOrAbsolute)
    {
        if (string.IsNullOrEmpty(relativeOrAbsolute))
        {
            return Path.Combine(ConfigDirectory, AppConstants.DefaultHotwordsFileName);
        }
        if (Path.IsPathRooted(relativeOrAbsolute)) return relativeOrAbsolute;
        return Path.Combine(ConfigDirectory, relativeOrAbsolute);
    }

    private static List<string> NormalizeHotwordFiles(IEnumerable<string>? input)
    {
        var normalized = new List<string> { AppConstants.DefaultHotwordsFileName };
        var seen = new HashSet<string>(normalized, StringComparer.OrdinalIgnoreCase);

        if (input is null) return normalized;

        foreach (var raw in input)
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !seen.Add(trimmed)) continue;
            normalized.Add(trimmed);
        }
        return normalized;
    }

    private static string NormalizeHotwordsText(string text)
    {
        var unified = text.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        return string.IsNullOrEmpty(unified) ? "" : unified + "\n";
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

        var hotwordFilesBlock = string.Join("\n", config.HotwordFiles.Select(f => $"  - {YamlString(f)}"));

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
            $"hotword_files:",
            hotwordFilesBlock,
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
        hotword_files:
          - "hotwords.txt"
        ui:
          opacity: 0.85
          width: 320
          height: 90
        """;

    private const string DefaultHotwordsContent = """
        # VoiceTyper 用户词库
        FunASR
        OpenAI
        ChatGPT
        """;
}
