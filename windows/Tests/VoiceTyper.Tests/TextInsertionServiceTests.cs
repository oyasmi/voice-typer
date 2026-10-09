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

    /// <summary>A 的恢复晚于 B 的插入到点：pending 已被 B 替换，A 的恢复必须放弃，
    /// 且不得清掉 B 的 pending。</summary>
    [Fact]
    public void ScheduledRestore_IsSkipped_WhenPendingWasReplacedByLaterInsert()
    {
        var pendingA = new object();
        var pendingB = new object();
        Assert.False(TextInsertionService.ShouldApplyScheduledRestore(false, pendingB, pendingA));
    }

    [Fact]
    public void ScheduledRestore_Applies_WhenStillOwnPending()
    {
        var pending = new object();
        Assert.True(TextInsertionService.ShouldApplyScheduledRestore(false, pending, pending));
    }

    [Fact]
    public void ScheduledRestore_IsSkipped_WhenCancelledOrPendingCleared()
    {
        var pending = new object();
        Assert.False(TextInsertionService.ShouldApplyScheduledRestore(true, pending, pending));
        Assert.False(TextInsertionService.ShouldApplyScheduledRestore(false, null, pending));
    }

    /// <summary>识别期间用户没碰剪贴板：提前备份的快照可直接使用，插入时不必再读一遍。</summary>
    [Fact]
    public void PrefetchedBackup_IsUsed_WhenClipboardUnchanged()
    {
        Assert.True(TextInsertionService.ShouldUsePrefetchedBackup(
            hasPendingRestore: false, currentSequence: 12, snapshotSequence: 12));
    }

    /// <summary>识别期间用户复制了新内容：旧快照不再是"用户原剪贴板"，必须重新备份。</summary>
    [Fact]
    public void PrefetchedBackup_IsDropped_WhenUserCopiedSomethingNew()
    {
        Assert.False(TextInsertionService.ShouldUsePrefetchedBackup(
            hasPendingRestore: false, currentSequence: 13, snapshotSequence: 12));
    }

    /// <summary>仍有待恢复的临时文本：剪贴板里是我们自己写的内容，走继承原始快照的路径，不用提前备份。</summary>
    [Fact]
    public void PrefetchedBackup_IsDropped_WhileARestoreIsPending()
    {
        Assert.False(TextInsertionService.ShouldUsePrefetchedBackup(
            hasPendingRestore: true, currentSequence: 12, snapshotSequence: 12));
    }
}
