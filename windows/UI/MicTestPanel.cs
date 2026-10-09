using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

/// <summary>实时电平条：条长是电平的分贝映射（<see cref="HudTextLayout.LevelToBarHeight"/>，与浮窗波形同一标尺），
/// 上升立即、回落平滑，每次收到电平时重绘，没有自己的定时器。</summary>
internal sealed class LevelMeter : Control
{
    private float _value;

    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(200, 14);
        TabStop = false;
    }

    public void SetLevel(float rms)
    {
        _value = Math.Max(HudTextLayout.LevelToBarHeight(rms), _value * 0.82f);
        Invalidate();
    }

    public void Clear()
    {
        _value = 0;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var trough = RoundedPath(area))
        using (var troughBrush = new SolidBrush(Color.FromArgb(226, 231, 238)))
        {
            g.FillPath(troughBrush, trough);
        }
        if (_value <= 0.01f) return;
        var fill = new RectangleF(area.X, area.Y, Math.Max(area.Height, area.Width * _value), area.Height);
        using var fillPath = RoundedPath(fill);
        // 接近满格时转橙色：提示音量过大、可能削波。
        using var fillBrush = new SolidBrush(_value > 0.9f ? Color.FromArgb(240, 150, 40) : Color.FromArgb(60, 190, 90));
        g.FillPath(fillBrush, fillPath);
    }

    private static GraphicsPath RoundedPath(RectangleF rect)
    {
        var path = new GraphicsPath();
        var d = Math.Min(rect.Height, rect.Width);
        path.AddArc(rect.X, rect.Y, d, d, 90, 180);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// 「测试麦克风」控件：按钮 + 电平条 + 一行状态。设置页与首启引导共用。
/// 控件自己只管显示与交互；真正打开麦克风由宿主经 <see cref="StartRequested"/> / <see cref="StopRequested"/>
/// 交给协调器，电平与结束原因再经 <see cref="ReportLevel"/> / <see cref="ReportStopped"/> 回来。
/// 测试最多持续 <see cref="AutoStopSeconds"/> 秒，开了没关不会让麦克风指示灯一直亮着。
/// 所有方法必须在 UI 线程调用。
/// </summary>
internal sealed class MicTestPanel : FlowLayoutPanel
{
    internal const int AutoStopSeconds = 20;
    /// <summary>开始后这么久还没听到声音，就提示检查静音与设备。</summary>
    internal const int SilenceHintSeconds = 3;

    private readonly Button _button = new();
    private readonly LevelMeter _meter = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private bool _running;
    private bool _heardSound;
    private long _startedAt;

    /// <summary>宿主提供当前选中的输入设备（配置值）。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string>? DeviceValue { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<string>? StartRequested { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action? StopRequested { get; set; }

    /// <summary>宿主按自己的风格设置按钮外观；文字由本控件管理。</summary>
    public Button ToggleButton => _button;
    public bool IsRunning => _running;

    public MicTestPanel()
    {
        // 两行：第一行按钮 + 电平条，第二行状态文字（可换行，不会把面板撑出页面）。
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = false;
        FlowDirection = FlowDirection.TopDown;
        Margin = new Padding(0, 4, 0, 4);

        _button.Text = L10n.T("测试麦克风");
        _button.AutoSize = true;
        _button.Click += (_, _) => Toggle();
        _meter.Margin = new Padding(8, 12, 0, 0);
        var row = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty,
        };
        row.Controls.AddRange(new Control[] { _button, _meter });
        _status.AutoSize = true;
        _status.MaximumSize = new Size(520, 0);
        _status.Margin = new Padding(0, 2, 0, 0);
        _status.ForeColor = Color.FromArgb(99, 110, 128);
        Controls.AddRange(new Control[] { row, _status });

        _timer.Tick += (_, _) => OnTimer();
        Disposed += (_, _) => _timer.Dispose();
    }

    /// <summary>
    /// 手工布局的宿主（首启引导按 DPI 自己算坐标，不依赖窗体的自动缩放）用它按当前缩放设置电平条与状态文字的尺寸。
    /// 依赖自动缩放的宿主（设置页）不需要调用。
    /// </summary>
    public void ApplyScale(int meterWidth, int meterHeight, int statusMaxWidth)
    {
        _meter.Size = new Size(meterWidth, meterHeight);
        _status.MaximumSize = new Size(statusMaxWidth, 0);
    }

    private void Toggle()
    {
        if (_running)
        {
            Stop();
            return;
        }
        _running = true;
        _heardSound = false;
        _startedAt = Stopwatch.GetTimestamp();
        _button.Text = L10n.T("停止测试");
        SetStatus(L10n.T("请对着麦克风说话，条形应随声音跳动。"), Color.FromArgb(99, 110, 128));
        _timer.Start();
        StartRequested?.Invoke(DeviceValue?.Invoke() ?? AudioConfig.Auto);
    }

    /// <summary>用户（或宿主：切换页面 / 设备 / 隐藏窗口）结束测试，并通知协调器关掉麦克风。没在测试时无效果。</summary>
    public void Stop()
    {
        if (!_running) return;
        Reset(L10n.T("测试已停止。"), Color.FromArgb(99, 110, 128));
        StopRequested?.Invoke();
    }

    public void ReportLevel(float rms)
    {
        if (!_running) return;
        _meter.SetLevel(rms);
        if (!_heardSound && rms >= AppConstants.SilenceRmsThreshold)
        {
            _heardSound = true;
            SetStatus(L10n.T("能听到声音了，这支麦克风可以用。"), Color.SeaGreen);
        }
    }

    /// <summary>协调器通知测试已结束：<paramref name="reason"/> 为 null 表示正常（只是被停掉）。</summary>
    public void ReportStopped(string? reason)
    {
        if (!_running && reason is null) return;
        Reset(reason ?? "", Color.Firebrick);
    }

    private void OnTimer()
    {
        var seconds = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds;
        if (seconds >= AutoStopSeconds)
        {
            Stop();
            SetStatus(L10n.T("测试已自动停止。"), Color.FromArgb(99, 110, 128));
        }
        else if (!_heardSound && seconds >= SilenceHintSeconds)
        {
            SetStatus(L10n.T("没有检测到声音。请确认麦克风未静音、选中的设备正确，再对着它说话。"), Color.DarkGoldenrod);
        }
    }

    private void Reset(string status, Color color)
    {
        _running = false;
        _timer.Stop();
        _meter.Clear();
        _button.Text = L10n.T("测试麦克风");
        SetStatus(status, color);
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }
}
