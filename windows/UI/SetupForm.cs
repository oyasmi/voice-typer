using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

internal enum SetupTab { Recognition = 0, Hotkey = 1, Permissions = 2, General = 3 }

/// <summary>
/// 设置窗口：识别 / 热键 / 权限 / 通用 四个 Tab（见 windows/DESIGN.md §5.6）。
/// 与 <c>client_windows_native/UI/SetupForm.cs</c> 相比："连接"Tab 被"识别"顶替（模型卡片 +
/// 语言 + LLM 纠错），新增"通用"（开机自启 / HUD 透明度 / 空闲卸载）。
/// 所有方法必须在 UI 线程调用。
/// </summary>
internal sealed class SetupForm : Form
{
    public Func<AppConfig, Task>? OnSaveConfig;
    public Action<string>? OnSaveLlmApiKey;
    public Action? OnStartModelDownload;
    public Action? OnCancelModelDownload;
    public Action? OnReloadModel;
    public Func<LlmConfig, string, Task<bool>>? OnTestLlmCorrection;
    public Action? OnRetryMicProbe;
    public Action<double>? OnPreviewHudOpacity;
    public Action? OnUserClosedWindow;

    private AppConfig _loadedConfig = new();
    private AsrState _lastAsrState = AsrState.Unloaded;

    // ─ 顶部横幅 ────────────────────────────────────────────────
    private readonly Panel _bannerPanel = new();
    private readonly Label _bannerLabel = new();
    private readonly Button _bannerOpenMicSettings = new();

    // ─ Tab 1：识别 ────────────────────────────────────────────
    private readonly Label _modelStatusLabel = new();
    private readonly Label _modelPathLabel = new();
    private readonly ProgressBar _modelProgressBar = new();
    private readonly Button _modelActionButton = new();
    private readonly ComboBox _languageCombo = new();
    private readonly CheckBox _llmEnabledCheck = new();
    private readonly TextBox _llmBaseUrlField = new();
    private readonly TextBox _llmApiKeyField = new();
    private readonly TextBox _llmModelField = new();
    private readonly NumericUpDown _llmTemperatureField = new();
    private readonly NumericUpDown _llmMaxTokensField = new();
    private readonly NumericUpDown _llmTimeoutField = new();
    private readonly Button _llmTestButton = new();
    private readonly Label _recognitionMessage = new();
    private readonly Button _saveRecognitionButton = new();

    // ─ Tab 2：热键 ────────────────────────────────────────────
    private readonly CheckBox _modCtrl = new();
    private readonly CheckBox _modAlt = new();
    private readonly CheckBox _modShift = new();
    private readonly CheckBox _modWin = new();
    private readonly TextBox _hotkeyKey = new();
    private readonly Label _hotkeyPreview = new();
    private readonly Button _saveHotkeyButton = new();
    private readonly Label _hotkeyMessage = new();

    // ─ Tab 3：权限 ────────────────────────────────────────────
    private readonly Label _micStatusLabel = new();
    private readonly Button _micRetryButton = new();
    private readonly Button _micOpenSettingsButton = new();

    // ─ Tab 4：通用 ────────────────────────────────────────────
    private readonly CheckBox _startupCheck = new();
    private readonly NumericUpDown _opacityField = new();
    private readonly NumericUpDown _idleUnloadField = new();
    private readonly NumericUpDown _previewWindowField = new();
    private readonly Button _saveGeneralButton = new();
    private readonly Label _generalMessage = new();

    private readonly TabControl _tabs = new();
    private readonly Label _versionLabel = new();

    public SetupForm()
    {
        Text = "VoiceTyper 设置";
        Size = new Size(760, 640);
        MinimumSize = new Size(680, 560);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = SystemColors.Control;

        BuildBanner();
        BuildTabs();
        BuildFooter();

        Controls.Add(_tabs);
        Controls.Add(_bannerPanel);
        Controls.Add(_versionLabel);
    }

    // ─────────────────────────────────────────────────────────
    // 公共接口
    // ─────────────────────────────────────────────────────────

    public void LoadEditableContent(AppConfig config)
    {
        _loadedConfig = config.Clone();

        _languageCombo.SelectedItem = config.Asr.LanguageValue;

        _llmEnabledCheck.Checked = config.Llm.Enabled;
        _llmBaseUrlField.Text = config.Llm.BaseUrl;
        _llmApiKeyField.Text = SecretStore.LoadLlmApiKey();
        _llmModelField.Text = config.Llm.Model;
        _llmTemperatureField.Value = (decimal)Math.Clamp(config.Llm.Temperature, 0, 2);
        _llmMaxTokensField.Value = Math.Clamp(config.Llm.MaxTokens, 64, 8000);
        _llmTimeoutField.Value = (decimal)Math.Clamp(config.Llm.Timeout, 1, 60);

        var mods = config.Hotkey.Modifiers.Select(m => m.ToLowerInvariant()).ToHashSet();
        _modCtrl.Checked = mods.Contains("ctrl") || mods.Contains("control");
        _modAlt.Checked = mods.Contains("alt") || mods.Contains("option");
        _modShift.Checked = mods.Contains("shift");
        _modWin.Checked = mods.Any(m => m is "win" or "win_l" or "win_r" or "super" or "command" or "cmd");
        _hotkeyKey.Text = config.Hotkey.Key;
        UpdateHotkeyPreview();

        _startupCheck.Checked = StartupRegistration.IsEnabled;
        _opacityField.Value = (decimal)Math.Clamp(config.UI.Opacity, 0.4, 1.0);
        _idleUnloadField.Value = Math.Clamp(config.Asr.IdleUnloadMinutes, 0, 120);
        _previewWindowField.Value = Math.Clamp(config.Asr.PreviewWindowSeconds, 0, 30);

        _recognitionMessage.Text = "";
        _hotkeyMessage.Text = "";
        _generalMessage.Text = "";
    }

    public void UpdateStatus(
        bool micAccessDenied,
        AsrState asrState,
        string? asrFailureMessage,
        double? downloadProgress,
        string hotkeyDisplay,
        string engineStatus)
    {
        if (micAccessDenied)
        {
            _bannerPanel.Visible = true;
            _bannerPanel.BackColor = Color.FromArgb(255, 245, 220);
            _bannerLabel.Text = "麦克风权限可能被禁用：请在 Windows 设置 → 隐私和安全 → 麦克风中允许桌面应用访问。";
            _bannerLabel.ForeColor = Color.FromArgb(120, 70, 0);
        }
        else
        {
            _bannerPanel.Visible = false;
        }

        _lastAsrState = asrState;
        _modelStatusLabel.Text = ModelStatusText(asrState, asrFailureMessage);
        _modelPathLabel.Text = asrState == AsrState.Ready ? $"模型目录：{ModelLocator.DownloadDestination}" : "";

        var progress = downloadProgress ?? 0;
        _modelProgressBar.Visible = asrState == AsrState.DownloadingModel;
        _modelProgressBar.Value = Math.Clamp((int)(progress * 100), 0, 100);

        (_modelActionButton.Text, _modelActionButton.Enabled) = asrState switch
        {
            AsrState.ModelMissing => ("开始下载模型", true),
            AsrState.DownloadingModel => ("取消下载", true),
            AsrState.Loading => ("加载中...", false),
            AsrState.Ready => ("重新加载模型", true),
            AsrState.Failed => ("重试加载", true),
            AsrState.Unloaded => ("重新加载模型", true),
            _ => ("重新加载模型", true),
        };

        _micStatusLabel.Text = micAccessDenied
            ? "麦克风不可用：可能被系统隐私设置阻止"
            : "麦克风可用";
        _micStatusLabel.ForeColor = micAccessDenied ? Color.Firebrick : Color.SeaGreen;
    }

    public void SelectTab(SetupTab tab)
    {
        if ((int)tab >= 0 && (int)tab < _tabs.TabPages.Count)
        {
            _tabs.SelectedIndex = (int)tab;
        }
    }

    public void Present()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private static string ModelStatusText(AsrState state, string? failureMessage) => state switch
    {
        AsrState.ModelMissing => "需要下载语音模型（约 230 MB）",
        AsrState.DownloadingModel => "正在下载模型...",
        AsrState.Loading => "模型加载中...",
        AsrState.Ready => "SenseVoice-Small（int8）· 已就绪",
        AsrState.Unloaded => "引擎未加载（空闲卸载后会在下次录音时自动重新加载）",
        AsrState.Failed => $"模型加载失败：{failureMessage}",
        _ => "",
    };

    // ─────────────────────────────────────────────────────────
    // UI 构建
    // ─────────────────────────────────────────────────────────

    private void BuildBanner()
    {
        _bannerPanel.Dock = DockStyle.Top;
        _bannerPanel.Height = 46;
        _bannerPanel.Padding = new Padding(14, 6, 14, 6);
        _bannerPanel.Visible = false;

        _bannerLabel.AutoSize = false;
        _bannerLabel.Dock = DockStyle.Fill;
        _bannerLabel.TextAlign = ContentAlignment.MiddleLeft;

        _bannerOpenMicSettings.Text = "打开麦克风设置";
        _bannerOpenMicSettings.AutoSize = true;
        _bannerOpenMicSettings.Dock = DockStyle.Right;
        _bannerOpenMicSettings.Click += (_, _) => OpenMicrophonePrivacySettings();

        _bannerPanel.Controls.Add(_bannerLabel);
        _bannerPanel.Controls.Add(_bannerOpenMicSettings);
    }

    private void BuildTabs()
    {
        _tabs.Dock = DockStyle.Fill;
        _tabs.Padding = new Point(14, 6);

        var recognitionPage = new TabPage("识别") { BackColor = SystemColors.Control };
        var hotkeyPage = new TabPage("热键") { BackColor = SystemColors.Control };
        var permissionsPage = new TabPage("权限") { BackColor = SystemColors.Control };
        var generalPage = new TabPage("通用") { BackColor = SystemColors.Control };

        BuildRecognitionPage(recognitionPage);
        BuildHotkeyPage(hotkeyPage);
        BuildPermissionsPage(permissionsPage);
        BuildGeneralPage(generalPage);

        _tabs.TabPages.Add(recognitionPage);
        _tabs.TabPages.Add(hotkeyPage);
        _tabs.TabPages.Add(permissionsPage);
        _tabs.TabPages.Add(generalPage);
    }

    private void BuildFooter()
    {
        _versionLabel.Text = $"版本 {AppConstants.Version} · Powered by SenseVoice-Small (FunAudioLLM)";
        _versionLabel.Dock = DockStyle.Bottom;
        _versionLabel.Height = 24;
        _versionLabel.Padding = new Padding(16, 4, 16, 4);
        _versionLabel.TextAlign = ContentAlignment.MiddleLeft;
        _versionLabel.ForeColor = Color.Gray;
        _versionLabel.Font = new Font(Font.FontFamily, 8f);
    }

    private void BuildRecognitionPage(TabPage page)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 16),
            ColumnCount = 2,
            AutoScroll = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 模型卡片
        layout.Controls.Add(MakeFieldLabel("语音模型："), 0, 0);
        var modelBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        _modelStatusLabel.AutoSize = true;
        _modelPathLabel.AutoSize = true;
        _modelPathLabel.ForeColor = Color.Gray;
        _modelPathLabel.Font = new Font(Font.FontFamily, 8f);
        _modelProgressBar.Width = 380;
        _modelProgressBar.Visible = false;
        _modelActionButton.Text = "开始下载模型";
        _modelActionButton.AutoSize = true;
        _modelActionButton.Padding = new Padding(10, 4, 10, 4);
        _modelActionButton.Margin = new Padding(0, 6, 0, 0);
        _modelActionButton.Click += (_, _) => HandleModelAction();
        modelBox.Controls.Add(_modelStatusLabel);
        modelBox.Controls.Add(_modelPathLabel);
        modelBox.Controls.Add(_modelProgressBar);
        modelBox.Controls.Add(_modelActionButton);
        layout.Controls.Add(modelBox, 1, 0);

        layout.Controls.Add(MakeFieldLabel("识别语言："), 0, 1);
        _languageCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _languageCombo.FormattingEnabled = true;
        _languageCombo.Width = 160;
        foreach (var lang in Enum.GetValues<AsrLanguage>())
        {
            _languageCombo.Items.Add(lang);
        }
        _languageCombo.Format += (_, e) =>
        {
            if (e.ListItem is AsrLanguage lang) e.Value = lang.DisplayName();
        };
        layout.Controls.Add(_languageCombo, 1, 1);

        // LLM 纠错分组
        _llmEnabledCheck.Text = "启用智能纠错（LLM）";
        _llmEnabledCheck.AutoSize = true;
        layout.Controls.Add(new Panel { Height = 8, Width = 1 }, 0, 2);
        layout.Controls.Add(_llmEnabledCheck, 1, 3);

        layout.Controls.Add(MakeFieldLabel("Base URL："), 0, 4);
        _llmBaseUrlField.Width = 380;
        _llmBaseUrlField.PlaceholderText = "https://api.openai.com/v1";
        layout.Controls.Add(_llmBaseUrlField, 1, 4);

        layout.Controls.Add(MakeFieldLabel("API Key："), 0, 5);
        _llmApiKeyField.Width = 380;
        _llmApiKeyField.UseSystemPasswordChar = true;
        layout.Controls.Add(_llmApiKeyField, 1, 5);

        layout.Controls.Add(MakeFieldLabel("模型："), 0, 6);
        _llmModelField.Width = 200;
        layout.Controls.Add(_llmModelField, 1, 6);

        var llmParamsRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _llmTemperatureField.DecimalPlaces = 1;
        _llmTemperatureField.Increment = 0.1m;
        _llmTemperatureField.Minimum = 0;
        _llmTemperatureField.Maximum = 2;
        _llmTemperatureField.Width = 70;
        _llmMaxTokensField.Minimum = 64;
        _llmMaxTokensField.Maximum = 8000;
        _llmMaxTokensField.Increment = 50;
        _llmMaxTokensField.Width = 80;
        _llmTimeoutField.Minimum = 1;
        _llmTimeoutField.Maximum = 60;
        _llmTimeoutField.Width = 70;
        llmParamsRow.Controls.Add(MakeInlineLabel("温度"));
        llmParamsRow.Controls.Add(_llmTemperatureField);
        llmParamsRow.Controls.Add(MakeInlineLabel("  最大 Token"));
        llmParamsRow.Controls.Add(_llmMaxTokensField);
        llmParamsRow.Controls.Add(MakeInlineLabel("  超时(秒)"));
        llmParamsRow.Controls.Add(_llmTimeoutField);
        layout.Controls.Add(MakeFieldLabel("参数："), 0, 7);
        layout.Controls.Add(llmParamsRow, 1, 7);

        _llmTestButton.Text = "测试纠错";
        _llmTestButton.AutoSize = true;
        _llmTestButton.Padding = new Padding(10, 4, 10, 4);
        _llmTestButton.Click += async (_, _) => await HandleTestLlmCorrection();
        layout.Controls.Add(_llmTestButton, 1, 8);

        _recognitionMessage.AutoSize = false;
        _recognitionMessage.Height = 40;
        _recognitionMessage.Width = 400;
        _recognitionMessage.ForeColor = Color.Gray;
        layout.Controls.Add(_recognitionMessage, 1, 9);

        _saveRecognitionButton.Text = "保存并应用";
        _saveRecognitionButton.AutoSize = true;
        _saveRecognitionButton.Padding = new Padding(10, 4, 10, 4);
        _saveRecognitionButton.Click += async (_, _) => await HandleSaveRecognition();
        layout.Controls.Add(_saveRecognitionButton, 1, 10);

        page.Controls.Add(layout);
    }

    private void BuildHotkeyPage(TabPage page)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 20, 20, 16),
            ColumnCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(MakeFieldLabel("修饰键："), 0, 0);
        var modsRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        _modCtrl.Text = "Ctrl";
        _modAlt.Text = "Alt";
        _modShift.Text = "Shift";
        _modWin.Text = "Win";
        foreach (var c in new[] { _modCtrl, _modAlt, _modShift, _modWin })
        {
            c.AutoSize = true;
            c.Margin = new Padding(0, 4, 16, 4);
            c.CheckedChanged += (_, _) => UpdateHotkeyPreview();
            modsRow.Controls.Add(c);
        }
        layout.Controls.Add(modsRow, 1, 0);

        layout.Controls.Add(MakeFieldLabel("主键："), 0, 1);
        _hotkeyKey.Width = 160;
        _hotkeyKey.TextChanged += (_, _) => UpdateHotkeyPreview();
        layout.Controls.Add(_hotkeyKey, 1, 1);

        layout.Controls.Add(MakeFieldLabel("预览："), 0, 2);
        _hotkeyPreview.AutoSize = true;
        _hotkeyPreview.Font = new Font("Consolas", 11f, FontStyle.Bold);
        _hotkeyPreview.ForeColor = Color.RoyalBlue;
        layout.Controls.Add(_hotkeyPreview, 1, 2);

        var hotkeyHint = new Label
        {
            Text = "支持的主键示例：a-z、0-9、space、tab、enter、esc、f1-f12、insert、delete、home/end、pageup/pagedown、↑↓←→",
            ForeColor = Color.Gray,
            AutoSize = false,
            Width = 460,
            Height = 36,
        };
        layout.Controls.Add(hotkeyHint, 1, 3);

        _hotkeyMessage.AutoSize = false;
        _hotkeyMessage.Height = 22;
        _hotkeyMessage.ForeColor = Color.Gray;
        layout.Controls.Add(_hotkeyMessage, 1, 4);

        _saveHotkeyButton.Text = "保存并应用";
        _saveHotkeyButton.AutoSize = true;
        _saveHotkeyButton.Padding = new Padding(10, 4, 10, 4);
        _saveHotkeyButton.Click += async (_, _) => await HandleSaveHotkey();
        layout.Controls.Add(_saveHotkeyButton, 1, 5);

        page.Controls.Add(layout);
    }

    private void BuildPermissionsPage(TabPage page)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 20, 20, 16),
            ColumnCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(MakeFieldLabel("麦克风："), 0, 0);
        var micRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _micStatusLabel.AutoSize = true;
        _micRetryButton.Text = "重新检测";
        _micRetryButton.AutoSize = true;
        _micRetryButton.Margin = new Padding(12, 0, 0, 0);
        _micRetryButton.Click += (_, _) => OnRetryMicProbe?.Invoke();
        _micOpenSettingsButton.Text = "打开麦克风设置";
        _micOpenSettingsButton.AutoSize = true;
        _micOpenSettingsButton.Margin = new Padding(12, 0, 0, 0);
        _micOpenSettingsButton.Click += (_, _) => OpenMicrophonePrivacySettings();
        micRow.Controls.Add(_micStatusLabel);
        micRow.Controls.Add(_micRetryButton);
        micRow.Controls.Add(_micOpenSettingsButton);
        layout.Controls.Add(micRow, 1, 0);

        var uipiNote = new Label
        {
            Text = "已知限制：以管理员身份运行的窗口（记事本、终端等）不会响应文本插入，"
                 + "这是 Windows UIPI 安全机制的限制，不是识别故障。识别结果仍会写入剪贴板，可手动粘贴。",
            ForeColor = Color.Gray,
            AutoSize = false,
            Width = 480,
            Height = 60,
        };
        layout.Controls.Add(new Panel { Height = 16, Width = 1 }, 0, 1);
        layout.Controls.Add(uipiNote, 1, 2);

        page.Controls.Add(layout);
    }

    private void BuildGeneralPage(TabPage page)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 20, 20, 16),
            ColumnCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _startupCheck.Text = "开机自启";
        _startupCheck.AutoSize = true;
        layout.Controls.Add(_startupCheck, 1, 0);

        layout.Controls.Add(MakeFieldLabel("HUD 不透明度："), 0, 1);
        _opacityField.DecimalPlaces = 2;
        _opacityField.Increment = 0.05m;
        _opacityField.Minimum = 0.4m;
        _opacityField.Maximum = 1.0m;
        _opacityField.Width = 80;
        _opacityField.ValueChanged += (_, _) => OnPreviewHudOpacity?.Invoke((double)_opacityField.Value);
        layout.Controls.Add(_opacityField, 1, 1);

        layout.Controls.Add(MakeFieldLabel("空闲卸载模型："), 0, 2);
        var idleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _idleUnloadField.Minimum = 0;
        _idleUnloadField.Maximum = 120;
        _idleUnloadField.Width = 70;
        idleRow.Controls.Add(_idleUnloadField);
        idleRow.Controls.Add(MakeInlineLabel(" 分钟（0 = 常驻不卸载）"));
        layout.Controls.Add(idleRow, 1, 2);

        layout.Controls.Add(MakeFieldLabel("预览窗口（进阶）："), 0, 3);
        var previewRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _previewWindowField.Minimum = 0;
        _previewWindowField.Maximum = 30;
        _previewWindowField.Width = 70;
        previewRow.Controls.Add(_previewWindowField);
        previewRow.Controls.Add(MakeInlineLabel(" 秒（0 = 首次加载后自动按本机性能校准）"));
        layout.Controls.Add(previewRow, 1, 3);

        _generalMessage.AutoSize = false;
        _generalMessage.Height = 22;
        _generalMessage.ForeColor = Color.Gray;
        layout.Controls.Add(_generalMessage, 1, 4);

        _saveGeneralButton.Text = "保存并应用";
        _saveGeneralButton.AutoSize = true;
        _saveGeneralButton.Padding = new Padding(10, 4, 10, 4);
        _saveGeneralButton.Click += async (_, _) => await HandleSaveGeneral();
        layout.Controls.Add(_saveGeneralButton, 1, 5);

        page.Controls.Add(layout);
    }

    private static Label MakeFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = 120,
        TextAlign = ContentAlignment.MiddleRight,
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
        Padding = new Padding(0, 4, 8, 0),
    };

    private static Label MakeInlineLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(0, 6, 0, 0),
    };

    // ─────────────────────────────────────────────────────────
    // 行为
    // ─────────────────────────────────────────────────────────

    private static void OpenMicrophonePrivacySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "ms-settings:privacy-microphone", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("ui", $"打开麦克风设置失败: {ex.Message}");
        }
    }

    private void HandleModelAction()
    {
        switch (_lastAsrState)
        {
            case AsrState.ModelMissing:
            case AsrState.Failed:
                OnStartModelDownload?.Invoke();
                break;
            case AsrState.DownloadingModel:
                OnCancelModelDownload?.Invoke();
                break;
            default:
                OnReloadModel?.Invoke();
                break;
        }
    }

    private void UpdateHotkeyPreview()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (_modCtrl.Checked) parts.Add("Ctrl");
        if (_modAlt.Checked) parts.Add("Alt");
        if (_modShift.Checked) parts.Add("Shift");
        if (_modWin.Checked) parts.Add("Win");
        var key = _hotkeyKey.Text.Trim();
        if (!string.IsNullOrEmpty(key)) parts.Add(key.ToUpperInvariant());
        _hotkeyPreview.Text = parts.Count == 0 ? "—" : string.Join("+", parts);
    }

    private async Task HandleSaveRecognition()
    {
        if (OnSaveConfig is null) return;

        var draft = _loadedConfig.Clone();
        draft.Asr.LanguageValue = (AsrLanguage)(_languageCombo.SelectedItem ?? AsrLanguage.Auto);
        draft.Llm = new LlmConfig
        {
            Enabled = _llmEnabledCheck.Checked,
            BaseUrl = _llmBaseUrlField.Text.Trim(),
            Model = string.IsNullOrWhiteSpace(_llmModelField.Text) ? "gpt-4o-mini" : _llmModelField.Text.Trim(),
            Temperature = (double)_llmTemperatureField.Value,
            MaxTokens = (int)_llmMaxTokensField.Value,
            Timeout = (double)_llmTimeoutField.Value,
        };

        _saveRecognitionButton.Enabled = false;
        SetMessage(_recognitionMessage, "保存中...", Color.Gray);
        try
        {
            OnSaveLlmApiKey?.Invoke(_llmApiKeyField.Text);
            await OnSaveConfig(draft).ConfigureAwait(true);
            _loadedConfig = draft;
            SetMessage(_recognitionMessage, "设置已保存并生效。", Color.SeaGreen);
        }
        catch (Exception ex)
        {
            SetMessage(_recognitionMessage, $"保存失败：{ex.Message}", Color.Firebrick);
        }
        finally
        {
            _saveRecognitionButton.Enabled = true;
        }
    }

    private async Task HandleTestLlmCorrection()
    {
        if (OnTestLlmCorrection is null) return;

        var llmConfig = new LlmConfig
        {
            Enabled = true,
            BaseUrl = _llmBaseUrlField.Text.Trim(),
            Model = string.IsNullOrWhiteSpace(_llmModelField.Text) ? "gpt-4o-mini" : _llmModelField.Text.Trim(),
            Temperature = (double)_llmTemperatureField.Value,
            MaxTokens = (int)_llmMaxTokensField.Value,
            Timeout = (double)_llmTimeoutField.Value,
        };

        _llmTestButton.Enabled = false;
        SetMessage(_recognitionMessage, "正在测试纠错...", Color.Gray);
        try
        {
            var ok = await OnTestLlmCorrection(llmConfig, _llmApiKeyField.Text).ConfigureAwait(true);
            SetMessage(
                _recognitionMessage,
                ok ? "纠错测试成功，配置可用。" : "纠错测试未产生预期结果，请检查 Base URL / API Key / 模型名。",
                ok ? Color.SeaGreen : Color.Firebrick
            );
        }
        catch (Exception ex)
        {
            SetMessage(_recognitionMessage, $"纠错测试失败：{ex.Message}", Color.Firebrick);
        }
        finally
        {
            _llmTestButton.Enabled = true;
        }
    }

    private async Task HandleSaveHotkey()
    {
        if (OnSaveConfig is null) return;

        var key = _hotkeyKey.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
        {
            SetMessage(_hotkeyMessage, "主键不能为空", Color.Firebrick);
            return;
        }

        var mods = new System.Collections.Generic.List<string>();
        if (_modCtrl.Checked) mods.Add("ctrl");
        if (_modAlt.Checked) mods.Add("alt");
        if (_modShift.Checked) mods.Add("shift");
        if (_modWin.Checked) mods.Add("win");

        var draft = _loadedConfig.Clone();
        draft.Hotkey = new HotkeyConfig { Modifiers = mods, Key = key };

        _saveHotkeyButton.Enabled = false;
        SetMessage(_hotkeyMessage, "保存中...", Color.Gray);
        try
        {
            await OnSaveConfig(draft).ConfigureAwait(true);
            _loadedConfig = draft;
            SetMessage(_hotkeyMessage, "热键已保存并生效。", Color.SeaGreen);
        }
        catch (Exception ex)
        {
            SetMessage(_hotkeyMessage, $"保存失败：{ex.Message}", Color.Firebrick);
        }
        finally
        {
            _saveHotkeyButton.Enabled = true;
        }
    }

    private async Task HandleSaveGeneral()
    {
        if (OnSaveConfig is null) return;

        var draft = _loadedConfig.Clone();
        draft.UI.Opacity = (double)_opacityField.Value;
        draft.Asr.IdleUnloadMinutes = (int)_idleUnloadField.Value;
        draft.Asr.PreviewWindowSeconds = (int)_previewWindowField.Value;

        _saveGeneralButton.Enabled = false;
        SetMessage(_generalMessage, "保存中...", Color.Gray);
        try
        {
            StartupRegistration.SetEnabled(_startupCheck.Checked);
            await OnSaveConfig(draft).ConfigureAwait(true);
            _loadedConfig = draft;
            SetMessage(_generalMessage, "设置已保存并生效。", Color.SeaGreen);
        }
        catch (Exception ex)
        {
            SetMessage(_generalMessage, $"保存失败：{ex.Message}", Color.Firebrick);
        }
        finally
        {
            _saveGeneralButton.Enabled = true;
        }
    }

    private static void SetMessage(Label label, string text, Color color)
    {
        label.Text = text;
        label.ForeColor = color;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 关闭按钮 → 隐藏到托盘
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            OnUserClosedWindow?.Invoke();
        }
        base.OnFormClosing(e);
    }
}
