using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Llm;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 校对请求默认关闭深度思考（<c>thinking: {"type":"disabled"}</c>）；服务拒绝该字段时去掉字段重发一次，
/// 并按「地址 + 模型」缓存结果（对应 macOS <c>LLMCorrectorTests</c> 的 thinking 系列）。
/// </summary>
public class LlmThinkingTests
{
    private const string OkBody = """{"choices":[{"message":{"content":"好的"},"finish_reason":"stop"}]}""";

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<JsonElement, HttpResponseMessage> _responder;
        public readonly List<JsonElement> Requests = new();

        public RecordingHandler(Func<JsonElement, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var json = JsonDocument.Parse(body).RootElement.Clone();
            Requests.Add(json);
            return _responder(json);
        }
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent(OkBody) };

    private static HttpResponseMessage RejectThinking() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent("""{"error":{"message":"Unrecognized request argument supplied: thinking"}}"""),
    };

    private static readonly Uri Url = new("https://example.invalid/v1/chat/completions");

    private static LlmCorrector Make(HttpMessageHandler handler, ILlmCapabilityStore store, double timeout = 5) =>
        new(new LlmCorrector.Config
        {
            ChatCompletionsUrl = Url,
            ApiKey = "test-key",
            Model = "test-model",
            Temperature = 0,
            MaxTokens = 800,
            Timeout = timeout,
        }, new HttpClient(handler), store);

    private static string Fingerprint => LlmCapabilityFingerprint.Make(Url, "test-model");

    [Fact]
    public async Task DefaultRequest_DisablesThinking()
    {
        var handler = new RecordingHandler(_ => Ok());
        var corrector = Make(handler, new InMemoryLlmCapabilityStore());

        var report = await corrector.CorrectWithReportAsync("原文");

        Assert.False(report.Outcome.DidFallBack);
        Assert.Equal(LlmCorrector.ThinkingParameterUsage.Sent, report.Thinking);
        var thinking = Assert.Single(handler.Requests).GetProperty("thinking");
        Assert.Equal("disabled", thinking.GetProperty("type").GetString());
    }

    [Fact]
    public async Task RejectedThinking_RetriesOnceWithoutIt_AndCachesTheAnswer()
    {
        var store = new InMemoryLlmCapabilityStore();
        var handler = new RecordingHandler(body => body.TryGetProperty("thinking", out _) ? RejectThinking() : Ok());
        var corrector = Make(handler, store);

        var report = await corrector.CorrectWithReportAsync("原文");

        Assert.False(report.Outcome.DidFallBack);
        Assert.Equal("好的", report.Outcome.Text);
        Assert.Equal(LlmCorrector.ThinkingParameterUsage.RejectedThenOmitted, report.Thinking);
        Assert.Equal(2, handler.Requests.Count);
        Assert.True(handler.Requests[0].TryGetProperty("thinking", out _));
        Assert.False(handler.Requests[1].TryGetProperty("thinking", out _));
        Assert.True(store.IsThinkingParameterUnsupported(Fingerprint));
    }

    [Fact]
    public async Task CachedUnsupported_SkipsTheParameterWithASingleRequest()
    {
        var store = new InMemoryLlmCapabilityStore();
        store.SetThinkingParameterUnsupported(true, Fingerprint);
        var handler = new RecordingHandler(_ => Ok());
        var corrector = Make(handler, store);

        var report = await corrector.CorrectWithReportAsync("原文");

        Assert.Equal(LlmCorrector.ThinkingParameterUsage.OmittedByCache, report.Thinking);
        var request = Assert.Single(handler.Requests);
        Assert.False(request.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task Rejection_OnlyCountsWhenBodyMentionsThinking()
    {
        // 400 但与 thinking 无关（例如鉴权/参数别的错）：不重发、不缓存，按普通失败回落原文。
        var store = new InMemoryLlmCapabilityStore();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"bad model"}"""),
        });
        var corrector = Make(handler, store);

        var report = await corrector.CorrectWithReportAsync("原文");

        Assert.True(report.Outcome.DidFallBack);
        Assert.Single(handler.Requests);
        Assert.False(store.IsThinkingParameterUnsupported(Fingerprint));
    }

    [Fact]
    public async Task TestProbe_IgnoresCache_AndClearsItWhenServiceNowAccepts()
    {
        var store = new InMemoryLlmCapabilityStore();
        store.SetThinkingParameterUnsupported(true, Fingerprint);
        var handler = new RecordingHandler(_ => Ok());
        var corrector = Make(handler, store);

        var result = await corrector.TestWithReportAsync("原文");

        Assert.Equal(LlmCorrector.ThinkingParameterUsage.Sent, result.Thinking);
        Assert.True(Assert.Single(handler.Requests).TryGetProperty("thinking", out _));
        Assert.False(store.IsThinkingParameterUnsupported(Fingerprint));
    }

    [Fact]
    public async Task TestProbe_ReportsRejectedThenOmitted()
    {
        var store = new InMemoryLlmCapabilityStore();
        var handler = new RecordingHandler(body => body.TryGetProperty("thinking", out _) ? RejectThinking() : Ok());
        var corrector = Make(handler, store);

        var result = await corrector.TestWithReportAsync("原文");
        Assert.Equal(LlmCorrector.ThinkingParameterUsage.RejectedThenOmitted, result.Thinking);
        Assert.True(store.IsThinkingParameterUnsupported(Fingerprint));
    }

    [Fact]
    public void Fingerprint_DependsOnUrlAndModel_ButNeverOnKey()
    {
        var a = LlmCapabilityFingerprint.Make(Url, "m1");
        Assert.Equal(16, a.Length);
        Assert.Equal(a, LlmCapabilityFingerprint.Make(Url, "m1"));
        Assert.NotEqual(a, LlmCapabilityFingerprint.Make(Url, "m2"));
        Assert.NotEqual(a, LlmCapabilityFingerprint.Make(new Uri("https://other.invalid/v1/chat/completions"), "m1"));
    }

    [Fact]
    public void FileStore_PersistsAndBoundsEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voicetyper-cap-{Guid.NewGuid():N}", "caps.txt");
        try
        {
            var store = new FileLlmCapabilityStore(path);
            store.SetThinkingParameterUnsupported(true, "aaaa");
            Assert.True(new FileLlmCapabilityStore(path).IsThinkingParameterUnsupported("aaaa")); // 跨实例（模拟重启）

            store.SetThinkingParameterUnsupported(false, "aaaa");
            Assert.False(store.IsThinkingParameterUnsupported("aaaa"));

            for (int i = 0; i < 40; i++) store.SetThinkingParameterUnsupported(true, $"fp{i:D2}");
            Assert.False(store.IsThinkingParameterUnsupported("fp00")); // 最旧的被淘汰
            Assert.True(store.IsThinkingParameterUnsupported("fp39"));
            Assert.Equal(32, File.ReadAllLines(path).Length);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
        }
    }

    [Fact]
    public void FileStore_UnreadableFile_MeansNothingCached()
    {
        var store = new FileLlmCapabilityStore(Path.Combine(Path.GetTempPath(), $"voicetyper-none-{Guid.NewGuid():N}", "x.txt"));
        Assert.False(store.IsThinkingParameterUnsupported("anything"));
    }
}
