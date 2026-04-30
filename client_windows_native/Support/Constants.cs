using System.Reflection;

namespace VoiceTyper.Support;

/// <summary>应用常量</summary>
internal static class Constants
{
    public const string AppName = "VoiceTyper";
    public const string BundleIdentifier = "com.voicetyper.app";
    public static readonly string Version =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "2.1.0";

    public const double MinimumRecordingDuration = 0.3;
    public const int TargetSampleRate = 16000;
    public const string ConfigDirectoryName = "voice_typer";
}
