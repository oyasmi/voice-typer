using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Forms;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using VoiceTyper.Services;
using VoiceTyper.Support;
using VoiceTyper.UI;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>在 STA 上验证真实控件的跨页草稿、保存失败、撤销与布局；不写用户配置和密钥。</summary>
[Collection("Settings UI")]
public class SetupFormTests
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

    private static T Field<T>(SetupForm form, string name) =>
        (T)typeof(SetupForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    [Fact]
    public void HotkeyPresets_SwitchFromLegacyRightAlt_AndPreserveTriggerMode() => OnUiThread(() =>
    {
        using var form = new SetupForm();
        var config = new AppConfig();
        config.Hotkey = new HotkeyConfig { Key = ModifierHotkeys.RightAlt, Modifiers = new(), ModeValue = HotkeyMode.Toggle };
        form.LoadEditableContent(config, refreshDevices: false);
        form.Show();
        form.SelectTab(SetupTab.Hotkey);
        Field<Button>(form, "_useCtrlTabButton").PerformClick();
        var draft = form.BuildDraft().Hotkey;
        Assert.Equal("tab", draft.Key);
        Assert.Equal(new[] { "ctrl" }, draft.Modifiers);
        Assert.Equal(HotkeyMode.Toggle, draft.ModeValue);
        Field<Button>(form, "_useRightCtrlButton").PerformClick();
        draft = form.BuildDraft().Hotkey;
        Assert.Equal(ModifierHotkeys.RightCtrl, draft.Key);
        Assert.Empty(draft.Modifiers);
        Assert.Equal(HotkeyMode.Toggle, draft.ModeValue);
        Field<Button>(form, "_useCtrlTabButton").PerformClick();
        Assert.Equal(new[] { "ctrl" }, form.BuildDraft().Hotkey.Modifiers);
        form.DiscardChanges();
        Assert.Equal(ModifierHotkeys.RightAlt, form.BuildDraft().Hotkey.Key);
    });

    [Fact]
    public void DraftSurvivesNavigationAndBackgroundReload_AndDiscardRestoresPreview() => OnUiThread(() =>
    {
        using var form = new SetupForm();
        var config = new AppConfig();
        form.LoadEditableContent(config, refreshDevices: false);
        Assert.False(form.HasUnsavedChanges);
        double preview = 0;
        form.OnPreviewHudOpacity = value => preview = value;
        Field<NumericUpDown>(form, "_opacityField").Value = 0.55m;
        Field<ComboBox>(form, "_languageCombo").SelectedItem = AsrLanguage.En;
        form.SelectTab(SetupTab.Correction);
        form.LoadEditableContent(new AppConfig(), refreshDevices: false);
        Assert.True(form.HasUnsavedChanges);
        Assert.Equal(0.55, form.BuildDraft().UI.Opacity);
        Assert.Equal(AsrLanguage.En, form.BuildDraft().Asr.LanguageValue);
        form.DiscardChanges();
        Assert.False(form.HasUnsavedChanges);
        Assert.Equal(config.UI.Opacity, preview);
    });

    [Fact]
    public void UnifiedSaveIncludesEveryPageAndChangedKey_AndFailureRetainsDraft() => OnUiThread(() =>
    {
        using var form = new SetupForm();
        form.LoadEditableContent(new AppConfig(), refreshDevices: false);
        Field<TextBox>(form, "_llmApiKeyField").Text = "test-only-key";
        Field<NumericUpDown>(form, "_idleUnloadField").Value = 15;
        Field<NumericUpDown>(form, "_opacityField").Value = 0.6m;
        Field<ComboBox>(form, "_hotkeyModeCombo").SelectedItem = HotkeyMode.Toggle;
        var microphones = Field<ComboBox>(form, "_micDeviceCombo");
        microphones.Items.Add(new MicChoice("test-device", "测试麦克风"));
        microphones.SelectedIndex = 1;
        form.OnSaveRecognition = (_, _) => Task.FromException(new InvalidOperationException("模拟保存拒绝"));
        form.HandleSaveAll().GetAwaiter().GetResult();
        Assert.True(form.HasUnsavedChanges);
        form.OnSaveRecognition = (draft, key) =>
        {
            Assert.Equal(15, draft.Asr.IdleUnloadMinutes);
            Assert.Equal(0.6, draft.UI.Opacity);
            Assert.Equal("test-only-key", key);
            Assert.Equal(HotkeyMode.Toggle, draft.Hotkey.ModeValue);
            Assert.Equal("test-device", draft.Audio.InputDevice);
            return Task.CompletedTask;
        };
        form.HandleSaveAll().GetAwaiter().GetResult();
        Assert.False(form.HasUnsavedChanges);
    });

    /// <summary>配置允许的边界值必须能被控件无损载入：不被夹逼、不被误判为"已修改"，BuildDraft 原值不变。</summary>
    [Fact]
    public void BoundaryConfigValues_LoadWithoutClampingOrDirtyState() => OnUiThread(() =>
    {
        using var form = new SetupForm();
        var config = new AppConfig();
        config.Asr.IdleUnloadMinutes = ConfigLimits.IdleUnloadMinutesMax;
        config.Llm.Timeout = ConfigLimits.TimeoutSecondsMax;
        config.Llm.MaxTokens = ConfigLimits.MaxTokensMax;
        config.UI.Opacity = ConfigLimits.OpacityMin;
        config.Llm.Temperature = 0.75;
        form.LoadEditableContent(config, refreshDevices: false);

        Assert.False(form.HasUnsavedChanges);
        var draft = form.BuildDraft();
        Assert.Equal(1440, draft.Asr.IdleUnloadMinutes);
        Assert.Equal(120, draft.Llm.Timeout);
        Assert.Equal(8192, draft.Llm.MaxTokens);
        Assert.Equal(0.1, draft.UI.Opacity);
        Assert.Equal(0.75, draft.Llm.Temperature);
    });

    [Fact]
    public void ReloadRejection_IsShownOnModelCard() => OnUiThread(() =>
    {
        using var form = new SetupForm();
        form.LoadEditableContent(new AppConfig(), refreshDevices: false);
        form.ShowModelActionMessage("正在听写");
        Assert.Equal("正在听写", Field<Label>(form, "_modelStatusLabel").Text);
    });

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [Fact]
    public void SettingsFields_ReflowWithoutLosingValuesOrChangingParents() => OnUiThread(() =>
    {
        using var title = new Label { Text = "超时 / Timeout" };
        using var hint = new Label { Text = "超时后仍使用原识别文本。Keep the original recognition result when the request times out." };
        using var editor = new NumericUpDown { Value = 17 };
        using var field = new SettingsField(title, hint, editor, fullWidth: false, editorWidth: 120);
        foreach (var width in new[] { 700, 320, 700, 320 })
        {
            var pixels = width * field.DeviceDpi / 96;
            field.Size = field.GetPreferredSize(new Size(pixels, 0));
            field.PerformLayout();
            Assert.True(field.ClientRectangle.Contains(editor.Bounds));
            Assert.True(field.ClientRectangle.Contains(hint.Bounds));
            Assert.True(title.Bottom <= hint.Top);
            if (width == 320) Assert.True(hint.Bottom <= editor.Top);
            else Assert.True(hint.Right <= editor.Left);
            Assert.Same(field, editor.Parent);
            Assert.Equal(17, editor.Value);
            Assert.True(editor.Width < pixels / 2);
        }
    });

    [Fact]
    public void NarrowSettings_AllPagesScrollVertically_AndKeepSaveActionsVisible() => OnUiThread(() =>
    {
        try
        {
            foreach (var language in new[] { AppLanguage.Zh, AppLanguage.En })
            {
                L10n.SetLanguage(language);
                using var form = new SetupForm();
                form.LoadEditableContent(new AppConfig(), refreshDevices: false);
                form.Show();
                form.MicTestStopped("麦克风测试已停止。The microphone test stopped because the selected device is unavailable. Please reconnect it and retry.");
                Field<CheckBox>(form, "_llmEnabledCheck").Checked = true;
                Field<Label>(form, "_generalMessage").Text = string.Join(" ", Enumerable.Repeat("保存失败，请检查设置后重试。Please check settings and retry.", 3));
                foreach (var width in new[] { 1000, 720, 900, 720 })
                {
                    form.ClientSize = new Size(width, 540);
                    foreach (var tab in Enum.GetValues<SetupTab>())
                    {
                        form.SelectTab(tab);
                        foreach (var toggle in Descendants(form).OfType<Button>().Where(b => b.Visible && b.Text.StartsWith("+ ")).ToArray())
                            toggle.PerformClick();
                        form.PerformLayout(); Application.DoEvents();
                        var pages = Field<Dictionary<SetupTab, Control>>(form, "_pages");
                        var page = (ScrollableControl)pages[tab];
                        Assert.False(page.HorizontalScroll.Visible, $"页面横向溢出：{language} / {tab} / {width}");
                        foreach (var button in new[] { Field<Button>(form, "_saveRecognitionButton"), Field<Button>(form, "_discardButton") })
                        {
                            var bounds = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                            Assert.True(form.ClientRectangle.Contains(bounds), $"底部按钮被裁切：{language} / {tab} / {width}");
                        }
                        foreach (var field in Descendants(page).OfType<SettingsField>().Where(f => f.Visible))
                        {
                            Assert.True(field.Height <= field.GetPreferredSize(new Size(field.Width, 0)).Height + 2,
                                $"字段存在多余留白：{language} / {tab} / {width} / {field.Height}");
                            foreach (Control control in field.Controls)
                                Assert.True(field.ClientRectangle.Contains(control.Bounds), $"字段被裁切：{language} / {tab} / {width} / {control.GetType().Name}");
                        }
                        foreach (var stack in Descendants(page).OfType<SettingsStack>().Where(s => s.Visible))
                        {
                            var bottom = stack.Controls.Cast<Control>().Where(c => c.Visible)
                                .Select(c => c.Bottom + c.Margin.Bottom).DefaultIfEmpty(stack.Padding.Top).Max();
                            Assert.InRange(stack.Height - bottom - stack.Padding.Bottom, 0, 2);
                        }
                    }
                }
            }
        }
        finally { L10n.SetLanguage(AppLanguage.Zh); }
    });

    [SkippableFact]
    public void RenderSettingsPreviews() => OnUiThread(() =>
    {
        var directory = Environment.GetEnvironmentVariable("VOICETYPER_SETTINGS_PREVIEW");
        Skip.If(string.IsNullOrEmpty(directory), "显式设置 VOICETYPER_SETTINGS_PREVIEW 才输出真实窗口预览");
        Directory.CreateDirectory(directory!);
        try
        {
            foreach (var language in new[] { AppLanguage.Zh, AppLanguage.En })
            {
                L10n.SetLanguage(language);
                using var form = new SetupForm();
                form.LoadEditableContent(new AppConfig(), refreshDevices: false);
                // 本测试窗只显示虚构数据，密钥从未接入 SecretStore。
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000);
                form.Show();
                form.UpdateStatus(MicProbeResult.Available, AsrState.ModelMissing, null, null, "Right Ctrl", "", null);
                foreach (var tab in Enum.GetValues<SetupTab>())
                {
                    form.SelectTab(tab);
                    if (tab == SetupTab.Correction) Field<CheckBox>(form, "_llmEnabledCheck").Checked = true;
                    form.PerformLayout(); Application.DoEvents();
                    var save = Field<Button>(form, "_saveRecognitionButton");
                    var saveBounds = new Rectangle(form.PointToClient(save.PointToScreen(Point.Empty)), save.Size);
                    Assert.True(form.ClientRectangle.Contains(saveBounds), $"保存按钮被裁切：{language} / {tab} / {saveBounds}");
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(Path.Combine(directory!, $"settings-{language}-{tab}.png"), ImageFormat.Png);
                    // 只输出本测试的虚构界面数据，不记录编辑器文本，便于定位容器的意外留白。
                    var layout = Descendants(form).Where(c => c.Visible).Select(c => new
                    {
                        Type = c.GetType().Name,
                        Caption = c is Label or Button ? c.Text : "",
                        Bounds = c.Bounds.ToString(),
                        Preferred = c.GetPreferredSize(new Size(c.Width, 0)).ToString(),
                        Rows = c is TableLayoutPanel table ? table.GetRowHeights() : Array.Empty<int>(),
                    });
                    File.WriteAllText(Path.Combine(directory!, $"settings-{language}-{tab}.json"), JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
                }
                form.ClientSize = new Size(740, 550);
                form.SelectTab(SetupTab.Recognition);
                form.UpdateStatus(MicProbeResult.AccessDenied, AsrState.ModelMissing, null, null, "", "",
                    "ModelScope：HTTP 403\nHugging Face：安全连接失败\n请检查系统代理与证书后手动重试。", "AuthenticationException: test");
                Application.DoEvents();
                using var errorBitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(errorBitmap, new Rectangle(Point.Empty, form.Size));
                errorBitmap.Save(Path.Combine(directory!, $"settings-{language}-errors.png"), ImageFormat.Png);
                form.Hide();
            }
        }
        finally { L10n.SetLanguage(AppLanguage.Zh); }
    });
}

// 界面语言和共享字体缓存属于进程状态，布局检查不能与其他测试并行切换它们。
[CollectionDefinition("Settings UI", DisableParallelization = true)]
public class SettingsUiCollection { }
