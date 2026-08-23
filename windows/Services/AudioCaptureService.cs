using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceTyper.Support;

namespace VoiceTyper.Services;

internal sealed class AudioStartException : Exception
{
    public bool IsAccessDenied { get; }
    public AudioStartException(string message, bool accessDenied, Exception? inner = null) : base(message, inner)
    {
        IsAccessDenied = accessDenied;
    }
}

/// <summary>
/// 流式录音服务。录音期间每凑满 600ms（9600 个 16kHz float32 样本）通过 <see cref="OnChunk"/> 发出；
/// 停止时将剩余尾音通过 <see cref="OnTailChunk"/> 发出。
///
/// <see cref="_lock"/> 同时保护 <see cref="_running"/> 与 <see cref="_chunker"/>：
/// <see cref="AppendSamples"/>（WASAPI 回调线程）与 <see cref="Stop"/>（UI 线程）若不在同一把锁下
/// 原子地"判断 running + 处理缓冲区"，会出现两类竞态（R3-01）：
/// (a) <see cref="Stop"/> 取走尾音之后，一个仍在途的 <see cref="AppendSamples"/> 才把新样本写进
///     已被清空、此后再也不会被读取的缓冲区——那部分音频（通常是松键前最后几十毫秒）静默丢失；
/// (b) <see cref="OnChunk"/>/<see cref="OnTailChunk"/> 分别从音频线程与 UI 线程独立发起，
///     没有强制的先后关系，可能乱序到达。
/// 让投递动作本身（入队到 <see cref="_deliveryQueue"/>）也发生在同一把锁内，即可让"入队顺序"
/// 严格遵循锁定义的临界区顺序，从根上同时解决两个问题。
/// </summary>
internal sealed class AudioCaptureService : IDisposable
{
    public Action<byte[]>? OnChunk;
    public Action<byte[]>? OnTailChunk;
    /// <summary>
    /// 录音期间输入设备变化（拔麦克风、切换音频设备等）导致本次录音被迫结束时触发一次。
    /// 已采到的音频仍会通过 <see cref="OnTailChunk"/>（本回调触发前已同步投递）正常交给当前会话
    /// 完成识别；这里只做"可见告知"，不做自动重建/自动恢复（F-15 / R2-03）。在 UI 线程触发。
    /// </summary>
    public Action? OnDeviceChanged;

    public int ChunkSamples { get; } = AppConstants.ChunkSamples;

    private readonly object _lock = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private BufferedWaveProvider? _inputBuffer;
    private ISampleProvider? _resampledProvider;
    private WaveFormat? _captureFormat;
    private AudioChunker _chunker;
    private bool _running;

    /// <summary>"stop() 之后的迟到样本"丢帧计数：R3-01 场景 (a) 的正常代价，不代表出错（R4-07）。</summary>
    private int _droppedNotRunningCount;

    /// <summary>
    /// 串行投递队列：<see cref="OnChunk"/>/<see cref="OnTailChunk"/> 统一从这里触发，而不是分别
    /// 直接从音频线程和 UI 线程调用，从而保证两者之间的相对顺序与 <see cref="_lock"/> 临界区顺序一致。
    /// </summary>
    private readonly BlockingCollection<Action> _deliveryQueue = new();
    private readonly Thread _deliveryThread;
    private bool _disposed;

    public bool IsRunning
    {
        get { lock (_lock) return _running; }
    }

    public AudioCaptureService()
    {
        _chunker = new AudioChunker(ChunkSamples);
        _deliveryThread = new Thread(RunDeliveryLoop) { IsBackground = true, Name = "VoiceTyper.AudioDelivery" };
        _deliveryThread.Start();
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;

            try
            {
                var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070005u)
            {
                throw new AudioStartException("麦克风访问被拒绝，请在 Windows 设置中允许应用访问麦克风", accessDenied: true, ex);
            }
            catch (Exception ex)
            {
                throw new AudioStartException("未找到可用麦克风设备", accessDenied: false, ex);
            }

            try
            {
                _capture = new WasapiCapture(_device, useEventSync: true);
                _captureFormat = _capture.WaveFormat;

                _inputBuffer = new BufferedWaveProvider(_captureFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(2),
                    DiscardOnBufferOverflow = true,
                };

                // 重采样到 16kHz / mono / float32（IEEE float）。
                // 选 WDL 而非 MediaFoundationResampler：纯托管、不依赖 MF DLL，且对语音 16kHz 重采样质量足够。
                var sampleProvider = _inputBuffer.ToSampleProvider();
                if (_captureFormat.Channels > 1)
                {
                    sampleProvider = sampleProvider.ToMono();
                }
                _resampledProvider = new WdlResamplingSampleProvider(sampleProvider, AppConstants.TargetSampleRate);

                _capture.DataAvailable += OnCaptureDataAvailable;
                _capture.RecordingStopped += OnCaptureStopped;

                _chunker = new AudioChunker(ChunkSamples);
                _droppedNotRunningCount = 0;
                // running 必须在 StartRecording 之前、且在锁内置位：DataAvailable 理论上可能在
                // StartRecording() 返回后的极窄窗口内几乎立即触发，若仍在锁外才置位，这段窗口里
                // 到达的样本会被误判为"stop() 已经跑过"而丢弃，且这本身就是一处不受锁保护的
                // 跨线程写（对齐 macOS R4-07 的教训）。
                _running = true;
                _capture.StartRecording();
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070005u)
            {
                _running = false;
                Cleanup();
                throw new AudioStartException("麦克风访问被拒绝，请在 Windows 设置中允许应用访问麦克风", accessDenied: true, ex);
            }
            catch (Exception ex)
            {
                _running = false;
                Cleanup();
                throw new AudioStartException($"启动录音失败: {ex.Message}", accessDenied: false, ex);
            }
        }

        AppLog.Info("audio", $"录音启动: device={_device?.FriendlyName}, format={_captureFormat}");
    }

    /// <summary>
    /// 停止录音，发出尾音帧。即使没有尾音也会以空 Data 触发 <see cref="OnTailChunk"/>，
    /// 让调用方知道录音流已经结束。
    /// </summary>
    public void Stop()
    {
        WasapiCapture? capture;
        bool wasRunning;
        int tailLength;
        lock (_lock)
        {
            wasRunning = _running;
            capture = _capture;
            if (_running)
            {
                _running = false;
                tailLength = DrainAndEnqueueTailLocked();
            }
            else
            {
                tailLength = 0;
            }
        }
        if (!wasRunning) return;

        try { capture?.StopRecording(); }
        catch (Exception ex) { AppLog.Warn("audio", $"StopRecording 异常: {ex.Message}"); }

        LogDroppedBuffersIfAny();
        AppLog.Info("audio", $"录音停止，尾音 {tailLength} bytes");
    }

    /// <summary>不发出尾音直接终止（如错误清理、用户主动取消）。</summary>
    public void StopWithoutResult()
    {
        WasapiCapture? capture;
        lock (_lock)
        {
            if (!_running) return;
            capture = _capture;
            _running = false;
            _chunker.Drain();
        }
        try { capture?.StopRecording(); }
        catch { /* swallow */ }
        LogDroppedBuffersIfAny();
    }

    /// <summary>必须在持有 <see cref="_lock"/> 时调用；返回尾音字节数供调用方日志使用。</summary>
    private int DrainAndEnqueueTailLocked()
    {
        var tail = _chunker.Drain();
        var handler = OnTailChunk;
        _deliveryQueue.Add(() => handler?.Invoke(tail));
        return tail.Length;
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        BufferedWaveProvider? input;
        ISampleProvider? resampled;
        lock (_lock)
        {
            input = _inputBuffer;
            resampled = _resampledProvider;
        }
        if (input is null || resampled is null) return;

        try
        {
            input.AddSamples(e.Buffer, 0, e.BytesRecorded);

            // 重采样输出：把所有可读样本拉出来。转换本身与 running 无关，
            // running 判定只在真正写入 chunker 时做（见 AppendSamples）。
            var pool = ArrayPool<float>.Shared;
            var tmp = pool.Rent(4096);
            try
            {
                int read;
                while ((read = resampled.Read(tmp, 0, tmp.Length)) > 0)
                {
                    AppendSamples(tmp, read);
                    if (read < tmp.Length) break;
                }
            }
            finally
            {
                pool.Return(tmp);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("audio", "处理音频数据异常", ex);
        }
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            AppLog.Warn("audio", $"音频设备意外停止（可能是设备被拔出/切换）: {e.Exception.Message}");
            UiDispatcher.Post(HandleDeviceChangedDuringRecording);
        }
    }

    /// <summary>
    /// 保留已采到的音频交给当前会话完成识别（走与正常停止相同的尾音刷出路径），
    /// 但明确告知用户设备已变化、本次录音已结束——不做自动重建/自动恢复（F-15 / R2-03）。
    /// </summary>
    private void HandleDeviceChangedDuringRecording()
    {
        bool wasRunning;
        lock (_lock)
        {
            wasRunning = _running;
            if (_running)
            {
                _running = false;
                DrainAndEnqueueTailLocked();
            }
        }
        if (!wasRunning) return;

        LogDroppedBuffersIfAny();
        OnDeviceChanged?.Invoke();
    }

    private void AppendSamples(float[] buffer, int count)
    {
        lock (_lock)
        {
            if (!_running)
            {
                // stop() 已经把 running 置 false 并取走尾音：这批样本必然是"迟到"的，
                // 若仍写进 chunker 会成为永远不会被 flush 的孤儿数据（R3-01 场景 a）。
                _droppedNotRunningCount++;
                return;
            }

            var chunks = _chunker.Append(buffer.AsSpan(0, count));
            if (chunks.Count == 0) return;

            var handler = OnChunk;
            // 入队动作必须在锁内完成，才能保证与 Stop()/设备变化那侧的入队顺序一致（见类注释）。
            foreach (var chunk in chunks)
            {
                _deliveryQueue.Add(() => handler?.Invoke(chunk));
            }
        }
    }

    /// <summary>只记计数，不含音频内容（AGENTS.md 日志约束）。"迟到"丢帧是 stop() 与音频线程
    /// 竞态下的正常代价，不代表出错，用 info 级别（R4-07）。</summary>
    private void LogDroppedBuffersIfAny()
    {
        int notRunning;
        lock (_lock)
        {
            notRunning = _droppedNotRunningCount;
            _droppedNotRunningCount = 0;
        }
        if (notRunning > 0)
        {
            AppLog.Info("audio", $"录音期间丢弃了 {notRunning} 个音频缓冲区（stop() 之后的迟到样本，属预期行为）");
        }
    }

    private void RunDeliveryLoop()
    {
        foreach (var action in _deliveryQueue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Error("audio", "音频投递任务异常", ex);
            }
        }
    }

    private void Cleanup()
    {
        try { _capture?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _capture = null;
        _resampledProvider = null;
        _device = null;
        _inputBuffer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWithoutResult();
        Cleanup();
        _deliveryQueue.CompleteAdding();
        _deliveryThread.Join(TimeSpan.FromSeconds(2));
        _deliveryQueue.Dispose();
    }
}
