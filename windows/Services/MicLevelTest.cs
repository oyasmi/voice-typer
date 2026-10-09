using System;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.Services;

/// <summary>
/// 「测试麦克风」：按所选输入设备打开采集，只把实时电平交给界面，不缓存、不送识别、不落盘任何音频。
/// 只在用户点了「测试」之后才打开麦克风（任务栏的麦克风指示灯会亮），停止即关闭。
///
/// 用独立的采集服务实例，与听写的采集互不干扰；听写开始前由协调器负责停掉本测试。
/// 每次 <see cref="Start"/> 递增代号，迟到的电平 / 启动结果 / 设备变化回调按代号作废。只在 UI 线程使用。
/// </summary>
internal sealed class MicLevelTest : IDisposable
{
    private readonly Func<IAudioCapturing> _createCapture;
    private readonly Action<Action> _post;
    private IAudioCapturing? _capture;
    private int _generation;

    public bool IsRunning { get; private set; }

    /// <param name="createCapture">创建采集服务；测试传假实现。</param>
    /// <param name="post">把音频线程上的回调送回 UI 线程；测试传同步实现。</param>
    public MicLevelTest(Func<IAudioCapturing>? createCapture = null, Action<Action>? post = null)
    {
        _createCapture = createCapture ?? (() => new AudioCaptureService());
        _post = post ?? UiDispatcher.PostAsync;
    }

    /// <param name="onLevel">线性 RMS 电平，约每 30ms 一次，在 UI 线程。</param>
    /// <param name="onStopped">测试因启动失败 / 设备变化而自行结束时触发一次：参数是给用户看的原因。
    /// 调用方自己调用 <see cref="Stop"/> 时不触发。</param>
    public void Start(AudioInputPolicy policy, Action<float> onLevel, Action<string> onStopped)
    {
        Stop();
        var generation = ++_generation;
        IsRunning = true;

        var capture = _capture ??= _createCapture();
        capture.OnLevel = level => _post(() =>
        {
            if (generation == _generation) onLevel(level);
        });
        capture.OnDeviceChanged = () => _post(() =>
        {
            // 已经结束（用户停止、启动失败、已经通知过一次）就不再重复通知。
            if (generation != _generation || !IsRunning) return;
            EndWithoutNotify();
            onStopped(L10n.T("输入设备已变化，测试已停止。"));
        });
        capture.BeginStart(policy, result => _post(() =>
        {
            if (generation != _generation) return;
            switch (result)
            {
                case AudioStartResult.Failed failed:
                    AppLog.Warn("audio", $"麦克风测试启动失败（{failed.Error.Kind}）");
                    EndWithoutNotify();
                    onStopped(failed.Error.Message);
                    break;
                case AudioStartResult.Cancelled:
                    EndWithoutNotify();
                    break;
            }
        }));
    }

    public void Stop()
    {
        _generation++;
        EndWithoutNotify();
    }

    private void EndWithoutNotify()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _capture?.StopWithoutResult();
    }

    public void Dispose()
    {
        Stop();
        _capture?.Dispose();
        _capture = null;
    }
}
