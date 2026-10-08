using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
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
/// 阶段：Recording → Recognizing（→ 纠错中）→ 一次性结果（成功 / 错误 / 已取消 / 没有识别到内容）。
///
/// 渲染路径：<b>逐像素 alpha 分层窗口</b>（<c>WS_EX_LAYERED</c> + <c>UpdateLayeredWindow</c>）。
/// 每帧把整个 HUD 画进 32bpp ARGB 位图，预乘 alpha 后一次性推给系统合成——
/// 背景半透明而文字保持全不透明（对齐 macOS 磨砂层的视觉层级），圆角自带抗锯齿，
/// Win10 / Win11 一致，不再需要 DWM 圆角探测与 Region 裁剪两条路径。
/// <c>ui.opacity</c> 的语义因此是<b>背景不透明度</b>（文字不随之变淡）。
/// 若 <c>UpdateLayeredWindow</c> 不可用（理论上仅在特殊驱动 / 远程会话下），
/// 重建句柄退回普通实心窗口 + WM_PAINT 绘制。
///
/// 出现 / 消失带约 120ms / 100ms 的整窗淡入淡出；错误类提示立即出现（要被注意到），
/// 只淡出。系统关闭动画时不做任何呼吸 / 脉冲 / 淡入淡出。
///
/// 落点由 <c>ui.hud_position</c> 决定；"不显示"只静音<b>过程</b>，错误与"没有识别到内容"仍会浮出来。
/// 所有像素尺寸按窗口当前 DPI 缩放（PerMonitorV2 下窗口拖到别的屏幕会收到 DPI 变化）。
/// 所有方法必须在 UI 线程调用。
/// </summary>
internal sealed class RecordingHud : Form
{
    private enum Phase { Hidden, Recording, Recognizing, Transient }
    private enum Glyph { None, Check, Cross, Exclaim }

    // ─── 几何常量（96 DPI 下的像素，绘制与布局时经 S() 缩放）───────────
    private const int CompactHeight = 48;
    private const int PreviewPadTop = 2;
    private const int PreviewPadBottom = 14;
    private const int HorizontalInset = 16;
    private const int BottomInset = 80;
    private const int CursorGap = 24;
    private const int ScreenMargin = 16;
    private const int PreviewMaxLines = 2;
    private const int BarCount = 9;
    /// <summary>宽度按档位增长而不是逐像素跟随文本，避免文字一边识别一边窗口一直在抖。</summary>
    private static readonly int[] WidthSteps = { 420, 560, 700, 860 };
    /// <summary>胶囊圆角半径（画进 ARGB 位图，边缘自带抗锯齿）。</summary>
    private const float CornerRadius = 14f;

    // ─── 淡入淡出节奏（计时器 33ms 一拍）─────────────────────────
    private const float FadeInStep = 0.3f;   // ≈110ms
    private const float FadeOutStep = 0.34f; // ≈100ms

    private readonly System.Windows.Forms.Timer _animationTimer;
    private readonly Font _statusFont;
    private readonly Font _timerFont;
    private readonly Font _previewFont;

    private Phase _phase = Phase.Hidden;
    /// <summary>录音开始的 <see cref="Stopwatch"/> 时间戳；单调时钟，不受系统时间调整影响。</summary>
    private long _startedTimestamp = Stopwatch.GetTimestamp();
    private double _pulsePhase;
    private string _statusText = "";
    private string _recordingStatusText = L10n.T("录音中");
    private string _recognizingStatusText = L10n.T("识别中");
    private string[] _previewLines = Array.Empty<string>();
    private string _lastPreviewSource = "";
    private Glyph _glyph = Glyph.None;
    private Color _accent = Color.FromArgb(235, 70, 60);
    /// <summary>状态文字是否处于 FlashWarning 的警示色闪现期（约 1.2s）。</summary>
    private bool _statusWarning;
    private bool _isCorrecting;
    private readonly float[] _bars = new float[BarCount];
    private long _lastLevelTimestamp; // 0 = 尚未收到过电平
    /// <summary>背景不透明度（来自 <c>ui.opacity</c>）；文字与描边不随它变淡。</summary>
    private double _bgAlpha = 0.9;

    private HudPlacement _placement = HudPlacement.BottomCenter;
    /// <summary>底部落点时，展开 / 收起保持底边不动、向上生长；跟随光标时保持左上角不动。</summary>
    private Point _anchor;
    private int _currentWidth;
    private int _currentHeight;
    private Screen? _targetScreen;

    // ─── 渲染资源（按窗口尺寸重建；UI 线程独占）──────────────────
    /// <summary>直通 alpha 的场景位图（画笔画的都是"未预乘"颜色）。</summary>
    private Bitmap? _scene;
    /// <summary>预乘 alpha + 淡出系数后的成品帧，UpdateLayeredWindow 直接消费。</summary>
    private Bitmap? _frame;
    private byte[]? _premultiplyBuffer;
    private IntPtr _memoryDc;
    /// <summary>ULW 调用失败后置位：重建句柄去掉 WS_EX_LAYERED，退回实心 WM_PAINT 绘制。</summary>
    private bool _layeredBroken;

    /// <summary>整窗淡入淡出的当前值与目标值（1 = 完全显示；淡出到位后真正 Hide）。</summary>
    private float _fadeAlpha = 1f;
    private float _fadeTarget = 1f;
    /// <summary>处于"淡出收起"中：期间继续画旧内容，alpha 到 0 才真正隐藏。</summary>
    private bool _hiding;

    /// <summary>一次性提示（成功/错误/已取消）的自动隐藏任务；状态变化时作废。</summary>
    private System.Windows.Forms.Timer? _transientHideTimer;
    /// <summary><see cref="FlashWarning"/> 的状态文字恢复任务；状态变化时作废。</summary>
    private System.Windows.Forms.Timer? _warningRestoreTimer;
    /// <summary>预览短暂清空时延迟收起，避免窗口一伸一缩地抖动。</summary>
    private System.Windows.Forms.Timer? _collapseTimer;

    /// <summary>系统关闭动画（设置 → 辅助功能 → 视觉效果 → 动画效果）时不做呼吸 / 脉冲 / 淡入淡出，
    /// 只保留电平波形（它是功能性反馈，不是装饰）。</summary>
    private static bool MotionEnabled => SystemInformation.UIEffectsEnabled;

    public RecordingHud(UIConfig uiConfig)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        // 注意：不设置 Form.Opacity——它会让整个窗口（含文字）统一变淡，
        // 与逐像素路径冲突；半透明只作用于背景（见 RenderScene）。
        _bgAlpha = Math.Clamp(uiConfig.Opacity, 0.4, 1.0);
        _placement = uiConfig.HudPositionValue;
        BackColor = Color.FromArgb(26, 28, 33); // 仅实心回退路径可见；分层路径整帧自绘
        Size = new Size(S(WidthSteps[0]), S(CompactHeight));
        _currentWidth = WidthSteps[0];
        _currentHeight = CompactHeight;

        // 层级约定与 macOS 一致：预览文字是 HUD 的正文（用户实时校对识别结果），
        // 字号与亮度都高于顶行的状态提示（呼吸点/波形已在传达录音状态）。
        _statusFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        _timerFont = new Font("Consolas", 9f, FontStyle.Regular);
        _previewFont = new Font("Segoe UI", 10.5f, FontStyle.Regular);

        _animationTimer = new System.Windows.Forms.Timer { Interval = 33 };
        _animationTimer.Tick += (_, _) => AnimationTick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (!_layeredBroken) cp.ExStyle |= WS_EX_LAYERED;
            return cp;
        }
    }

    private int S(int pixels) => (int)Math.Round(pixels * DeviceDpi / 96.0);
    private float S(float pixels) => pixels * DeviceDpi / 96f;

    /// <summary>"不显示"下要跳过的过程类展示（录音、预览、识别、成功、取消）。</summary>
    private bool SuppressesProgress => _placement == HudPlacement.Hidden;

    // ─── 公共接口 ─────────────────────────────────────────────────

    /// <param name="inputDeviceName">本次录音实际使用的输入设备名（可能与系统默认不同，见 AudioInputPolicy）。</param>
    public void ShowRecording(string? inputDeviceName)
    {
        if (SuppressesProgress) return;
        ResetTimers();

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
        Present(anchorToScreen: true, fadeIn: true);
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
        EnsureTimerRunning();
        RequestRender();
    }

    /// <summary>
    /// 本地识别已出结果，正在等 LLM 纠错。与"识别中"分开的理由：本地推理是几百毫秒且完全可控，
    /// 而纠错要走网络、最长可以等到 <c>llm.timeout</c>；两者都显示"识别中"的话，用户既不知道
    /// 该不该继续等，也无从判断慢在哪。
    /// </summary>
    public void SetCorrecting()
    {
        if (SuppressesProgress || _phase != Phase.Recognizing) return;
        CancelWarningRestore();
        _isCorrecting = true;
        _recognizingStatusText = L10n.T("纠错中…");
        SetStatus(_recognizingStatusText);
        _accent = Color.FromArgb(70, 140, 255);
        RequestRender();
    }

    public void HideHud()
    {
        ResetTimers();
        _isCorrecting = false;
        SetPreviewSource("");
        if (!Visible)
        {
            _phase = Phase.Hidden;
            _fadeAlpha = 0;
            _fadeTarget = 0;
            return;
        }
        if (MotionEnabled)
        {
            // 先淡出再隐藏；淡出期间继续显示当前内容（阶段在到位后才切到 Hidden）。
            _hiding = true;
            _fadeTarget = 0;
            EnsureTimerRunning();
        }
        else
        {
            _phase = Phase.Hidden;
            _fadeAlpha = _fadeTarget = 0;
            Hide();
            _animationTimer.Stop();
        }
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
        SetPreviewSource(accumulated);
        Relayout();
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
            TimeSpan.FromSeconds(string.IsNullOrEmpty(note) ? 0.7 : 1.8), fadeIn: true);
    }

    /// <summary>一次性错误提示，约 2.5s 后自动隐藏——托盘的错误态回落时长与此对齐（R3-04）。</summary>
    public void ShowError(string message) =>
        ShowTransient(L10n.T("错误"), string.IsNullOrEmpty(message) ? L10n.T("服务异常") : message,
            Glyph.Exclaim, Color.FromArgb(235, 70, 60), TimeSpan.FromSeconds(2.5), fadeIn: false);

    /// <summary>识别跑通但结果为空的一次性提示。与"错误"分开：这不是故障，而是没采到语音，
    /// 用户需要的是"去检查麦克风 / 输入设备"这条具体线索，而不是一个红色叉。</summary>
    public void ShowNoSpeech() =>
        ShowTransient(L10n.T("没有识别到内容"), L10n.T("没听到说话内容，请确认麦克风未静音、输入设备选择正确。"),
            Glyph.Exclaim, Color.FromArgb(240, 150, 40), TimeSpan.FromSeconds(2.5), fadeIn: false);

    /// <summary>尚未就绪时按下热键的说明（模型下载中等）。不是故障，用橙色而不是红色。</summary>
    public void ShowNotice(string status, string message) =>
        ShowTransient(status, message, Glyph.Exclaim, Color.FromArgb(240, 150, 40), TimeSpan.FromSeconds(2.5), fadeIn: false);

    /// <summary>用户按 Esc 取消后的一次性提示，约 1.0s 后自动隐藏。</summary>
    public void ShowCanceled()
    {
        if (SuppressesProgress) return;
        ShowTransient(L10n.T("已取消"), "", Glyph.Cross, Color.FromArgb(170, 170, 170), TimeSpan.FromSeconds(1.0), fadeIn: true);
    }

    /// <summary>
    /// 录音中或识别中的非致命提示（如预览失败、上一段听写尚未完成）。仅闪烁状态文字，约 1.2s 后恢复；
    /// 两个阶段都要能看到，否则识别中按热键会得到完全无反馈的"死键"观感。警告是状态行上唯一必须被注意
    /// 的内容：闪现期临时升为警示色。
    /// </summary>
    public void FlashWarning(string message)
    {
        if (_phase is not (Phase.Recording or Phase.Recognizing)) return;

        CancelWarningRestore();
        _statusText = string.IsNullOrEmpty(message) ? L10n.T("识别提示") : message;
        _statusWarning = true;
        RequestRender();

        var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            _warningRestoreTimer = null;
            _statusWarning = false;
            _statusText = _phase == Phase.Recording ? _recordingStatusText : _recognizingStatusText;
            RequestRender();
        };
        _warningRestoreTimer = timer;
        timer.Start();
    }

    /// <summary>应用已保存的 UI 配置。就地生效，不重建 HUD 实例（VW-15）。</summary>
    public void ApplyConfig(UIConfig config)
    {
        _bgAlpha = Math.Clamp(config.Opacity, 0.4, 1.0);
        _placement = config.HudPositionValue;
        // 切到"不显示"时，正在显示的过程类浮窗要立刻收掉，而不是等这次听写结束。
        if (SuppressesProgress && (_phase is Phase.Recording or Phase.Recognizing)) HideHud();
        else RequestRender();
    }

    /// <summary>背景不透明度是设置页可实时预览的外观项；文字不随之变淡。</summary>
    public void ApplyOpacity(double opacity)
    {
        _bgAlpha = Math.Clamp(opacity, 0.4, 1.0);
        RequestRender();
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
        if (string.IsNullOrEmpty(text)) _previewLines = Array.Empty<string>();
    }

    private void ShowTransient(string status, string message, Glyph glyph, Color accent,
        TimeSpan autoHideAfter, bool fadeIn)
    {
        ResetTimers();

        _phase = Phase.Transient;
        _isCorrecting = false;
        _glyph = glyph;
        _accent = accent;
        SetStatus(status);
        SetPreviewSource(message);
        Present(anchorToScreen: true, fadeIn: fadeIn);

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

    // ─── 动画节拍 ────────────────────────────────────────────────

    private void EnsureTimerRunning()
    {
        if (!_animationTimer.Enabled) _animationTimer.Start();
    }

    /// <summary>一拍（约 33ms）：呼吸相位、电平衰减、淡入淡出推进，随后重绘一帧。
    /// 静止且无动画可推进时停表，不空转。</summary>
    private void AnimationTick()
    {
        bool animate = false;

        if (_phase is Phase.Recording or Phase.Recognizing)
        {
            // 电平自然衰减：静音时波形回落，而不是停在最后一次的高度。衰减不依赖动画开关。
            for (int i = 0; i < _bars.Length; i++) _bars[i] *= 0.94f;
            animate = true;
        }
        if (MotionEnabled && _phase is Phase.Recording or Phase.Recognizing)
        {
            _pulsePhase = (_pulsePhase + 0.16) % (Math.PI * 2);
            animate = true;
        }

        if (Math.Abs(_fadeAlpha - _fadeTarget) > 0.001f)
        {
            var step = _fadeTarget > _fadeAlpha ? FadeInStep : FadeOutStep;
            _fadeAlpha = _fadeTarget > _fadeAlpha
                ? Math.Min(_fadeTarget, _fadeAlpha + step)
                : Math.Max(_fadeTarget, _fadeAlpha - step);
            animate = true;
            if (_hiding && _fadeAlpha <= 0f)
            {
                _hiding = false;
                _phase = Phase.Hidden;
                Hide();
            }
        }

        if (Visible)
        {
            if (_layeredBroken) Invalidate(); // 实心回退路径走系统 WM_PAINT
            else PushFrame();
        }
        if (!animate && Math.Abs(_fadeAlpha - _fadeTarget) <= 0.001f) _animationTimer.Stop();
    }

    // ─── 布局与定位 ───────────────────────────────────────────────

    /// <summary>错误 / "没有识别到内容"即使在"不显示"下也要浮出来，此时落在底部居中。</summary>
    private HudPlacement EffectivePlacement => SuppressesProgress ? HudPlacement.BottomCenter : _placement;

    /// <summary>确定目标屏幕与锚点，按当前内容布局后显示。
    /// 测试可通过 <see cref="SuppressPresentationForTest"/> 关掉显示（只做布局），
    /// 避免 dotnet test 在真实屏幕上弹出 HUD。</summary>
    internal bool SuppressPresentationForTest;

    private void Present(bool anchorToScreen, bool fadeIn)
    {
        _hiding = false;
        _targetScreen = ResolveTargetScreen();
        if (anchorToScreen) ComputeAnchor(_targetScreen);
        Relayout();
        if (SuppressPresentationForTest) return;
        if (!Visible)
        {
            _fadeAlpha = fadeIn && MotionEnabled ? 0f : 1f;
            _fadeTarget = 1f;
            Show();
        }
        EnsureTimerRunning();
        RequestRender();
    }

    private Screen ResolveTargetScreen()
    {
        try
        {
            if (EffectivePlacement == HudPlacement.NearCursor) return Screen.FromPoint(Cursor.Position);
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
                var cursor = Cursor.Position;
                _anchor = new Point(cursor.X, cursor.Y + S(CursorGap));                       // 左上角
                break;
            default:
                _anchor = new Point(wa.Left + wa.Width / 2, wa.Bottom - S(BottomInset));      // 底边中点
                break;
        }
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
        _currentHeight = height;

        Point location = EffectivePlacement switch
        {
            HudPlacement.BottomRight => new Point(_anchor.X - _currentWidth, _anchor.Y - height),
            HudPlacement.NearCursor => new Point(_anchor.X - _currentWidth / 2, _anchor.Y),
            _ => new Point(_anchor.X - _currentWidth / 2, _anchor.Y - height),
        };
        // 不出屏：跟随光标时靠近屏幕边缘要翻到光标上方 / 收回边界内。
        if (EffectivePlacement == HudPlacement.NearCursor && location.Y + height > wa.Bottom)
        {
            location.Y = Math.Max(wa.Top, Cursor.Position.Y - S(CursorGap) - height);
        }
        location.X = Math.Clamp(location.X, wa.Left + S(ScreenMargin), Math.Max(wa.Left + S(ScreenMargin), wa.Right - _currentWidth - S(ScreenMargin)));
        location.Y = Math.Clamp(location.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - height));

        var bounds = new Rectangle(location, new Size(_currentWidth, height));
        if (Bounds != bounds) Bounds = bounds;
        RequestRender();
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
        _scene?.Dispose(); _scene = null;
        _frame?.Dispose(); _frame = null;
        if (Visible) Relayout();
    }

    // ─── 帧合成（UpdateLayeredWindow）────────────────────────────

    /// <summary>请求重绘一帧；分层路径立即推帧，实心回退路径走 Invalidate。</summary>
    private void RequestRender()
    {
        if (!Visible && !_hiding) return;
        if (_layeredBroken)
        {
            Invalidate();
            return;
        }
        PushFrame();
    }

    private void EnsureFrameResources(int width, int height)
    {
        if (_scene is not null && _scene.Width == width && _scene.Height == height) return;
        _scene?.Dispose();
        _frame?.Dispose();
        _scene = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        // 位图分辨率跟随窗口 DPI：点阵字号（pt）据此换算成像素，跨屏拖动后重建即可。
        _scene.SetResolution(DeviceDpi, DeviceDpi);
        _frame = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        _frame.SetResolution(DeviceDpi, DeviceDpi);
        _premultiplyBuffer = new byte[width * height * 4]; // 32bpp 的 Stride 恒等于 w*4
    }

    /// <summary>渲染场景 → 预乘 + 淡出 → UpdateLayeredWindow。失败一次即永久回退实心窗口。</summary>
    private void PushFrame()
    {
        if (_layeredBroken) return;
        try
        {
            EnsureFrameResources(Width, Height);
            RenderSceneTo(_scene!);
            // 先把直通 alpha 的场景整帧拷进成品位图，再做预乘（GDI+ 的 DrawImage 保留 alpha 通道）。
            using (var fg = Graphics.FromImage(_frame!))
            {
                fg.DrawImage(_scene!, new Rectangle(0, 0, _frame!.Width, _frame.Height),
                    new Rectangle(0, 0, _scene!.Width, _scene.Height), GraphicsUnit.Pixel);
            }
            PremultiplyWithFade(_frame, (int)Math.Round(Math.Clamp(_fadeAlpha, 0f, 1f) * 255));
            PresentLayered(_frame);
        }
        catch (Exception error)
        {
            // 渲染管线出问题（特殊驱动 / 远程会话）不应让 HUD 永远消失：退回实心窗口。
            AppLog.Warn("hud", $"分层渲染失败，回退实心窗口：{error.GetType().Name}");
            DisableLayeredMode();
        }
    }

    private void PremultiplyWithFade(Bitmap frame, int fade)
    {
        var rect = new Rectangle(0, 0, frame.Width, frame.Height);
        var bits = frame.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = _premultiplyBuffer!;
            Marshal.Copy(bits.Scan0, bytes, 0, bytes.Length);
            for (int i = 0; i < bytes.Length; i += 4)
            {
                var a = bytes[i + 3] * fade / 255;
                bytes[i + 3] = (byte)a;
                // GDI+ 画出来是直通 alpha；UpdateLayeredWindow 要求预乘。
                bytes[i] = (byte)(bytes[i] * a / 255);
                bytes[i + 1] = (byte)(bytes[i + 1] * a / 255);
                bytes[i + 2] = (byte)(bytes[i + 2] * a / 255);
            }
            Marshal.Copy(bytes, 0, bits.Scan0, bytes.Length);
        }
        finally
        {
            frame.UnlockBits(bits);
        }
    }

    private void PresentLayered(Bitmap frame)
    {
        if (_memoryDc == IntPtr.Zero) _memoryDc = CreateCompatibleDC(IntPtr.Zero);
        if (_memoryDc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC 失败");

        // hBitmap 保持选中状态，下一帧 SelectObject 换新的时释放旧的；Dispose 时统一清理。
        var hBitmap = frame.GetHbitmap(Color.FromArgb(0));
        var previous = SelectObject(_memoryDc, hBitmap);
        if (previous != IntPtr.Zero) DeleteObject(previous);

        var location = new Point(Left, Top);
        var size = new Size(frame.Width, frame.Height);
        var source = Point.Empty;
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };
        if (!UpdateLayeredWindow(Handle, IntPtr.Zero, ref location, ref size,
                _memoryDc, ref source, 0, ref blend, ULW_ALPHA))
        {
            throw new InvalidOperationException($"UpdateLayeredWindow 失败：{Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>ULW 不可用：去掉 WS_EX_LAYERED 重建句柄，改走普通 WM_PAINT（OnPaintFallback）。</summary>
    private void DisableLayeredMode()
    {
        if (_layeredBroken) return;
        _layeredBroken = true;
        var wasVisible = Visible;
        RecreateHandle();
        if (wasVisible && !IsDisposed)
        {
            Show();
            Invalidate();
        }
    }

    // ─── 绘制 ─────────────────────────────────────────────────────

    /// <summary>测试与预览用途：把当前状态渲染成一张直通 alpha 的完整帧（不做预乘、不推窗口）。
    /// 前提是先驱动过一次状态（如 ShowRecording），让布局几何生效。</summary>
    internal Bitmap RenderPreviewFrame()
    {
        var preview = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        preview.SetResolution(DeviceDpi, DeviceDpi);
        RenderSceneTo(preview);
        return preview;
    }

    private void RenderSceneTo(Bitmap target)
    {
        using var g = Graphics.FromImage(target);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // ClearType 依赖不透明底色；半透明背景上会出彩色镶边，改用灰度抗锯齿。
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        RenderScene(g, opaque: false);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_layeredBroken) return; // 分层路径的内容不走 WM_PAINT
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        RenderScene(g, opaque: true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_layeredBroken) base.OnPaintBackground(e);
        // 分层窗口的背景擦除会破坏 ULW 内容，跳过。
    }

    private void RenderScene(Graphics g, bool opaque)
    {
        g.Clear(Color.Transparent);

        // 胶囊底：半透明深色（实心回退时不透明）。文字与波形的亮度不随 ui.opacity 变化。
        var bgAlpha = opaque ? 255 : (int)(255 * _bgAlpha);
        using (var path = BuildRoundedPath(new RectangleF(0, 0, Width, Height), S(CornerRadius)))
        {
            using (var background = new SolidBrush(Color.FromArgb(bgAlpha, 26, 28, 33)))
            {
                g.FillPath(background, path);
            }
            using var border = new Pen(Color.FromArgb(38, 255, 255, 255), 1f);
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

        // 波形（录音与识别阶段）
        if (_phase is Phase.Recording or Phase.Recognizing)
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
        using (var statusBrush = new SolidBrush(statusColor))
        {
            var statusRect = new RectangleF(x, 0, Math.Max(0, rightEdge - x), S(CompactHeight));
            g.DrawString(_statusText, _statusFont, statusBrush, statusRect, _statusFormat);
        }

        // 预览 / 提示正文
        if (_previewLines.Length > 0)
        {
            using var previewBrush = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            var lineHeight = PreviewLineHeight();
            var y = (float)(S(CompactHeight) + S(PreviewPadTop));
            var format = _previewFormat;
            foreach (var line in _previewLines)
            {
                g.DrawString(line, _previewFont, previewBrush, inset, y, format);
                y += lineHeight;
            }
        }
    }

    private double _frozenElapsedSeconds;

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
        var color = _phase == Phase.Recording ? Color.FromArgb(230, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255);
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
            _transientHideTimer?.Dispose();
            _warningRestoreTimer?.Dispose();
            _collapseTimer?.Dispose();
            _statusFont.Dispose();
            _timerFont.Dispose();
            _previewFont.Dispose();
            _previewFormat.Dispose();
            _statusFormat.Dispose();
            _scene?.Dispose();
            _frame?.Dispose();
            if (_memoryDc != IntPtr.Zero)
            {
                DeleteDC(_memoryDc);
                _memoryDc = IntPtr.Zero;
            }
        }
        base.Dispose(disposing);
    }
}
