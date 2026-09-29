using System.Collections.Generic;
using VoiceTyper.Services;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>输入设备选择策略与端点分类（对应 macOS <c>AudioInputDeviceTests</c>；纯函数，不碰 WASAPI）。</summary>
public class AudioInputDeviceTests
{
    private static readonly AudioDeviceInfo Builtin = new("builtin", "Microphone Array", AudioTransport.BuiltIn);
    private static readonly AudioDeviceInfo Bluetooth = new("bt", "Headset (WH-1000XM4 Hands-Free)", AudioTransport.Bluetooth);
    private static readonly AudioDeviceInfo Usb = new("usb", "USB Mic", AudioTransport.Usb);
    private static readonly IReadOnlyList<AudioDeviceInfo> All = new[] { Builtin, Bluetooth, Usb };

    [Fact]
    public void Automatic_SwitchesToBuiltIn_OnlyWhenBothInputAndOutputAreBluetooth()
    {
        var (id, switched) = AudioInputSelector.Resolve(new AudioInputPolicy.Automatic(), All, "bt", AudioTransport.Bluetooth);
        Assert.Equal("builtin", id);
        Assert.True(switched);
    }

    [Fact]
    public void Automatic_RespectsBluetoothInput_WhenOutputIsNotBluetooth()
    {
        // 输出不是蓝牙：蓝牙麦克风是用户有意的选择，不替他改。
        var (id, switched) = AudioInputSelector.Resolve(new AudioInputPolicy.Automatic(), All, "bt", AudioTransport.BuiltIn);
        Assert.Equal("bt", id);
        Assert.False(switched);
    }

    [Fact]
    public void Automatic_KeepsDefault_WhenDefaultInputIsNotBluetooth()
    {
        var (id, switched) = AudioInputSelector.Resolve(new AudioInputPolicy.Automatic(), All, "usb", AudioTransport.Bluetooth);
        Assert.Equal("usb", id);
        Assert.False(switched);
    }

    [Fact]
    public void Automatic_KeepsDefault_WhenNoBuiltInMicrophoneExists()
    {
        var (id, switched) = AudioInputSelector.Resolve(
            new AudioInputPolicy.Automatic(), new[] { Bluetooth, Usb }, "bt", AudioTransport.Bluetooth);
        Assert.Equal("bt", id);
        Assert.False(switched);
    }

    [Fact]
    public void SystemDefault_NeverSwitches()
    {
        var (id, switched) = AudioInputSelector.Resolve(new AudioInputPolicy.SystemDefault(), All, "bt", AudioTransport.Bluetooth);
        Assert.Equal("bt", id);
        Assert.False(switched);
    }

    [Fact]
    public void ExplicitDevice_UsedWhenPresent_ElseFallsBackToDefault()
    {
        Assert.Equal(("usb", false), AudioInputSelector.Resolve(new AudioInputPolicy.Device("usb"), All, "bt", AudioTransport.Bluetooth));
        Assert.Equal(("bt", false), AudioInputSelector.Resolve(new AudioInputPolicy.Device("gone"), All, "bt", AudioTransport.Bluetooth));
    }

    [Theory]
    [InlineData(null, "", "auto")]
    [InlineData("", "", "auto")]
    [InlineData("  ", "", "auto")]
    [InlineData("auto", "", "auto")]
    [InlineData("AUTO", "", "auto")]
    [InlineData("system", "", "system")]
    [InlineData("{0.0.1.00000000}.{abc}", "{0.0.1.00000000}.{abc}", "device")]
    public void PolicyParsing(string? raw, string expectedId, string expectedKind)
    {
        var policy = AudioInputPolicy.FromConfigValue(raw);
        switch (expectedKind)
        {
            case "auto": Assert.IsType<AudioInputPolicy.Automatic>(policy); break;
            case "system": Assert.IsType<AudioInputPolicy.SystemDefault>(policy); break;
            default: Assert.Equal(expectedId, Assert.IsType<AudioInputPolicy.Device>(policy).Id); break;
        }
    }

    [Theory]
    [InlineData("BTHENUM", "Headset", 5u, "Bluetooth")]
    [InlineData("BTHHFENUM", null, null, "Bluetooth")]
    [InlineData("USB", "Webcam", 4u, "Usb")]
    [InlineData("HDAUDIO", "Microphone Array", 4u, "BuiltIn")]
    [InlineData("INTELAUDIO", "Mic", 4u, "BuiltIn")]
    [InlineData("HDAUDIO", "Stereo Mix", 2u, "Other")]   // 立体声混音是线路输入，不是内置麦克风
    [InlineData(null, "Headset (AirPods Hands-Free AG Audio)", null, "Bluetooth")]
    [InlineData(null, "耳机 (Foo 免提)", null, "Bluetooth")]
    [InlineData(null, "Some USB Device", null, "Usb")]
    [InlineData(null, "Mystery", null, "Unknown")]
    public void ClassifyTransport(string? bus, string? name, uint? formFactor, string expected)
    {
        // 不在公开测试签名里出现 internal 枚举（否则 CS0051）；用名称字符串比对。
        Assert.Equal(expected, AudioDeviceCatalog.ClassifyTransport(bus, name, formFactor).ToString());
    }
}
