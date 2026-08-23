using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VoiceTyper.Services;

/// <summary>
/// 纯逻辑的环形缓冲分帧器：累积样本 → 吐出定长 chunk，<see cref="Drain"/> 吐出剩余尾音。
/// 与 WASAPI/线程无关，可脱离真实麦克风单测。C# 直译自
/// <c>macos/Sources/VoiceTyper/Services/AudioCaptureService.swift</c> 的 <c>AudioChunker</c>。
/// 调用方（<see cref="AudioCaptureService"/>）负责所有线程安全（R3-01）。
/// </summary>
internal sealed class AudioChunker
{
    public int ChunkSamples { get; }

    private readonly Queue<float> _buffer = new();

    public AudioChunker(int chunkSamples)
    {
        ChunkSamples = chunkSamples;
    }

    /// <summary>累积新样本，返回本次凑满的完整 chunk（可能为空、一个或多个），每个已编码为小端 float32 字节数组。</summary>
    public List<byte[]> Append(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _buffer.Enqueue(sample);
        }

        List<byte[]>? chunks = null;
        while (_buffer.Count >= ChunkSamples)
        {
            var chunk = new byte[ChunkSamples * sizeof(float)];
            var floats = MemoryMarshal.Cast<byte, float>(chunk);
            for (int i = 0; i < ChunkSamples; i++)
            {
                floats[i] = _buffer.Dequeue();
            }
            (chunks ??= new List<byte[]>()).Add(chunk);
        }
        return chunks ?? new List<byte[]>();
    }

    /// <summary>取走并清空当前缓冲区（不足一个完整 chunk 的尾音）。</summary>
    public byte[] Drain()
    {
        if (_buffer.Count == 0) return Array.Empty<byte>();

        var buf = new byte[_buffer.Count * sizeof(float)];
        var floats = MemoryMarshal.Cast<byte, float>(buf);
        int i = 0;
        while (_buffer.Count > 0)
        {
            floats[i++] = _buffer.Dequeue();
        }
        return buf;
    }
}
