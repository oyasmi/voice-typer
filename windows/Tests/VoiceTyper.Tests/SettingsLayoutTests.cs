using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.UI;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 设置窗口的排版纪律闸门：短值编辑器（下拉框 / 数字输入）不得拉满整列宽度——
/// 曾经一个 NumericUpDown 会被 Dock 拉到 400px 宽，视觉上像布局坏了。宽度上限留出
/// DPI 缩放余量（96 DPI 基准 320 / 110，上限按 1.1 倍断言）。
/// </summary>
public class SettingsLayoutTests
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

    private static IEnumerable<Control> All(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in All(child)) yield return descendant;
        }
    }

    [Fact]
    public void CompactEditorsKeepSaneWidths() => OnUiThread(() =>
    {
        using var form = new SetupForm();
        form.LoadEditableContent(new AppConfig(), refreshDevices: false);
        // 页面默认全部构建（Visible=false 不影响宽度），逐页显示一遍确保布局已执行。
        foreach (var tab in Enum.GetValues<SetupTab>()) form.SelectTab(tab);

        var combos = All(form).OfType<ComboBox>().ToList();
        var numbers = All(form).OfType<NumericUpDown>().ToList();
        Assert.True(combos.Count >= 5, $"应有至少 5 个下拉框，实际 {combos.Count}");
        Assert.True(numbers.Count >= 6, $"应有至少 6 个数字输入，实际 {numbers.Count}");
        foreach (var combo in combos)
        {
            Assert.InRange(combo.Width, 100, 360);
        }
        foreach (var number in numbers)
        {
            Assert.InRange(number.Width, 80, 140);
        }
    });

    [Fact]
    public void DefaultWindowSizeIsCompact() => OnUiThread(() =>
    {
        using var form = new SetupForm();
        Assert.Equal(new Size(960, 700), form.ClientSize);
        Assert.Equal(new Size(860, 640), form.MinimumSize);
    });

    /// <summary>保存与撤销按钮必须完整落在客户区内（沿袭此前渲染预览里的防裁切断言，改为常驻）。</summary>
    [Fact]
    public void FooterButtonsStayVisibleAtMinimumSize() => OnUiThread(() =>
    {
        using var form = new SetupForm();
        form.LoadEditableContent(new AppConfig(), refreshDevices: false);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-4000, -4000);
        form.Show();
        form.ClientSize = new Size(860, 640);
        foreach (var tab in Enum.GetValues<SetupTab>()) form.SelectTab(tab);
        form.PerformLayout();
        Application.DoEvents();

        foreach (var name in new[] { "_saveRecognitionButton", "_discardButton" })
        {
            var button = (Button)typeof(SetupForm).GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
            var bounds = new Rectangle(form.PointToClient(button.PointToScreen(Point.Empty)), button.Size);
            Assert.True(form.ClientRectangle.Contains(bounds), $"{name} 被裁切：{bounds}");
        }
        form.Hide();
    });
}
