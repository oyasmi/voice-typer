using System;
using VoiceTyper.Services;
using VoiceTyper.Support;

namespace VoiceTyper.Core;

/// <summary>麦克风探测的结构化结果（R3-3）。旧实现把成功布尔值直接丢弃，
/// "无设备 / 设备忙 / 初始化失败"全部落到"可用"。</summary>
internal enum MicProbeResult
{
    Available,
    NoDevice,
    AccessDenied,
    DeviceFailure,
    Unknown,
}

/// <summary>
/// Windows 对非打包桌面应用的麦克风管控在"设置 → 隐私和安全性 → 麦克风"里，
/// 没有对应的查询 API——只能通过实际尝试打开设备来判断。启动时做一次极短的
/// 静默打开-关闭探测即可。
/// </summary>
internal static class MicPermissionProbe
{
    public static MicProbeResult Probe()
    {
        var capture = new AudioCaptureService();
        try
        {
            capture.Start();
            capture.StopWithoutResult();
            return MicProbeResult.Available;
        }
        catch (AudioStartException ex)
        {
            return ex.Kind switch
            {
                AudioStartFailureKind.AccessDenied => MicProbeResult.AccessDenied,
                AudioStartFailureKind.NoDevice => MicProbeResult.NoDevice,
                _ => MicProbeResult.DeviceFailure,
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn("permission", $"麦克风探测异常: {ex.Message}");
            return MicProbeResult.Unknown;
        }
        finally
        {
            capture.Dispose();
        }
    }
}
