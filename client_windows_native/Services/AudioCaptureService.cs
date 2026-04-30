using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceTyper.Support;

namespace VoiceTyper.Services;

/// <summary>
/// 录音服务。使用 NAudio WASAPI 以设备原生格式捕获音频，
/// 停止录音时一次性转换为 16kHz float32 mono（与服务端要求一致）。
/// 转换策略与 macOS Swift 版的 AVAudioConverter 后处理模式对应。
/// </summary>
internal sealed class AudioCaptureService : IDisposable
{
    private WasapiCapture? _capture;
    private MemoryStream? _rawBuffer;
    private readonly object _lock = new();
    private bool _isRecording;

    /// <summary>检测可用的音频捕获设备数量（供 SetupForm 使用）</summary>
    public static int GetCaptureDeviceCount()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>开始录音</summary>
    public void Start()
    {
        if (_isRecording) return;

        _rawBuffer = new MemoryStream();

        try
        {
            _capture = new WasapiCapture();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "没有可用的音频输入设备，请检查麦克风连接。", ex);
        }

        if (_capture.WaveFormat.SampleRate == 0)
        {
            _capture.Dispose();
            _capture = null;
            throw new InvalidOperationException("音频设备采样率无效，请检查麦克风设置。");
        }

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        _isRecording = true;

        AppLog.Info($"开始录音，设备格式: {_capture.WaveFormat}");
    }

    /// <summary>停止录音并返回转换后的音频数据</summary>
    public (byte[] AudioData, TimeSpan Duration) Stop()
    {
        if (!_isRecording || _capture == null)
            return (Array.Empty<byte>(), TimeSpan.Zero);

        _isRecording = false;
        _capture.StopRecording();

        byte[] rawData;
        WaveFormat sourceFormat = _capture.WaveFormat;

        lock (_lock)
        {
            rawData = _rawBuffer?.ToArray() ?? Array.Empty<byte>();
            _rawBuffer?.Dispose();
            _rawBuffer = null;
        }

        Cleanup();

        if (rawData.Length == 0)
            return (Array.Empty<byte>(), TimeSpan.Zero);

        // 后处理：转换为 16kHz float32 mono
        var converted = ConvertToTarget(rawData, sourceFormat);
        var sampleCount = converted.Length / sizeof(float);
        var duration = TimeSpan.FromSeconds(sampleCount / (double)Constants.TargetSampleRate);

        return (converted, duration);
    }

    /// <summary>停止录音，丢弃所有数据</summary>
    public void StopWithoutResult()
    {
        if (!_isRecording) return;

        _isRecording = false;
        _capture?.StopRecording();

        lock (_lock)
        {
            _rawBuffer?.Dispose();
            _rawBuffer = null;
        }

        Cleanup();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isRecording || e.BytesRecorded == 0) return;

        lock (_lock)
        {
            _rawBuffer?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            AppLog.Error("录音停止异常", e.Exception);
    }

    /// <summary>
    /// 将原始音频转换为 16kHz float32 mono。
    /// 转换链: 原始 PCM → ISampleProvider → ToMono → WdlResampling → byte[]
    /// </summary>
    private byte[] ConvertToTarget(byte[] rawData, WaveFormat sourceFormat)
    {
        try
        {
            using var rawStream = new RawSourceWaveStream(
                rawData, 0, rawData.Length, sourceFormat);

            ISampleProvider provider = rawStream.ToSampleProvider();

            // 立体声 → 单声道
            if (provider.WaveFormat.Channels > 1)
                provider = provider.ToMono();

            // 重采样 → 16kHz（使用纯托管 WDL 重采样，Trim 友好）
            if (provider.WaveFormat.SampleRate != Constants.TargetSampleRate)
                provider = new WdlResamplingSampleProvider(provider, Constants.TargetSampleRate);

            // 读出所有 float32 样本
            var samples = new List<float>();
            var buffer = new float[4096];
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                    samples.Add(buffer[i]);
            }

            // float[] → byte[]
            var floatArray = samples.ToArray();
            var result = new byte[floatArray.Length * sizeof(float)];
            Buffer.BlockCopy(floatArray, 0, result, 0, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Error("音频格式转换失败", ex);
            return Array.Empty<byte>();
        }
    }

    private void Cleanup()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
    }

    public void Dispose()
    {
        StopWithoutResult();
    }
}
