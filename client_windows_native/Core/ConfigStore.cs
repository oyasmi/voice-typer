using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using VoiceTyper.Support;

namespace VoiceTyper.Core;

/// <summary>
/// 配置管理器。
/// 配置文件路径与 Python 版完全一致: %APPDATA%\voice_typer\config.yaml
/// 用户可在 Python 版和原生版之间无缝切换。
/// </summary>
internal sealed class ConfigStore
{
    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Constants.ConfigDirectoryName);

    public static string ConfigPath => Path.Combine(ConfigDir, "config.yaml");

    public static string DefaultHotwordsPath => Path.Combine(ConfigDir, "hotwords.txt");

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <summary>加载或创建配置（含词库）</summary>
    public (AppConfig Config, List<string> Hotwords) LoadOrCreate()
    {
        EnsureDefaultFiles();

        var config = LoadConfig();
        var hotwords = LoadHotwords(config.HotwordFiles);

        return (config, hotwords);
    }

    /// <summary>保存配置到文件（原子写入）</summary>
    public void Save(AppConfig config)
    {
        try
        {
            var yaml = Serializer.Serialize(config);
            var tempPath = ConfigPath + ".tmp";
            File.WriteAllText(tempPath, yaml);
            File.Move(tempPath, ConfigPath, overwrite: true);
            AppLog.Info("配置已保存");
        }
        catch (Exception ex)
        {
            // 清理临时文件
            try { File.Delete(ConfigPath + ".tmp"); } catch { }
            AppLog.Error("保存配置失败", ex);
        }
    }

    /// <summary>加载词库文件</summary>
    public List<string> LoadHotwords(List<string>? hotwordFiles)
    {
        var words = new List<string>();
        var seen = new HashSet<string>();

        if (hotwordFiles == null || hotwordFiles.Count == 0)
            return words;

        foreach (var filePath in hotwordFiles)
        {
            var path = Path.IsPathRooted(filePath)
                ? filePath
                : Path.Combine(ConfigDir, filePath);

            try
            {
                if (!File.Exists(path)) continue;

                foreach (var line in File.ReadAllLines(path))
                {
                    var word = line.Trim();
                    if (!string.IsNullOrEmpty(word) && !word.StartsWith('#') && seen.Add(word))
                        words.Add(word);
                }

                AppLog.Info($"加载词库: {path} ({words.Count} 词)");
            }
            catch (Exception ex)
            {
                AppLog.Warning($"加载词库失败 {path}: {ex.Message}");
            }
        }

        return words;
    }

    private AppConfig LoadConfig()
    {
        try
        {
            var yaml = File.ReadAllText(ConfigPath);
            return Deserializer.Deserialize<AppConfig>(yaml) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            AppLog.Error("加载配置失败，使用默认配置", ex);
            return new AppConfig();
        }
    }

    private void EnsureDefaultFiles()
    {
        Directory.CreateDirectory(ConfigDir);

        if (!File.Exists(ConfigPath))
        {
            File.WriteAllText(ConfigPath, DefaultConfigContent);
            AppLog.Info($"已创建默认配置: {ConfigPath}");
        }

        if (!File.Exists(DefaultHotwordsPath))
        {
            File.WriteAllText(DefaultHotwordsPath, DefaultHotwordsContent);
            AppLog.Info($"已创建默认词库: {DefaultHotwordsPath}");
        }
    }

    private const string DefaultConfigContent = """
        # VoiceTyper 客户端配置

        # 语音识别服务地址
        server:
          host: "127.0.0.1"
          port: 6008
          timeout: 60.0
          api_key: ""          # 设置API密钥用于连接远程服务器，本地可留空
          llm_recorrect: true  # 是否启用 LLM 修正识别错误（需要服务端支持）

        # 热键配置
        # 支持的修饰键: ctrl, alt, shift, win_l, win_r
        hotkey:
          modifiers:
            - "ctrl"
          key: "f2"

        # 用户词库文件（相对于配置目录）
        hotword_files:
          - "hotwords.txt"

        # UI 配置
        ui:
          opacity: 0.85
          width: 240
          height: 70
        """;

    private const string DefaultHotwordsContent = """
        # VoiceTyper 用户自定义词库
        # 每行一个词，支持中英文
        # 以 # 开头的行为注释

        # 技术术语示例
        FunASR
        Python
        GitHub
        OpenAI
        ChatGPT

        # 在下方添加你的自定义词汇...
        """;
}
