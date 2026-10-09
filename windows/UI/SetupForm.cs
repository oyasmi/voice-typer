using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using VoiceTyper.Llm;
using VoiceTyper.Services;
using VoiceTyper.Support;
using static VoiceTyper.Support.NativeMethods;

namespace VoiceTyper.UI;

internal enum SetupTab { Recognition = 0, Hotkey = 1, Permissions = 2, General = 3, Correction = 4 }

/// <summary>麦克风下拉框的一项：<see cref="Value"/> 是写进 <c>audio.input_device</c> 的值。</summary>
internal sealed record MicChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 设置窗口：侧栏导航、跨页草稿和统一保存。
/// 所有方法必须在 UI 线程调用。
/// </summary>
internal sealed partial class SetupForm : Form
{
    /// <summary>统一保存：draft + 新 API Key（null = 密钥未改动）。由协调器作为单一事务处理
    /// —— 先校验、先判断是否允许应用，通过之后再依次写密钥与配置，任一步失败给出分状态提示，
    /// 不出现"密钥已落盘但提示保存失败"（R2-4）。</summary>
    public Func<AppConfig, string?, Task>? OnSaveRecognition;
    /// <summary>区分"从未保存"与"读取失败"（DPAPI 解密失败等）；后者应向用户展示明确提示（R4-06）。</summary>
    public Func<(SecretReadStatus Status, string ApiKey)>? OnLoadLlmApiKey;
    public Action? OnStartModelDownload;
    public Action? OnCancelModelDownload;
    /// <summary>请求重新加载模型；返回非 null 表示被拒绝（如正在听写），内容是展示给用户的原因。</summary>
    public Func<string?>? OnReloadModel;
    public Func<LlmConfig, string, Task<LlmTestResult>>? OnTestLlmCorrection;
    /// <summary>用户手动点击"重新检测"。</summary>
    public Action? OnRetryMicProbe;
    /// <summary>权限页轮询触发的自动探测；与手动检测分开，协调器可在听写中跳过它。</summary>
    public Action? OnPollMicProbe;
    public Action<double>? OnPreviewHudOpacity;
    public Action? OnUserClosedWindow;
    /// <summary>开始录制热键前暂停全局热键监听（否则按下当前热键会触发听写，而不是被录进来）。
    /// 返回 false 表示拒绝（例如正在听写）。</summary>
    public Func<bool>? OnBeginHotkeyRecording;
    public Action? OnEndHotkeyRecording;

    private AppConfig _loadedConfig = new();
    /// <summary>已载入配置的序列化结果，供脏检查比较；随 <see cref="_loadedConfig"/> 一起更新（见 <see cref="SetLoadedConfig"/>）。
    /// 脏检查在每次按键、每个控件变化时都会运行，已载入的一侧不必每次重新序列化。</summary>
    private string? _loadedConfigJson;

    private void SetLoadedConfig(AppConfig config)
    {
        _loadedConfig = config;
        _loadedConfigJson = null;
    }
    private AsrState _lastAsrState = AsrState.Unloaded;
    private bool _lastIsDownloading;
    private MicProbeResult _lastMicProbe = MicProbeResult.Unknown;
    /// <summary>权限页可见且麦克风未就绪时的自愈轮询（R4-14：只刷新勾选状态，绝不
    /// 在轮询路径里抢焦点）。</summary>
    private readonly System.Windows.Forms.Timer _permissionPollTimer = new() { Interval = 4000 };

    // ─ 顶部横幅 ────────────────────────────────────────────────
    private readonly Panel _bannerPanel = new();
    private readonly Label _bannerLabel = new();
    private readonly Button _bannerOpenMicSettings = new();

    // ─ Tab 1：识别 ────────────────────────────────────────────
    private readonly Label _modelStatusLabel = new();
    private readonly Label _modelPathLabel = new();
    private readonly Label _modelErrorLabel = new();
    private readonly ProgressBar _modelProgressBar = new();
    private readonly Button _modelActionButton = new();
    private readonly ComboBox _languageCombo = new();
    private readonly CheckBox _llmEnabledCheck = new();
    private readonly TextBox _llmBaseUrlField = new();
    private readonly TextBox _llmApiKeyField = new();
    /// <summary>LoadEditableContent 时载入的 API Key 原值，用于判断用户是否改动了密钥。</summary>
    private string _loadedApiKey = "";
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
    private readonly ComboBox _hotkeyModeCombo = new();
    private readonly Button _useRightCtrlButton = new();
    private readonly Button _useRightAltButton = new();
    private readonly Button _recordHotkeyButton = new();
    private bool _isRecordingHotkey;

    private readonly Label _hotkeyMessage = new();

    // ─ Tab 3：权限 ────────────────────────────────────────────
    private readonly Label _micStatusLabel = new();
    private readonly Button _micRetryButton = new();
    private readonly Button _micOpenSettingsButton = new();

    // ─ Tab 4：通用 ────────────────────────────────────────────
    private readonly CheckBox _startupCheck = new();
    private readonly ComboBox _interfaceLanguageCombo = new();
    private readonly NumericUpDown _opacityField = new();
    private readonly NumericUpDown _idleUnloadField = new();
    private readonly NumericUpDown _previewWindowField = new();
    private readonly ComboBox _micDeviceCombo = new();
    private readonly ComboBox _hudPositionCombo = new();
    private readonly CheckBox _preloadCheck = new();

    private readonly Label _generalMessage = new();

    private SetupTab _selectedTab = SetupTab.Hotkey;
    private readonly Label _versionLabel = new();
    private readonly TableLayoutPanel _windowLayout = new();

    public SetupForm()
    {
        Text = L10n.F("{0} 设置", "VoiceTyper");
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(1080, 800);
        MinimumSize = new Size(860, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = UiFonts.Get(9f);
        BackColor = Color.FromArgb(247, 248, 250);
        BuildSettingsLayout();
        WireDraftEvents(this);
        VisibleChanged += (_, _) =>
        {
            RefreshPermissionPolling();
            if (!Visible)
            {
                StopHotkeyRecording();
                OnPreviewHudOpacity?.Invoke(_loadedConfig.UI.Opacity);
            }
            else if (_dirty) OnPreviewHudOpacity?.Invoke((double)_opacityField.Value);
        };
        Deactivate += (_, _) => StopHotkeyRecording();
        _permissionPollTimer.Tick += (_, _) => OnPollMicProbe?.Invoke();
    }
    /// <summary>首次显示前按所在屏幕的可用区域收紧窗口：设计尺寸按逻辑像素写死，
    /// 缩放后在常见笔记本屏幕上会超出可用区域（标题栏或保存按钮落到屏幕外）。</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var workingArea = Screen.FromControl(this).WorkingArea;
        var (size, minimum) = WindowFit.Fit(Size, MinimumSize, workingArea);
        if (size == Size && minimum == MinimumSize) return;
        MinimumSize = minimum; // 先降最小尺寸，否则缩小尺寸会被抬回去
        Size = size;
        Location = WindowFit.Center(size, workingArea);
    }

    /// <summary>
    /// 权限页可见且权限未齐时启动 2–5s 轮询（对齐 macOS bb25282 权限页轮询、5ac5aab R4-14
    /// 的抢焦点修复）；离开权限页/窗口隐藏/权限已齐时停掉。真开一次 WASAPI 采集才能探测
    /// 麦克风可用性，2s 一次会让系统托盘的"麦克风使用中"指示灯反复闪烁，故放宽到 4s
    /// （Windows 独有的适配，需真机复测）。
    /// </summary>
    private void RefreshPermissionPolling()
    {
        var shouldPoll = Visible
            && _selectedTab == SetupTab.Permissions
            && _lastMicProbe is MicProbeResult.AccessDenied or MicProbeResult.NoDevice or MicProbeResult.DeviceFailure;

        if (shouldPoll && !_permissionPollTimer.Enabled)
        {
            _permissionPollTimer.Start();
        }
        else if (!shouldPoll && _permissionPollTimer.Enabled)
        {
            _permissionPollTimer.Stop();
        }
    }

    // ─────────────────────────────────────────────────────────
    // 公共接口
    // ─────────────────────────────────────────────────────────

    public void LoadEditableContent(AppConfig config, bool refreshDevices = true)
    {
        if (_dirty && !_saving) return; // 后台状态刷新或重复打开不能覆盖用户的草稿。
        _loading = true;
        SetLoadedConfig(config.Clone());

        _languageCombo.SelectedItem = config.Asr.LanguageValue;

        _llmEnabledCheck.Checked = config.Llm.Enabled;
        _llmBaseUrlField.Text = config.Llm.BaseUrl;
        var (apiKeyStatus, apiKey) = OnLoadLlmApiKey?.Invoke() ?? (SecretReadStatus.NotSaved, "");
        _llmApiKeyField.Text = apiKey;
        _loadedApiKey = apiKey;
        _llmModelField.Text = config.Llm.Model;
        _llmTemperatureField.Value = (decimal)Math.Clamp(config.Llm.Temperature, ConfigLimits.TemperatureMin, ConfigLimits.TemperatureMax);
        _llmMaxTokensField.Value = Math.Clamp(config.Llm.MaxTokens, ConfigLimits.MaxTokensMin, ConfigLimits.MaxTokensMax);
        _llmTimeoutField.Value = (decimal)Math.Clamp(config.Llm.Timeout, ConfigLimits.TimeoutSecondsMin, ConfigLimits.TimeoutSecondsMax);

        var mods = config.Hotkey.Modifiers.Select(m => m.ToLowerInvariant()).ToHashSet();
        _modCtrl.Checked = mods.Contains("ctrl") || mods.Contains("control");
        _modAlt.Checked = mods.Contains("alt") || mods.Contains("option");
        _modShift.Checked = mods.Contains("shift");
        _modWin.Checked = mods.Any(m => m is "win" or "win_l" or "win_r" or "super" or "command" or "cmd");
        _hotkeyKey.Text = config.Hotkey.Key;
        _hotkeyModeCombo.SelectedItem = config.Hotkey.ModeValue;
        UpdateHotkeyPreview();

        _startupCheck.Checked = StartupRegistration.IsEnabled;
        _interfaceLanguageCombo.SelectedItem = config.UI.InterfaceLanguageValue;
        _opacityField.Value = (decimal)Math.Clamp(config.UI.Opacity, ConfigLimits.OpacityMin, ConfigLimits.OpacityMax);
        _idleUnloadField.Value = Math.Clamp(config.Asr.IdleUnloadMinutes, ConfigLimits.IdleUnloadMinutesMin, ConfigLimits.IdleUnloadMinutesMax);
        _previewWindowField.Value = Math.Clamp(config.Asr.PreviewWindowSeconds, ConfigLimits.PreviewWindowSecondsMin, ConfigLimits.PreviewWindowSecondsMax);
        _preloadCheck.Checked = config.Asr.PreloadOnLaunch;
        _hudPositionCombo.SelectedItem = config.UI.HudPositionValue;
        if (refreshDevices) RefreshMicChoices(config.Audio.InputDevice);
        else
        {
            _micDeviceCombo.Items.Clear();
            _micDeviceCombo.Items.Add(new MicChoice(config.Audio.InputDevice, config.Audio.InputDevice));
            _micDeviceCombo.SelectedIndex = 0;
        }

        if (apiKeyStatus == SecretReadStatus.Failed)
        {
            SetMessage(_recognitionMessage, L10n.T("无法读取已保存的 API Key，请重新填写并保存。"), Color.Firebrick);
        }
        else
        {
            _recognitionMessage.Text = "";
        }
        _hotkeyMessage.Text = "";
        _hotkeyMessage.Visible = false;
        _generalMessage.Text = "";
        _loadedStartup = _startupCheck.Checked;
        _loading = false;
        RefreshDirtyState();
    }

    public void UpdateStatus(
        MicProbeResult micProbe,
        AsrState asrState,
        string? asrFailureMessage,
        double? downloadProgress,
        string hotkeyDisplay,
        string engineStatus,
        string? downloadError = null, string? downloadDetails = null,
        string? modelDirectory = null, string? micProbeDetail = null)
    {
        // 下载态不是 AsrState 的成员——下载是 AppCoordinator 的职责，用 downloadProgress
        // 是否非空判定，与 macOS syncSetupWindow(downloadProgress:) 结构一致（W-00）。
        var isDownloading = downloadProgress is not null;

        var micBanner = micProbe switch
        {
            MicProbeResult.AccessDenied => L10n.T("麦克风权限可能被禁用：请在 Windows 设置 → 隐私和安全 → 麦克风中允许桌面应用访问。"),
            MicProbeResult.NoDevice => L10n.T("未检测到麦克风设备：请插入麦克风或在系统声音设置中启用输入设备。"),
            MicProbeResult.DeviceFailure => L10n.T("麦克风设备打开失败：可能被其他应用独占，或驱动异常。"),
            MicProbeResult.Silent => L10n.T("麦克风已打开但只收到静音：请确认「让桌面应用访问你的麦克风」已打开，且麦克风没有被静音。"),
            _ => null,
        };
        if (micBanner is not null)
        {
            // 「被独占 / 驱动异常」只是按分类的猜测；真实原因（HRESULT、声道数、端点错误）来自探测，
            // 拼在后面让用户与日志可以直接对照，不再需要靠猜。
            if (micProbe == MicProbeResult.DeviceFailure && !string.IsNullOrWhiteSpace(micProbeDetail))
                micBanner += $"（{micProbeDetail}）";
            _bannerPanel.Visible = true;
            _bannerPanel.BackColor = Color.FromArgb(255, 245, 220);
            _bannerLabel.Text = micBanner;
            _bannerLabel.ForeColor = Color.FromArgb(120, 70, 0);
        }
        else
        {
            _bannerPanel.Visible = false;
        }

        _lastAsrState = asrState;
        _lastIsDownloading = isDownloading;
        _modelStatusLabel.Text = ModelStatusText(asrState, isDownloading, asrFailureMessage);
        // 显示模型实际所在的目录（可能来自 ModelScope 缓存或手动指定的 model_dir），而不是一律显示下载目录。
        _modelPathLabel.Text = (asrState is AsrState.Ready or AsrState.SuspendedForIdle) && !string.IsNullOrEmpty(modelDirectory)
            ? L10n.F("模型目录：{0}", modelDirectory)
            : "";
        _modelErrorLabel.Text = downloadError ?? "";
        _downloadDetails = downloadDetails ?? "";
        _modelDetailsButton.Visible = !string.IsNullOrEmpty(_downloadDetails);
        _modelStatusLabel.ForeColor = asrState == AsrState.Ready ? Color.SeaGreen : ForeColor;
        _modelErrorLabel.Visible = !string.IsNullOrEmpty(downloadError);

        var progress = downloadProgress ?? 0;
        _modelProgressBar.Visible = isDownloading;
        _modelProgressBar.Value = Math.Clamp((int)(progress * 100), 0, 100);
        _modelProgressText.Visible = isDownloading;
        _modelProgressText.Text = L10n.F("已下载 {0:F1} / {1:F1} MB · {2}%", progress * ModelDownloader.TotalBytes / 1_000_000d,
            ModelDownloader.TotalBytes / 1_000_000d, (int)(progress * 100));

        (_modelActionButton.Text, _modelActionButton.Enabled) = isDownloading
            ? (L10n.T("取消下载"), true)
            : asrState switch
            {
                AsrState.ModelMissing => (string.IsNullOrEmpty(downloadError) ? L10n.T("开始下载模型") : L10n.T("重试下载"), true),
                AsrState.Loading => (L10n.T("加载中..."), false),
                AsrState.Ready => (L10n.T("重新加载模型"), true),
                AsrState.SuspendedForIdle => (L10n.T("重新加载模型"), true),
                AsrState.Failed => (L10n.T("重试加载"), true),
                AsrState.Unloaded => (L10n.T("重新加载模型"), true),
                _ => (L10n.T("重新加载模型"), true),
            };

        (_micStatusLabel.Text, _micStatusLabel.ForeColor) = micProbe switch
        {
            MicProbeResult.Available => (L10n.T("麦克风可用"), Color.SeaGreen),
            MicProbeResult.Silent => (L10n.T("麦克风已打开，但只收到静音"), Color.DarkGoldenrod),
            MicProbeResult.AccessDenied => (L10n.T("麦克风不可用：被系统隐私设置阻止"), Color.Firebrick),
            MicProbeResult.NoDevice => (L10n.T("未检测到麦克风设备"), Color.Firebrick),
            MicProbeResult.DeviceFailure => (L10n.T("麦克风打开失败：可能被其他应用占用"), Color.Firebrick),
            _ => (L10n.T("麦克风状态未知"), Color.DarkGoldenrod),
        };

        _lastMicProbe = micProbe;
        RefreshPermissionPolling();
    }

    public void SelectTab(SetupTab tab)
    {
        ShowSettingsPage(tab);
    }
    public void Present()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private static string ModelStatusText(AsrState state, bool isDownloading, string? failureMessage)
    {
        if (isDownloading) return L10n.T("正在下载模型...");

        return state switch
        {
            AsrState.ModelMissing => L10n.T("需要下载语音模型（约 230 MB）"),
            AsrState.Loading => L10n.T("模型加载中..."),
            AsrState.Ready => L10n.T("SenseVoice-Small（int8）· 已就绪"),
            AsrState.SuspendedForIdle => L10n.T("引擎已空闲卸载（下次录音自动重新加载）"),
            AsrState.Unloaded => L10n.T("引擎未加载（空闲卸载后会在下次录音时自动重新加载）"),
            AsrState.Failed => L10n.F("模型加载失败：{0}", failureMessage),
            _ => "",
        };
    }

    // ─────────────────────────────────────────────────────────
    // UI 构建
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
        if (_lastIsDownloading)
        {
            OnCancelModelDownload?.Invoke();
            return;
        }

        switch (_lastAsrState)
        {
            case AsrState.ModelMissing:

                OnStartModelDownload?.Invoke();
                break;
            default:
                if (OnReloadModel?.Invoke() is { } rejection) ShowModelActionMessage(rejection);
                break;
        }
    }

    /// <summary>在模型卡片的状态行显示一条操作被拒绝的说明；下一次状态刷新会恢复正常文案。</summary>
    internal void ShowModelActionMessage(string message)
    {
        _modelStatusLabel.Text = message;
        _modelStatusLabel.ForeColor = Color.FromArgb(180, 100, 0);
    }

    private void UpdateHotkeyPreview()
    {
        var key = _hotkeyKey.Text.Trim();
        // 单独修饰键本身就是完整热键：组合键的修饰键选择不再适用。
        var modifierOnly = ModifierHotkeys.IsModifierOnlyKey(key);
        foreach (var c in new[] { _modCtrl, _modAlt, _modShift, _modWin }) c.Enabled = !modifierOnly;
        if (modifierOnly)
        {
            _hotkeyPreview.Text = ModifierHotkeys.DisplayName(key) ?? key;
            return;
        }

        var parts = new System.Collections.Generic.List<string>();
        if (_modCtrl.Checked) parts.Add("Ctrl");
        if (_modAlt.Checked) parts.Add("Alt");
        if (_modShift.Checked) parts.Add("Shift");
        if (_modWin.Checked) parts.Add("Win");
        if (!string.IsNullOrEmpty(key)) parts.Add(key.ToUpperInvariant());
        _hotkeyPreview.Text = parts.Count == 0 ? "—" : string.Join("+", parts);
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
        SetMessage(_recognitionMessage, L10n.T("正在测试纠错..."), Color.Gray);
        try
        {
            var result = await OnTestLlmCorrection(llmConfig, _llmApiKeyField.Text).ConfigureAwait(true);
            SetMessage(_recognitionMessage, result.Message, result.Ok ? Color.SeaGreen : Color.Firebrick);
        }
        catch (Exception ex)
        {
            SetMessage(_recognitionMessage, L10n.F("纠错测试失败：{0}", ex.Message), Color.Firebrick);
        }
        finally
        {
            _llmTestButton.Enabled = true;
        }
    }

    // ─── 录制热键 ─────────────────────────────────────────────

    private void ToggleHotkeyRecording()
    {
        if (_isRecordingHotkey)
        {
            StopHotkeyRecording();
            return;
        }
        if (OnBeginHotkeyRecording?.Invoke() == false)
        {
            SetMessage(_hotkeyMessage, L10n.T("正在听写，请等这一段结束后再录制热键。"), Color.Firebrick);
            return;
        }
        _isRecordingHotkey = true;
        _recordHotkeyButton.Text = L10n.T("停止录制");
        SetMessage(_hotkeyMessage, L10n.T("请按下要使用的快捷键（Esc 取消）……"), Color.RoyalBlue);
        ActiveControl = null; // 焦点离开输入框，按键才会走到 ProcessCmdKey
    }

    private void StopHotkeyRecording()
    {
        if (!_isRecordingHotkey) return;
        _isRecordingHotkey = false;
        _recordHotkeyButton.Text = L10n.T("录制热键");
        OnEndHotkeyRecording?.Invoke();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_isRecordingHotkey) return base.ProcessCmdKey(ref msg, keyData);

        var code = (int)(keyData & Keys.KeyCode);
        var modifiers = keyData & Keys.Modifiers;
        var win = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
        var result = HotkeyRecording.Interpret(code, modifiers.HasFlag(Keys.Control), modifiers.HasFlag(Keys.Alt), modifiers.HasFlag(Keys.Shift), win);
        switch (result.Kind)
        {
            case HotkeyRecording.Kind.WaitForMore:
                break;
            case HotkeyRecording.Kind.Cancel:
                SetMessage(_hotkeyMessage, L10n.T("已取消录制。"), Color.Gray);
                StopHotkeyRecording();
                break;
            case HotkeyRecording.Kind.NeedModifier:
                SetMessage(_hotkeyMessage, L10n.T("至少选择一个修饰键（Ctrl/Alt/Shift/Win），否则会拦截普通输入。"), Color.Firebrick);
                break;
            case HotkeyRecording.Kind.Unsupported:
                SetMessage(_hotkeyMessage, L10n.T("这个键不能用作热键，请换一个（字母、数字、F1–F12、方向键等）。"), Color.Firebrick);
                break;
            default:
                _modCtrl.Checked = result.Modifiers!.Contains("ctrl");
                _modAlt.Checked = result.Modifiers!.Contains("alt");
                _modShift.Checked = result.Modifiers!.Contains("shift");
                _modWin.Checked = result.Modifiers!.Contains("win");
                _hotkeyKey.Text = result.Key!;
                UpdateHotkeyPreview();
                SetMessage(_hotkeyMessage, L10n.F("已录制：{0}，点「保存并应用」生效。", _hotkeyPreview.Text), Color.SeaGreen);
                StopHotkeyRecording();
                break;
        }
        return true; // 录制期间吞掉所有按键，不让它们落到窗口里的别的控件上
    }

    /// <summary>刷新麦克风下拉框：自动 / 跟随系统 / 当前所有输入设备。已保存的设备不在线时保留一项，
    /// 免得保存别的设置时把它悄悄改回"自动"。</summary>
    private void RefreshMicChoices(string selectedValue)
    {
        var choices = new System.Collections.Generic.List<MicChoice>
        {
            new(AudioConfig.Auto, L10n.T("自动（戴蓝牙耳机时改用内置麦克风）")),
            new(AudioConfig.System, L10n.T("跟随系统默认输入")),
        };
        foreach (var device in AudioDeviceCatalog.ListCaptureDevices())
        {
            choices.Add(new MicChoice(device.Id, device.Name));
        }
        if (choices.All(c => c.Value != selectedValue))
        {
            choices.Add(new MicChoice(selectedValue, L10n.T("（已保存的设备，当前未连接）")));
        }

        _micDeviceCombo.BeginUpdate();
        _micDeviceCombo.Items.Clear();
        foreach (var choice in choices) _micDeviceCombo.Items.Add(choice);
        _micDeviceCombo.SelectedItem = choices.First(c => c.Value == selectedValue);
        _micDeviceCombo.EndUpdate();
    }

    private static void SetMessage(Label label, string text, Color color)
    {
        label.Text = text;
        label.Visible = !string.IsNullOrEmpty(text);
        label.ForeColor = color;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 关闭按钮保留草稿并隐藏到托盘；取消浮窗的临时预览。
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            OnUserClosedWindow?.Invoke();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _permissionPollTimer.Stop();
            _permissionPollTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
