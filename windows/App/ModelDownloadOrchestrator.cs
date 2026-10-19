using System;
using System.Diagnostics;
using System.Threading.Tasks;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.App;

/// <summary>
/// 模型下载的编排：发起 / 取消、进度与测速、失败后的退避自动重试、给界面看的失败说明。
/// 下载本身（分段、续传、校验）在 <see cref="ModelDownloader"/>；这里只管"什么时候下、下到哪一步、失败了怎么办"。
/// 下载态不是 <see cref="AsrState"/> 的成员——引擎状态与下载是两件事，与 macOS 一致。
///
/// 所有公共成员与回调都在 UI 线程；同一时刻至多一次下载，回调按下载器身份过滤，过期下载器的迟到结果被丢弃。
/// </summary>
internal sealed class ModelDownloadOrchestrator : IDisposable
{
    /// <summary>
    /// 失败后的自动重试退避序列（秒）。网络抖动时用户什么都不用做；重试只是续上而不是从头再来
    /// （<see cref="ModelDownloader"/> 保留 .part 数据）。次数用完就停下，把手动重试交还给用户——
    /// 真的没网时无限重试会变成一个用户看不见的死循环。
    /// </summary>
    internal static readonly int[] RetryDelaysSeconds = { 5, 20, 60 };

    /// <summary>状态变化（开始、进度）。协调器据此刷新托盘、设置页与热键门禁。</summary>
    public Action? Changed;
    /// <summary>全部文件下载并校验完成。</summary>
    public Action? Completed;
    /// <summary>用户主动取消（.part 数据已保留）。</summary>
    public Action? Cancelled;
    /// <summary>下载失败。参数：失败原因摘要；是否已放弃自动重试（此后不会再有任何主动信号）。
    /// 带重试进度的完整说明见 <see cref="Error"/>。</summary>
    public Action<string, bool>? Failed;

    private readonly Func<bool> _shouldRetry;
    private readonly Func<IModelDownloading> _create;
    private readonly Action<Action> _post;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly DownloadSpeedTracker _speed = new();
    private IModelDownloading? _downloader;
    private IDisposable? _retryTimer;
    private int _retryAttempt;

    public bool IsDownloading => _downloader is not null;
    /// <summary>当前下载的总体进度（0…1）；不在下载时无意义。</summary>
    public double Progress { get; private set; }
    /// <summary>最近一次失败的说明（含自动重试进度）；成功或重新开始时清空。</summary>
    public string? Error { get; private set; }
    /// <summary>最近一次失败的诊断详情（脱敏后的异常链），供设置页「查看下载详情」。</summary>
    public string? Details { get; private set; }

    /// <summary>下载进度的文字（已下载 / 速度 / 剩余时间）；不在下载时为 null。</summary>
    public string? StatusLine => IsDownloading
        ? DownloadStatusText.Format(Progress, ModelDownloader.TotalBytes, _speed.BytesPerSecond(ModelDownloader.TotalBytes))
        : null;

    /// <summary>当前这次下载的后台任务；只供测试等待。</summary>
    internal Task? Running { get; private set; }

    /// <param name="shouldRetry">退避到点时是否仍需要下载（期间用户可能已手动放入模型）。</param>
    /// <param name="create">创建下载器；测试传假实现。</param>
    /// <param name="post">把后台线程上的结果送回 UI 线程；测试传同步实现。</param>
    /// <param name="schedule">一次性延迟回调，回调须在 UI 线程触发；返回的句柄 Dispose 即取消。</param>
    public ModelDownloadOrchestrator(Func<bool> shouldRetry, Func<IModelDownloading>? create = null,
        Action<Action>? post = null, Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _shouldRetry = shouldRetry;
        _create = create ?? (() => new ModelDownloader());
        _post = post ?? UiDispatcher.Post;
        _schedule = schedule ?? UiTimer.Once;
    }

    /// <summary>用户（或首次缺模型时自动）发起下载。算作一次新的尝试序列：退避计数清零。</summary>
    public void Start() => Start(isAutomaticRetry: false);

    /// <summary>用户主动取消：同时取消尚未触发的自动重试——他刚刚表达了"先不下"。</summary>
    public void Cancel()
    {
        CancelRetry();
        _downloader?.Cancel();
    }

    private void Start(bool isAutomaticRetry)
    {
        if (IsDownloading) return;
        CancelRetry();
        if (!isAutomaticRetry) _retryAttempt = 0;
        Progress = 0;
        _speed.Reset();
        Error = null;
        Details = null;
        var downloader = _create();
        _downloader = downloader;
        Changed?.Invoke();
        Running = RunAsync(downloader);
    }

    private async Task RunAsync(IModelDownloading downloader)
    {
        try
        {
            // 放到线程池：开头要对已存在的文件算 sha256（两百多 MB），不能占用 UI 线程。
            await Task.Run(() => downloader.DownloadAllAsync(progress => _post(() => OnProgress(downloader, progress))))
                .ConfigureAwait(false);
            _post(() =>
            {
                if (!Release(downloader)) return;
                _retryAttempt = 0;
                Completed?.Invoke();
            });
        }
        catch (OperationCanceledException)
        {
            _post(() =>
            {
                if (Release(downloader)) Cancelled?.Invoke();
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("model", "模型下载失败: " + DownloadFailure.Diagnostics(ex));
            _post(() =>
            {
                if (Release(downloader)) HandleFailure(ex);
            });
        }
    }

    private void OnProgress(IModelDownloading downloader, double progress)
    {
        if (!ReferenceEquals(_downloader, downloader)) return;
        Progress = progress;
        _speed.Add(Stopwatch.GetTimestamp(), progress);
        Changed?.Invoke();
    }

    /// <summary>结束当前下载；不是当前下载器（已结束或已被释放）时返回 false。</summary>
    private bool Release(IModelDownloading downloader)
    {
        if (!ReferenceEquals(_downloader, downloader)) return false;
        _downloader = null;
        downloader.Dispose();
        return true;
    }

    private void HandleFailure(Exception error)
    {
        var reason = DownloadFailure.Summary(error);
        Details = DownloadFailure.Diagnostics(error);
        var canRetry = DownloadFailure.CanRetry(error);
        if (canRetry && _retryAttempt < RetryDelaysSeconds.Length)
        {
            var delay = RetryDelaysSeconds[_retryAttempt];
            _retryAttempt++;
            Error = L10n.F("{0} 将在 {1} 秒后自动重试（第 {2}/{3} 次）。",
                reason, delay, _retryAttempt, RetryDelaysSeconds.Length);
            ScheduleRetry(TimeSpan.FromSeconds(delay));
            Failed?.Invoke(reason, false);
            return;
        }
        Error = canRetry
            ? L10n.F("{0} 已自动重试 {1} 次仍未成功，请检查网络后手动重试。", reason, RetryDelaysSeconds.Length)
            : reason + "\n" + L10n.T("请处理上述问题后手动重试。");
        Failed?.Invoke(reason, true);
    }

    private void ScheduleRetry(TimeSpan delay)
    {
        CancelRetry();
        _retryTimer = _schedule(delay, () =>
        {
            _retryTimer = null;
            // 期间用户可能已经手动下载完、放入了模型，或者又手动开始了一次。
            if (IsDownloading || !_shouldRetry()) return;
            AppLog.Info("model", $"模型下载自动重试（第 {_retryAttempt} 次）");
            Start(isAutomaticRetry: true);
        });
    }

    private void CancelRetry()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
    }

    public void Dispose()
    {
        CancelRetry();
        var downloader = _downloader;
        _downloader = null;
        downloader?.Dispose();
    }
}
