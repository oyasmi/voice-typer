using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.Support;
using static VoiceTyper.Support.NativeMethods;

namespace VoiceTyper.UI;

/// <summary>
/// 无边框、置顶、不抢焦点的听写指示器。对应 macOS 的 <c>RecordingHUDController</c>。
///
/// 上方一行：圆点 / 结果图标 + 实时波形 + 状态文字（含输入设备名）+ 计时。
/// 下方（有内容时才展开）：最多两行的预览，放不下时保留<b>尾部</b>并以省略号开头。
/// 阶段：Preparing（麦克风启动中）→ Recording → Recognizing（→ 纠错中）→ 一次性结果
/// （成功 / 错误 / 已取消 / 没有识别到内容）。
///
/// 落点由 <c>ui.hud_position</c> 决定；"不显示"只静音<b>过程</b>，错误与"没有识别到内容"仍会浮出来。
/// 所有像素尺寸按窗口当前 DPI 缩放（PerMonitorV2 下窗口拖到别的屏幕会收到 DPI 变化）。
/// 所有方法必须在 UI 线程调用。
/// </summary>
internal sealed class RecordingHud : Form
{
    /// <summary>Preparing：已按下热键、麦克风还在打开，此时说的话录不进去，必须和 Recording 一眼可分。</summary>
    private enum Phase { Hidden, Preparing, Recording, Recognizing, Transient }
    private enum Glyph { None, Check, Cross, Exclaim }

    // ─── 几何常量（96 DPI 下的像素，绘制与布局时经 S() 缩放）───────────
    private const int CompactHeight = 48;
    private const int PreviewPadTop = 2;
    private const int PreviewPadBottom = 14;
    private const int HorizontalInset = 16;
    private const int BottomInset = 80;
    private const int CursorGap = 24;
    /// <summary>浮窗与文字插入点之间的间距：比鼠标指针的 <see cref="CursorGap"/> 小，插入点本身很细。</summary>
    private const int CaretGap = 8;
    private const int ScreenMargin = 16;
    private const int PreviewMaxLines = 2;
    private const int BarCount = 9;
    /// <summary>宽度按档位增长而不是逐像素跟随文本，避免文字一边识别一边窗口一直在抖。</summary>
    private static readonly int[] WidthSteps = { 420, 560, 700, 860 };

    private readonly System.Windows.Forms.Timer _animationTimer;
    /// <summary>状态 / 预览字体取自 <see cref="UiFonts"/>（与设置窗口同一字体族，中文用微软雅黑 UI），
    /// 是进程共享实例，窗体不拥有、不 Dispose。计时用等宽 Consolas，由窗体自己持有。</summary>
    private readonly Font _statusFont;
    private readonly Font _timerFont;
    private readonly Font _previewFont;

    private Phase _phase = Phase.Hidden;
    /// <summary>录音开始的 <see cref="Stopwatch"/> 时间戳；单调时钟，不受系统时间调整影响。</summary>
    private long _startedTimestamp = Stopwatch.GetTimestamp();
    private double _pulsePhase;
    private string _statusText = "";
    private string _preparingStatusText = L10n.T("麦克风启动中…");
    private string _recordingStatusText = L10n.T("录音中");
    private string _recognizingStatusText = L10n.T("识别中");
    private string[] _previewLines = Array.Empty<string>();
    private string _lastPreviewSource = "";
    private Glyph _glyph = Glyph.None;
    private Color _accent = Color.FromArgb(235, 70, 60);
    /// <summary>状态文字是否处于 FlashWarning 的警示色闪现期（约 1.2s）。</summary>
    private bool _statusWarning;
    private bool _isCorrecting;
    private bool _useDwmRoundCorners;
    private readonly float[] _bars = new float[BarCount];
    private long _lastLevelTimestamp; // 0 = 尚未收到过电平

    private HudPlacement _placement = HudPlacement.BottomCenter;
    /// <summary>用户配置的不透明度（<see cref="Form.Opacity"/> 在淡入淡出期间会临时偏离它）。</summary>
    private double _userOpacity;
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private double _fadeTarget;
    /// <summary>淡出到 0 之后隐藏窗口。</summary>
    private bool _hideAfterFade;
    private const int FadeIntervalMs = 16;
    /// <summary>淡入 / 淡出各约 100ms。</summary>
    private const int FadeSteps = 6;

    /// <summary>最近一次预览相对上一次变化的字符数（尾部）。这部分文字在 <see cref="StableAfterMs"/> 内以较暗的亮度显示，
    /// 让用户一眼分清"刚出来、可能还会被修正"与"已稳定"。</summary>
    private int _changedChars;
    private long _previewChangedAt;
    private const int StableAfterMs = 300;
    /// <summary>录音还剩这么多秒时，状态行改为倒计时（见 <see cref="CurrentStatusText"/>）。</summary>
    private const int CountdownSeconds = 15;
    /// <summary>底部落点时，展开 / 收起保持底边不动、向上生长；跟随光标时保持上边中点不动。</summary>
    private Point _anchor;
    /// <summary>"跟随光标"的参照：水平中心 X，放下方时的上边 Y，放上方（下方放不下时翻转）时的下边 Y。
    /// 参照点优先取文字插入点，取不到再用鼠标位置；只在重新定锚时更新。</summary>
    private readonly record struct FollowPoint(int X, int BelowY, int AboveY);
    private FollowPoint _follow;
    private int _currentWidth;
    private Screen? _targetScreen;

    /// <summary>一次性提示（成功/错误/已取消）的自动隐藏任务；状态变化时作废。</summary>
    private System.Windows.Forms.Timer? _transientHideTimer;
    /// <summary><see cref="FlashWarning"/> 的状态文字恢复任务；状态变化时作废。</summary>
    private System.Windows.Forms.Timer? _warningRestoreTimer;
    /// <summary>预览短暂清空时延迟收起，避免窗口一伸一缩地抖动。</summary>
    private System.Windows.Forms.Timer? _collapseTimer;

    /// <summary>系统关闭动画（设置 → 辅助功能 → 视觉效果 → 动画效果）时不做呼吸 / 脉冲，
    /// 只保留电平波形（它是功能性反馈，不是装饰）。</summary>
    private static bool MotionEnabled => SystemInformation.UIEffectsEnabled;

    /// <summary>"麦克风启动中"（false）/"录音中"（true）第一次真正画到屏幕上时触发，参数二是
    /// <see cref="Stopwatch"/> 时间戳。只用于耗时日志：Show/Invalidate 之后要等消息循环处理 WM_PAINT 才算显示出来。</summary>
    public Action<bool, long>? ProgressPainted;
    /// <summary>等待下一次绘制时上报的里程碑；null 表示没有。</summary>
    private bool? _pendingPaintReady;

    public RecordingHud(UIConfig uiConfig)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        _userOpacity = Math.Clamp(uiConfig.Opacity, ConfigLimits.OpacityMin, ConfigLimits.OpacityMax);
        Opacity = _userOpacity;
        _placement = uiConfig.HudPositionValue;
        BackColor = Color.FromArgb(20, 20, 22);
        DoubleBuffered = true;
        Size = new Size(S(WidthSteps[0]), S(CompactHeight));
        _currentWidth = WidthSteps[0];

        // 层级约定与 macOS 一致：预览文字是 HUD 的正文（用户实时校对识别结果），
        // 字号与亮度都高于顶行的状态提示（呼吸点/波形已在传达录音状态）。
        _statusFont = UiFonts.Get(9.5f);
        _timerFont = new Font("Consolas", 9f, FontStyle.Regular);
        _previewFont = UiFonts.Get(10.5f);

        HandleCreated += (_, _) => RefreshCornerStrategy();
        Resize += (_, _) => ApplyRoundedRegionIfNeeded();

        _fadeTimer = new System.Windows.Forms.Timer { Interval = FadeIntervalMs };
        _fadeTimer.Tick += (_, _) => FadeTick();

        _animationTimer = new System.Windows.Forms.Timer { Interval = 33 };
        _animationTimer.Tick += (_, _) =>
        {
            _pulsePhase = (_pulsePhase + 0.16) % (Math.PI * 2);
            // 电平自然衰减：静音时波形回落，而不是停在最后一次的高度。
            for (int i = 0; i < _bars.Length; i++) _bars[i] *= 0.94f;
            Invalidate();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WS_EX_TRANSPARENT 与分层窗口（Opacity < 1）合用才是真正的点击穿透：浮窗跟随插入点时就在输入行下面，
            // 不能挡住用户的点击。不透明度 100% 时窗口不分层，该位不产生穿透（非分层窗口的 HTTRANSPARENT
            // 也穿不过进程边界），行为与此前一致。
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    private int S(int pixels) => (int)Math.Round(pixels * DeviceDpi / 96.0);
    private float S(float pixels) => pixels * DeviceDpi / 96f;

    /// <summary>"不显示"下要跳过的过程类展示（录音、预览、识别、成功、取消）。</summary>
    private bool SuppressesProgress => _placement == HudPlacement.Hidden;

    // ─── 公共接口 ─────────────────────────────────────────────────

    /// <summary>
    /// 已按下热键、麦克风还在打开：灰色静止圆点、平直波形、不计时，明确表示"还不能说话"。
    /// 麦克风出声后由 <see cref="ShowRecording"/> 原地切成红色脉冲的"录音中"。
    /// </summary>
    public void ShowPreparing()
    {
        if (SuppressesProgress) return;
        ResetTimers();

        _phase = Phase.Preparing;
        _isCorrecting = false;
        _glyph = Glyph.None;
        _accent = Color.FromArgb(150, 150, 150);
        _preparingStatusText = L10n.T("麦克风启动中…");
        SetStatus(_preparingStatusText);
        Array.Clear(_bars);
        SetPreviewSource("");
        _pendingPaintReady = false;
        Present(anchorToScreen: true);
        // 此阶段画面完全静止（灰点、平直暗条、不计时），不需要动画帧；切到录音时由 ShowRecording 启动。
        _animationTimer.Stop();
    }

    /// <param name="inputDeviceName">本次录音实际使用的输入设备名（可能与系统默认不同，见 AudioInputPolicy）。</param>
    public void ShowRecording(string? inputDeviceName)
    {
        if (SuppressesProgress) return;
        ResetTimers();
        // 从"麦克风启动中"原地切换：不重新取锚点，否则跟随光标落点下浮窗会跳到光标的新位置。
        var keepAnchor = _phase == Phase.Preparing && Visible;

        _phase = Phase.Recording;
        _isCorrecting = false;
        _startedTimestamp = Stopwatch.GetTimestamp();
        _glyph = Glyph.None;
        _accent = Color.FromArgb(235, 70, 60);
        _recordingStatusText = string.IsNullOrWhiteSpace(inputDeviceName)
            ? L10n.T("录音中")
            : L10n.T("录音中") + " · " + HudTextLayout.TruncateDeviceName(inputDeviceName!);
        SetStatus(_recordingStatusText);
        Array.Clear(_bars);
        SetPreviewSource("");
        _pendingPaintReady = true;
        Present(anchorToScreen: !keepAnchor);
        _animationTimer.Start();
    }

    public void SetRecognizing()
    {
        if (SuppressesProgress) return;
        CancelTransientHide();
        CancelWarningRestore();
        _isCorrecting = false;
        _phase = Phase.Recognizing;
        _recognizingStatusText = L10n.T("识别中");
        SetStatus(_recognizingStatusText);
        _accent = Color.FromArgb(250, 190, 40);
        _glyph = Glyph.None;
        // 松键后冻结计时（保留最后时长）；动画继续，用于"处理中"的呼吸。
        _frozenElapsedSeconds = ElapsedRecordingSeconds();
        if (!_animationTimer.Enabled) _animationTimer.Start();
        Invalidate();
    }

    /// <summary>
    /// 本地识别已出结果，正在等 LLM 纠错。与"识别中"分开的理由：本地推理是几百毫秒且完全可控，
    /// 而纠错要走网络、最长可以等到 <c>llm.timeout</c>；两者都显示"识别中"的话，用户既不知道
    /// 该不该继续等，也无从判断慢在哪。
    /// </summary>
    /// <param name="skipHotkey">热键的显示名；给出时在状态行提示"再按一次可跳过纠错"。
    /// 此前用户不知道有这个操作，等不及时按 Esc 会把整段结果取消掉。</param>
    public void SetCorrecting(string? skipHotkey = null)
    {
        if (SuppressesProgress || _phase != Phase.Recognizing) return;
        CancelWarningRestore();
        _isCorrecting = true;
        _recognizingStatusText = string.IsNullOrEmpty(skipHotkey)
            ? L10n.T("纠错中…")
            : L10n.F("纠错中… 再按 {0} 跳过", skipHotkey);
        SetStatus(_recognizingStatusText);
        _accent = Color.FromArgb(70, 140, 255);
        Invalidate();
    }

    /// <summary>
    /// 松键后识别引擎仍在加载（空闲卸载后的冷恢复，可能要数秒）：把"识别中"改成加载说明，
    /// 让用户知道在等的是模型而不是卡住了；等待结束后恢复。不影响纠错阶段的文案。
    /// </summary>
    public void SetEngineWait(bool waiting)
    {
        if (SuppressesProgress || _phase != Phase.Recognizing || _isCorrecting) return;
        CancelWarningRestore();
        _recognizingStatusText = waiting ? L10n.T("正在加载识别模型…") : L10n.T("识别中");
        SetStatus(_recognizingStatusText);
        Invalidate();
    }

    public void HideHud()
    {
        _animationTimer.Stop();
        ResetTimers();
        _phase = Phase.Hidden;
        _isCorrecting = false;
        _pendingPaintReady = null;
        if (Visible && CanFade)
        {
            // 逻辑状态立即收尾，只是窗口再用约 100ms 淡出（内容保持不变，不在淡出时露出清空后的样子）；
            // 淡出期间再次 Present 会反向淡回。
            _hideAfterFade = true;
            StartFade(0);
            return;
        }
        HideNow();
    }

    private void HideNow()
    {
        _fadeTimer.Stop();
        _hideAfterFade = false;
        SetPreviewSource("");
        if (Visible) Hide();
        Opacity = _userOpacity;
    }

    /// <summary>显示流式 partial 文本。本地识别引擎给出的是<b>全量</b>预览，这里整体替换而非追加。</summary>
    public void ShowPreview(string accumulated)
    {
        if (SuppressesProgress || _phase is not (Phase.Recording or Phase.Recognizing)) return;

        if (string.IsNullOrWhiteSpace(accumulated))
        {
            // 短暂清空时防抖收起。
            if (_previewLines.Length > 0 && _collapseTimer is null)
            {
                var timer = new System.Windows.Forms.Timer { Interval = 400 };
                timer.Tick += (_, _) =>
                {
                    CancelCollapse();
                    SetPreviewSource("");
                    Relayout();
                };
                _collapseTimer = timer;
                timer.Start();
            }
            return;
        }

        CancelCollapse();
        var previous = _lastPreviewSource;
        SetPreviewSource(accumulated);
        if (!string.Equals(previous, accumulated, StringComparison.Ordinal))
        {
            _changedChars = accumulated.Length - CommonPrefixLength(previous, accumulated);
            _previewChangedAt = Stopwatch.GetTimestamp();
        }
        Relayout();
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>实时音量电平（线性 RMS），驱动波形。仅录音阶段生效，内部节流。</summary>
    public void UpdateLevel(float level)
    {
        if (_phase != Phase.Recording) return;
        var now = Stopwatch.GetTimestamp();
        if (_lastLevelTimestamp != 0 && Stopwatch.GetElapsedTime(_lastLevelTimestamp, now).TotalMilliseconds < 30) return;
        _lastLevelTimestamp = now;

        // 条形向左滚动，新电平进右侧：波形呈现"最近一小段时间的音量轮廓"。
        Array.Copy(_bars, 1, _bars, 0, _bars.Length - 1);
        _bars[^1] = HudTextLayout.LevelToBarHeight(level);
    }

    /// <summary>final 文本插入成功后的一次性反馈，约 0.7s 后自动隐藏。</summary>
    /// <param name="note">可选副标题；非空时展示时间延长到约 1.8s，保证用户来得及读完。</param>
    public void ShowSuccess(string? note = null)
    {
        if (SuppressesProgress) return;
        ShowTransient(L10n.T("已输入"), note ?? "", Glyph.Check, Color.FromArgb(60, 190, 90),
            TimeSpan.FromSeconds(string.IsNullOrEmpty(note) ? 0.7 : 1.8));
    }

    /// <summary>一次性错误提示，约 2.5s 后自动隐藏——托盘的错误态回落时长与此对齐（R3-04）。</summary>
    public void ShowError(string message) =>
        ShowTransient(L10n.T("错误"), string.IsNullOrEmpty(message) ? L10n.T("服务异常") : message,
            Glyph.Exclaim, Color.FromArgb(235, 70, 60), TimeSpan.FromSeconds(2.5));

    /// <summary>识别跑通但结果为空的一次性提示。与"错误"分开：这不是故障，而是没采到语音，
    /// 用户需要的是"去检查麦克风 / 输入设备"这条具体线索，而不是一个红色叉。</summary>
    public void ShowNoSpeech() =>
        ShowTransient(L10n.T("没有识别到内容"), L10n.T("没听到说话内容，请确认麦克风未静音、输入设备选择正确。"),
            Glyph.Exclaim, Color.FromArgb(240, 150, 40), TimeSpan.FromSeconds(2.5));

    /// <summary>尚未就绪时按下热键的说明（模型下载中等）。不是故障，用橙色而不是红色。</summary>
    public void ShowNotice(string status, string message) =>
        ShowTransient(status, message, Glyph.Exclaim, Color.FromArgb(240, 150, 40), TimeSpan.FromSeconds(2.5));

    /// <summary>"复制上一次识别结果"的反馈。这是用户主动操作，即使浮窗设为"不显示"也要给出确认。</summary>
    public void ShowCopied() =>
        ShowTransient(L10n.T("已复制"), L10n.T("上一次的识别结果已复制到剪贴板，可手动粘贴。"),
            Glyph.Check, Color.FromArgb(60, 190, 90), TimeSpan.FromSeconds(1.8));

    /// <summary>用户按 Esc 取消后的一次性提示，约 1.0s 后自动隐藏。</summary>
    public void ShowCanceled()
    {
        if (SuppressesProgress) return;
        ShowTransient(L10n.T("已取消"), "", Glyph.Cross, Color.FromArgb(170, 170, 170), TimeSpan.FromSeconds(1.0));
    }

    /// <summary>
    /// 录音中或识别中的非致命提示（如预览失败、上一段听写尚未完成）。仅闪烁状态文字，约 1.2s 后恢复；
    /// 两个阶段都要能看到，否则识别中按热键会得到完全无反馈的"死键"观感。警告是状态行上唯一必须被注意
    /// 的内容：闪现期临时升为警示色。
    /// </summary>
    public void FlashWarning(string message)
    {
        if (_phase is not (Phase.Preparing or Phase.Recording or Phase.Recognizing)) return;

        CancelWarningRestore();
        _statusText = string.IsNullOrEmpty(message) ? L10n.T("识别提示") : message;
        _statusWarning = true;
        Invalidate();

        var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            _warningRestoreTimer = null;
            _statusWarning = false;
            _statusText = _phase switch
            {
                Phase.Preparing => _preparingStatusText,
                Phase.Recording => _recordingStatusText,
                _ => _recognizingStatusText,
            };
            Invalidate();
        };
        _warningRestoreTimer = timer;
        timer.Start();
    }

    /// <summary>应用已保存的 UI 配置。就地生效，不重建 HUD 实例（VW-15）。</summary>
    public void ApplyConfig(UIConfig config)
    {
        ApplyOpacity(config.Opacity);
        _placement = config.HudPositionValue;
        // 切到"不显示"时，正在显示的过程类浮窗要立刻收掉，而不是等这次听写结束。
        if (SuppressesProgress && (_phase is Phase.Preparing or Phase.Recording or Phase.Recognizing)) HideHud();
    }

    /// <summary>透明度是设置页可实时预览的外观项。</summary>
    public void ApplyOpacity(double opacity)
    {
        _fadeTimer.Stop();
        _userOpacity = Math.Clamp(opacity, ConfigLimits.OpacityMin, ConfigLimits.OpacityMax);
        Opacity = _userOpacity;
        // 透明度变化会改变窗口是否分层（WS_EX_LAYERED），圆角策略必须跟着换。
        RefreshCornerStrategy();
        if (_hideAfterFade) HideNow();
    }

    /// <summary>
    /// 设置页改浮窗位置时的示例：在新位置显示一条约 2 秒的示例浮窗，让用户看到效果。
    /// 听写进行中不打扰；位置只是预览，保存或撤销后由 <see cref="ApplyConfig"/> 恢复为已保存的设置。
    /// </summary>
    public void ShowSample(HudPlacement? placement = null)
    {
        if (_phase is Phase.Preparing or Phase.Recording or Phase.Recognizing) return;
        if (placement is { } preview) _placement = preview;
        if (SuppressesProgress)
        {
            HideHud();
            return;
        }
        ShowTransient(L10n.T("浮窗预览"), L10n.T("听写时，识别出的文字会实时显示在这里。"),
            Glyph.Check, Color.FromArgb(60, 190, 90), TimeSpan.FromSeconds(2));
    }

    // ─── 内部状态 ─────────────────────────────────────────────────

    private void SetStatus(string text)
    {
        _statusText = text;
        _statusWarning = false;
    }

    private void SetPreviewSource(string text)
    {
        _lastPreviewSource = text;
        _changedChars = 0;
        if (string.IsNullOrEmpty(text)) _previewLines = Array.Empty<string>();
    }

    private void ShowTransient(string status, string message, Glyph glyph, Color accent, TimeSpan autoHideAfter)
    {
        ResetTimers();
        _animationTimer.Stop();

        _phase = Phase.Transient;
        _isCorrecting = false;
        _pendingPaintReady = null;
        _glyph = glyph;
        _accent = accent;
        SetStatus(status);
        SetPreviewSource(message);
        Present(anchorToScreen: true);

        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, (int)autoHideAfter.TotalMilliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            _transientHideTimer = null;
            HideHud();
        };
        _transientHideTimer = timer;
        timer.Start();
    }

    private void ResetTimers()
    {
        CancelTransientHide();
        CancelWarningRestore();
        CancelCollapse();
    }

    private void CancelTransientHide()
    {
        _transientHideTimer?.Stop();
        _transientHideTimer?.Dispose();
        _transientHideTimer = null;
    }

    private void CancelWarningRestore()
    {
        _warningRestoreTimer?.Stop();
        _warningRestoreTimer?.Dispose();
        _warningRestoreTimer = null;
        // 警告被阶段切换打断时一并回落警示色，避免橙色被带进下一个状态。
        _statusWarning = false;
    }

    private void CancelCollapse()
    {
        _collapseTimer?.Stop();
        _collapseTimer?.Dispose();
        _collapseTimer = null;
    }

    // ─── 布局与定位 ───────────────────────────────────────────────

    /// <summary>错误 / "没有识别到内容"即使在"不显示"下也要浮出来，此时落在底部居中。</summary>
    private HudPlacement EffectivePlacement => SuppressesProgress ? HudPlacement.BottomCenter : _placement;

    /// <summary>确定目标屏幕与锚点，按当前内容布局后显示。</summary>
    private void Present(bool anchorToScreen)
    {
        if (anchorToScreen && EffectivePlacement == HudPlacement.NearCursor) _follow = ResolveFollowPoint();
        _targetScreen = ResolveTargetScreen();
        if (anchorToScreen) ComputeAnchor(_targetScreen);
        Relayout();
        if (!Visible)
        {
            if (CanFade)
            {
                Opacity = 0;
                Show();
                StartFade(_userOpacity);
            }
            else
            {
                Show();
            }
        }
        else if (_hideAfterFade)
        {
            // 淡出到一半又有新内容：反向淡回，不隐藏。
            _hideAfterFade = false;
            StartFade(_userOpacity);
        }
        Invalidate();
    }

    // ─── 淡入淡出 ─────────────────────────────────────────────────

    /// <summary>只在窗口已分层（<c>Opacity &lt; 1</c>）且系统开着动画时淡入淡出：不透明度 100% 时为了动画而临时进入
    /// 分层会重走 DWM 圆角那条路径（DESIGN §21），得不偿失。</summary>
    private bool CanFade => MotionEnabled && _userOpacity < 1.0 && IsHandleCreated;

    private void StartFade(double target)
    {
        _fadeTarget = target;
        if (!_fadeTimer.Enabled) _fadeTimer.Start();
    }

    private void FadeTick()
    {
        var step = _userOpacity / FadeSteps;
        var next = Opacity < _fadeTarget ? Math.Min(_fadeTarget, Opacity + step) : Math.Max(_fadeTarget, Opacity - step);
        Opacity = next;
        if (Math.Abs(next - _fadeTarget) > 1e-6) return;

        _fadeTimer.Stop();
        if (_hideAfterFade) HideNow();
    }

    private Screen ResolveTargetScreen()
    {
        try
        {
            if (EffectivePlacement == HudPlacement.NearCursor) return Screen.FromPoint(new Point(_follow.X, _follow.BelowY));
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero) return Screen.FromHandle(fg);
        }
        catch
        {
            // 落到主屏。
        }
        return Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    private void ComputeAnchor(Screen screen)
    {
        var wa = screen.WorkingArea;
        switch (EffectivePlacement)
        {
            case HudPlacement.BottomRight:
                _anchor = new Point(wa.Right - S(ScreenMargin), wa.Bottom - S(BottomInset)); // 右下角
                break;
            case HudPlacement.NearCursor:
                _anchor = new Point(_follow.X, _follow.BelowY);                               // 上边中点
                break;
            default:
                _anchor = new Point(wa.Left + wa.Width / 2, wa.Bottom - S(BottomInset));      // 底边中点
                break;
        }
    }

    /// <summary>"跟随光标"的参照点：正在输入的文字插入点优先，取不到（自绘输入框、提权窗口等）回落到鼠标位置。</summary>
    private FollowPoint ResolveFollowPoint()
    {
        if (CaretLocator.TryGetScreenRect(out var caret))
        {
            var gap = S(CaretGap);
            return new FollowPoint(caret.Left, caret.Bottom + gap, caret.Top - gap);
        }
        var cursor = Cursor.Position;
        return new FollowPoint(cursor.X, cursor.Y + S(CursorGap), cursor.Y - S(CursorGap));
    }

    /// <summary>按当前预览内容重排文字、确定宽高并放到锚点上。</summary>
    private void Relayout()
    {
        var screen = _targetScreen ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var wa = screen.WorkingArea;
        var maxWidth = Math.Min(S(WidthSteps[^1]), (int)(wa.Width * 0.5));

        using (var g = CreateGraphics())
        {
            var format = _previewFormat;
            float Measure(string s) => g.MeasureString(s, _previewFont, PointF.Empty, format).Width;

            // 先定宽再排文：能容纳多少字取决于最终宽度，顺序反了会按旧宽度截出错误的片段。
            var text = _lastPreviewSource;
            int width = S(WidthSteps[0]);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var needed = (int)Math.Ceiling(Measure(text.Trim())) + 2 * S(HorizontalInset);
                width = maxWidth;
                foreach (var step in WidthSteps)
                {
                    if (S(step) >= needed && S(step) <= maxWidth) { width = S(step); break; }
                }
            }
            width = Math.Max(Math.Min(width, maxWidth), Math.Min(S(WidthSteps[0]), maxWidth));

            // 折行按单字符宽度累加，与整串绘制的实际宽度会有细微出入，留 3% 余量避免行尾被裁。
            _previewLines = HudTextLayout.FitTail(text, PreviewMaxLines, (width - 2 * S(HorizontalInset)) * 0.97f, Measure);
            _currentWidth = width;
        }

        var height = S(CompactHeight);
        if (_previewLines.Length > 0)
        {
            height += S(PreviewPadTop) + _previewLines.Length * PreviewLineHeight() + S(PreviewPadBottom);
        }

        Point location = EffectivePlacement switch
        {
            HudPlacement.BottomRight => new Point(_anchor.X - _currentWidth, _anchor.Y - height),
            HudPlacement.NearCursor => new Point(_anchor.X - _currentWidth / 2, _anchor.Y),
            _ => new Point(_anchor.X - _currentWidth / 2, _anchor.Y - height),
        };
        // 不出屏：跟随光标时靠近屏幕边缘要翻到光标 / 插入点上方 / 收回边界内。
        if (EffectivePlacement == HudPlacement.NearCursor && location.Y + height > wa.Bottom)
        {
            location.Y = Math.Max(wa.Top, _follow.AboveY - height);
        }
        location.X = Math.Clamp(location.X, wa.Left + S(ScreenMargin), Math.Max(wa.Left + S(ScreenMargin), wa.Right - _currentWidth - S(ScreenMargin)));
        location.Y = Math.Clamp(location.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - height));

        var bounds = new Rectangle(location, new Size(_currentWidth, height));
        if (Bounds != bounds) Bounds = bounds;
        Invalidate();
    }

    private int PreviewLineHeight() => (int)Math.Ceiling(_previewFont.GetHeight(DeviceDpi) * 1.15);

    /// <summary>预览文字的测量与绘制共用同一份格式，宽度才一致；不加 NoWrap 会让 MeasureString 自行折行。
    /// 所有权：窗体持有，构造时创建一次、<see cref="Dispose(bool)"/> 时释放；使用方不得 Dispose 它，
    /// 也不得修改（只在 UI 线程使用）。</summary>
    private readonly StringFormat _previewFormat = CreatePreviewFormat();

    /// <summary>状态文字的绘制格式；所有权与用法同 <see cref="_previewFormat"/>。</summary>
    private readonly StringFormat _statusFormat = CreateStatusFormat();

    private static StringFormat CreatePreviewFormat()
    {
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        return format;
    }

    private static StringFormat CreateStatusFormat()
    {
        var format = (StringFormat)StringFormat.GenericDefault.Clone();
        format.Trimming = StringTrimming.EllipsisCharacter;
        format.FormatFlags |= StringFormatFlags.NoWrap;
        format.LineAlignment = StringAlignment.Center;
        return format;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (Visible) Relayout();
    }

    /// <summary>
    /// 决定圆角实现并立即应用，两条路径按窗口是否分层选择：
    /// - <c>Opacity &lt; 1</c>（WinForms 会挂 <c>WS_EX_LAYERED</c> 并用
    ///   <c>SetLayeredWindowAttributes</c> 做整窗半透明）：<b>不</b>设置 DWM 圆角，并显式要求
    ///   DWM 不圆角、不画边框，回到与 Win10 一致的 Region 裁剪 + 自绘边框。Win11 真机上对这类
    ///   分层窗口设置 <c>DWMWA_WINDOW_CORNER_PREFERENCE</c> 会导致客户区不再合成——表现为
    ///   HUD 只剩一条 DWM 画的顶部细线、其余纯透明（2026-10 Win11 真机反馈）。
    /// - <c>Opacity = 1</c>（普通窗口）：优先 DWM 圆角（无锯齿、无自绘边框）；Win10 等老系统
    ///   调用失败，以调用结果为准回退 Region。
    /// 不判断系统版本号，直接以调用是否成功为准更可靠。透明度可在设置中实时调整，
    /// 分层状态随之切换，因此不能只在句柄创建时决定一次。
    /// </summary>
    private void RefreshCornerStrategy()
    {
        if (!IsHandleCreated) return;

        bool isLayered = (GetWindowLong(Handle, GWL_EXSTYLE) & WS_EX_LAYERED) != 0;
        if (isLayered)
        {
            TrySetDwmAttribute(DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DONOTROUND);
            TrySetDwmAttribute(DWMWA_BORDER_COLOR, DWMWA_COLOR_NONE);
            _useDwmRoundCorners = false;
        }
        else
        {
            _useDwmRoundCorners = TrySetDwmAttribute(DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        }
        ApplyRoundedRegionIfNeeded();
    }

    /// <summary>尽力设置 DWM 窗口属性；老系统返回错误 HRESULT 时返回 false，调用方自行回退。</summary>
    private bool TrySetDwmAttribute(int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(Handle, attribute, ref value, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyRoundedRegionIfNeeded()
    {
        if (!IsHandleCreated) return;
        if (_useDwmRoundCorners)
        {
            // 从 DWM 圆角切回（如透明度调低）时清掉 Region，避免残留裁剪。
            if (Region is not null)
            {
                var old = Region;
                Region = null;
                old.Dispose();
            }
            return;
        }
        var previous = Region;
        Region = BuildRoundedRegion(ClientRectangle, S(14));
        previous?.Dispose();
    }

    // ─── 绘制 ─────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        if (!_useDwmRoundCorners)
        {
            using var border = new Pen(Color.FromArgb(40, 255, 255, 255), 1f);
            using var path = BuildRoundedPath(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), S(13f));
            g.DrawPath(border, path);
        }

        var inset = S(HorizontalInset);
        var rowCenterY = S(CompactHeight) / 2f;
        var x = (float)inset;

        // 圆点 / 结果图标
        var dot = S(12f);
        var dotRect = new RectangleF(x, rowCenterY - dot / 2, dot, dot);
        DrawIndicator(g, dotRect);
        x += dot + S(10f);

        // 波形（启动中、录音与识别阶段；启动中是平直的暗条，表示还没在听）
        if (_phase is Phase.Preparing or Phase.Recording or Phase.Recognizing)
        {
            var waveWidth = S(40f);
            DrawWaveform(g, new RectangleF(x, rowCenterY - S(11f), waveWidth, S(22f)));
            x += waveWidth + S(10f);
        }

        // 计时（右对齐）；首秒不显示，避免 "0s"→"1s" 的无谓跳变。
        float rightEdge = Width - inset;
        if (_phase is Phase.Recording or Phase.Recognizing)
        {
            var elapsed = (int)(_phase == Phase.Recording ? ElapsedRecordingSeconds() : _frozenElapsedSeconds);
            if (elapsed > 0)
            {
                var timerText = $"{elapsed}s";
                var timerSize = g.MeasureString(timerText, _timerFont);
                using var timerBrush = new SolidBrush(Color.FromArgb(160, 255, 255, 255));
                g.DrawString(timerText, _timerFont, timerBrush, rightEdge - timerSize.Width, rowCenterY - timerSize.Height / 2);
                rightEdge -= timerSize.Width + S(10f);
            }
        }

        // 状态文字。状态 / 计时属装饰行（约 65% 白），预览是正文行（约 92% 白），层级与 macOS 对齐。
        // FlashWarning 闪现期升为不透明警示橙，确保警告被注意到；纠错中用呼吸代替流光。
        int statusAlpha = 165;
        if (_isCorrecting && MotionEnabled) statusAlpha = (int)(150 + 90 * (0.5 + 0.5 * Math.Sin(_pulsePhase)));
        var statusColor = _statusWarning ? Color.FromArgb(255, 250, 190, 40) : Color.FromArgb(statusAlpha, 255, 255, 255);
        var statusText = CurrentStatusText(out var countingDown);
        if (countingDown && !_statusWarning) statusColor = Color.FromArgb(255, 250, 190, 40);
        using (var statusBrush = new SolidBrush(statusColor))
        {
            var statusRect = new RectangleF(x, 0, Math.Max(0, rightEdge - x), S(CompactHeight));
            g.DrawString(statusText, _statusFont, statusBrush, statusRect, _statusFormat);
        }

        // 预览 / 提示正文
        if (_previewLines.Length > 0)
        {
            using var previewBrush = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            using var freshBrush = new SolidBrush(Color.FromArgb(140, 255, 255, 255));
            var lineHeight = PreviewLineHeight();
            var y = (float)(S(CompactHeight) + S(PreviewPadTop));
            var format = _previewFormat;
            // 最后 dimRemaining 个字符属于"刚变化"的部分；显示行是原文的尾部，所以从最后一行往前数。
            var dimRemaining = FreshCharCount();
            var dimPerLine = new int[_previewLines.Length];
            for (int i = _previewLines.Length - 1; i >= 0 && dimRemaining > 0; i--)
            {
                dimPerLine[i] = Math.Min(dimRemaining, _previewLines[i].Length);
                dimRemaining -= dimPerLine[i];
            }
            for (int i = 0; i < _previewLines.Length; i++)
            {
                var line = _previewLines[i];
                var stable = line[..(line.Length - dimPerLine[i])];
                var fresh = line[stable.Length..];
                float lineX = inset;
                if (stable.Length > 0)
                {
                    g.DrawString(stable, _previewFont, previewBrush, lineX, y, format);
                    if (fresh.Length > 0) lineX += g.MeasureString(stable, _previewFont, PointF.Empty, format).Width;
                }
                if (fresh.Length > 0) g.DrawString(fresh, _previewFont, freshBrush, lineX, y, format);
                y += lineHeight;
            }
        }

        if (_pendingPaintReady is { } ready)
        {
            _pendingPaintReady = null;
            ProgressPainted?.Invoke(ready, Stopwatch.GetTimestamp());
        }
    }

    private double _frozenElapsedSeconds;

    /// <summary>仍处于"刚变化"着色期的尾部字符数；非录音 / 识别阶段、系统关闭动画、已过 <see cref="StableAfterMs"/> 时为 0。</summary>
    private int FreshCharCount()
    {
        if (_changedChars <= 0 || !MotionEnabled || _phase is not (Phase.Recording or Phase.Recognizing)) return 0;
        return Stopwatch.GetElapsedTime(_previewChangedAt).TotalMilliseconds < StableAfterMs ? _changedChars : 0;
    }

    /// <summary>状态行文字。录音接近单段上限时改为倒计时——到点会自动结束并上屏，用户需要知道还能说多久。
    /// 闪现中的警告优先，不被覆盖。</summary>
    private string CurrentStatusText(out bool countingDown)
    {
        countingDown = false;
        if (_phase != Phase.Recording || _statusWarning) return _statusText;
        var remaining = AppConstants.MaxSessionSeconds - (int)ElapsedRecordingSeconds();
        if (remaining > CountdownSeconds) return _statusText;
        countingDown = true;
        return L10n.F("录音将在 {0} 秒后自动结束", Math.Max(remaining, 0));
    }

    private double ElapsedRecordingSeconds() => Stopwatch.GetElapsedTime(_startedTimestamp).TotalSeconds;

    private void DrawIndicator(Graphics g, RectangleF rect)
    {
        var accent = _accent;
        if (_phase == Phase.Recording)
        {
            var alpha = MotionEnabled ? (int)(180 + 75 * Math.Sin(_pulsePhase)) : 255;
            using var brush = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 80, 255), accent));
            g.FillEllipse(brush, rect);
            return;
        }
        using (var brush = new SolidBrush(accent))
        {
            g.FillEllipse(brush, rect);
        }
        if (_glyph == Glyph.None) return;

        using var pen = new Pen(Color.White, Math.Max(1.5f, S(1.8f))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        var u = rect.Width / 12f;
        switch (_glyph)
        {
            case Glyph.Check:
                g.DrawLines(pen, new[] { new PointF(cx - 3 * u, cy), new PointF(cx - u, cy + 2.4f * u), new PointF(cx + 3 * u, cy - 2.4f * u) });
                break;
            case Glyph.Cross:
                g.DrawLine(pen, cx - 2.6f * u, cy - 2.6f * u, cx + 2.6f * u, cy + 2.6f * u);
                g.DrawLine(pen, cx - 2.6f * u, cy + 2.6f * u, cx + 2.6f * u, cy - 2.6f * u);
                break;
            case Glyph.Exclaim:
                g.DrawLine(pen, cx, cy - 3.4f * u, cx, cy + 0.8f * u);
                g.DrawLine(pen, cx, cy + 2.9f * u, cx, cy + 3.0f * u);
                break;
        }
    }

    private void DrawWaveform(Graphics g, RectangleF area)
    {
        var barWidth = area.Width / (BarCount * 2 - 1);
        var color = _phase switch
        {
            Phase.Recording => Color.FromArgb(230, 255, 255, 255),
            Phase.Preparing => Color.FromArgb(90, 255, 255, 255),
            _ => Color.FromArgb(150, 255, 255, 255),
        };
        using var brush = new SolidBrush(color);
        for (int i = 0; i < BarCount; i++)
        {
            float level = _bars[i];
            if (_phase == Phase.Recognizing)
            {
                // 识别阶段：条形依次起伏，表示"在处理"，而不是停在录音时的最后形状。
                level = MotionEnabled ? 0.18f + 0.14f * (float)Math.Sin(_pulsePhase * 1.5 + i * 0.7) : 0.2f;
            }
            var barHeight = Math.Max(S(3f), level * area.Height);
            var barX = area.X + i * barWidth * 2;
            var barRect = new RectangleF(barX, area.Y + (area.Height - barHeight) / 2, barWidth, barHeight);
            using var path = BuildRoundedPath(barRect, barWidth / 2);
            g.FillPath(brush, path);
        }
    }

    private static Region BuildRoundedRegion(Rectangle rect, float radius)
    {
        using var path = BuildRoundedPath(rect, radius);
        return new Region(path);
    }

    private static GraphicsPath BuildRoundedPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        radius = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        var d = radius * 2;
        if (d <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
            _fadeTimer.Dispose();
            _transientHideTimer?.Dispose();
            _warningRestoreTimer?.Dispose();
            _collapseTimer?.Dispose();
            _timerFont.Dispose();
            _previewFormat.Dispose();
            _statusFormat.Dispose();
        }
        base.Dispose(disposing);
    }
}
