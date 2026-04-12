namespace VoiceTyper.Core;

/// <summary>
/// 配置数据模型。属性名使用 PascalCase，
/// 配合 YamlDotNet 的 UnderscoredNamingConvention 自动映射到 snake_case YAML 键名。
/// 与 Python 版和 macOS Swift 版的 config.yaml 格式完全兼容。
/// </summary>

internal sealed class ServerConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6008;
    public double Timeout { get; set; } = 60.0;
    public string? ApiKey { get; set; }
    public bool LlmRecorrect { get; set; } = true;
}

internal sealed class HotkeyConfig
{
    public List<string> Modifiers { get; set; } = new() { "ctrl" };
    public string Key { get; set; } = "f2";
}

internal sealed class UiConfig
{
    public double Opacity { get; set; } = 0.85;
    public int Width { get; set; } = 240;
    public int Height { get; set; } = 70;
}

internal sealed class AppConfig
{
    public ServerConfig Server { get; set; } = new();
    public HotkeyConfig Hotkey { get; set; } = new();
    public List<string> HotwordFiles { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
}
