using System;
using System.Threading;
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
    /// <summary>设备能打开，但探测期间收到的全是<b>数字静音</b>（样本恒为 0）。Windows 在
    /// 「让桌面应用访问你的麦克风」关闭时不会让打开失败，而是悄悄送零；硬件静音键也是同样的现象。
    /// 不阻塞使用（少数带降噪门的虚拟 / USB 麦克风空闲时也会输出全零），但要明确提示。</summary>
    Silent,
    Unknown,
}

/// <summary>
/// Windows 对非打包桌面应用的麦克风管控在"设置 → 隐私和安全性 → 麦克风"里，
/// 没有对应的查询 API——只能通过实际尝试打开设备来判断。启动时做一次极短的
/// 静默打开-关闭探测即可。
/// </summary>
internal static class MicPermissionProbe
{
    /// <summary>打开设备后监听多久来判断是不是只有数字静音。</summary>
    private const int ListenMilliseconds = 600;
    /// <summary>至少收到这么多次电平回调（约 30ms 一次）才敢下「全是零」的结论，避免设备还没开始出数据就误判。</summary>
    internal const int MinimumCallbacks = 3;

    /// <summary>纯判定：真实麦克风即使在安静的房间里也有量化噪声，样本恒为 0 说明数据被系统或硬件截断了。</summary>
    internal static MicProbeResult Classify(int callbackCount, float peakLevel) =>
        callbackCount >= MinimumCallbacks && peakLevel <= 0f ? MicProbeResult.Silent : MicProbeResult.Available;

    public static MicProbeResult Probe()
    {
        var capture = new AudioCaptureService();
        try
        {
            var gate = new object();
            int callbacks = 0;
            float peak = 0;
            capture.OnLevel = level =>
            {
                lock (gate)
                {
                    callbacks++;
                    if (level > peak) peak = level;
                }
            };
            capture.Start(new AudioInputPolicy.Automatic());
            Thread.Sleep(ListenMilliseconds);
            capture.StopWithoutResult();
            lock (gate) return Classify(callbacks, peak);
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
