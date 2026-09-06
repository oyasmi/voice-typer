using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VoiceTyper.Support;
using static VoiceTyper.Support.NativeMethods;

namespace VoiceTyper.Services;

internal enum TextInsertionResult
{
    Inserted,
    /// <summary>录音开始到插入之间前台窗口已切换：为避免写入用户未预期的窗口
    /// （最坏情况是密码框），不再插入，只把结果复制到剪贴板（F-10）。</summary>
    FocusChanged,
    /// <summary>插入前检测到 Alt/Shift/Win 仍被按住——会改变 Ctrl+V 的语义，
    /// 放弃自动粘贴改为复制到剪贴板（R3-2）。</summary>
    ModifiersHeld,
    Failed,
}

/// <summary>前台窗口相对本进程的提权关系（R3-2）。无法判定时为 <see cref="Unknown"/>，
/// 不再像旧实现那样一律当作"未提权"。</summary>
internal enum ForegroundElevation { NotElevated, Elevated, Unknown }

/// <summary>
/// 文本插入服务：剪贴板 + SendInput Ctrl+V。
/// 必须在 UI 线程调用（剪贴板 API 是 STA-affined）。
/// </summary>
internal sealed class TextInsertionService
{
    /// <summary>恢复原剪贴板内容前的等待时长；500ms 对部分慢应用偏短（对齐 macOS d572f86）。</summary>
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _pendingRestoreCts;

    /// <summary>
    /// 上一次粘贴兜底尚未完成的恢复：记录当时备份的「用户原始剪贴板」快照，以及我们写进
    /// 剪贴板的临时文本和写入后的剪贴板序列号。在临时恢复窗口内再次走粘贴兜底时，若剪贴板
    /// 仍是这次的临时文本（用户没有复制新内容），下一次必须**继承**这份原始快照，而不是把
    /// 当前这段听写临时文本当成「用户原始内容」快照下来——否则连续两次兜底会把用户最初的
    /// 剪贴板永久覆盖掉（对齐 macOS <c>TextInsertionService.PendingRestore</c>）。
    /// </summary>
    private sealed record PendingRestore(ClipboardSnapshot Snapshot, string WrittenText, uint WrittenSequence);
    private PendingRestore? _pendingRestore;

    /// <summary>
    /// 纯函数：连续粘贴兜底时，判断本次是否应继承上一次 pending 的原始快照。
    /// true = 剪贴板仍是上一段听写写入的临时文本且用户未复制新内容，应继承；
    /// false = 剪贴板已变化（用户复制了新内容，或已被别处改写），应重新快照当前内容。
    /// </summary>
    internal static bool ShouldInheritPendingSnapshot(
        uint currentSequence, string? currentText,
        uint pendingWrittenSequence, string pendingWrittenText)
        => currentSequence == pendingWrittenSequence && currentText == pendingWrittenText;

    /// <summary>
    /// 一份剪贴板内容的真实快照：备份时立即遍历原剪贴板的每个格式并取走数据，
    /// 而不是持有对原剪贴板所有者的 COM 引用——原所有者进程若在恢复前退出，
    /// 持有引用的恢复会静默失败，用户原剪贴板内容永久丢失（W-08b）。
    /// </summary>
    private sealed class ClipboardSnapshot
    {
        public List<(string Format, object Data)> Entries { get; } = new();
        /// <summary>true = 读取原剪贴板本身失败（区别于"原剪贴板为空"）。恢复阶段绝不
        /// <see cref="Clipboard.Clear"/>，优先保留识别结果、让用户自行复制（R3-2）。</summary>
        public bool ReadFailed { get; set; }
    }

    /// <param name="expectedForegroundWindow">录音开始时记录的前台窗口句柄；插入前若与当前
    /// 前台窗口不一致，说明用户已切换焦点，直接放弃插入（F-10）。</param>
    /// <param name="expectedForegroundProcessId">同一时刻的前台窗口所属进程 id，用于防止
    /// 句柄被系统复用给另一个窗口时误判为"焦点未变化"。</param>
    public TextInsertionResult Insert(string text, IntPtr expectedForegroundWindow, uint expectedForegroundProcessId)
    {
        if (string.IsNullOrEmpty(text)) return TextInsertionResult.Inserted;

        var currentWindow = GetForegroundWindow();
        GetWindowThreadProcessId(currentWindow, out var currentProcessId);
        if (currentWindow != expectedForegroundWindow || currentProcessId != expectedForegroundProcessId)
        {
            return TextInsertionResult.FocusChanged;
        }

        // Alt/Shift/Win 仍按住时 Ctrl+V 会变成别的快捷键（R3-2）。放弃自动粘贴改为复制。
        if (AreNonPasteModifiersHeld())
        {
            return TextInsertionResult.ModifiersHeld;
        }

        // 取消上一轮的剪贴板恢复任务（如果还在等待）
        _pendingRestoreCts?.Cancel();
        _pendingRestoreCts = null;

        var backup = BackupSnapshotInheritingPendingIfNeeded();

        if (!TryWriteConcealedText(text))
        {
            AppLog.Error("input", "写入剪贴板失败");
            _pendingRestore = null;
            return TextInsertionResult.Failed;
        }

        var expectedSequence = GetClipboardSequenceNumber();

        if (!SendCtrlV())
        {
            AppLog.Error("input", "SendInput 模拟 Ctrl+V 失败");
            RestoreClipboardSnapshotUnconditionally(backup);
            _pendingRestore = null;
            return TextInsertionResult.Failed;
        }

        _pendingRestore = new PendingRestore(backup, text, expectedSequence);
        var cts = new CancellationTokenSource();
        _pendingRestoreCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RestoreDelay, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            UiDispatcher.Post(() =>
            {
                RestoreClipboardSnapshotIfUnchanged(backup, text, expectedSequence);
                _pendingRestore = null;
            });
        });

        return TextInsertionResult.Inserted;
    }

    /// <summary>插入失败/焦点已变化时的兜底：把识别结果写入剪贴板，避免长听写内容彻底丢失。
    /// 取消待恢复任务，防止把这次的文本又还原掉。返回是否真正写入成功——调用方不应在
    /// 写失败时仍告诉用户"结果已复制到剪贴板"（R3-2）。</summary>
    public bool CopyToClipboard(string text)
    {
        _pendingRestoreCts?.Cancel();
        _pendingRestoreCts = null;
        _pendingRestore = null;
        if (TryWriteConcealedText(text)) return true;
        AppLog.Error("input", "兜底写入剪贴板失败");
        return false;
    }

    private static bool AreNonPasteModifiersHeld() =>
        (GetAsyncKeyState(VK_MENU) & 0x8000) != 0    // Alt
        || (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0
        || (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0
        || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

    /// <summary>
    /// 取备份快照：若仍处于上一次粘贴兜底的临时恢复窗口内、且剪贴板未被用户改动，继承上一次
    /// 备份的「用户原始快照」；否则重新快照当前剪贴板内容。
    /// </summary>
    private ClipboardSnapshot BackupSnapshotInheritingPendingIfNeeded()
    {
        if (_pendingRestore is { } pending)
        {
            string? current = null;
            try { current = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { /* 读取失败按"已变化"处理，走重新快照 */ }
            if (ShouldInheritPendingSnapshot(
                    GetClipboardSequenceNumber(), current,
                    pending.WrittenSequence, pending.WrittenText))
            {
                return pending.Snapshot;
            }
        }
        return SnapshotClipboard();
    }

    private static bool TryWriteConcealedText(string text)
    {
        try
        {
            var dataObject = new DataObject();
            // autoConvert:true——大量粘贴目标仍依赖从 CF_UNICODETEXT 自动派生 CF_TEXT/CF_OEMTEXT，
            // 关掉会影响兼容性；下面的三个剪贴板排除标记是不透明的自定义格式，不涉及自动转换，
            // 各自单独用 autoConvert:false 写入即可。
            dataObject.SetData(DataFormats.UnicodeText, true, text);
            ApplyClipboardExclusionFormats(dataObject);
            Clipboard.SetDataObject(dataObject, copy: true, retryTimes: 5, retryDelay: 20);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug("input", $"写入剪贴板失败: {ex.Message}");
            return false;
        }
    }

    private static void ApplyClipboardExclusionFormats(DataObject dataObject)
    {
        // ExcludeClipboardContentFromMonitorProcessing 只要求格式"存在"，数据被忽略。
        dataObject.SetData("ExcludeClipboardContentFromMonitorProcessing", false, new MemoryStream(new byte[] { 0 }));
        // CanIncludeInClipboardHistory / CanUploadToCloudClipboard 的契约要求写 4 字节 DWORD = 0（R3-2）。
        foreach (var format in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            dataObject.SetData(format, false, new MemoryStream(BitConverter.GetBytes(0)));
        }
    }

    /// <summary>遍历原剪贴板每个原生格式立即取走数据，构成一份不依赖原剪贴板所有者存活的快照。
    /// 单个格式读取失败会被跳过并只计数，不记内容（AGENTS.md 日志约束）。</summary>
    private static ClipboardSnapshot SnapshotClipboard()
    {
        var snapshot = new ClipboardSnapshot();
        IDataObject? original;
        try { original = Clipboard.GetDataObject(); }
        catch (Exception ex)
        {
            AppLog.Debug("input", $"读取原剪贴板失败: {ex.Message}");
            snapshot.ReadFailed = true;
            return snapshot;
        }
        if (original is null) return snapshot; // 原剪贴板确实为空（非读取失败）。

        int failedFormats = 0;
        foreach (var format in original.GetFormats(false))
        {
            try
            {
                var data = original.GetData(format, false);
                if (data is not null)
                {
                    snapshot.Entries.Add((format, data));
                }
            }
            catch (Exception)
            {
                failedFormats++;
            }
        }
        if (failedFormats > 0)
        {
            AppLog.Debug("input", $"剪贴板快照跳过了 {failedFormats} 个无法读取的格式");
        }
        return snapshot;
    }

    private static void RestoreClipboardSnapshotUnconditionally(ClipboardSnapshot snapshot)
    {
        try
        {
            if (snapshot.ReadFailed)
            {
                // 此前读不出原剪贴板：不清空、不覆盖，保留识别结果让用户自行复制（R3-2）。
                AppLog.Debug("input", "原剪贴板此前读取失败，保留识别结果不做清空");
                return;
            }
            if (snapshot.Entries.Count == 0)
            {
                // 原剪贴板确实为空：不能"什么都不做"，否则我们写入的识别文本会永久留在剪贴板（W-08b）。
                Clipboard.Clear();
                return;
            }

            var restored = new DataObject();
            foreach (var (format, data) in snapshot.Entries)
            {
                restored.SetData(format, false, data);
            }
            Clipboard.SetDataObject(restored, copy: true, retryTimes: 5, retryDelay: 20);
        }
        catch (Exception ex)
        {
            AppLog.Debug("input", $"恢复剪贴板失败（忽略）: {ex.Message}");
        }
    }

    private static void RestoreClipboardSnapshotIfUnchanged(ClipboardSnapshot snapshot, string expectedText, uint expectedSequence)
    {
        try
        {
            // 序列号 + 内容双重守卫：确保不会覆盖用户在等待期间新复制的内容，
            // 延长到 1s 只是给慢应用更多时间读取剪贴板。
            if (GetClipboardSequenceNumber() != expectedSequence) return;

            string? current = null;
            try { current = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { /* swallow */ }
            if (current != expectedText) return;

            RestoreClipboardSnapshotUnconditionally(snapshot);
        }
        catch (Exception ex)
        {
            AppLog.Debug("input", $"恢复剪贴板失败（忽略）: {ex.Message}");
        }
    }

    /// <summary>
    /// UIPI（User Interface Privilege Isolation）会阻止非提权进程向提权窗口的
    /// <c>SendInput</c> 生效——在以管理员身份运行的记事本/终端里听写会静默失败。
    /// 读取前台进程令牌的 TokenElevation 与本进程比较（R3-2）。无法判定时返回
    /// <see cref="ForegroundElevation.Unknown"/> 而不是一律当作未提权。
    /// </summary>
    public static ForegroundElevation CheckForegroundWindowElevation()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return ForegroundElevation.Unknown;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return ForegroundElevation.Unknown;

        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            // 连"受限查询"句柄都打不开：目标权限比本进程高的强信号。
            return ForegroundElevation.Elevated;
        }
        try
        {
            if (!OpenProcessToken(hProcess, TOKEN_QUERY, out var hToken))
            {
                return ForegroundElevation.Unknown;
            }
            try
            {
                if (!GetTokenInformation(hToken, TokenElevation, out var targetElevated, (uint)sizeof(uint), out uint _))
                {
                    return ForegroundElevation.Unknown;
                }
                // 目标已提权而本进程未提权 → UIPI 会拦 SendInput；其余情况不拦。
                if (targetElevated != 0 && !IsSelfElevated())
                {
                    return ForegroundElevation.Elevated;
                }
                return ForegroundElevation.NotElevated;
            }
            finally { CloseHandle(hToken); }
        }
        finally { CloseHandle(hProcess); }
    }

    private static bool IsSelfElevated()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var hToken)) return false;
            try
            {
                return GetTokenInformation(hToken, TokenElevation, out var elevated, (uint)sizeof(uint), out uint _)
                    && elevated != 0;
            }
            finally { CloseHandle(hToken); }
        }
        catch
        {
            return false;
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
