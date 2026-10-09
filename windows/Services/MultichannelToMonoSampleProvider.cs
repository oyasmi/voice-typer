using System;
using NAudio.Wave;

namespace VoiceTyper.Services;

/// <summary>
/// 任意声道数（≥3）→ 单声道的平均下混。NAudio 只内置立体声下混，而 Windows 11 麦克风阵列
/// （Intel Smart Sound 等）在共享模式下的混音格式可能是 4 声道：此前这类端点在
/// <see cref="AudioCaptureService.BuildResamplingChain"/> 里被直接拒绝，设置页只能显示笼统的
/// 「麦克风设备打开失败」。约定与立体声下混一致：按帧取各声道平均。
/// 只在重采样器的唯一拉取线程上被调用，内部帧缓冲只增不减。
/// </summary>
internal sealed class MultichannelToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _frameBuffer = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public MultichannelToMonoSampleProvider(ISampleProvider source)
    {
        _channels = source.WaveFormat.Channels;
        if (_channels < 3)
            throw new ArgumentOutOfRangeException(nameof(source), $"声道数 {_channels} 小于 3，应走单声道 / 立体声的既有路径");
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int sourceSamples = count * _channels;
        if (_frameBuffer.Length < sourceSamples) Array.Resize(ref _frameBuffer, sourceSamples);
        int read = _source.Read(_frameBuffer, 0, sourceSamples);
        int frames = read / _channels; // 上游按整帧供给；万一出现不足整帧的尾巴，丢弃而不是错位折叠
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            int b = f * _channels;
            for (int c = 0; c < _channels; c++) sum += _frameBuffer[b + c];
            buffer[offset + f] = (float)(sum / _channels);
        }
        return frames;
    }
}
