using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
/// 回调可能在工作线程触发，调用方负责自行 marshal。
/// </summary>
internal sealed class AudioCaptureService : IDisposable
{
    public Action<byte[]>? OnChunk;
    public Action<byte[]>? OnTailChunk;

    public int ChunkSamples { get; } = AppConstants.ChunkSamples;

    private readonly object _lock = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private BufferedWaveProvider? _inputBuffer;
    private ISampleProvider? _resampledProvider;
    private readonly Queue<float> _ringBuffer = new();
    private WaveFormat? _captureFormat;
    private bool _running;

    public bool IsRunning
    {
        get { lock (_lock) return _running; }
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
                _capture.StartRecording();

                _ringBuffer.Clear();
                _running = true;
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070005u)
            {
                Cleanup();
                throw new AudioStartException("麦克风访问被拒绝，请在 Windows 设置中允许应用访问麦克风", accessDenied: true, ex);
            }
            catch (Exception ex)
            {
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
        lock (_lock)
        {
            if (!_running) return;
            capture = _capture;
            _running = false;
        }

        try { capture?.StopRecording(); }
        catch (Exception ex) { AppLog.Warn("audio", $"StopRecording 异常: {ex.Message}"); }

        // 取走剩余尾音
        byte[] tail;
        lock (_lock)
        {
            tail = DrainRingBufferLocked();
            _ringBuffer.Clear();
        }

        OnTailChunk?.Invoke(tail);
        AppLog.Info("audio", $"录音停止，尾音 {tail.Length} bytes");
    }

    /// <summary>不发出尾音直接终止（如错误清理）。</summary>
    public void StopWithoutResult()
    {
        WasapiCapture? capture;
        lock (_lock)
        {
            if (!_running) return;
            capture = _capture;
            _running = false;
            _ringBuffer.Clear();
        }
        try { capture?.StopRecording(); }
        catch { /* swallow */ }
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        BufferedWaveProvider? input;
        ISampleProvider? resampled;
        lock (_lock)
        {
            if (!_running) return;
            input = _inputBuffer;
            resampled = _resampledProvider;
        }
        if (input is null || resampled is null) return;

        try
        {
            input.AddSamples(e.Buffer, 0, e.BytesRecorded);

            // 重采样输出：把所有可读样本拉出来
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
            AppLog.Error("audio", "WASAPI 停止异常", e.Exception);
        }
    }

    private void AppendSamples(float[] buffer, int count)
    {
        // 把样本塞入 ring buffer，每凑满 ChunkSamples 个就 emit 一帧。
        List<byte[]>? toEmit = null;
        lock (_lock)
        {
            for (int i = 0; i < count; i++) _ringBuffer.Enqueue(buffer[i]);

            while (_ringBuffer.Count >= ChunkSamples)
            {
                var chunk = new byte[ChunkSamples * sizeof(float)];
                var floats = MemoryMarshal.Cast<byte, float>(chunk);
                for (int i = 0; i < ChunkSamples; i++) floats[i] = _ringBuffer.Dequeue();
                (toEmit ??= new List<byte[]>()).Add(chunk);
            }
        }

        if (toEmit is null) return;
        var cb = OnChunk;
        if (cb is null) return;
        foreach (var c in toEmit) cb(c);
    }

    private byte[] DrainRingBufferLocked()
    {
        if (_ringBuffer.Count == 0) return Array.Empty<byte>();
        var buf = new byte[_ringBuffer.Count * sizeof(float)];
        var floats = MemoryMarshal.Cast<byte, float>(buf);
        int i = 0;
        while (_ringBuffer.Count > 0) floats[i++] = _ringBuffer.Dequeue();
        return buf;
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
        StopWithoutResult();
        Cleanup();
    }
}
