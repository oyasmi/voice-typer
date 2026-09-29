using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using VoiceTyper.Support;

namespace VoiceTyper.Services;

/// <summary>
/// WASAPI 音频端点的枚举与分类（薄封装：任何 COM 失败都返回空 / Unknown，不抛异常）。
/// 选择策略本身是纯函数，见 <see cref="AudioInputSelector"/>。
///
/// <b>分类依据尚未在真机上核实</b>：端点属性库里的 <c>PKEY_Device_EnumeratorName</c> 是否总能给出
/// 底层总线（BTHENUM / USB / HDAUDIO …）取决于驱动，读不到时退回按名称关键字判断。
/// 每次录音会把分类结果（枚举值，不含设备名）写进耗时日志，便于真机核对。
/// </summary>
internal static class AudioDeviceCatalog
{
    // PKEY_Device_EnumeratorName {A45C254E-DF1C-4EFD-8020-67D146A850E0},24
    private static readonly PropertyKey EnumeratorNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 24);
    // PKEY_AudioEndpoint_FormFactor {1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E},0
    private static readonly PropertyKey FormFactorKey = new(new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 0);

    private const uint FormFactorMicrophone = 4;

    /// <summary>纯函数分类，见 AudioInputDeviceTests。</summary>
    internal static AudioTransport ClassifyTransport(string? enumeratorName, string? friendlyName, uint? formFactor)
    {
        var bus = (enumeratorName ?? "").Trim().ToUpperInvariant();
        if (bus.Contains("BTH")) return AudioTransport.Bluetooth;
        if (bus.Contains("USB")) return AudioTransport.Usb;

        var name = (friendlyName ?? "").ToLowerInvariant();
        // 总线读不到时才看名称。"Hands-Free" 是蓝牙耳机通话端点的固定叫法，中文系统里是"免提"。
        if (bus.Length == 0)
        {
            if (name.Contains("bluetooth") || name.Contains("hands-free") || name.Contains("handsfree")
                || name.Contains("蓝牙") || name.Contains("免提")) return AudioTransport.Bluetooth;
            if (name.Contains("usb")) return AudioTransport.Usb;
            return AudioTransport.Unknown;
        }

        // 已知是板载总线：只有「麦克风」形态才当作内置麦克风（排除立体声混音等线路输入）。
        return formFactor == FormFactorMicrophone ? AudioTransport.BuiltIn : AudioTransport.Other;
    }

    public static AudioDeviceInfo Describe(MMDevice device)
    {
        string name;
        try { name = device.FriendlyName ?? ""; } catch { name = ""; }
        return new AudioDeviceInfo(device.ID, name, TransportOf(device, name));
    }

    private static AudioTransport TransportOf(MMDevice device, string name)
    {
        string? bus = null;
        uint? formFactor = null;
        try
        {
            var props = device.Properties;
            if (props.Contains(EnumeratorNameKey)) bus = props[EnumeratorNameKey].Value as string;
            if (props.Contains(FormFactorKey) && props[FormFactorKey].Value is uint ff) formFactor = ff;
        }
        catch (Exception ex)
        {
            AppLog.Debug("audio", $"读取端点属性失败（忽略）: {ex.Message}");
        }
        return ClassifyTransport(bus, name, formFactor);
    }

    /// <summary>当前所有活动的采集端点。</summary>
    public static IReadOnlyList<AudioDeviceInfo> ListCaptureDevices()
    {
        var result = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device) result.Add(Describe(device));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("audio", $"枚举采集设备失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>默认播放设备的传输类型；取不到时为 Unknown。</summary>
    public static AudioTransport DefaultRenderTransport(MMDeviceEnumerator enumerator)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            return Describe(device).Transport;
        }
        catch
        {
            return AudioTransport.Unknown;
        }
    }
}
