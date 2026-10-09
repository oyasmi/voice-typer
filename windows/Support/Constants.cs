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

    /// <summary>低于此线性 RMS 视为"基本没有采到声音"（约 -48 dBFS）。两处共用同一个门限，语义才一致：
    /// <c>LocalAsrSession</c> 用它跳过"这一轮全是静音"的预览（省 CPU）；控制器用它判断"录了一会儿
    /// 还是一片死寂"，提示用户检查输入设备。判错的代价都只是提示措辞或多跑一次推理，不影响最终识别结果，
    /// 因此用最简单的能量门限而不是真正的 VAD。</summary>
    public const float SilenceRmsThreshold = 0.004f;

    public const string RepositoryUrl = "https://github.com/oyasmi/voice-typer";

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
    /// <summary>日志放本机目录而非漫游目录：它只对本机排障有意义，不该随域账户漫游到别的机器。</summary>
    public static string LogDirectory => Path.Combine(LocalDataDirectory, "logs");
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
