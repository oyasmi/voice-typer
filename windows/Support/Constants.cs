using System;
using System.IO;
using System.Reflection;

namespace VoiceTyper.Support;

internal static class AppConstants
{
    public const string AppName = "VoiceTyper";
    public const string ConfigDirectoryName = "VoiceTyper";
    public const string ConfigFileName = "config.yaml";
    public const string LogFileName = "app.log";
    public const string SecretFileName = "llm_api_key.dat";
    public const string ModelSubdirectory = "models\\sensevoice-small";

    /// <summary>老客户端（client_windows_native，改名 VoiceTyperClient 后）的配置目录名，不变。</summary>
    public const string LegacyConfigDirectoryName = "voice_typer";

    public const int TargetSampleRate = 16_000;
    public const int ChunkSamples = 9_600; // 600ms @ 16kHz

    public static string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (attr is not null)
            {
                var raw = attr.InformationalVersion;
                var plus = raw.IndexOf('+');
                return plus > 0 ? raw[..plus] : raw;
            }
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    /// <summary>漫游配置目录：热键/UI 配置等小文件，换机器应跟随用户漫游。</summary>
    public static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ConfigDirectoryName
    );

    public static string ConfigFilePath => Path.Combine(ConfigDirectory, ConfigFileName);
    public static string LogDirectory => Path.Combine(ConfigDirectory, "logs");
    public static string LogFilePath => Path.Combine(LogDirectory, LogFileName);
    public static string SecretFilePath => Path.Combine(ConfigDirectory, SecretFileName);

    /// <summary>
    /// 非漫游本地目录：模型下载落点（230MB 级）绝不能跟着域漫游配置文件走。
    /// </summary>
    public static string LocalDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ConfigDirectoryName
    );

    public static string ModelDownloadDestination => Path.Combine(LocalDataDirectory, ModelSubdirectory);

    /// <summary>Python 服务端下载模型的缓存位置；跑过 client-server/server/ 的机器可零下载复用。</summary>
    public static string ModelScopeCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "modelscope", "hub", "models", "iic", "SenseVoiceSmall-onnx"
    );

    public static string LegacyConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        LegacyConfigDirectoryName,
        "config.yaml"
    );
}
