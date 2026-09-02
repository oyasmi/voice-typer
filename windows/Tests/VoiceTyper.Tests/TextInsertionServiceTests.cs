using VoiceTyper.Services;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 连续粘贴兜底时的 pending 快照继承判定（对齐 macOS
/// <c>TextInsertionServiceTests</c> 的 <c>shouldInheritPendingSnapshot</c> 系列）：
/// 避免第二次兜底把用户最初的剪贴板当成"用户内容"重新快照、进而永久覆盖掉。
/// </summary>
public class TextInsertionServiceTests
{
    /// <summary>临时恢复窗口内再次兜底，剪贴板仍是上一段听写写入的临时文本、序列号未变：
    /// 必须继承上一次备份的「用户原始快照」。</summary>
    [Fact]
    public void Inherits_WhenClipboardStillHoldsPreviousDictationText()
    {
        Assert.True(TextInsertionService.ShouldInheritPendingSnapshot(
            currentSequence: 7, currentText: "上一段听写",
            pendingWrittenSequence: 7, pendingWrittenText: "上一段听写"));
    }

    /// <summary>用户在两次兜底之间复制了新内容（序列号变化）：不继承，重新快照当前剪贴板。</summary>
    [Fact]
    public void DoesNotInherit_WhenUserCopiedNewContent()
    {
        Assert.False(TextInsertionService.ShouldInheritPendingSnapshot(
            currentSequence: 9, currentText: "用户刚复制的新内容",
            pendingWrittenSequence: 7, pendingWrittenText: "上一段听写"));
    }

    /// <summary>序列号恰好相同但剪贴板字符串已被别处改写：同样不继承。</summary>
    [Fact]
    public void DoesNotInherit_WhenClipboardStringChangedWithoutSequenceBump()
    {
        Assert.False(TextInsertionService.ShouldInheritPendingSnapshot(
            currentSequence: 7, currentText: "别的内容",
            pendingWrittenSequence: 7, pendingWrittenText: "上一段听写"));
    }

    /// <summary>剪贴板读取失败（currentText 为 null）：不继承。</summary>
    [Fact]
    public void DoesNotInherit_WhenClipboardTextUnreadable()
    {
        Assert.False(TextInsertionService.ShouldInheritPendingSnapshot(
            currentSequence: 7, currentText: null,
            pendingWrittenSequence: 7, pendingWrittenText: "上一段听写"));
    }
}
