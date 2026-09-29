using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VoiceTyper.Services;

/// <summary>
/// 纯逻辑的分帧器：累积样本 → 吐出定长 chunk，<see cref="Drain"/> 吐出剩余尾音。
/// 与 WASAPI/线程无关，可脱离真实麦克风单测。C# 直译自
/// <c>macos/Sources/VoiceTyper/Services/AudioCaptureService.swift</c> 的 <c>AudioChunker</c>。
/// 调用方（<see cref="AudioCaptureService"/>）负责所有线程安全（R3-01）。
///
/// 内部用一块定长 <c>float[]</c> 线性累积（容量 = 一个 chunk），凑满即整块拷出——
/// 不再逐样本入队出队（VW-17）。
/// </summary>
internal sealed class AudioChunker
{
    public int ChunkSamples { get; }

    private readonly float[] _buffer;
    private int _count;

    public AudioChunker(int chunkSamples)
    {
        if (chunkSamples <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSamples));
        ChunkSamples = chunkSamples;
        _buffer = new float[chunkSamples];
    }

    /// <summary>累积新样本，返回本次凑满的完整 chunk（可能为空、一个或多个），每个已编码为小端 float32 字节数组。</summary>
    public List<byte[]> Append(ReadOnlySpan<float> samples)
    {
        var chunks = new List<byte[]>();
        while (!samples.IsEmpty)
        {
            var take = Math.Min(ChunkSamples - _count, samples.Length);
            samples[..take].CopyTo(_buffer.AsSpan(_count));
            _count += take;
            samples = samples[take..];

            if (_count == ChunkSamples)
            {
                chunks.Add(Encode(_buffer.AsSpan(0, _count)));
                _count = 0;
            }
        }
        return chunks;
    }

    /// <summary>取走并清空当前缓冲区（不足一个完整 chunk 的尾音）。</summary>
    public byte[] Drain()
    {
        if (_count == 0) return Array.Empty<byte>();
        var tail = Encode(_buffer.AsSpan(0, _count));
        _count = 0;
        return tail;
    }

    private static byte[] Encode(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        // 显式 AsSpan：C# 14 的 first-class span 会让 Cast(byte[]) 选中 ReadOnlySpan 重载，
        // 之后对结果赋值会编译失败。
        MemoryMarshal.AsBytes(samples).CopyTo(bytes.AsSpan());
        return bytes;
    }
}
