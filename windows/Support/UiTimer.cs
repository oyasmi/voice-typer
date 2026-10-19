using System;

namespace VoiceTyper.Support;

/// <summary>UI 线程上的一次性延迟回调（<see cref="System.Windows.Forms.Timer"/>）。</summary>
internal static class UiTimer
{
    /// <summary>到点在 UI 线程触发一次后自行释放；返回的句柄 Dispose 即取消。必须在 UI 线程调用。</summary>
    public static IDisposable Once(TimeSpan delay, Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = (int)Math.Clamp(delay.TotalMilliseconds, 1, int.MaxValue) };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            action();
        };
        timer.Start();
        return timer;
    }
}
