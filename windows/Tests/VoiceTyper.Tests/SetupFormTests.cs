using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using VoiceTyper.Services;
using VoiceTyper.Support;
using VoiceTyper.UI;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>在 STA 上验证真实控件的跨页草稿、保存失败、撤销与布局；不写用户配置和密钥。</summary>
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
