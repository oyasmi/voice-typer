using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using VoiceTyper.Services;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

internal sealed partial class SetupForm
{
    private static readonly Color Accent = Color.FromArgb(38, 99, 218);
    private static readonly Color Muted = Color.FromArgb(99, 110, 128);
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<SetupTab, Control> _pages = new();
    private readonly Dictionary<SetupTab, Button> _navigation = new();
    private readonly Button _discardButton = new();
    private readonly Button _modelDetailsButton = new();
    private readonly Label _modelProgressText = new();
    private bool _loading;
    private bool _saving;
    private bool _dirty;
    private bool _loadedStartup;
    private string _downloadDetails = "";

    private void BuildSettingsLayout()
    {
        _windowLayout.Dock = DockStyle.Fill;
        _windowLayout.ColumnCount = 2;
        _windowLayout.RowCount = 2;
        _windowLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 178));
        _windowLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _windowLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _windowLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var sidebar = Stack();
        sidebar.Dock = DockStyle.Fill;
        sidebar.BackColor = Color.FromArgb(239, 242, 247);
        sidebar.Padding = new Padding(16, 26, 16, 16);
        Add(sidebar, TextLabel("VoiceTyper", 15, true));
        Add(sidebar, TextLabel(L10n.T("设置"), 9, false, Muted));

        AddNavigation(sidebar, SetupTab.Hotkey, L10n.T("听写"));
        AddNavigation(sidebar, SetupTab.Recognition, L10n.T("语音模型"));
        AddNavigation(sidebar, SetupTab.Correction, L10n.T("智能纠错"));
        AddNavigation(sidebar, SetupTab.General, L10n.T("外观与通用"));
        AddNavigation(sidebar, SetupTab.Permissions, L10n.T("诊断与帮助"));
        _versionLabel.Text = L10n.F("版本 {0}", AppConstants.Version);
        StyleLabel(_versionLabel, Muted);
        _versionLabel.Margin = new Padding(0, 24, 0, 0);
        Add(sidebar, _versionLabel);
        _windowLayout.Controls.Add(sidebar, 0, 0);
        _windowLayout.SetRowSpan(sidebar, 2);

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _bannerPanel.Dock = DockStyle.Fill;
        _bannerPanel.AutoSize = true;
        _bannerPanel.Visible = false;
        var banner = Stack();
        banner.Padding = new Padding(24, 12, 24, 12);
        StyleLabel(_bannerLabel, Color.DarkGoldenrod);
        Add(banner, _bannerLabel);
        StyleButton(_bannerOpenMicSettings, L10n.T("打开麦克风设置"));
        _bannerOpenMicSettings.Click += (_, _) => OpenMicrophonePrivacySettings();
        Add(banner, _bannerOpenMicSettings);
        _bannerPanel.Controls.Add(banner);
        main.Controls.Add(_bannerPanel, 0, 0);
        main.Controls.Add(_pageHost, 0, 1);
        _windowLayout.Controls.Add(main, 1, 0);

        BuildDictationSettings();
        BuildModelSettings();
        BuildCorrectionSettings();
        BuildAppearanceSettings();
        BuildDiagnosticsSettings();
        BuildSaveFooter();
        Controls.Add(_windowLayout);
        ShowSettingsPage(SetupTab.Hotkey);
    }

    private void AddNavigation(TableLayoutPanel sidebar, SetupTab tab, string title)
    {
        var button = new Button
        {
            Text = title, Dock = DockStyle.Fill, Height = 42, FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0),
            Margin = new Padding(0, tab == SetupTab.Hotkey ? 24 : 4, 0, 0), Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => ShowSettingsPage(tab);
        _navigation.Add(tab, button);
        Add(sidebar, button);
    }

    private void ShowSettingsPage(SetupTab tab)
    {
        if (!_pages.ContainsKey(tab)) return;
        StopHotkeyRecording();
        _selectedTab = tab;
        foreach (var (key, page) in _pages) page.Visible = key == tab;
        foreach (var (key, button) in _navigation)
        {
            button.BackColor = key == tab ? Color.FromArgb(222, 232, 251) : Color.FromArgb(239, 242, 247);
            button.ForeColor = key == tab ? Accent : Color.FromArgb(52, 62, 80);
        }
        RefreshPermissionPolling();
    }

    private TableLayoutPanel Page(SetupTab tab, string title, string description)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(26, 22, 26, 18), Visible = false };
        var content = Stack();
        content.Dock = DockStyle.Top;
        Add(content, TextLabel(title, 18, true));
        var hint = TextLabel(description, 9, false, Muted);
        hint.Margin = new Padding(0, 5, 0, 18);
        Add(content, hint);
        scroll.Controls.Add(content);
        _pageHost.Controls.Add(scroll);
        _pages.Add(tab, scroll);
        return content;
    }

    private void BuildDictationSettings()
    {
        var page = Page(SetupTab.Hotkey, L10n.T("听写"), L10n.T("选择输入方式，按下快捷键即可开始说话。"));
        var hotkey = Card(page, L10n.T("快捷键"));
        StyleLabel(_hotkeyPreview, Accent, 17, true);
        Add(hotkey, _hotkeyPreview);
        StyleButton(_recordHotkeyButton, L10n.T("录制热键"), true);
        _recordHotkeyButton.Click += (_, _) => ToggleHotkeyRecording();
        StyleButton(_useRightCtrlButton, L10n.T("使用右 Ctrl"));
        _useRightCtrlButton.Click += (_, _) =>
        {
            foreach (var modifier in new[] { _modCtrl, _modAlt, _modShift, _modWin }) modifier.Checked = false;
            _hotkeyKey.Text = ModifierHotkeys.RightCtrl;
        };
        StyleButton(_useRightAltButton, L10n.T("使用右 Alt"));
        _useRightAltButton.Click += (_, _) =>
        {
            foreach (var modifier in new[] { _modCtrl, _modAlt, _modShift, _modWin }) modifier.Checked = false;
            _hotkeyKey.Text = ModifierHotkeys.RightAlt;
        };
        Add(hotkey, ButtonRow(_recordHotkeyButton, _useRightCtrlButton, _useRightAltButton));
        ConfigureCombo(_hotkeyModeCombo, Enum.GetValues<HotkeyMode>(), value => ((HotkeyMode)value).DisplayName());
        AddField(hotkey, L10n.T("触发方式"), L10n.T("按住说话，或按一次开始、再按一次结束。"), _hotkeyModeCombo);
        var manual = Stack();
        var modifiers = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        foreach (var (control, title) in new[] { (_modCtrl, "Ctrl"), (_modAlt, "Alt"), (_modShift, "Shift"), (_modWin, "Win") })
        {
            control.Text = title; control.AutoSize = true;
            control.CheckedChanged += (_, _) => UpdateHotkeyPreview();
            modifiers.Controls.Add(control);
        }
        Add(manual, modifiers);
        _hotkeyKey.TextChanged += (_, _) => UpdateHotkeyPreview();
        AddField(manual, L10n.T("主键"), L10n.T("字母、数字、F1–F12、右 Ctrl 或右 Alt。"), _hotkeyKey);
        AddDisclosure(hotkey, L10n.T("手动编辑快捷键"), manual);
        StyleLabel(_hotkeyMessage, Muted);
        Add(hotkey, _hotkeyMessage);

        var input = Card(page, L10n.T("语音输入"));
        _micDeviceCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        AddField(input, L10n.T("麦克风"), L10n.T("自动模式优先避免蓝牙通话模式影响声音。"), _micDeviceCombo);
        ConfigureCombo(_languageCombo, Enum.GetValues<AsrLanguage>(), value => ((AsrLanguage)value).DisplayName());
        AddField(input, L10n.T("识别语言"), L10n.T("通常使用自动识别，也可以指定语言。"), _languageCombo);
    }

    private void BuildModelSettings()
    {
        var page = Page(SetupTab.Recognition, L10n.T("语音模型"), L10n.T("模型保存在本机，准备完成后即可离线听写。"));
        var model = Card(page, "SenseVoice-Small");
        StyleLabel(_modelStatusLabel, Muted, 10, true);
        StyleLabel(_modelPathLabel, Muted, 8);
        StyleLabel(_modelErrorLabel, Color.Firebrick);
        _modelProgressBar.Dock = DockStyle.Top;
        _modelProgressBar.Height = 8;
        _modelProgressBar.Visible = false;
        StyleLabel(_modelProgressText, Muted);
        _modelProgressText.Visible = false;
        Add(model, _modelStatusLabel); Add(model, _modelPathLabel); Add(model, _modelProgressBar);
        Add(model, _modelProgressText); Add(model, _modelErrorLabel);
        StyleButton(_modelActionButton, L10n.T("开始下载模型"), true);
        _modelActionButton.Click += (_, _) => HandleModelAction();
        StyleButton(_modelDetailsButton, L10n.T("查看下载详情"));
        _modelDetailsButton.Visible = false;
        _modelDetailsButton.Click += (_, _) => ShowDownloadDetails();
        Add(model, ButtonRow(_modelActionButton, _modelDetailsButton));
        var performance = Card(page, L10n.T("运行方式"));
        _preloadCheck.Text = L10n.T("启动时预加载模型");
        _preloadCheck.AutoSize = true;
        Add(performance, _preloadCheck);
        Add(performance, TextLabel(L10n.T("提前准备识别引擎，减少首次听写的等待。"), 9, false, Muted));
        ConfigureNumber(_idleUnloadField, ConfigLimits.IdleUnloadMinutesMin, ConfigLimits.IdleUnloadMinutesMax, 0, 1);
        AddField(performance, L10n.T("空闲卸载（分钟）"), L10n.T("设为 0 时保持模型常驻。"), _idleUnloadField);
        var advanced = Stack();
        ConfigureNumber(_previewWindowField, ConfigLimits.PreviewWindowSecondsMin, ConfigLimits.PreviewWindowSecondsMax, 0, 1);
        AddField(advanced, L10n.T("预览窗口（秒）"), L10n.T("设为 0 时自动按本机性能校准。"), _previewWindowField);
        AddDisclosure(performance, L10n.T("高级设置"), advanced);
    }

    private void BuildCorrectionSettings()
    {
        var page = Page(SetupTab.Correction, L10n.T("智能纠错"), L10n.T("可选功能：仅发送识别后的文字，不发送音频。"));
        var card = Card(page, L10n.T("文字纠错"));
        _llmEnabledCheck.Text = L10n.T("启用智能纠错（LLM）");
        _llmEnabledCheck.AutoSize = true;
        Add(card, _llmEnabledCheck);
        var fields = Stack();
        _llmBaseUrlField.PlaceholderText = "https://api.openai.com/v1";
        AddField(fields, L10n.T("服务地址"), L10n.T("兼容 OpenAI 的 API 地址。"), _llmBaseUrlField);
        _llmApiKeyField.UseSystemPasswordChar = true;
        AddField(fields, "API Key", L10n.T("密钥由 Windows 加密保存。"), _llmApiKeyField);
        AddField(fields, L10n.T("模型名称"), L10n.T("填写服务提供方支持的模型名称。"), _llmModelField);
        var advanced = Stack();
        ConfigureNumber(_llmTemperatureField, (decimal)ConfigLimits.TemperatureMin, (decimal)ConfigLimits.TemperatureMax, 2, 0.05m);
        ConfigureNumber(_llmMaxTokensField, ConfigLimits.MaxTokensMin, ConfigLimits.MaxTokensMax, 0, 64);
        ConfigureNumber(_llmTimeoutField, (decimal)ConfigLimits.TimeoutSecondsMin, (decimal)ConfigLimits.TimeoutSecondsMax, 0, 1);
        AddField(advanced, L10n.T("温度"), L10n.T("数值越低，纠错结果越稳定。"), _llmTemperatureField);
        AddField(advanced, L10n.T("最大 Token"), L10n.T("限制纠错回复的长度。"), _llmMaxTokensField);
        AddField(advanced, L10n.T("超时（秒）"), L10n.T("超时后仍使用原识别文本。"), _llmTimeoutField);
        AddDisclosure(fields, L10n.T("高级设置"), advanced);
        StyleButton(_llmTestButton, L10n.T("测试纠错"));
        _llmTestButton.Click += async (_, _) => await HandleTestLlmCorrection();
        Add(fields, _llmTestButton);
        Add(card, fields);
        _llmEnabledCheck.CheckedChanged += (_, _) => fields.Visible = _llmEnabledCheck.Checked;
        fields.Visible = false;
        StyleLabel(_recognitionMessage, Muted);
        Add(card, _recognitionMessage);
    }

    private void BuildAppearanceSettings()
    {
        var page = Page(SetupTab.General, L10n.T("外观与通用"), L10n.T("调整浮窗显示和应用启动方式。"));
        var appearance = Card(page, L10n.T("听写浮窗"));
        ConfigureCombo(_hudPositionCombo, Enum.GetValues<HudPlacement>(), value => ((HudPlacement)value).DisplayName());
        AddField(appearance, L10n.T("浮窗位置"), L10n.T("选择听写反馈在屏幕上的位置。"), _hudPositionCombo);
        ConfigureNumber(_opacityField, (decimal)ConfigLimits.OpacityMin, (decimal)ConfigLimits.OpacityMax, 2, 0.05m);
        AddField(appearance, L10n.T("不透明度"), L10n.T("修改时立即预览；撤销后恢复。"), _opacityField);
        _opacityField.ValueChanged += (_, _) => { if (!_loading) OnPreviewHudOpacity?.Invoke((double)_opacityField.Value); };
        var general = Card(page, L10n.T("应用"));
        _startupCheck.Text = L10n.T("开机自启"); _startupCheck.AutoSize = true;
        Add(general, _startupCheck);
        ConfigureCombo(_interfaceLanguageCombo, Enum.GetValues<AppLanguage>(), value => ((AppLanguage)value).DisplayName());
        AddField(general, L10n.T("界面语言"), L10n.T("保存后重启应用，使全部界面文本生效。"), _interfaceLanguageCombo);
    }

    private void BuildDiagnosticsSettings()
    {
        var page = Page(SetupTab.Permissions, L10n.T("诊断与帮助"), L10n.T("检查麦克风访问状态，查看故障与使用说明。"));
        var microphone = Card(page, L10n.T("麦克风访问"));
        StyleLabel(_micStatusLabel, Muted, 10, true); Add(microphone, _micStatusLabel);
        StyleButton(_micRetryButton, L10n.T("重新检测"));
        _micRetryButton.Click += (_, _) => OnRetryMicProbe?.Invoke();
        StyleButton(_micOpenSettingsButton, L10n.T("打开麦克风设置"));
        _micOpenSettingsButton.Click += (_, _) => OpenMicrophonePrivacySettings();
        Add(microphone, ButtonRow(_micRetryButton, _micOpenSettingsButton));
        Add(microphone, TextLabel(L10n.T("如果无法向以管理员身份运行的应用输入文字，请让两个应用以相同权限运行。"), 9, false, Muted));
        var help = Card(page, L10n.T("帮助与诊断"));
        var logs = new Button(); StyleButton(logs, L10n.T("打开日志目录"));
        logs.Click += (_, _) => OpenLocalFolder(AppConstants.LogDirectory);
        var config = new Button(); StyleButton(config, L10n.T("打开配置目录"));
        config.Click += (_, _) => OpenLocalFolder(AppConstants.ConfigDirectory);
        Add(help, ButtonRow(logs, config));
        Add(help, TextLabel("Powered by SenseVoice-Small · FunAudioLLM", 9, false, Muted));
    }

    private void BuildSaveFooter()
    {
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2,
            Padding = new Padding(26, 14, 26, 14), BackColor = Color.White, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        StyleLabel(_generalMessage, Muted);
        footer.Controls.Add(_generalMessage, 0, 0);
        StyleButton(_discardButton, L10n.T("撤销更改"));
        _discardButton.Click += (_, _) => DiscardChanges();
        StyleButton(_saveRecognitionButton, L10n.T("保存并应用"), true);
        _saveRecognitionButton.Click += async (_, _) => await HandleSaveAll();
        var actions = ButtonRow(_discardButton, _saveRecognitionButton);
        actions.WrapContents = false;
        actions.Dock = DockStyle.None;
        actions.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        footer.Controls.Add(actions, 1, 0);
        _windowLayout.Controls.Add(footer, 1, 1);
        AcceptButton = _saveRecognitionButton;
    }

    private static TableLayoutPanel Stack() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
        Dock = DockStyle.Top, Margin = Padding.Empty, Padding = Padding.Empty,
        ColumnStyles = { new ColumnStyle(SizeType.Percent, 100) },
    };

    private static void Add(TableLayoutPanel stack, Control control)
    {
        var row = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(control, 0, row);
    }

    private static TableLayoutPanel Card(TableLayoutPanel page, string title)
    {
        var card = Stack();
        card.BackColor = Color.White;
        card.Padding = new Padding(20, 12, 20, 12);
        card.Margin = new Padding(0, 0, 0, 14);
        var heading = TextLabel(title, 11, true);
        heading.Margin = new Padding(0, 0, 0, 12);
        Add(card, heading); Add(page, card);
        return card;
    }

    private static Label TextLabel(string text, float size, bool bold = false, Color? color = null)
    {
        var label = new Label { Text = text };
        StyleLabel(label, color ?? Color.FromArgb(34, 43, 58), size, bold);
        return label;
    }

    private static void StyleLabel(Label label, Color color, float size = 9, bool bold = false)
    {
        label.AutoSize = true; label.Dock = DockStyle.Top;
        label.Margin = new Padding(0, 3, 0, 6);
        label.Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
        label.ForeColor = color;
        // 包括错误和长设备说明在内，所有文本随实际可用宽度换行，不依赖固定高度。
        label.ParentChanged += (_, _) =>
        {
            if (label.Parent is not { } parent) return;
            void ResizeLabel()
            {
                var width = parent.ClientSize.Width - parent.Padding.Horizontal;
                if (parent is TableLayoutPanel table)
                {
                    var columns = table.GetColumnWidths();
                    var column = table.GetColumn(label);
                    if (column >= 0 && column < columns.Length && columns[column] > 0) width = columns[column];
                }
                var maximum = new Size(Math.Max(40, width - label.Margin.Horizontal), 0);
                if (label.MaximumSize != maximum) label.MaximumSize = maximum;
            }
            parent.SizeChanged += (_, _) => ResizeLabel();
            parent.Layout += (_, _) => ResizeLabel();
            ResizeLabel();
        };
    }

    private static void StyleButton(Button button, string text, bool primary = false)
    {
        button.Text = text; button.AutoSize = true; button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(12, 6, 12, 6); button.MinimumSize = new Size(92, 34);
        button.FlatStyle = FlatStyle.Flat; button.Cursor = Cursors.Hand;
        button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(213, 220, 230);
        button.BackColor = primary ? Accent : Color.White;
        button.ForeColor = primary ? Color.White : Color.FromArgb(43, 54, 71);
        button.Margin = new Padding(0, 3, 8, 3);
    }

    private static FlowLayoutPanel ButtonRow(params Button[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top, WrapContents = true, Margin = Padding.Empty };
        row.Controls.AddRange(buttons);
        return row;
    }

    private static void AddField(TableLayoutPanel card, string title, string description, Control editor)
    {
        var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = new Padding(0, 6, 0, 6) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        var text = Stack(); text.Margin = new Padding(0, 0, 16, 0);
        Add(text, TextLabel(title, 9, true)); Add(text, TextLabel(description, 8, false, Muted));
        editor.Dock = DockStyle.Top;
        editor.Margin = new Padding(0, 5, 0, 0);
        row.Controls.Add(text, 0, 0); row.Controls.Add(editor, 1, 0); Add(card, row);
    }

    private static void ConfigureNumber(NumericUpDown number, decimal min, decimal max, int decimals, decimal step)
    {
        number.Minimum = min; number.Maximum = max; number.DecimalPlaces = decimals; number.Increment = step;
    }

    private static void ConfigureCombo<T>(ComboBox combo, T[] items, Func<object, string> display) where T : notnull
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.FormattingEnabled = true;
        foreach (var item in items) combo.Items.Add(item);
        combo.Format += (_, args) => { if (args.ListItem is not null) args.Value = display(args.ListItem); };
    }

    private static void AddDisclosure(TableLayoutPanel parent, string title, TableLayoutPanel content)
    {
        var toggle = new Button(); StyleButton(toggle, "+ " + title);
        toggle.FlatAppearance.BorderSize = 0; toggle.ForeColor = Accent;
        content.Visible = false;
        toggle.Click += (_, _) => { content.Visible = !content.Visible; toggle.Text = (content.Visible ? "− " : "+ ") + title; };
        Add(parent, toggle); Add(parent, content);
    }

    private void ShowDownloadDetails()
    {
        using var dialog = new Form { Text = L10n.T("下载诊断详情"), Size = new Size(680, 480),
            StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false };
        var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            WordWrap = false, Dock = DockStyle.Fill, Text = _downloadDetails.Replace("\n", Environment.NewLine) };
        dialog.Controls.Add(text);
        dialog.ShowDialog(this); // 标准文本框支持 Ctrl+A / Ctrl+C；不自动覆盖剪贴板。
    }

    private static void OpenLocalFolder(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception error) { AppLog.Error("ui", "打开诊断目录失败", error); }
    }

    private void WireDraftEvents(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            switch (control)
            {
                case TextBox text: text.TextChanged += (_, _) => RefreshDirtyState(); break;
                case ComboBox combo: combo.SelectedIndexChanged += (_, _) => RefreshDirtyState(); break;
                case CheckBox check: check.CheckedChanged += (_, _) => RefreshDirtyState(); break;
                case NumericUpDown number: number.ValueChanged += (_, _) => RefreshDirtyState(); break;
            }
            WireDraftEvents(control);
        }
    }

    internal AppConfig BuildDraft()
    {
        var draft = _loadedConfig.Clone();
        draft.Asr.LanguageValue = (AsrLanguage)(_languageCombo.SelectedItem ?? AsrLanguage.Auto);
        draft.Asr.IdleUnloadMinutes = (int)_idleUnloadField.Value;
        draft.Asr.PreviewWindowSeconds = (int)_previewWindowField.Value;
        draft.Asr.PreloadOnLaunch = _preloadCheck.Checked;
        draft.Audio.InputDevice = (_micDeviceCombo.SelectedItem as MicChoice)?.Value ?? AudioConfig.Auto;
        draft.UI.Opacity = (double)_opacityField.Value;
        draft.UI.InterfaceLanguageValue = (AppLanguage)(_interfaceLanguageCombo.SelectedItem ?? AppLanguage.Zh);
        draft.UI.HudPositionValue = (HudPlacement)(_hudPositionCombo.SelectedItem ?? HudPlacement.BottomCenter);
        var modifiers = new List<string>();
        if (_modCtrl.Checked) modifiers.Add("ctrl"); if (_modAlt.Checked) modifiers.Add("alt");
        if (_modShift.Checked) modifiers.Add("shift"); if (_modWin.Checked) modifiers.Add("win");
        var key = _hotkeyKey.Text.Trim().ToLowerInvariant();
        if (ModifierHotkeys.IsModifierOnlyKey(key)) modifiers.Clear();
        draft.Hotkey = new HotkeyConfig { Key = key, Modifiers = modifiers,
            ModeValue = (HotkeyMode)(_hotkeyModeCombo.SelectedItem ?? HotkeyMode.Hold) };
        draft.Llm = new LlmConfig { Enabled = _llmEnabledCheck.Checked, BaseUrl = _llmBaseUrlField.Text.Trim(),
            Model = _llmModelField.Text.Trim(), Temperature = (double)_llmTemperatureField.Value,
            MaxTokens = (int)_llmMaxTokensField.Value, Timeout = (double)_llmTimeoutField.Value };
        return draft;
    }

    internal bool HasUnsavedChanges => _dirty;

    private void RefreshDirtyState()
    {
        if (_loading || _saving) return;
        _dirty = JsonSerializer.Serialize(BuildDraft()) != JsonSerializer.Serialize(_loadedConfig)
            || _llmApiKeyField.Text != _loadedApiKey || _startupCheck.Checked != _loadedStartup;
        _saveRecognitionButton.Enabled = _discardButton.Enabled = _dirty;
        _generalMessage.Text = _dirty ? L10n.T("有未保存的更改") : L10n.T("所有更改已保存");
        _generalMessage.ForeColor = _dirty ? Accent : Muted;
    }

    internal void DiscardChanges()
    {
        StopHotkeyRecording();
        _dirty = false;
        LoadEditableContent(_loadedConfig);
        OnPreviewHudOpacity?.Invoke(_loadedConfig.UI.Opacity);
    }

    internal async Task HandleSaveAll()
    {
        if (_saving || OnSaveRecognition is null) return;
        StopHotkeyRecording();
        var draft = BuildDraft();
        if (!HotkeyService.IsSupportedKey(draft.Hotkey.Key)
            || (!ModifierHotkeys.IsModifierOnlyKey(draft.Hotkey.Key) && draft.Hotkey.Modifiers.Count == 0))
        {
            ShowSettingsPage(SetupTab.Hotkey);
            SetMessage(_generalMessage, L10n.T("请录制有效快捷键；组合键至少需要一个修饰键。"), Color.Firebrick);
            return;
        }
        var startup = _startupCheck.Checked;
        var startupChanged = startup != _loadedStartup;
        var languageChanged = draft.UI.InterfaceLanguageValue != _loadedConfig.UI.InterfaceLanguageValue;
        var apiKey = _llmApiKeyField.Text;
        string? newKey = apiKey == _loadedApiKey ? null : apiKey;
        _saving = true;
        _pageHost.Enabled = _saveRecognitionButton.Enabled = _discardButton.Enabled = false;
        SetMessage(_generalMessage, L10n.T("保存中..."), Muted);
        try
        {
            await OnSaveRecognition(draft, newKey).ConfigureAwait(true);
            _loadedConfig = draft.Clone(); _loadedApiKey = apiKey;
            // 先保存并应用配置，之后才改变自启；拒绝保存不会偷偷改变注册表。
            var startupOk = !startupChanged || StartupRegistration.SetEnabled(startup);
            _loadedStartup = StartupRegistration.IsEnabled;
            _startupCheck.Checked = _loadedStartup;
            _dirty = false;
            SetMessage(_generalMessage, !startupOk ? L10n.T("设置已保存，但开机自启更新失败，请重试。")
                : languageChanged ? L10n.T("界面语言已保存。请重启 VoiceTyper 使全部界面文本生效。")
                : L10n.T("设置已保存并生效。"), startupOk ? Color.SeaGreen : Color.Firebrick);
        }
        catch (Exception error)
        {
            AppLog.Warn("ui", $"设置保存失败：{error.GetType().Name}");
            SetMessage(_generalMessage, L10n.F("保存失败：{0}", error.Message), Color.Firebrick);
        }
        finally
        {
            _saving = false; _pageHost.Enabled = true;
            _saveRecognitionButton.Enabled = _discardButton.Enabled = _dirty;
        }
    }
}
