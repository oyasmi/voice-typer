using VoiceTyper.Llm;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>C# 直译自 macos/Tests/VoiceTyperTests/LLMEndpointTests.swift。</summary>
public class LlmEndpointTests
{
    [Fact]
    public void AppendsChatCompletionsPath()
    {
        var url = LlmEndpoint.ChatCompletionsUrl("https://api.openai.com/v1");
        Assert.Equal("https://api.openai.com/v1/chat/completions", url?.AbsoluteUri);
    }

    [Fact]
    public void DoesNotDuplicateExistingSuffix()
    {
        var url = LlmEndpoint.ChatCompletionsUrl("https://api.openai.com/v1/chat/completions");
        Assert.Equal("https://api.openai.com/v1/chat/completions", url?.AbsoluteUri);
    }

    [Fact]
    public void TrimsTrailingSlashes()
    {
        var url = LlmEndpoint.ChatCompletionsUrl("https://api.openai.com/v1///");
        Assert.Equal("https://api.openai.com/v1/chat/completions", url?.AbsoluteUri);
    }

    [Fact]
    public void AllowsHttpLoopbackForLocalModels()
    {
        var url = LlmEndpoint.ChatCompletionsUrl("http://localhost:1234/v1");
        Assert.Equal("http://localhost:1234/v1/chat/completions", url?.AbsoluteUri);
    }

    [Fact]
    public void RejectsInputWithInternalWhitespace()
    {
        Assert.Null(LlmEndpoint.ChatCompletionsUrl("https://api.openai.com/v1 extra"));
    }

    [Fact]
    public void RejectsMissingScheme()
    {
        Assert.Null(LlmEndpoint.ChatCompletionsUrl("api.openai.com/v1"));
    }

    [Fact]
    public void RejectsNonHttpScheme()
    {
        Assert.Null(LlmEndpoint.ChatCompletionsUrl("ftp://api.openai.com/v1"));
    }

    [Fact]
    public void RejectsPureChineseInput()
    {
        Assert.Null(LlmEndpoint.ChatCompletionsUrl("这不是一个网址"));
    }

    [Fact]
    public void RejectsEmptyInput()
    {
        Assert.Null(LlmEndpoint.ChatCompletionsUrl("   "));
    }

    // ─── R2-07：明文 HTTP 限回环/私网 ─────────────────────────────

    [Fact]
    public void AllowsHttpIPv4Loopback()
    {
        Assert.NotNull(LlmEndpoint.ChatCompletionsUrl("http://127.0.0.1:8080/v1"));
    }

    [Fact]
    public void AllowsHttpIPv6Loopback()
    {
        Assert.NotNull(LlmEndpoint.ChatCompletionsUrl("http://[::1]:8080/v1"));
    }

    [Fact]
    public void AllowsHttpPrivateNetwork10()
    {
        Assert.NotNull(LlmEndpoint.ChatCompletionsUrl("http://10.0.1.5:11434/v1"));
    }

    [Fact]
    public void AllowsHttpPrivateNetwork192168()
    {
        Assert.NotNull(LlmEndpoint.ChatCompletionsUrl("http://192.168.1.20/v1"));
    }

    [Fact]
    public void AllowsHttpPrivateNetwork172Range()
    {
        Assert.NotNull(LlmEndpoint.ChatCompletionsUrl("http://172.16.0.1/v1"));
        Assert.NotNull(LlmEndpoint.ChatCompletionsUrl("http://172.31.255.255/v1"));
        Assert.Null(LlmEndpoint.ChatCompletionsUrl("http://172.32.0.1/v1"));
    }

    [Fact]
    public void AllowsHttpDotLocalHost()
    {
        Assert.NotNull(LlmEndpoint.ChatCompletionsUrl("http://ollama.local:11434/v1"));
    }

    [Fact]
    public void RejectsHttpPublicHost()
    {
        var result = LlmEndpoint.Resolve("http://example.com/v1");
        Assert.Null(result.Url);
        Assert.Equal(LlmEndpoint.ErrorKind.InsecurePlaintextHost, result.Error);
        Assert.Contains("example.com", result.ErrorMessage);
    }

    [Fact]
    public void AllowsHttpsForPublicHost()
    {
        Assert.NotNull(LlmEndpoint.ChatCompletionsUrl("https://example.com/v1"));
    }
}
