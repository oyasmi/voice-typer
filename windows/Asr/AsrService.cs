using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.Asr;

internal enum AsrState
{
    Unloaded,
    Loading,
    Ready,
    /// <summary>
    /// 空闲超时后的有意卸载：与 <see cref="Unloaded"/>（尚未加载/加载失败前）区分，避免状态
    /// 变化被无条件转发时又反向触发自动预加载（F-04）。语义上仍视为"就绪"——热键监听正常，
    /// 下次 <see cref="AsrService.MakeSession"/> 时按需加载。
    /// </summary>
    SuspendedForIdle,
    ModelMissing,
    Failed,
}

/// <summary>
/// 识别引擎的生命周期门面：定位模型 → 后台加载 → 提供录音会话 → 空闲卸载。
/// C# 直译自 <c>macos/Sources/VoiceTyper/ASR/ASRService.swift</c>。所有公共方法必须在 UI 线程调用；
/// 耗时操作（模型加载、推理）都经由 <see cref="AsrPump"/> 串行执行。
/// </summary>
internal sealed class AsrService : IDisposable
{
    public AsrState State { get; private set; } = AsrState.Unloaded;
    public string? FailureMessage { get; private set; }
    public Action<AsrState>? OnStateChange;

    private readonly AsrPump _pump = new();
    /// <summary>
    /// 引擎引用本身用 <see cref="_engineLock"/> 保护，而不是靠"只在 AsrPump 上访问"的约定——
    /// <see cref="CurrentEngine"/> 会被 <see cref="LocalAsrSession"/> 从 UI 线程直接调用
    /// （不经过 AsrPump），此前那条约定实际已被违反，属于无同步的跨线程读写（N-01）。
    /// </summary>
    private readonly object _engineLock = new();
    private SenseVoiceEngine? _engine;
    private AsrConfig _config = new();
    private System.Windows.Forms.Timer? _idleTimer;
    private int _loadGeneration;
    private int _resolvedPreviewWindowSamples = 15 * AppConstants.TargetSampleRate;
    /// <summary><see cref="PreloadAsync"/>/<see cref="ReloadAsync"/> 的互斥标志：两者都可能各自
    /// 触发一次加载，若不 guard，空闲卸载把状态置 Unloaded 触发的 OnStateChange 会被
    /// AppCoordinator 无差别转发进 ReevaluateReadinessAsync，其 Unloaded 分支又发起一次独立的
    /// 预加载——两次加载会各自构建一份约 500MB 的 ORT session，在谁都还没替换掉 <see cref="_engine"/>
    /// 之前短暂同时存活，峰值内存可翻倍（R3-02）。</summary>
    private bool _isLoadInFlight;
    /// <summary>加载进行中又收到一次加载请求：不重入执行，只记一次"完成后再跑一遍"，
    /// 确保最新配置最终生效，而不是静默丢弃。</summary>
    private bool _reloadRequestedWhileLoading;
    private bool _disposed;

    /// <summary>配置变化时调用：语言变化直接热更新引擎；模型目录/线程数变化触发重新加载。</summary>
    public void UpdateConfig(AsrConfig newConfig)
    {
        bool languageChanged = _config.LanguageValue != newConfig.LanguageValue;
        bool reloadNeeded = _config.ModelDir != newConfig.ModelDir || _config.Threads != newConfig.Threads;
        _config = newConfig;

        if (languageChanged)
        {
            var lang = newConfig.LanguageValue;
            _pump.Post(() => CurrentEngine()?.SetLanguage(lang));
        }
        if (reloadNeeded)
        {
            _ = ReloadAsync();
        }
        else
        {
            ScheduleIdleUnloadIfNeeded();
        }
    }

    public Task PreloadAsync()
    {
        if (State is AsrState.Loading or AsrState.Ready) return Task.CompletedTask;
        return RequestLoadAsync(unloadFirst: false);
    }

    public Task ReloadAsync() => RequestLoadAsync(unloadFirst: true);

    /// <summary>
    /// <see cref="PreloadAsync"/>/<see cref="ReloadAsync"/> 的统一入口：若已有加载在跑，只记一个
    /// "完成后再跑一次"的标记，绝不发起第二次加载（R3-02）。
    /// </summary>
    private async Task RequestLoadAsync(bool unloadFirst)
    {
        if (_isLoadInFlight)
        {
            _reloadRequestedWhileLoading = true;
            return;
        }
        _isLoadInFlight = true;
        if (unloadFirst)
        {
            await UnloadNowAsync(dueToIdle: false).ConfigureAwait(true);
        }
        await LoadAsync().ConfigureAwait(true);
        _isLoadInFlight = false;

        if (_reloadRequestedWhileLoading)
        {
            _reloadRequestedWhileLoading = false;
            // 合并的请求按"重新加载"处理：确保加载期间发生的最新配置变化最终会生效，
            // 而不是被静默吞掉。
            await RequestLoadAsync(unloadFirst: true).ConfigureAwait(true);
        }
    }

    /// <summary>引擎当前实例。可从任意线程调用（<see cref="LocalAsrSession"/> 会在 UI 线程上直接
    /// 读取，而写入发生在 <see cref="AsrPump"/>）——引用本身由 <see cref="_engineLock"/> 保护，
    /// 可安全跨线程访问。</summary>
    public SenseVoiceEngine? CurrentEngine()
    {
        lock (_engineLock) return _engine;
    }

    /// <summary>写入引擎引用；只应在 AsrPump 上调用，与推理串行化的约定保持一致。</summary>
    private void SetEngine(SenseVoiceEngine? newEngine)
    {
        lock (_engineLock) _engine = newEngine;
    }

    /// <summary>原子地取走并清空当前引擎引用，供调用方在 AsrPump 上 Dispose。</summary>
    private SenseVoiceEngine? TakeAndClearEngine()
    {
        lock (_engineLock)
        {
            var old = _engine;
            _engine = null;
            return old;
        }
    }

    /// <summary>
    /// 创建一次录音会话。若引擎当前未加载（首次使用、或刚从空闲卸载中恢复），会异步触发
    /// 重新加载并与录音并行——用户通常正在说第一句话，加载耗时对感知延迟几乎不可见。
    /// </summary>
    public LocalAsrSession MakeSession(LlmCorrector? llmCorrector)
    {
        if (State is not (AsrState.Ready or AsrState.Loading or AsrState.SuspendedForIdle))
        {
            _ = PreloadAsync();
        }
        _idleTimer?.Stop();
        _idleTimer?.Dispose();
        _idleTimer = null; // 录音期间不应触发空闲卸载；结束后由 SessionEnded 重新安排
        return new LocalAsrSession(_pump, CurrentEngine, llmCorrector, _resolvedPreviewWindowSamples);
    }

    /// <summary>录音会话结束后由调用方（VoiceTyperController）调用，重新安排空闲卸载计时。</summary>
    public void SessionEnded() => ScheduleIdleUnloadIfNeeded();

    private async Task LoadAsync()
    {
        _loadGeneration++;
        var generation = _loadGeneration;

        var bundle = ModelLocator.Locate(_config.ModelDir);
        if (bundle is null)
        {
            SetState(AsrState.ModelMissing);
            return;
        }
        SetState(AsrState.Loading);

        var language = _config.LanguageValue;
        var threads = _config.Threads;

        try
        {
            var built = await _pump.PostAsync(() => new SenseVoiceEngine(bundle, language, threads)).ConfigureAwait(true);

            if (generation != _loadGeneration)
            {
                // 期间又发起了一次 reload，丢弃过期结果。
                _pump.Post(() => built.Dispose());
                return;
            }

            // load() 开始时用来构造 built 的 language 快照可能已经过期：UpdateConfig 在加载期间
            // 收到的语言变化会走 _pump.Post(() => CurrentEngine()?.SetLanguage(...))，这次入队与
            // 下面这次 SetEngine(built) 的相对顺序不保证——若前者先执行，它当时要么作用在旧引擎、
            // 要么作用在 null 上，随后被这里的 SetEngine 覆盖掉，语言设置静默丢失，要等下一次
            // reload 才会生效（R4-13）。用装载完成这一刻的最新 _config.LanguageValue 重放一次，
            // 保证无论期间发生过什么，最终生效的语言一定是最新配置。
            var latestLanguage = _config.LanguageValue;
            await _pump.PostAsync(() =>
            {
                SetEngine(built);
                built.SetLanguage(latestLanguage);
            }).ConfigureAwait(true);

            CalibratePreviewWindowIfNeeded(built);
            SetState(AsrState.Ready);
            ScheduleIdleUnloadIfNeeded();
        }
        catch (Exception ex)
        {
            AppLog.Error("asr", "模型加载失败", ex);
            SetState(AsrState.Failed, ex.Message);
        }
    }

    /// <param name="dueToIdle">true 表示由空闲计时器触发的有意卸载，卸载完成后进入
    /// <see cref="AsrState.SuspendedForIdle"/> 而非 <see cref="AsrState.Unloaded"/>，避免被
    /// 无差别转发的状态变化误判为"尚未加载"从而立即触发自动预加载（F-04）。
    /// <see cref="ReloadAsync"/> 内部调用时传 false。</param>
    private async Task UnloadNowAsync(bool dueToIdle)
    {
        _idleTimer?.Stop();
        _idleTimer?.Dispose();
        _idleTimer = null;

        // 取走引用与 Dispose 都在 AsrPump 上完成：Dispose 必须与"推理仍在跑"这件事互斥，
        // 而正在执行中的 Recognize() 调用同样跑在 AsrPump 上——两者天然靠这条串行队列互斥，
        // 换到别的线程 Dispose 就可能与一次仍在进行的推理调用竞争同一个 ONNX session。
        var hadEngine = await _pump.PostAsync(() =>
        {
            var old = TakeAndClearEngine();
            old?.Dispose();
            return old is not null;
        }).ConfigureAwait(true);
        if (!hadEngine) return;

        // 归还工作集给系统；Windows 特有（macOS 无对应概念，见 windows/DESIGN.md §4.5）。
        try
        {
            NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }
        catch (Exception ex)
        {
            AppLog.Debug("asr", $"SetProcessWorkingSetSize 失败（忽略）: {ex.Message}");
        }

        if (State == AsrState.Ready)
        {
            SetState(dueToIdle ? AsrState.SuspendedForIdle : AsrState.Unloaded);
        }
    }

    /// <summary>
    /// 首次加载后用 5 秒静音张量测一次本机 RTF，据此把预览窗口自动选到 15/10/6 秒之一——
    /// 不改识别算法，只是调一个服务端本来就有的参数（<c>asr.preview_window</c>）。
    /// 用户在设置里显式指定了非 0 值时跳过校准，直接采用配置值。
    /// </summary>
    private void CalibratePreviewWindowIfNeeded(SenseVoiceEngine engine)
    {
        if (_config.PreviewWindowSeconds > 0)
        {
            _resolvedPreviewWindowSamples = _config.PreviewWindowSeconds * AppConstants.TargetSampleRate;
            return;
        }

        _pump.Post(() =>
        {
            try
            {
                var silence = new float[5 * AppConstants.TargetSampleRate];
                var sw = Stopwatch.StartNew();
                engine.Recognize(silence);
                sw.Stop();
                double rtf = sw.Elapsed.TotalSeconds / 5.0;

                int seconds = rtf <= 0.05 ? 15 : rtf <= 0.15 ? 10 : 6;
                _resolvedPreviewWindowSamples = seconds * AppConstants.TargetSampleRate;
                AppLog.Info("asr", $"预览窗口自校准: RTF={rtf:F3} → preview_window={seconds}s");
            }
            catch (Exception ex)
            {
                AppLog.Warn("asr", $"预览窗口自校准失败，使用默认 15s: {ex.Message}");
                _resolvedPreviewWindowSamples = 15 * AppConstants.TargetSampleRate;
            }
        });
    }

    private void ScheduleIdleUnloadIfNeeded()
    {
        _idleTimer?.Stop();
        _idleTimer?.Dispose();
        _idleTimer = null;
        if (_config.IdleUnloadMinutes <= 0) return;

        _idleTimer = new System.Windows.Forms.Timer { Interval = _config.IdleUnloadMinutes * 60 * 1000 };
        _idleTimer.Tick += async (_, _) =>
        {
            _idleTimer?.Stop();
            try
            {
                await UnloadNowAsync(dueToIdle: true).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Error("asr", "空闲卸载失败", ex);
            }
        };
        _idleTimer.Start();
    }

    private void SetState(AsrState state, string? message = null)
    {
        State = state;
        FailureMessage = message;
        OnStateChange?.Invoke(state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer?.Stop();
        _idleTimer?.Dispose();
        _pump.Post(() => TakeAndClearEngine()?.Dispose());
        _pump.Dispose();
    }
}
