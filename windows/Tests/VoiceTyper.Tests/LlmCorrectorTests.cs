using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Llm;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 用 <see cref="HttpMessageHandler"/> 打桩验证：正常 / <c>finish_reason=length</c> / 5xx /
/// 响应格式错误，均不应把异常抛给调用方——<see cref="LlmCorrector.CorrectAsync"/> 的契约是
/// 任何失败都返回原文，不丢已识别出的文本。
/// </summary>
public class LlmCorrectorTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = _responder(request);
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_exception);
    }

    private static LlmCorrector MakeCorrector(Func<HttpRequestMessage, HttpResponseMessage> responder, double timeout = 5) =>
        MakeCorrectorWithHandler(new StubHandler(responder), timeout);

    private static LlmCorrector MakeCorrectorWithHandler(HttpMessageHandler handler, double timeout = 5)
    {
        var client = new HttpClient(handler);
        return new LlmCorrector(new LlmCorrector.Config
        {
            ChatCompletionsUrl = new Uri("https://example.invalid/v1/chat/completions"),
            ApiKey = "test-key",
            Model = "test-model",
            Temperature = 0,
            MaxTokens = 800,
            Timeout = timeout,
        }, client);
    }

    [Fact]
    public async Task Correct_ReturnsCorrectedText_OnSuccess()
    {
        var corrector = MakeCorrector(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"content":"修正后的文本"},"finish_reason":"stop"}]}"""),
        });

        var result = await corrector.CorrectAsync("原始文本");
        Assert.Equal("修正后的文本", result);
    }

    [Fact]
    public async Task Correct_ReturnsOriginalText_WhenFinishReasonIsLength()
    {
        var corrector = MakeCorrector(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"content":"被截断的文本"},"finish_reason":"length"}]}"""),
        });

        var result = await corrector.CorrectAsync("原始文本");
        Assert.Equal("原始文本", result);
    }

    [Fact]
    public async Task Correct_ReturnsOriginalText_On5xxError()
    {
        var corrector = MakeCorrector(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("internal error"),
        });

        var result = await corrector.CorrectAsync("原始文本");
        Assert.Equal("原始文本", result);
    }

    [Fact]
    public async Task Correct_ReturnsOriginalText_OnMalformedJson()
    {
        var corrector = MakeCorrector(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json"),
        });

        var result = await corrector.CorrectAsync("原始文本");
        Assert.Equal("原始文本", result);
    }

    [Fact]
    public async Task Correct_StripsEchoedAsrTextTags()
    {
        var corrector = MakeCorrector(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":\"<asr_text>\\n修正后的文本\\n</asr_text>\"},\"finish_reason\":\"stop\"}]}"),
        });

        var result = await corrector.CorrectAsync("原始文本");
        Assert.Equal("修正后的文本", result);
    }

    [Fact]
    public async Task Correct_ReturnsOriginalText_OnTimeout()
    {
        var corrector = MakeCorrectorWithHandler(new ThrowingHandler(new TaskCanceledException("simulated timeout")), timeout: 1);

        var result = await corrector.CorrectAsync("原始文本");
        Assert.Equal("原始文本", result);
    }

    /// <summary>W-04（R2-08）：判空必须放在剥标签之后。<c>&lt;asr_text&gt;\n&lt;/asr_text&gt;</c>
    /// 剥离前非空、剥离后为空，若判空放在剥标签前会漏判，把整段听写文本丢掉。</summary>
    [Fact]
    public async Task Correct_ReturnsOriginalText_WhenResponseIsTagsOnly()
    {
        var corrector = MakeCorrector(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":\"<asr_text>\\n</asr_text>\"},\"finish_reason\":\"stop\"}]}"),
        });

        var result = await corrector.CorrectAsync("原始文本");
        Assert.Equal("原始文本", result);
    }

    /// <summary>W-02：非 2xx 响应体可能回显了送去的识别文本，异常消息绝不能包含它，只暴露状态码。</summary>
    [Fact]
    public async Task Test_ThrowsWithoutResponseBody_On5xxError()
    {
        const string sensitiveBody = "呃这是用户的听写内容，不应该出现在异常消息里";
        var corrector = MakeCorrector(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(sensitiveBody),
        });

        var ex = await Assert.ThrowsAsync<LlmException>(() => corrector.TestAsync("原始文本"));
        Assert.DoesNotContain(sensitiveBody, ex.Message);
        Assert.Contains("500", ex.Message);
    }

    /// <summary>W-06（R3-13）："测试纠错"必须能看到真实失败原因，而不是像 <see cref="LlmCorrector.CorrectAsync"/>
    /// 那样把任何失败都吞成原文回落。</summary>
    [Fact]
    public async Task Test_Throws_OnMalformedJson()
    {
        var corrector = MakeCorrector(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json"),
        });

        await Assert.ThrowsAsync<LlmException>(() => corrector.TestAsync("原始文本"));
    }

    [Fact]
    public async Task Test_ReturnsCorrectedText_OnSuccess()
    {
        var corrector = MakeCorrector(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"content":"修正后的文本"},"finish_reason":"stop"}]}"""),
        });

        var result = await corrector.TestAsync("原始文本");
        Assert.Equal("修正后的文本", result);
    }
}
