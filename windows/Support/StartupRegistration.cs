using System;
using Microsoft.Win32;

namespace VoiceTyper.Support;

/// <summary>
/// 开机自启：写 HKCU\...\Run。不用计划任务（需提权）、不用启动文件夹
/// （用户容易误删且无法程序化查询状态）。
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VoiceTyper";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                var value = key?.GetValue(ValueName) as string;
                return !string.IsNullOrEmpty(value);
            }
            catch (Exception ex)
            {
                AppLog.Warn("startup", $"读取开机自启注册表失败: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>返回是否真正写入成功（R4-2）：调用方应在失败时把 UI 勾选状态恢复为
    /// <see cref="IsEnabled"/> 的真实值并提示，而不是让用户以为已生效。</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("startup", "写入开机自启注册表失败", ex);
            return false;
        }
    }
}
