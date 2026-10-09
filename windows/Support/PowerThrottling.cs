using System;
using System.Runtime.InteropServices;
using static VoiceTyper.Support.NativeMethods;

namespace VoiceTyper.Support;

/// <summary>
/// 让本进程退出 Windows 的 EcoQoS 执行速度节流。托盘应用没有前台窗口，Windows 11 会把这类后台进程放到
/// 能效核或降频，识别推理因此变慢；显式声明"不要节流"后系统不再对本进程这样做。
///
/// 启动时调用一次即可，不随听写开关：空闲时进程没有工作可调度，节流与否没有功耗差别，
/// 而来回切换只会引入时序问题。调用失败（系统不支持、策略禁止）不影响功能，只记一条日志。
/// </summary>
internal static class PowerThrottling
{
    /// <summary>null = 尚未尝试；耗时摘要的 <c>qos</c> 字段据此输出。</summary>
    public static bool? OptedOut { get; private set; }

    public static void TryOptOut()
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0, // 控制位置 1、状态位置 0 = 明确关闭执行速度节流。
            };
            OptedOut = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state,
                (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            if (OptedOut == false)
            {
                AppLog.Debug("app", $"退出 EcoQoS 节流失败 (Win32 error {Marshal.GetLastWin32Error()})");
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            OptedOut = false;
            AppLog.Debug("app", $"当前系统不支持退出 EcoQoS 节流: {ex.Message}");
        }
    }
}
