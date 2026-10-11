using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

/// <summary>
/// 首启引导：欢迎 → 麦克风 → 语音模型 → 试一试。对应 macOS 的 <c>OnboardingView</c>。
///
/// 最后一步会让用户在窗口自己的输入框里跑一次<b>真实</b>听写（热键 → 麦克风 → 识别 → 文本插入），
/// 哪一环出问题当场指出。窗口本身只负责呈现 <see cref="OnboardingModel"/> 的状态，不驱动听写：
/// 听写事件由协调器推送进模型，避免出现第二条驱动听写的路径。所有方法必须在 UI 线程调用。
/// </summary>
internal sealed class OnboardingForm : Form
{
    public Action? OnRetryMicProbe;
    public Action? OnStartModelDownload;
    public Action? OnCancelModelDownload;
    /// <summary>用户在下拉框里换了输入设备：协调器把它存进配置（与设置页保存同一条路径）。</summary>
    public Action<string>? OnSelectInputDevice;
    /// <summary>「测试麦克风」，语义同 <see cref="SetupForm.OnStartMicTest"/>。</summary>
    public Action<string>? OnStartMicTest;
    public Action? OnStopMicTest;

    private readonly OnboardingModel _model;

    private readonly Label _stepLabel = new();
    private readonly Label _titleLabel = new();
    private readonly Panel _content = new();
    private readonly Button _backButton = new();
    private readonly Button _primaryButton = new();

    // 内容区控件（按步骤显示 / 隐藏）
    private readonly Label _bodyLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _detailLabel = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _actionButton = new();
    private readonly Button _secondaryButton = new();
    private readonly TextBox _trialBox = new();
    private readonly Label _deviceCaption = new();
    private readonly ComboBox _deviceCombo = new();
    private readonly MicTestPanel _micTest = new();
    /// <summary>设备下拉框已为本次进入麦克风步骤填充过。填充一次后不再随每次刷新重置，免得打断用户的选择。</summary>
    private bool _deviceListLoaded;
    private bool _populatingDevices;
    private float _layoutScale = 1f;
    /// <summary>本次重排要显示的控件集合（<see cref="Place"/> 系列填入）。重排结束统一做可见性 diff：
    /// 先全部隐藏再逐个显示会在下载进度每 200ms 刷一次时产生可见的闪烁（REVIEW_UX U-01）。</summary>
    private readonly HashSet<Control> _shown = new();

    public OnboardingForm(OnboardingModel model)
    {
        _model = model;

        Text = L10n.F("欢迎使用 {0}", "VoiceTyper");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 440);
        Font = UiFonts.Get(9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);

        _stepLabel.SetBounds(28, 20, 544, 20);
        _stepLabel.AutoSize = false;
        _stepLabel.TextAlign = ContentAlignment.MiddleLeft;
        _stepLabel.ForeColor = Color.Gray;

        _titleLabel.SetBounds(28, 42, 544, 36);
        _titleLabel.AutoSize = false;
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _titleLabel.Font = UiFonts.Get(16f, bold: true);

        _content.SetBounds(28, 90, 544, 270);

        _backButton.Text = L10n.T("上一步");
        _backButton.SetBounds(28, 384, 96, 32);
        _backButton.Click += (_, _) => _model.GoBack();

        _primaryButton.SetBounds(444, 384, 128, 32);
        _primaryButton.Click += (_, _) => _model.GoForward();

        _bodyLabel.SetBounds(0, 0, 544, 150);
        _bodyLabel.AutoSize = false;
        _bodyLabel.TextAlign = ContentAlignment.TopLeft;
        _statusLabel.SetBounds(0, 0, 544, 26);
        _statusLabel.AutoSize = false;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Font = UiFonts.Get(11f, bold: true);
        _detailLabel.SetBounds(0, 0, 544, 90);
        _detailLabel.AutoSize = false;
        _detailLabel.TextAlign = ContentAlignment.TopLeft;
        _detailLabel.ForeColor = Color.DimGray;
        _progress.SetBounds(0, 0, 400, 18);
        _actionButton.AutoSize = true;
        _actionButton.Padding = new Padding(10, 3, 10, 3);
        _actionButton.Click += (_, _) => HandleAction();
        _secondaryButton.AutoSize = true;
        _secondaryButton.Padding = new Padding(10, 3, 10, 3);
        _secondaryButton.Click += (_, _) => HandleSecondaryAction();
        _trialBox.Multiline = true;
        _trialBox.SetBounds(0, 0, 544, 90);
        _trialBox.ScrollBars = ScrollBars.Vertical;

        _deviceCaption.AutoSize = true;
        _deviceCaption.Text = L10n.T("麦克风设备：");
        _deviceCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_populatingDevices || _deviceCombo.SelectedItem is not MicChoice choice) return;
            _micTest.Stop();
            OnSelectInputDevice?.Invoke(choice.Value);
        };
        _micTest.DeviceValue = () => (_deviceCombo.SelectedItem as MicChoice)?.Value ?? _model.InputDevice;
        _micTest.StartRequested = value => OnStartMicTest?.Invoke(value);
        _micTest.StopRequested = () => OnStopMicTest?.Invoke();
        VisibleChanged += (_, _) =>
        {
            if (!Visible) _micTest.Stop();
        };

        _content.Controls.AddRange(new Control[] { _bodyLabel, _statusLabel, _detailLabel, _progress, _actionButton, _secondaryButton, _trialBox,
            _deviceCaption, _deviceCombo, _micTest });
        Controls.AddRange(new Control[] { _stepLabel, _titleLabel, _content, _backButton, _primaryButton });

        // 引导本身可被随时关掉（右上角 ×）：不算"完成"，下次启动还会出现。
        _model.OnStepChanged = RefreshFromModel;
        RefreshFromModel();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyDpiLayout(DeviceDpi);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyDpiLayout(e.DeviceDpiNew);
    }

    private int S(int value) => (int)Math.Round(value * _layoutScale);

    private void ApplyDpiLayout(int dpi)
    {
        _layoutScale = Math.Max(1f, dpi / 96f);
        SuspendLayout();
        try
        {
            ClientSize = new Size(S(600), S(440));

            _stepLabel.SetBounds(S(28), S(20), S(544), S(24));
            _titleLabel.SetBounds(S(28), S(42), S(544), S(36));
            _content.SetBounds(S(28), S(90), S(544), S(270));
            _backButton.SetBounds(S(28), S(384), S(96), S(32));
            _primaryButton.SetBounds(S(444), S(384), S(128), S(32));

            _bodyLabel.SetBounds(0, 0, S(544), S(150));
            _statusLabel.SetBounds(0, 0, S(544), S(26));
            _detailLabel.SetBounds(0, 0, S(544), S(90));
            _progress.SetBounds(0, 0, S(400), S(18));
            _trialBox.SetBounds(0, 0, S(544), S(90));
            _micTest.ApplyScale(S(200), S(14), S(520));
            _actionButton.Padding = new Padding(S(10), S(3), S(10), S(3));
            _secondaryButton.Padding = new Padding(S(10), S(3), S(10), S(3));

            RefreshFromModel();
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    public void Present()
    {
        if (!Visible)
        {
            // 协调器在窗口不可见时只更新模型、不做整体重排；显示前补一次，保证内容是最新的。
            RefreshFromModel();
            Show();
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
        if (_model.Step == OnboardingStep.Trial) _trialBox.Focus();
    }

    /// <summary>按当前模型状态重排整个内容区。步骤内容很简单，整体重排比逐控件增量更新更不容易出错；
    /// 可见性只在重排结束时按 <see cref="_shown"/> 做 diff（见其注释）。</summary>
    public void RefreshFromModel()
    {
        if (IsDisposed) return;

        var step = _model.Step;
        _stepLabel.Text = L10n.F("第 {0} 步，共 {1} 步", (int)step + 1, 4);
        _titleLabel.Text = step switch
        {
            OnboardingStep.Welcome => L10n.T("欢迎"),
            OnboardingStep.Microphone => L10n.T("麦克风"),
            OnboardingStep.Model => L10n.T("语音模型"),
            _ => L10n.T("试一试"),
        };
        _backButton.Enabled = _model.CanGoBack;
        _primaryButton.Text = _model.PrimaryActionTitle;

        _shown.Clear();
        if (step != OnboardingStep.Microphone)
        {
            // 离开麦克风步骤：关掉测试（指示灯不能一直亮着），下次进来重新列设备。
            _micTest.Stop();
            _deviceListLoaded = false;
        }

        SuspendLayout();
        _content.SuspendLayout();
        try
        {
            switch (step)
            {
                case OnboardingStep.Welcome: LayoutWelcome(); break;
                case OnboardingStep.Microphone: LayoutMicrophone(); break;
                case OnboardingStep.Model: LayoutModel(); break;
                default: LayoutTrial(); break;
            }
        }
        finally
        {
            _content.ResumeLayout(performLayout: true);
            ResumeLayout(performLayout: true);
        }

        foreach (Control control in _content.Controls)
        {
            var want = _shown.Contains(control);
            if (control.Visible != want) control.Visible = want;
        }
    }

    private void Place(Control control, int y)
    {
        control.Location = new Point(0, S(y));
        _shown.Add(control);
    }

    /// <summary>
    /// 按当前文本把标签撑到刚好放得下（宽度不变）。引导页是绝对坐标布局，写死的高度只够中文文案，
    /// 英文通常长出一到两行，会被截断；文本为空时高度为 0。返回新高度，调用方据此排下面的控件。
    /// <paramref name="maxHeight"/> 是内容区剩余的像素高度，超出时截到它（宁可截尾也不让控件落出内容区）。
    /// </summary>
    private int FitHeight(Label label, int maxHeight = int.MaxValue)
    {
        if (string.IsNullOrEmpty(label.Text))
        {
            label.Height = 0;
            return 0;
        }
        var measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        label.Height = Math.Max(0, Math.Min(maxHeight, measured + S(6)));
        return label.Height;
    }

    /// <summary>放在 <paramref name="above"/> 下方 <paramref name="gap"/>（逻辑像素）处。</summary>
    private void PlaceBelow(Control control, Control above, int gap)
    {
        control.Location = new Point(0, above.Bottom + S(gap));
        _shown.Add(control);
    }

    private string HotkeyInstruction() => _model.HotkeyMode == HotkeyMode.Hold
        ? L10n.F("按住 {0} 说话，松开后识别结果会出现在光标所在的位置。", _model.HotkeyDisplay)
        : L10n.F("按一次 {0} 开始说话，再按一次结束，识别结果会出现在光标所在的位置。", _model.HotkeyDisplay);

    private void LayoutWelcome()
    {
        var body =
            L10n.T("VoiceTyper 是一个离线语音输入工具：识别完全在这台电脑上完成，音频不会上传。")
            + "\n\n" + HotkeyInstruction()
            + "\n\n" + L10n.T("接下来用两分钟确认麦克风、语音模型都准备好，并亲自试说一句话。")
            + "\n" + L10n.T("引导之后也可以从托盘菜单的「使用引导...」再次打开。");
        // 模型在首次启动时就已经开始在后台下载：欢迎页就告诉用户，别让他以为还没开始。
        if (_model.DownloadProgress is { } background)
        {
            body += "\n\n" + L10n.F("语音模型正在后台下载：{0}%", (int)(background * 100));
        }
        _bodyLabel.Text = body;
        Place(_bodyLabel, 0);
        FitHeight(_bodyLabel, _content.Height);
    }

    private void LayoutMicrophone()
    {
        var (text, color) = _model.Mic switch
        {
            MicProbeResult.Available => (L10n.T("麦克风可用"), Color.SeaGreen),
            MicProbeResult.Silent => (L10n.T("麦克风已打开，但只收到静音"), Color.DarkGoldenrod),
            MicProbeResult.AccessDenied => (L10n.T("麦克风不可用：被系统隐私设置阻止"), Color.Firebrick),
            MicProbeResult.NoDevice => (L10n.T("未检测到麦克风设备"), Color.Firebrick),
            MicProbeResult.DeviceFailure => (L10n.T("麦克风打开失败：可能被其他应用占用"), Color.Firebrick),
            _ => (L10n.T("正在检测麦克风..."), Color.DimGray),
        };
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
        _detailLabel.Text = _model.Mic switch
        {
            MicProbeResult.Available => L10n.T("Windows 没有单独的授权弹窗；能打开麦克风就说明桌面应用被允许访问了。"),
            MicProbeResult.Silent => L10n.T("请确认 Windows 设置 → 隐私和安全性 → 麦克风中「让桌面应用访问你的麦克风」已打开，并且麦克风没有被静音（包括键盘 / 耳机上的静音键）。也可能只是当前环境太安静——可以直接进入下一步试说一句话。"),
            MicProbeResult.AccessDenied => L10n.T("请在 Windows 设置 → 隐私和安全性 → 麦克风中，打开「麦克风访问」和「让桌面应用访问你的麦克风」，然后点「重新检测」。"),
            MicProbeResult.NoDevice => L10n.T("请插入麦克风，或在 Windows 设置 → 系统 → 声音 → 输入中启用一个输入设备，然后点「重新检测」。"),
            MicProbeResult.DeviceFailure => AppendMicDetail(L10n.T("请关闭正在独占麦克风的应用（会议软件、录音软件等），然后点「重新检测」。"), _model.MicDetail),
            _ => "",
        };
        _actionButton.Text = L10n.T("重新检测");
        _secondaryButton.Text = L10n.T("打开麦克风设置");
        Place(_statusLabel, 0);
        Place(_detailLabel, 34);
        // 下半部分留给设备选择与测试，说明文字最多占 84：宁可截尾，也不让控件互相压住。
        FitHeight(_detailLabel, S(84));
        PlaceBelow(_actionButton, _detailLabel, 8);
        _secondaryButton.Location = new Point(_actionButton.Right + S(12), _actionButton.Top);
        _shown.Add(_secondaryButton);

        PopulateDevicesIfNeeded();
        _deviceCaption.Location = new Point(0, S(174));
        _shown.Add(_deviceCaption);
        _deviceCombo.SetBounds(S(110), S(170), S(434), S(28));
        _shown.Add(_deviceCombo);
        _micTest.Location = new Point(0, S(204));
        _shown.Add(_micTest);
    }

    /// <summary>进入麦克风步骤（或点「重新检测」）时列一次输入设备，选中当前保存的那个。</summary>
    private void PopulateDevicesIfNeeded()
    {
        if (_deviceListLoaded) return;
        _deviceListLoaded = true;
        _populatingDevices = true;
        try
        {
            var choices = MicChoice.Build(_model.InputDevice);
            _deviceCombo.BeginUpdate();
            _deviceCombo.Items.Clear();
            foreach (var choice in choices) _deviceCombo.Items.Add(choice);
            _deviceCombo.SelectedItem = choices.First(c => c.Value == _model.InputDevice);
            _deviceCombo.EndUpdate();
        }
        finally
        {
            _populatingDevices = false;
        }
    }

    public void MicTestLevel(float rms) => _micTest.ReportLevel(rms);

    public void MicTestStopped(string? reason) => _micTest.ReportStopped(reason);

    /// <summary>「被独占」只是按分类的猜测；探测拿到的真实原因（HRESULT、声道数）拼在后面，便于对照日志反馈。</summary>
    private static string AppendMicDetail(string message, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? message : $"{message}\n{detail}";

    private void LayoutModel()
    {
        string status;
        Color color;
        string detail = "";
        var downloading = _model.DownloadProgress is not null;

        if (downloading)
        {
            status = L10n.F("正在下载模型 {0}%", (int)((_model.DownloadProgress ?? 0) * 100));
            color = Color.DimGray;
            // 第一行是已下载 / 速度 / 剩余时间，让用户知道在动、还要等多久；其后是固定说明。
            detail = (string.IsNullOrEmpty(_model.DownloadStatus) ? "" : _model.DownloadStatus + "\n")
                + L10n.T("模型约 230 MB，只需下载一次，之后完全离线使用。可以先点「下一步」，下载在后台继续。");
            _progress.Value = Math.Clamp((int)((_model.DownloadProgress ?? 0) * 100), 0, 100);
            Place(_progress, 34);
        }
        else
        {
            switch (_model.AsrState)
            {
                case AsrState.Ready:
                case AsrState.SuspendedForIdle:
                    status = L10n.T("语音模型已就绪");
                    color = Color.SeaGreen;
                    break;
                case AsrState.Loading:
                case AsrState.Unloaded:
                    status = L10n.T("模型加载中...");
                    color = Color.DimGray;
                    break;
                case AsrState.Failed:
                    status = L10n.T("模型加载失败");
                    color = Color.Firebrick;
                    detail = L10n.T("可以到设置 → 语音模型中重新加载，或重新下载模型。");
                    break;
                default:
                    status = L10n.T("需要下载语音模型（约 230 MB）");
                    color = Color.DarkGoldenrod;
                    break;
            }
            if (!string.IsNullOrEmpty(_model.DownloadError)) detail = _model.DownloadError!;
        }

        _statusLabel.Text = status;
        _statusLabel.ForeColor = color;
        _detailLabel.Text = detail;
        Place(_statusLabel, 0);
        Place(_detailLabel, downloading ? 62 : 34);
        FitHeight(_detailLabel, _content.Height - _detailLabel.Top - _actionButton.Height - S(12));

        if (downloading)
        {
            _actionButton.Text = L10n.T("取消下载");
            PlaceBelow(_actionButton, _detailLabel, 12);
        }
        else if (_model.AsrState is AsrState.ModelMissing or AsrState.Failed)
        {
            _actionButton.Text = !string.IsNullOrEmpty(_model.DownloadError) ? L10n.T("重试下载") : L10n.T("开始下载模型");
            PlaceBelow(_actionButton, _detailLabel, 12);
        }
    }

    private void LayoutTrial()
    {
        _bodyLabel.Text = _model.CanRunTrial
            ? L10n.T("点一下下面的输入框，然后试说一句话：") + "\n" + HotkeyInstruction()
            : (_model.TrialBlockingHint ?? "");
        Place(_bodyLabel, 0);
        FitHeight(_bodyLabel, S(110));

        _trialBox.Enabled = _model.CanRunTrial;
        PlaceBelow(_trialBox, _bodyLabel, 6);

        if (_model.TrialSummary is { } summary)
        {
            _statusLabel.Text = summary.Title;
            _statusLabel.ForeColor = _model.Trial.Kind switch
            {
                OnboardingTrialKind.Succeeded => Color.SeaGreen,
                OnboardingTrialKind.Recording => Color.Firebrick,
                OnboardingTrialKind.Recognizing or OnboardingTrialKind.NoSpeech or OnboardingTrialKind.Blocked => Color.DarkGoldenrod,
                OnboardingTrialKind.Failed => Color.Firebrick,
                _ => Color.DimGray,
            };
            _detailLabel.Text = summary.Detail;
            PlaceBelow(_statusLabel, _trialBox, 8);
            PlaceBelow(_detailLabel, _statusLabel, 2);
            FitHeight(_detailLabel, _content.Height - _detailLabel.Top);
        }
        if (_model.CanRunTrial && _trialBox.CanFocus && !_trialBox.Focused) _trialBox.Focus();
    }

    private void HandleAction()
    {
        switch (_model.Step)
        {
            case OnboardingStep.Microphone:
                _deviceListLoaded = false; // 用户可能刚插上了麦克风：重新列设备
                OnRetryMicProbe?.Invoke();
                break;
            case OnboardingStep.Model:
                if (_model.DownloadProgress is not null) OnCancelModelDownload?.Invoke();
                else OnStartModelDownload?.Invoke();
                break;
        }
    }

    private void HandleSecondaryAction()
    {
        if (_model.Step != OnboardingStep.Microphone) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "ms-settings:privacy-microphone", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("ui", $"打开麦克风设置失败: {ex.Message}");
        }
    }
}
