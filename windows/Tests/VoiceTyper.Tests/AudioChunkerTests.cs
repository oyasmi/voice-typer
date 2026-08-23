using System;
using VoiceTyper.Services;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>C# 直译自 macOS <c>AudioCaptureService.swift</c> 内嵌的 <c>AudioChunker</c> 及其测试用例
/// （R3-01 拆分出的纯逻辑分帧器，与 WASAPI/线程无关，可脱离真实麦克风单测）。</summary>
public class AudioChunkerTests
{
    private static float[] DecodeFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    [Fact]
    public void Append_EmitsNoChunk_WhenBelowThreshold()
    {
        var chunker = new AudioChunker(chunkSamples: 100);
        var chunks = chunker.Append(new float[50]);
        Assert.Empty(chunks);
    }

    [Fact]
    public void Append_EmitsOneChunk_WhenExactlyThreshold()
    {
        var chunker = new AudioChunker(chunkSamples: 100);
        var samples = new float[100];
        for (int i = 0; i < samples.Length; i++) samples[i] = i;

        var chunks = chunker.Append(samples);

        Assert.Single(chunks);
        Assert.Equal(samples, DecodeFloats(chunks[0]));
    }

    [Fact]
    public void Append_EmitsMultipleChunks_WhenFarAboveThreshold()
    {
        var chunker = new AudioChunker(chunkSamples: 100);
        var chunks = chunker.Append(new float[250]);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(50, DecodeFloats(chunker.Drain()).Length); // 250 - 2*100 = 50 个样本留在缓冲区
    }

    [Fact]
    public void Append_CarriesRemainderAcrossCalls()
    {
        var chunker = new AudioChunker(chunkSamples: 100);

        Assert.Empty(chunker.Append(new float[60]));
        var chunks = chunker.Append(new float[60]); // 60+60=120 → 一个 100 的 chunk，剩 20

        Assert.Single(chunks);
        Assert.Equal(20, DecodeFloats(chunker.Drain()).Length);
    }

    [Fact]
    public void Drain_ReturnsRemainderAndClearsBuffer()
    {
        var chunker = new AudioChunker(chunkSamples: 100);
        chunker.Append(new float[30]);

        var tail = DecodeFloats(chunker.Drain());
        Assert.Equal(30, tail.Length);

        var second = chunker.Drain();
        Assert.Empty(second);
    }

    [Fact]
    public void Drain_ReturnsEmpty_WhenNothingAppended()
    {
        var chunker = new AudioChunker(chunkSamples: 100);
        Assert.Empty(chunker.Drain());
    }
}
