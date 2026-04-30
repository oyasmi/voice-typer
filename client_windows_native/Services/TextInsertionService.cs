using System.Runtime.InteropServices;
using System.Windows.Forms;
using VoiceTyper.Support;

namespace VoiceTyper.Services;

/// <summary>
/// 文本插入服务。使用 Clipboard + SendInput(Ctrl+V) 方式。
/// 与 macOS Swift 版的剪贴板回退策略对应。
///
/// 设计要点:
/// - 备份原剪贴板内容 → 写入识别文本 → 模拟 Ctrl+V → 延迟恢复剪贴板
/// - 使用 WinForms Timer 延迟恢复（确保在 UI 线程执行，剪贴板操作需要 STA）
/// - 与 macOS Swift 版一样追踪 pendingRestore，防止连续输入时恢复逻辑冲突
/// </summary>
internal sealed class TextInsertionService
{
    private System.Windows.Forms.Timer? _restoreTimer;
    private string? _clipboardBackup;

    /// <summary>将文本插入到当前焦点位置</summary>
    public bool Insert(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        try
        {
            return InsertUsingClipboard(text);
        }
        catch (Exception ex)
        {
            AppLog.Error("文本插入失败", ex);
            return false;
        }
    }

    private bool InsertUsingClipboard(string text)
    {
        // 取消上一次未完成的剪贴板恢复
        _restoreTimer?.Stop();
        _restoreTimer?.Dispose();
        _restoreTimer = null;

        // 备份当前剪贴板
        string? backup = null;
        try
        {
            if (Clipboard.ContainsText())
                backup = Clipboard.GetText();
        }
        catch
        {
            // 剪贴板可能被其他进程锁定，忽略
        }

        // 写入识别文本
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            AppLog.Error("写入剪贴板失败", ex);
            return false;
        }

        // 短暂等待剪贴板就绪（与 Python 版的 time.sleep(0.05) 对应）
        Thread.Sleep(30);

        // 模拟 Ctrl+V 粘贴
        SimulatePaste();

        // 延迟恢复剪贴板（使用 WinForms Timer 保证在 UI 线程执行）
        _clipboardBackup = backup;
        _restoreTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _restoreTimer.Tick += OnRestoreClipboard;
        _restoreTimer.Start();

        return true;
    }

    private void OnRestoreClipboard(object? sender, EventArgs e)
    {
        _restoreTimer?.Stop();
        _restoreTimer?.Dispose();
        _restoreTimer = null;

        try
        {
            if (_clipboardBackup != null)
                Clipboard.SetText(_clipboardBackup);
        }
        catch
        {
            // 恢复失败不影响主流程
        }
    }

    /// <summary>使用 SendInput 模拟 Ctrl+V 按键</summary>
    private static void SimulatePaste()
    {
        const ushort VK_V = 0x56;

        var inputs = new[]
        {
            NativeInterop.CreateKeyInput((ushort)NativeInterop.VK_CONTROL, down: true),
            NativeInterop.CreateKeyInput(VK_V, down: true),
            NativeInterop.CreateKeyInput(VK_V, down: false),
            NativeInterop.CreateKeyInput((ushort)NativeInterop.VK_CONTROL, down: false),
        };

        var sent = NativeInterop.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeInterop.INPUT>());

        if (sent != inputs.Length)
            AppLog.Warning($"SendInput 只发送了 {sent}/{inputs.Length} 个事件");
    }
}
