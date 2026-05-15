using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VoiceTyper.Support;
using static VoiceTyper.Support.NativeMethods;

namespace VoiceTyper.Services;

/// <summary>
/// 文本插入服务：剪贴板 + SendInput Ctrl+V。
/// 必须在 UI 线程调用（剪贴板 API 是 STA-affined）。
/// </summary>
internal sealed class TextInsertionService
{
    private CancellationTokenSource? _pendingRestoreCts;

    public bool Insert(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;

        // 取消上一轮的剪贴板恢复任务（如果还在等待）
        _pendingRestoreCts?.Cancel();
        _pendingRestoreCts = null;

        // 备份当前剪贴板
        IDataObject? backup = null;
        try { backup = Clipboard.GetDataObject(); }
        catch (Exception ex)
        {
            AppLog.Debug("input", $"读取原剪贴板失败（忽略）: {ex.Message}");
        }

        // 写入识别文本
        try
        {
            Clipboard.SetDataObject(text, copy: true, retryTimes: 5, retryDelay: 20);
        }
        catch (Exception ex)
        {
            AppLog.Error("input", "写入剪贴板失败", ex);
            return false;
        }

        // 模拟 Ctrl+V
        if (!SendCtrlV())
        {
            AppLog.Error("input", "SendInput 模拟 Ctrl+V 失败");
            return false;
        }

        // 500ms 后尝试恢复原剪贴板内容（若期间未被其它进程改写）
        if (backup is not null)
        {
            var cts = new CancellationTokenSource();
            _pendingRestoreCts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                UiDispatcher.Post(() => RestoreClipboardIfUnchanged(backup, text));
            });
        }

        return true;
    }

    private static void RestoreClipboardIfUnchanged(IDataObject backup, string expectedText)
    {
        try
        {
            // 当前剪贴板的字符串内容仍然是我们刚写入的 expectedText 才恢复；
            // 否则说明用户/其它程序又写了新内容，不要打扰。
            string? current = null;
            try { current = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { /* swallow */ }

            if (current != expectedText) return;
            Clipboard.SetDataObject(backup, copy: true, retryTimes: 5, retryDelay: 20);
        }
        catch (Exception ex)
        {
            AppLog.Debug("input", $"恢复剪贴板失败（忽略）: {ex.Message}");
        }
    }

    private static bool SendCtrlV()
    {
        // 序列：CTRL down, V down, V up, CTRL up
        var inputs = new INPUT[4];

        inputs[0] = MakeKeyInput((ushort)VK_CONTROL, keyUp: false);
        inputs[1] = MakeKeyInput((ushort)VK_V, keyUp: false);
        inputs[2] = MakeKeyInput((ushort)VK_V, keyUp: true);
        inputs[3] = MakeKeyInput((ushort)VK_CONTROL, keyUp: true);

        var sent = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        return sent == inputs.Length;
    }

    private static INPUT MakeKeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };
}
