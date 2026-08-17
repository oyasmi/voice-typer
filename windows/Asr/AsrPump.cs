using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Support;

namespace VoiceTyper.Asr;

/// <summary>
/// 专用后台串行执行线程，是 macOS 侧 <c>DispatchQueue(label: "...asr", qos: .userInitiated)</c>
/// 的 Windows 对应物。ONNX session 与 fbank 前端都有可变状态，必须串行访问——与服务端
/// "单 worker executor" 的约束一致。
///
/// 用专用 <see cref="Thread"/> 而不是线程池调度：推理是几百毫秒级的阻塞工作，占用线程池
/// 线程会干扰 <c>HttpClient</c>/定时器等其它任务；专用线程也便于在任务管理器/日志里按线程名定位。
/// </summary>
internal sealed class AsrPump : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public AsrPump(string threadName = "VoiceTyper.AsrPump")
    {
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = threadName,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>非阻塞投递，不等待结果。</summary>
    public void Post(Action action) => _queue.Add(action);

    /// <summary>投递并返回一个在动作执行完成后完成的 Task。</summary>
    public Task PostAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>投递并返回携带结果的 Task。</summary>
    public Task<T> PostAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private void RunLoop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Error("asr", "AsrPump 任务异常", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}
