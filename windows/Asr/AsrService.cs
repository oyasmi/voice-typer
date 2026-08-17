using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.Asr;

internal enum AsrState { Unloaded, Loading, Ready, ModelMissing, Failed }

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
    private SenseVoiceEngine? _engine;
    private AsrConfig _config = new();
    private System.Windows.Forms.Timer? _idleTimer;
    private int _loadGeneration;
    private int _resolvedPreviewWindowSamples = 15 * AppConstants.TargetSampleRate;
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
            _pump.Post(() => _engine?.SetLanguage(lang));
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
        return LoadAsync();
    }

    public async Task ReloadAsync()
    {
        await UnloadNowAsync().ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 创建一次录音会话。若引擎当前未加载（首次使用、或刚从空闲卸载中恢复），会异步触发
    /// 重新加载并与录音并行——用户通常正在说第一句话，加载耗时对感知延迟几乎不可见。
    /// </summary>
    public LocalAsrSession MakeSession(LlmCorrector? llmCorrector)
    {
        if (State is not (AsrState.Ready or AsrState.Loading))
        {
            _ = PreloadAsync();
        }
        _idleTimer?.Stop();
        _idleTimer?.Dispose();
        _idleTimer = null; // 录音期间不应触发空闲卸载；结束后由 SessionEnded 重新安排
        return new LocalAsrSession(_pump, () => _engine, llmCorrector, _resolvedPreviewWindowSamples);
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

            await _pump.PostAsync(() => { _engine = built; }).ConfigureAwait(true);
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

    private async Task UnloadNowAsync()
    {
        _idleTimer?.Stop();
        _idleTimer?.Dispose();
        _idleTimer = null;
        if (_engine is null) return;

        await _pump.PostAsync(() =>
        {
            _engine?.Dispose();
            _engine = null;
        }).ConfigureAwait(true);

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
            SetState(AsrState.Unloaded);
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
                await UnloadNowAsync().ConfigureAwait(true);
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
        _pump.Post(() => _engine?.Dispose());
        _pump.Dispose();
    }
}
