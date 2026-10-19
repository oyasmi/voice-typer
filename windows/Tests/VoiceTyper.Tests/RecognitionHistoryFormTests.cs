using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.UI;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>验证历史窗口的实际选择、复制与实时刷新；必须在 Windows STA 线程执行。</summary>
[Collection("Settings UI")]
public class RecognitionHistoryFormTests
{
    private static void OnUiThread(Action test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { Application.EnableVisualStyles(); test(); }
            catch (Exception caught) { error = caught; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static T Field<T>(RecognitionHistoryForm form, string name) =>
        (T)typeof(RecognitionHistoryForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    [Fact]
    public void EmptyThenLiveHistory_CopiesFullSelectedText_AndAllInSpeakingOrder() => OnUiThread(() =>
    {
        var history = new RecognitionHistory();
        string? copied = null;
        using var form = new RecognitionHistoryForm(history, text => { copied = text; return null; });
        form.Show();
        var entries = Field<ListBox>(form, "_entries");
        var copySelected = Field<Button>(form, "_copySelected");
        var copyAll = Field<Button>(form, "_copyAll");
        Assert.False(copySelected.Enabled);
        Assert.False(copyAll.Enabled);
        var first = "第一段\r\n" + new string('长', 300);
        history.Add(DateTime.Now, first);
        history.Add(DateTime.Now, "第二段");
        form.RefreshEntries();
        Assert.Equal("第二段", Field<TextBox>(form, "_content").Text);
        entries.SelectedIndex = 1;
        Assert.Equal(first, Field<TextBox>(form, "_content").Text);
        copySelected.PerformClick();
        Assert.Equal(first, copied);
        history.Add(DateTime.Now, "第三段");
        form.RefreshEntries();
        Assert.Equal(first, Field<TextBox>(form, "_content").Text);
        copyAll.PerformClick();
        Assert.Equal(first + "\r\n第二段\r\n第三段", copied);
        // 选中记录被容量上限淘汰后，切回最新一条。
        for (var i = 0; i < 20; i++) history.Add(DateTime.Now, $"新段{i}");
        form.RefreshEntries();
        Assert.Equal(20, entries.Items.Count);
        Assert.Equal("新段19", Field<TextBox>(form, "_content").Text);
    });

    [Fact]
    public void CopyRejection_IsShownInWindow_AndRetryCanSucceed() => OnUiThread(() =>
    {
        var history = new RecognitionHistory();
        history.Add(DateTime.Now, "正文");
        string? error = "正在听写，请等这一段结束后再复制";
        using var form = new RecognitionHistoryForm(history, _ => error);
        form.Show();
        var button = Field<Button>(form, "_copySelected");
        button.PerformClick();
        Assert.Equal(error, Field<Label>(form, "_status").Text);
        error = "复制到剪贴板失败，请重试。";
        button.PerformClick();
        Assert.Equal(error, Field<Label>(form, "_status").Text);
        error = null;
        button.PerformClick();
        Assert.Equal("已复制", Field<Label>(form, "_status").Text);
    });
}
