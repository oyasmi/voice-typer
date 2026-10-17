using System;
using VoiceTyper.Services;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>C# 直译自 macOS <c>AudioCaptureService.swift</c> 内嵌的 <c>AudioChunker</c> 及其测试用例
/// （纯逻辑分帧器，与 WASAPI/线程无关）。丢一个样本就会让整段识别错位，所以这条规则值得钉死：
/// 凑满即吐出 chunk，不足的尾音靠 Drain 取走。</summary>
public class AudioChunkerTests
{
    private static float[] DecodeFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    [Fact]
    public void Append_AccumulatesAcrossCalls_AndEmitsEveryFullChunkInOrder()
    {
        var chunker = new AudioChunker(chunkSamples: 3);
        Assert.Empty(chunker.Append(new float[] { 1, 2 }));

        var chunks = chunker.Append(new float[] { 3, 4, 5, 6, 7 });

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new float[] { 1, 2, 3 }, DecodeFloats(chunks[0]));
        Assert.Equal(new float[] { 4, 5, 6 }, DecodeFloats(chunks[1]));
        Assert.Equal(new float[] { 7 }, DecodeFloats(chunker.Drain()));
    }

    [Fact]
    public void Drain_ClearsTheBuffer()
    {
        var chunker = new AudioChunker(chunkSamples: 100);
        Assert.Empty(chunker.Drain());

        chunker.Append(new float[30]);
        Assert.Equal(30, DecodeFloats(chunker.Drain()).Length);
        Assert.Empty(chunker.Drain()); // 重复调用不能吐出同一批样本
    }
}
