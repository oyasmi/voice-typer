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
            BaseUrl = "https://example.invalid/v1",
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
}
