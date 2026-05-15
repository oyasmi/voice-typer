using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace VoiceTyper.Core;

internal sealed class AppConfig
{
    [YamlMember(Alias = "server")]
    public ServerConfig Server { get; set; } = new();

    [YamlMember(Alias = "hotkey")]
    public HotkeyConfig Hotkey { get; set; } = new();

    [YamlMember(Alias = "hotword_files")]
    public List<string> HotwordFiles { get; set; } = new() { Support.AppConstants.DefaultHotwordsFileName };

    [YamlMember(Alias = "ui")]
    public UIConfig UI { get; set; } = new();

    public AppConfig Clone() => new()
    {
        Server = Server.Clone(),
        Hotkey = Hotkey.Clone(),
        HotwordFiles = new List<string>(HotwordFiles),
        UI = UI.Clone(),
    };
}

internal sealed class ServerConfig
{
    [YamlMember(Alias = "host")]
    public string Host { get; set; } = "127.0.0.1";

    [YamlMember(Alias = "port")]
    public int Port { get; set; } = 6008;

    [YamlMember(Alias = "timeout")]
    public double Timeout { get; set; } = 60.0;

    [YamlMember(Alias = "api_key")]
    public string ApiKey { get; set; } = "";

    [YamlMember(Alias = "llm_recorrect")]
    public bool LlmRecorrect { get; set; } = true;

    [YamlMember(Alias = "streaming")]
    public bool Streaming { get; set; } = true;

    public ServerConfig Clone() => new()
    {
        Host = Host,
        Port = Port,
        Timeout = Timeout,
        ApiKey = ApiKey,
        LlmRecorrect = LlmRecorrect,
        Streaming = Streaming,
    };
}

internal sealed class HotkeyConfig
{
    [YamlMember(Alias = "modifiers")]
    public List<string> Modifiers { get; set; } = new() { "ctrl" };

    [YamlMember(Alias = "key")]
    public string Key { get; set; } = "f2";

    public HotkeyConfig Clone() => new()
    {
        Modifiers = new List<string>(Modifiers),
        Key = Key,
    };

    public string DisplayString
    {
        get
        {
            var parts = new List<string>();
            foreach (var m in Modifiers)
            {
                parts.Add(NormalizeModifierDisplay(m));
            }
            parts.Add(Key.ToUpperInvariant());
            return string.Join("+", parts);
        }
    }

    private static string NormalizeModifierDisplay(string m) => m.ToLowerInvariant() switch
    {
        "ctrl" or "control" => "Ctrl",
        "alt" or "option" => "Alt",
        "shift" => "Shift",
        "win" or "win_l" or "win_r" or "super" or "command" or "cmd" => "Win",
        _ => m,
    };
}

internal sealed class UIConfig
{
    [YamlMember(Alias = "opacity")]
    public double Opacity { get; set; } = 0.85;

    [YamlMember(Alias = "width")]
    public double Width { get; set; } = 320;

    [YamlMember(Alias = "height")]
    public double Height { get; set; } = 90;

    public UIConfig Clone() => new()
    {
        Opacity = Opacity,
        Width = Width,
        Height = Height,
    };
}
