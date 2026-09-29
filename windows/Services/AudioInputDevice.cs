using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceTyper.Services;

/// <summary>输入设备的传输类型。取值同时用于耗时日志，只含稳定枚举，不含设备名。</summary>
internal enum AudioTransport { BuiltIn, Bluetooth, Usb, Other, Unknown }

internal static class AudioTransportExtensions
{
    public static string LogName(this AudioTransport transport) => transport switch
    {
        AudioTransport.BuiltIn => "builtin",
        AudioTransport.Bluetooth => "bluetooth",
        AudioTransport.Usb => "usb",
        AudioTransport.Other => "other",
        _ => "unknown",
    };
}

/// <summary>一个可用的音频输入端点（WASAPI 采集端点）。</summary>
internal sealed record AudioDeviceInfo(string Id, string Name, AudioTransport Transport);

/// <summary>录音时选哪个输入设备，对应配置项 <c>audio.input_device</c>。</summary>
internal abstract record AudioInputPolicy
{
    /// <summary>默认：跟随系统，仅在「蓝牙耳机通话模式」场景改用内置麦克风。</summary>
    public sealed record Automatic : AudioInputPolicy;
    /// <summary>严格跟随系统默认输入。</summary>
    public sealed record SystemDefault : AudioInputPolicy;
    /// <summary>指定端点 ID（ID 不存在时回落系统默认）。</summary>
    public sealed record Device(string Id) : AudioInputPolicy;

    public static AudioInputPolicy FromConfigValue(string? value) => (value ?? "").Trim() switch
    {
        "" => new Automatic(),
        var v when v.Equals(Core.AudioConfig.Auto, StringComparison.OrdinalIgnoreCase) => new Automatic(),
        var v when v.Equals(Core.AudioConfig.System, StringComparison.OrdinalIgnoreCase) => new SystemDefault(),
        var v => new Device(v),
    };
}

/// <summary>一次录音实际使用的输入设备，供 HUD 与耗时日志展示。</summary>
internal sealed record ActiveInputDevice(string Name, AudioTransport Transport, bool SwitchedByAuto);

/// <summary>
/// 输入设备选择策略（纯函数，见 AudioInputDeviceTests）。
///
/// Windows 上的相应问题：默认<b>通信</b>设备是蓝牙耳机时，打开它的麦克风会把耳机切到
/// 「免提通话」(HFP) 模式——音质骤降到电话级，且耳机里正在播放的音乐也会一起变糟。
/// 因此 <see cref="AudioInputPolicy.Automatic"/> 只有一种情况偏离系统默认：默认输入与默认输出
/// 都是蓝牙，且存在可用的内置麦克风。输出不是蓝牙时，蓝牙输入是用户有意的选择，一律尊重。
/// </summary>
internal static class AudioInputSelector
{
    /// <returns>选中的端点 ID；<c>null</c> 表示「不指定，交给系统默认」。</returns>
    public static (string? DeviceId, bool SwitchedByAuto) Resolve(
        AudioInputPolicy policy,
        IReadOnlyList<AudioDeviceInfo> devices,
        string? defaultInputId,
        AudioTransport defaultOutputTransport)
    {
        switch (policy)
        {
            case AudioInputPolicy.Device device:
                if (devices.Any(d => d.Id == device.Id)) return (device.Id, false);
                return (defaultInputId, false);

            case AudioInputPolicy.Automatic:
                if (defaultOutputTransport != AudioTransport.Bluetooth || defaultInputId is null)
                {
                    return (defaultInputId, false);
                }
                var current = devices.FirstOrDefault(d => d.Id == defaultInputId);
                if (current?.Transport != AudioTransport.Bluetooth) return (defaultInputId, false);
                var builtIn = devices.FirstOrDefault(d => d.Transport == AudioTransport.BuiltIn);
                return builtIn is null ? (defaultInputId, false) : (builtIn.Id, true);

            default:
                return (defaultInputId, false);
        }
    }
}
