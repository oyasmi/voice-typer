using System;
using System.Threading;

namespace VoiceTyper.Support;

/// <summary>
/// 把任意线程上的回调投递回 UI 线程（即 Application.Run 所在的消息泵线程）。
/// 在 Program.Main 早期通过 <see cref="Install"/> 捕获 <see cref="SynchronizationContext"/> 之后才能使用。
/// </summary>
internal static class UiDispatcher
{
    private static SynchronizationContext? _context;
    private static int _uiThreadId;

    public static void Install()
    {
        _context = SynchronizationContext.Current
            ?? throw new InvalidOperationException("UiDispatcher.Install 必须在 UI 线程，且需要在 Application 启动 WinForms 同步上下文之后调用");
        _uiThreadId = Environment.CurrentManagedThreadId;
    }

    public static bool IsOnUiThread => Environment.CurrentManagedThreadId == _uiThreadId;

    /// <summary>
    /// 非阻塞投递到 UI 线程。若当前已经在 UI 线程，直接同步执行以避免不必要的延迟。
    /// </summary>
    public static void Post(Action action)
    {
        if (_context is null) throw new InvalidOperationException("UiDispatcher 未初始化");
        if (IsOnUiThread)
        {
            try { action(); }
            catch (Exception ex)
            {
                AppLog.Error("dispatcher", "同步执行 UI 动作抛异常", ex);
            }
            return;
        }
        _context.Post(static state =>
        {
            try { ((Action)state!)(); }
            catch (Exception ex)
            {
                AppLog.Error("dispatcher", "UI 投递动作抛异常", ex);
            }
        }, action);
    }
}
