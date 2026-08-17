using System;
using VoiceTyper.Services;
using VoiceTyper.Support;

namespace VoiceTyper.Core;

/// <summary>
/// Windows 对非打包桌面应用的麦克风管控在"设置 → 隐私和安全性 → 麦克风"里，
/// 没有对应的查询 API——只能通过实际尝试打开设备来判断。启动时做一次极短的
/// 静默打开-关闭探测即可。
/// </summary>
internal static class MicPermissionProbe
{
    public static bool TryProbe(out bool accessDenied)
    {
        accessDenied = false;
        var capture = new AudioCaptureService();
        try
        {
            capture.Start();
            capture.StopWithoutResult();
            return true;
        }
        catch (AudioStartException ex)
        {
            accessDenied = ex.IsAccessDenied;
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("permission", $"麦克风探测异常: {ex.Message}");
            return false;
        }
        finally
        {
            capture.Dispose();
        }
    }
}
