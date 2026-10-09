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

/// <summary>一次探测的完整结局：分类 + 供界面横幅展示的失败详情（真实异常消息与 HRESULT）。</summary>
internal sealed record MicProbeOutcome(MicProbeResult Result, string? FailureDetail = null);

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

    public static MicProbeOutcome Probe()
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
            lock (gate) return new MicProbeOutcome(Classify(callbacks, peak));
        }
        catch (AudioStartException ex)
        {
            var result = ex.Kind switch
            {
                AudioStartFailureKind.AccessDenied => MicProbeResult.AccessDenied,
                AudioStartFailureKind.NoDevice => MicProbeResult.NoDevice,
                _ => MicProbeResult.DeviceFailure,
            };
            // 横幅只按分类显示笼统文案；真实原因（HRESULT、声道数、端点错误）必须完整落日志，
            // 否则真机上这类失败无法定位（横幅里的「被独占 / 驱动异常」只是猜测，不是事实）。
            AppLog.Error("permission", $"麦克风探测失败（{ex.Kind}）: {DescribeFailure(ex)}", ex);
            return new MicProbeOutcome(result, result is MicProbeResult.DeviceFailure ? DescribeFailure(ex) : null);
        }
        catch (Exception ex)
        {
            AppLog.Error("permission", $"麦克风探测异常: {ex.Message}", ex);
            return new MicProbeOutcome(MicProbeResult.Unknown, $"{ex.Message} (0x{ex.HResult:X8})");
        }
        finally
        {
            capture.Dispose();
        }
    }

    /// <summary>取异常链最深处的真实原因：最外层的 AudioStartException 消息是分类文案，
    /// 设备层的 COMException 才带 HRESULT 与具体错误。</summary>
    private static string DescribeFailure(AudioStartException ex)
    {
        Exception deepest = ex;
        while (deepest.InnerException is not null) deepest = deepest.InnerException;
        return ReferenceEquals(deepest, ex) ? ex.Message : $"{ex.Message}（{deepest.Message}，0x{deepest.HResult:X8}）";
    }
}
