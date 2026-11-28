using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Asr;
using VoiceTyper.Asr.Hotword;
using VoiceTyper.Core;
using VoiceTyper.Llm;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// HINT-8：局部声学低置信度备选提取（共享夹具 <c>asr_hints_cases.json</c>）、词汇表一致性过滤、
/// Prompt 拼装与标签防御、TokenUsage 解析与度量字段测试。
/// </summary>
public class AcousticHintExtractorTests
{
    [Fact]
    public void SharedFixtureCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "asr_hints_cases.json");
        Assert.True(File.Exists(path), $"共享夹具缺失：{path}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        var tokens = root.GetProperty("tokens").EnumerateArray().Select(t => t.GetString()!).ToList();
        var floor = root.GetProperty("floor").GetSingle();
        var marginThreshold = root.GetProperty("margin_threshold").GetDouble();
        var id = tokens.Select((t, i) => (t, i)).ToDictionary(p => p.t, p => p.i);
        var meta = new AcousticHintExtractor.TokenMetadata(tokens);
        var parameters = AcousticHintExtractor.Parameters.Default with { MarginThreshold = marginThreshold };

        var cases = root.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 11);

        foreach (var item in cases)
        {
            var name = item.GetProperty("name").GetString()!;
            var note = item.GetProperty("note").GetString()!;
            var mode = item.GetProperty("mode").GetString()!;
            var frames = item.GetProperty("frames").EnumerateArray().ToList();
            var logits = Enumerable.Repeat(floor, frames.Count * tokens.Count).ToArray();
            for (int t = 0; t < frames.Count; t++)
            {
                foreach (var prop in frames[t].EnumerateObject())
                {
                    logits[t * tokens.Count + id[prop.Name]] = prop.Value.GetSingle();
                }
            }

            string text;
            IReadOnlyList<AcousticHint> hints;
            if (mode == "beam")
            {
                var bias = VocabularyBiasExtensions.Parse(item.GetProperty("preset").GetString());
                var beamParams = CtcPrefixBeamSearch.Parameters.Preset(bias)!;
                var sequences = item.GetProperty("hotwords").EnumerateArray()
                    .Select(h => h.EnumerateArray().Select(t => id[t.GetString()!]).ToArray()).ToList();
                var graph = new ContextGraph(sequences, beamParams.PerTokenBias, beamParams.MaxScoredDepth);
                var classes = new CtcPrefixBeamSearch.TokenClasses(tokens);
                var decoded = CtcPrefixBeamSearch.Decode(logits, frames.Count, tokens.Count, graph, classes, beamParams);
                var raw = string.Concat(decoded.Tokens.Select(tid => tokens[tid]));
                text = TextPostprocessor.Process(raw);
                var aligned = AcousticHintExtractor.ForcedAlign(logits, frames.Count, tokens.Count, decoded.Tokens, tokens, graph);
                Assert.NotNull(aligned);
                hints = AcousticHintExtractor.Extract(logits, frames.Count, tokens.Count, aligned!, text, meta, parameters);
            }
            else
            {
                var (ids, aligned) = CtcDecoder.GreedyAlignedTokens(logits, frames.Count, tokens.Count, tokens);
                var raw = string.Concat(ids.Select(tid => tokens[tid]));
                text = TextPostprocessor.Process(raw);
                hints = AcousticHintExtractor.Extract(logits, frames.Count, tokens.Count, aligned, text, meta, parameters);
            }

            Assert.Equal(item.GetProperty("expected_text").GetString(), text);
            var expectedHints = item.GetProperty("expected_hints").EnumerateArray().ToList();
            Assert.True(expectedHints.Count == hints.Count, $"{name} ({note}): expected {expectedHints.Count} hints, got {hints.Count}");
            for (int i = 0; i < expectedHints.Count; i++)
            {
                Assert.Equal(expectedHints[i].GetProperty("surface").GetString(), hints[i].Surface);
                var expectedAlts = expectedHints[i].GetProperty("alternatives").EnumerateArray().Select(a => a.GetString()!).ToList();
                Assert.Equal(expectedAlts, hints[i].Alternatives);
            }
        }
    }

    [Fact]
    public void NaNLogitsSafelyReturnEmptyHints()
    {
        var tokens = new[] { "<unk>", "<s>", "</s>", "密", "幂", "等" };
        var meta = new AcousticHintExtractor.TokenMetadata(tokens);
        var logits = Enumerable.Repeat(-20f, 2 * tokens.Length).ToArray();
        logits[3] = 0f;
        logits[4] = float.NaN;
        logits[tokens.Length + 5] = 0f;
        var aligned = new List<AlignedToken>
        {
            new(3, 0, false),
            new(5, 1, false),
        };
        var hints = AcousticHintExtractor.Extract(logits, 2, tokens.Length, aligned, "密等", meta);
        Assert.Empty(hints);
    }

    [Fact]
    public void ReconcileHintsDropsReplacedAndExactTermsAndNormalizesAliases()
    {
        var vocab = new VocabularyConfig
        {
            Entries =
            {
                new VocabularyEntry { Term = "Claude", Aliases = { "克劳德" } },
                new VocabularyEntry { Term = "VoiceTyper", Aliases = { "语音打字" } },
            },
        };
        var replacer = new VocabularyReplacer(vocab);
        var rawHints = new List<AcousticHint>
        {
            new("克劳德", new[] { "可劳德" }, 0.4),
            new("claude", new[] { "cloud" }, 0.5),
            new("密等", new[] { "克劳德", "Claude", "幂等" }, 0.6),
            new("坏<tag>", new[] { "好" }, 0.2),
        };
        var (active, dropped) = replacer.ReconcileHints(rawHints, "用Claude检查密等性");
        Assert.Equal(2, dropped);
        Assert.Single(active);
        Assert.Equal("密等", active[0].Surface);
        Assert.Equal(new[] { "Claude", "幂等" }, active[0].Alternatives);
    }

    [Fact]
    public void LlmUserMessageOrderingAndEmptyHintsByteIdentical()
    {
        Assert.Equal("<asr_text>\n你好\n</asr_text>", LlmCorrector.UserMessage("你好", Array.Empty<string>(), Array.Empty<AcousticHint>()));
        var hints = new List<AcousticHint>
        {
            new("密等", new[] { "幂等", "密度" }, 0.4),
            new("将近", new[] { "降级" }, 0.8),
        };
        var msg = LlmCorrector.UserMessage("测试文本", new[] { "VoiceTyper", "SenseVoice" }, hints);
        Assert.Equal(1.15, AcousticHintExtractor.Parameters.Default.MarginThreshold, precision: 6);
        Assert.Equal(
            "<glossary>VoiceTyper、SenseVoice</glossary>\n<asr_hints>\n「密等」备选：幂等 / 密度\n「将近」备选：降级\n</asr_hints>\n<asr_text>\n测试文本\n</asr_text>",
            msg);
    }

    [Fact]
    public async Task LlmCorrectorStripsEchoedTagsGuardsLeakedTagsAndParsesTokenUsage()
    {
        var echoedJson = """
        {
          "choices": [{"message": {"content": "<asr_hints>noise</asr_hints>\n幂等性。"}, "finish_reason": "stop"}],
          "usage": {"prompt_tokens": 512, "completion_tokens": 24, "prompt_tokens_details": {"cached_tokens": 448}}
        }
        """;
        using var http1 = new HttpClient(new StubHandler(echoedJson));
        using var corrector1 = new LlmCorrector(MakeConfig(), http1);
        var report1 = await corrector1.CorrectWithReportAsync("密等性");
        Assert.False(report1.Outcome.DidFallBack);
        Assert.Equal("幂等性。", report1.Outcome.Text);
        Assert.NotNull(report1.TokenUsage);
        Assert.Equal(512, report1.TokenUsage!.PromptTokens);
        Assert.Equal(24, report1.TokenUsage.CompletionTokens);
        Assert.Equal(448, report1.TokenUsage.CachedTokens);

        var leakedJson = """
        {
          "choices": [{"message": {"content": "<asr_hints\n幂等性。"}, "finish_reason": "stop"}]
        }
        """;
        using var http2 = new HttpClient(new StubHandler(leakedJson));
        using var corrector2 = new LlmCorrector(MakeConfig(), http2);
        var report2 = await corrector2.CorrectWithReportAsync("密等性");
        Assert.True(report2.Outcome.DidFallBack);
        Assert.Equal("密等性", report2.Outcome.Text);
        Assert.Null(report2.TokenUsage);
    }

    [Fact]
    public void DictationMetricsSummaryLineAppendsAsrHints()
    {
        var m = new DictationMetrics { SessionId = 0x1a2b, Timings = { LlmResult = AsrSessionTimings.LlmOutcome.Off } };
        Assert.EndsWith(" asr_hints=-", m.SummaryLine(), StringComparison.Ordinal);

        m.Timings.LlmResult = AsrSessionTimings.LlmOutcome.Corrected;
        m.Timings.AsrHintCount = 2;
        Assert.EndsWith(" asr_hints=2", m.SummaryLine(), StringComparison.Ordinal);
    }

    private static LlmCorrector.Config MakeConfig() => new()
    {
        ChatCompletionsUrl = new Uri("https://example.test/v1/chat/completions"),
        ApiKey = "test-key",
        Model = "gpt-4o-mini",
        Temperature = 0.0,
        MaxTokens = 800,
        Timeout = 5.0,
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        public StubHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }
}
