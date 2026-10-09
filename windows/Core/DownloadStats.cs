using System;
using System.Collections.Generic;
using System.Diagnostics;
using VoiceTyper.Support;

namespace VoiceTyper.Core;

/// <summary>
/// 下载速度估计：取最近几秒内进度的变化量除以经过的时间。窗口很短，网速变化能很快反映出来，
/// 又足够长，不会被单次进度回调的抖动带偏。时间戳是 <see cref="Stopwatch.GetTimestamp"/>，由调用方传入，便于单测。
/// 只在 UI 线程使用。
/// </summary>
internal sealed class DownloadSpeedTracker
{
    private const double WindowSeconds = 8;
    private const double MinimumSpanSeconds = 1;

    private readonly Queue<(long Timestamp, double Fraction)> _samples = new();
    private (long Timestamp, double Fraction) _latest;

    public void Reset()
    {
        _samples.Clear();
        _latest = default;
    }

    /// <param name="fraction">总体进度 0…1。</param>
    public void Add(long timestamp, double fraction)
    {
        _latest = (timestamp, fraction);
        _samples.Enqueue(_latest);
        while (_samples.Count > 1 && Stopwatch.GetElapsedTime(_samples.Peek().Timestamp, timestamp).TotalSeconds > WindowSeconds)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>字节每秒；数据不足 1 秒或窗口内没有进展时为 null（此时不显示速度，免得显示 0 或乱跳的数字）。</summary>
    public double? BytesPerSecond(long totalBytes)
    {
        if (_samples.Count < 2) return null;
        var first = _samples.Peek();
        var seconds = Stopwatch.GetElapsedTime(first.Timestamp, _latest.Timestamp).TotalSeconds;
        if (seconds < MinimumSpanSeconds) return null;
        var bytes = (_latest.Fraction - first.Fraction) * totalBytes;
        return bytes > 0 ? bytes / seconds : null;
    }
}

/// <summary>模型下载进度的文字：已下载 / 总量 / 百分比，速度已知时再加速度与剩余时间。</summary>
internal static class DownloadStatusText
{
    public static string Format(double fraction, long totalBytes, double? bytesPerSecond)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var text = L10n.F("已下载 {0:F1} / {1:F1} MB · {2}%", fraction * totalBytes / 1_000_000d,
            totalBytes / 1_000_000d, (int)(fraction * 100));
        if (bytesPerSecond is > 0 and var speed)
        {
            var remainingSeconds = (1 - fraction) * totalBytes / speed;
            text += " · " + L10n.F("{0:F1} MB/s，约剩 {1}", speed / 1_000_000d, FormatRemaining(remainingSeconds));
        }
        return text;
    }

    internal static string FormatRemaining(double seconds)
    {
        var total = (int)Math.Ceiling(Math.Max(0, seconds));
        if (total >= 3600) return L10n.T("超过 1 小时");
        if (total < 60) return L10n.F("{0} 秒", total);
        return L10n.F("{0} 分 {1} 秒", total / 60, total % 60);
    }
}
