using System;
using System.IO;
using System.Reflection;

namespace VoiceTyper.Support;

internal static class AppConstants
{
    public const string AppName = "VoiceTyper";
    public const string ConfigDirectoryName = "voice_typer";
    public const string ConfigFileName = "config.yaml";
    public const string DefaultHotwordsFileName = "hotwords.txt";
    public const string LogFileName = "client.log";

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

    public static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ConfigDirectoryName
    );

    public static string ConfigFilePath => Path.Combine(ConfigDirectory, ConfigFileName);
    public static string DefaultHotwordsPath => Path.Combine(ConfigDirectory, DefaultHotwordsFileName);
    public static string LogFilePath => Path.Combine(ConfigDirectory, LogFileName);
}
