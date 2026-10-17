using VoiceTyper.Llm;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>API Base URL 是用户手输的，且 API Key 会随请求发往这个地址：归一化要稳，
/// 明文 HTTP 只允许回环 / 私网（R2-07），其余一律拒绝。对应 macOS <c>LLMEndpointTests</c>。</summary>
public class LlmEndpointTests
{
    private static string? Resolved(string input) => LlmEndpoint.ChatCompletionsUrl(input)?.AbsoluteUri;

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1///")]                 // 尾部斜杠
    [InlineData("https://api.openai.com/v1/chat/completions")]   // 不重复追加后缀
    public void NormalizesToChatCompletionsUrl(string input)
    {
        Assert.Equal("https://api.openai.com/v1/chat/completions", Resolved(input));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("api.openai.com/v1")]
    [InlineData("ftp://api.openai.com/v1")]
    [InlineData("这不是一个网址")]
    [InlineData("https://api.openai.com/v1 extra")]
    public void RejectsMalformedInput(string input)
    {
        Assert.Null(Resolved(input));
    }

    [Theory]
    [InlineData("http://localhost:1234/v1")]
    [InlineData("http://127.0.0.1:8080/v1")]
    [InlineData("http://[::1]:8080/v1")]
    [InlineData("http://10.0.1.5:11434/v1")]
    [InlineData("http://192.168.1.20/v1")]
    [InlineData("http://172.16.0.1/v1")]
    [InlineData("http://172.31.255.255/v1")]
    [InlineData("http://ollama.local:11434/v1")]
    [InlineData("https://example.com/v1")]                       // HTTPS 不限制 host
    public void AllowsPlainHttpOnlyForLoopbackAndPrivateHosts(string input)
    {
        Assert.NotNull(Resolved(input));
    }

    [Fact]
    public void RejectsPlainHttpToPublicHosts()
    {
        Assert.Null(Resolved("http://172.32.0.1/v1")); // 172.16/12 之外不属于私网

        var result = LlmEndpoint.Resolve("http://example.com/v1");
        Assert.Null(result.Url);
        Assert.Equal(LlmEndpoint.ErrorKind.InsecurePlaintextHost, result.Error);
        Assert.Contains("example.com", result.ErrorMessage);
    }
}
