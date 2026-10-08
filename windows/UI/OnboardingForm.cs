using System;
using System.Diagnostics;
using System.Drawing;
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
    private float _layoutScale = 1f;

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
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);

        _stepLabel.SetBounds(28, 20, 544, 20);
        _stepLabel.AutoSize = false;
        _stepLabel.TextAlign = ContentAlignment.MiddleLeft;
        _stepLabel.ForeColor = UiPalette.TextSecondary;

        _titleLabel.SetBounds(28, 42, 544, 36);
        _titleLabel.AutoSize = false;
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _titleLabel.Font = new Font(Font.FontFamily, 16f, FontStyle.Bold);

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
        _statusLabel.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        _detailLabel.SetBounds(0, 0, 544, 90);
        _detailLabel.AutoSize = false;
        _detailLabel.TextAlign = ContentAlignment.TopLeft;
        _detailLabel.ForeColor = UiPalette.TextSecondary;
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

        _content.Controls.AddRange(new Control[] { _bodyLabel, _statusLabel, _detailLabel, _progress, _actionButton, _secondaryButton, _trialBox });
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
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
        if (_model.Step == OnboardingStep.Trial) _trialBox.Focus();
    }

    /// <summary>按当前模型状态重排整个内容区。步骤内容很简单，整体重排比逐控件增量更新更不容易出错。</summary>
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

        foreach (Control control in _content.Controls) control.Visible = false;

        switch (step)
        {
            case OnboardingStep.Welcome: LayoutWelcome(); break;
            case OnboardingStep.Microphone: LayoutMicrophone(); break;
            case OnboardingStep.Model: LayoutModel(); break;
            default: LayoutTrial(); break;
        }
    }

    private void Place(Control control, int y)
    {
        control.Location = new Point(0, S(y));
        control.Visible = true;
    }

    private string HotkeyInstruction() => _model.HotkeyMode == HotkeyMode.Hold
        ? L10n.F("按住 {0} 说话，松开后识别结果会出现在光标所在的位置。", _model.HotkeyDisplay)
        : L10n.F("按一次 {0} 开始说话，再按一次结束，识别结果会出现在光标所在的位置。", _model.HotkeyDisplay);

    private void LayoutWelcome()
    {
        _bodyLabel.Text =
            L10n.T("VoiceTyper 是一个离线语音输入工具：识别完全在这台电脑上完成，音频不会上传。")
            + "\n\n" + HotkeyInstruction()
            + "\n\n" + L10n.T("接下来用两分钟确认麦克风、语音模型都准备好，并亲自试说一句话。")
            + "\n" + L10n.T("引导之后也可以从托盘菜单的「使用引导...」再次打开。");
        Place(_bodyLabel, 0);
    }

    private void LayoutMicrophone()
    {
        var (text, color) = _model.Mic switch
        {
            MicProbeResult.Available => (L10n.T("麦克风可用"), UiPalette.Success),
            MicProbeResult.Silent => (L10n.T("麦克风已打开，但只收到静音"), UiPalette.Warning),
            MicProbeResult.AccessDenied => (L10n.T("麦克风不可用：被系统隐私设置阻止"), UiPalette.Error),
            MicProbeResult.NoDevice => (L10n.T("未检测到麦克风设备"), UiPalette.Error),
            MicProbeResult.DeviceFailure => (L10n.T("麦克风打开失败：可能被其他应用占用"), UiPalette.Error),
            _ => (L10n.T("正在检测麦克风..."), UiPalette.TextSecondary),
        };
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
        _detailLabel.Text = _model.Mic switch
        {
            MicProbeResult.Available => L10n.T("Windows 没有单独的授权弹窗；能打开麦克风就说明桌面应用被允许访问了。"),
            MicProbeResult.Silent => L10n.T("请确认 Windows 设置 → 隐私和安全性 → 麦克风中「让桌面应用访问你的麦克风」已打开，并且麦克风没有被静音（包括键盘 / 耳机上的静音键）。也可能只是当前环境太安静——可以直接进入下一步试说一句话。"),
            MicProbeResult.AccessDenied => L10n.T("请在 Windows 设置 → 隐私和安全性 → 麦克风中，打开「麦克风访问」和「让桌面应用访问你的麦克风」，然后点「重新检测」。"),
            MicProbeResult.NoDevice => L10n.T("请插入麦克风，或在 Windows 设置 → 系统 → 声音 → 输入中启用一个输入设备，然后点「重新检测」。"),
            MicProbeResult.DeviceFailure => L10n.T("请关闭正在独占麦克风的应用（会议软件、录音软件等），然后点「重新检测」。"),
            _ => "",
        };
        _actionButton.Text = L10n.T("重新检测");
        _secondaryButton.Text = L10n.T("打开麦克风设置");
        Place(_statusLabel, 0);
        Place(_detailLabel, 34);
        Place(_actionButton, 130);
        _secondaryButton.Location = new Point(_actionButton.Right + S(12), S(130));
        _secondaryButton.Visible = true;
    }

    private void LayoutModel()
    {
        string status;
        Color color;
        string detail = "";
        var downloading = _model.DownloadProgress is not null;

        if (downloading)
        {
            status = L10n.F("正在下载模型 {0}%", (int)((_model.DownloadProgress ?? 0) * 100));
            color = UiPalette.TextSecondary;
            detail = L10n.T("模型约 230 MB，只需下载一次，之后完全离线使用。可以先点「下一步」，下载在后台继续。");
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
                    color = UiPalette.Success;
                    break;
                case AsrState.Loading:
                case AsrState.Unloaded:
                    status = L10n.T("模型加载中...");
                    color = UiPalette.TextSecondary;
                    break;
                case AsrState.Failed:
                    status = L10n.T("模型加载失败");
                    color = UiPalette.Error;
                    detail = L10n.T("可以到设置 → 语音模型中重新加载，或重新下载模型。");
                    break;
                default:
                    status = L10n.T("需要下载语音模型（约 230 MB）");
                    color = UiPalette.Warning;
                    break;
            }
            if (!string.IsNullOrEmpty(_model.DownloadError)) detail = _model.DownloadError!;
        }

        _statusLabel.Text = status;
        _statusLabel.ForeColor = color;
        _detailLabel.Text = detail;
        Place(_statusLabel, 0);
        Place(_detailLabel, downloading ? 62 : 34);

        if (downloading)
        {
            _actionButton.Text = L10n.T("取消下载");
            Place(_actionButton, 150);
        }
        else if (_model.AsrState is AsrState.ModelMissing or AsrState.Failed)
        {
            _actionButton.Text = !string.IsNullOrEmpty(_model.DownloadError) ? L10n.T("重试下载") : L10n.T("开始下载模型");
            Place(_actionButton, 150);
        }
    }

    private void LayoutTrial()
    {
        _bodyLabel.Text = _model.CanRunTrial
            ? L10n.T("点一下下面的输入框，然后试说一句话：") + "\n" + HotkeyInstruction()
            : (_model.TrialBlockingHint ?? "");
        _bodyLabel.Height = S(56);
        Place(_bodyLabel, 0);

        _trialBox.Enabled = _model.CanRunTrial;
        Place(_trialBox, 62);

        if (_model.TrialSummary is { } summary)
        {
            _statusLabel.Text = summary.Title;
            _statusLabel.ForeColor = _model.Trial.Kind switch
            {
                OnboardingTrialKind.Succeeded => UiPalette.Success,
                OnboardingTrialKind.Recording => UiPalette.Error,
                OnboardingTrialKind.Recognizing or OnboardingTrialKind.NoSpeech or OnboardingTrialKind.Blocked => UiPalette.Warning,
                OnboardingTrialKind.Failed => UiPalette.Error,
                _ => UiPalette.TextSecondary,
            };
            _detailLabel.Text = summary.Detail;
            _detailLabel.Height = S(70);
            Place(_statusLabel, 162);
            Place(_detailLabel, 190);
        }
        if (_model.CanRunTrial && _trialBox.CanFocus && !_trialBox.Focused) _trialBox.Focus();
    }

    private void HandleAction()
    {
        switch (_model.Step)
        {
            case OnboardingStep.Microphone:
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
