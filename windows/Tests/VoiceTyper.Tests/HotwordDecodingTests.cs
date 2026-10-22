using System.Diagnostics;
using System.Text.Json;
using VoiceTyper.Asr;
using VoiceTyper.Asr.Hotword;
using VoiceTyper.Core;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// VOC-3 / VOC-4 / VOC-5：热词编译、上下文图与带偏置的 CTC 前缀束搜索。规则用例来自与 macOS 共享的
/// <c>ctc_hotword_cases.json</c>（csproj 里 Link 自 <c>macos/Tests/VoiceTyperTests/Fixtures/</c>）；
/// 编译与图的用例与 macOS <c>HotwordCompilerTests</c> / <c>ContextGraphTests</c> 一一对应。
/// </summary>
public class HotwordDecodingTests
{
    private static Dictionary<string, int> Ids(IReadOnlyList<string> tokens) =>
        tokens.Select((t, i) => (t, i)).ToDictionary(p => p.t, p => p.i);

    private static CtcPrefixBeamSearch.Result Decode(float[] logits, int frames, IReadOnlyList<string> tokens,
        IEnumerable<int[]> sequences, VocabularyBias bias)
    {
        var parameters = CtcPrefixBeamSearch.Parameters.Preset(bias)!;
        var graph = new ContextGraph(sequences, parameters.PerTokenBias, parameters.MaxScoredDepth);
        return CtcPrefixBeamSearch.Decode(logits, frames, tokens.Count, graph, new CtcPrefixBeamSearch.TokenClasses(tokens), parameters);
    }

    // ─── 共享夹具 ─────────────────────────────────────────────

    [Fact]
    public void SharedFixtureCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ctc_hotword_cases.json");
        Assert.True(File.Exists(path), $"共享夹具缺失：{path}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        var tokens = root.GetProperty("tokens").EnumerateArray().Select(t => t.GetString()!).ToList();
        var floor = root.GetProperty("floor").GetSingle();
        var id = Ids(tokens);
        var cases = root.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 11, "规格要求的初始用例不得减少");

        foreach (var item in cases)
        {
            var name = item.GetProperty("name").GetString();
            var frames = item.GetProperty("frames").EnumerateArray().ToList();
            var logits = Enumerable.Repeat(floor, frames.Count * tokens.Count).ToArray();
            for (int t = 0; t < frames.Count; t++)
            {
                foreach (var score in frames[t].EnumerateObject())
                {
                    logits[t * tokens.Count + id[score.Name]] = score.Value.GetSingle();
                }
            }
            var sequences = item.GetProperty("hotwords").EnumerateArray()
                .Select(h => h.EnumerateArray().Select(t => id[t.GetString()!]).ToArray()).ToList();
            var bias = VocabularyBiasExtensions.Parse(item.GetProperty("preset").GetString());
            var expected = item.GetProperty("expected").EnumerateArray().Select(t => t.GetString()!).ToList();

            var result = Decode(logits, frames.Count, tokens, sequences, bias);
            Assert.True(expected.SequenceEqual(result.Tokens.Select(t => tokens[t])),
                $"{name}：{item.GetProperty("note").GetString()}（实际 {string.Join(" ", result.Tokens.Select(t => tokens[t]))}）");
            Assert.True(item.GetProperty("expected_hits").GetInt32() == result.HotwordHits, $"{name} 命中次数");
        }
    }

    // ─── 束搜索性质 ───────────────────────────────────────────

    /// <summary>可复现的伪随机数（与 macOS 测试同一个 LCG）。</summary>
    private sealed class Lcg
    {
        private ulong _state;
        public Lcg(ulong seed) => _state = seed;
        public ulong Next()
        {
            _state = unchecked(_state * 6_364_136_223_846_793_005UL + 1_442_695_040_888_963_407UL);
            return _state >> 33;
        }
        public float Unit() => (Next() % 1_000_000) / 1_000_000f;
    }

    private static List<int> Greedy(float[] logits, int frames, int vocabSize)
    {
        var result = new List<int>();
        int previous = -1;
        for (int t = 0; t < frames; t++)
        {
            int argmax = 0;
            for (int k = 1; k < vocabSize; k++)
            {
                if (logits[t * vocabSize + k] > logits[t * vocabSize + argmax]) argmax = k;
            }
            if (argmax != previous && argmax != 0) result.Add(argmax);
            previous = argmax;
        }
        return result;
    }

    /// <summary>①：尖锐分布（第二名都在门限外）下，带无关热词、带与原文重合的热词，结果都与贪心逐 token 相同。</summary>
    [Fact]
    public void MatchesGreedyOnSharpDistributions()
    {
        var tokens = new[] { "<unk>", "<s>", "</s>" }.Concat(Enumerable.Range(3, 37).Select(i => $"t{i}")).Append("<|zh|>").ToList();
        int vocabSize = tokens.Count;
        var rng = new Lcg(2024);
        for (int trial = 0; trial < 200; trial++)
        {
            int frames = 20 + (int)(rng.Next() % 60);
            var logits = new float[frames * vocabSize];
            for (int t = 0; t < frames; t++)
            {
                for (int k = 0; k < vocabSize; k++) logits[t * vocabSize + k] = -8 - 12 * rng.Unit();
                int top = rng.Next() % 5 < 3 ? 0 : 3 + (int)(rng.Next() % 37);
                logits[t * vocabSize + top] = 0;
            }
            var reference = Greedy(logits, frames, vocabSize);
            var sequences = new List<int[]> { new[] { 38, 39, 37 } };
            if (reference.Count >= 3) sequences.Add(reference.Skip(1).Take(2).ToArray());
            foreach (var bias in new[] { VocabularyBias.Light, VocabularyBias.Standard, VocabularyBias.Strong })
            {
                var result = Decode(logits, frames, tokens, sequences, bias);
                Assert.True(reference.SequenceEqual(result.Tokens), $"trial {trial} {bias}");
            }
        }
    }

    /// <summary>⑧：同一输入重复解码，结果逐 token 相同（不依赖哈希表遍历顺序）。</summary>
    [Fact]
    public void DecodingIsReproducible()
    {
        var tokens = new[] { "<unk>", "<s>", "</s>", "A", "B", "C", "D" };
        var rng = new Lcg(99);
        const int frames = 120;
        var logits = new float[frames * tokens.Length];
        for (int i = 0; i < logits.Length; i++) logits[i] = -4 * rng.Unit();
        var sequences = new[] { new[] { 3, 4 }, new[] { 5, 6, 3 } };
        var first = Decode(logits, frames, tokens, sequences, VocabularyBias.Strong);
        for (int i = 0; i < 5; i++)
        {
            var again = Decode(logits, frames, tokens, sequences, VocabularyBias.Strong);
            Assert.Equal(first.Tokens, again.Tokens);
            Assert.Equal(first.HotwordHits, again.HotwordHits);
        }
    }

    /// <summary>§5.8：logits 含 NaN 时抛错（由引擎回退贪心），不返回垃圾结果、不崩溃。</summary>
    [Fact]
    public void NonFiniteLogitsThrow()
    {
        var tokens = new[] { "<unk>", "<s>", "</s>", "A", "B" };
        var logits = Enumerable.Repeat(float.NaN, 3 * tokens.Length).ToArray();
        Assert.Throws<CtcPrefixBeamSearch.DecodeException>(() =>
            Decode(logits, 3, tokens, new[] { new[] { 3, 4 } }, VocabularyBias.Standard));
    }

    [Fact]
    public void OffPresetHasNoParameters()
    {
        Assert.Null(CtcPrefixBeamSearch.Parameters.Preset(VocabularyBias.Off));
        var light = CtcPrefixBeamSearch.Parameters.Preset(VocabularyBias.Light)!;
        var strong = CtcPrefixBeamSearch.Parameters.Preset(VocabularyBias.Strong)!;
        Assert.True(light.PerTokenBias < strong.PerTokenBias);
        Assert.True(light.AcousticGate < strong.AcousticGate);
    }

    /// <summary>§10.4：2000 帧（≈120s）× 25055、约 75% blank 帧、200 条词汇。只断言宽松上限防数量级退化；
    /// 真实数值看真机度量日志的 <c>decode_ms</c>。</summary>
    [Fact]
    public void DecodeTimeStaysWithinLooseBound()
    {
        const int vocabSize = 25_055;
        var tokens = Enumerable.Range(0, vocabSize).Select(i => i switch
        {
            0 => "<unk>",
            1 or 2 => "<s>",
            >= 24_884 => $"<|t{i}|>",
            < 10_000 => char.ConvertFromUtf32(0x4E00 + i),
            _ => $"w{i}",
        }).ToList();
        var rng = new Lcg(42);
        const int frames = 2000;
        var logits = new float[frames * vocabSize];
        Array.Fill(logits, -20f);
        for (int t = 0; t < frames; t++)
        {
            int b = t * vocabSize;
            if (t < 4) logits[b + 24_884 + t] = 0;
            else if (rng.Next() % 4 != 0) logits[b] = 0;
            else
            {
                logits[b + 3 + (int)(rng.Next() % 24_000)] = 0;
                logits[b] = -3;
                for (int i = 0; i < 4; i++) logits[b + 3 + (int)(rng.Next() % 24_000)] = -2 - 4 * rng.Unit();
            }
        }
        var entries = Enumerable.Range(0, 200).Select(_ =>
            new VocabularyEntry(string.Concat(Enumerable.Range(0, 3).Select(_ => tokens[3 + (int)(rng.Next() % 9_990)])))).ToList();
        var compiled = HotwordCompiler.Compile(new VocabularyConfig { Bias = "strong", Entries = entries }, new TokenTrie(tokens));
        Assert.Equal(200, compiled.Report.CountOf(HotwordCompileReport.Status.Biased));

        var stopwatch = Stopwatch.StartNew();
        var result = Decode(logits, frames, tokens, compiled.Sequences, VocabularyBias.Strong);
        stopwatch.Stop();
        Assert.NotEmpty(result.Tokens);
        Assert.True(stopwatch.ElapsedMilliseconds < 2_000, $"解码 {stopwatch.ElapsedMilliseconds}ms");
    }

    // ─── 编译（与 macOS HotwordCompilerTests 对应）──────────────

    private static List<string> MakeTokens(params string[] extra) =>
        new[] { "<unk>", "<s>", "</s>", "▁", "-", "+", "." }
            .Concat("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(c => c.ToString()))
            .Concat(Enumerable.Range(0, 10).Select(i => i.ToString()))
            .Concat(new[] { "▁Vo", "ice", "▁Ty", "per", "▁voice", "▁type", "▁Cla", "ude", "张", "三", "丰", "李", "<|zh|>" })
            .Concat(extra).ToList();

    private static List<string> Pieces(HotwordCompiler.SurfaceResult result, IReadOnlyList<string> tokens) =>
        result.Sequences.Select(s => string.Join(" ", s.Select(t => tokens[t]))).ToList();

    [Fact]
    public void VariantsCoverBoundarySplitAndCase()
    {
        Assert.Equal(new[]
        {
            "▁VoiceTyper", "VoiceTyper", "▁Voice▁Typer", "Voice▁Typer", "▁voice▁typer", "voice▁typer",
            "▁voicetyper", "voicetyper", "▁Voicetyper", "Voicetyper",
        }, HotwordCompiler.RawVariants("VoiceTyper"));
        Assert.Equal(new[] { "用▁Claude写", "用Claude写", "用▁claude写", "用claude写" }, HotwordCompiler.RawVariants("用Claude写"));
        Assert.Equal(new[] { "张三丰" }, HotwordCompiler.RawVariants("张三丰"));
        Assert.Equal(new[] { "▁Claude▁Code", "Claude▁Code" }, HotwordCompiler.RawVariants("Claude Code").Take(2));
        Assert.Equal(new[]
        {
            "▁GPT-4o", "GPT-4o", "▁GPT-4▁o", "GPT-4▁o", "▁gpt-4▁o", "gpt-4▁o",
            "▁gpt-4o", "gpt-4o", "▁Gpt-4o", "Gpt-4o", "▁Gpt-4▁O", "Gpt-4▁O",
        }, HotwordCompiler.RawVariants("GPT-4o"));
    }

    [Fact]
    public void SplitPoints()
    {
        Assert.Equal(new[] { 5 }, HotwordCompiler.SplitPoints(HotwordCompiler.Scalars("VoiceTyper")));
        Assert.Equal(new[] { 3, 4 }, HotwordCompiler.SplitPoints(HotwordCompiler.Scalars("GPT4o")));
        Assert.Empty(HotwordCompiler.SplitPoints(HotwordCompiler.Scalars("API")));
    }

    [Fact]
    public void SegmentationPrefersFewestTokensThenLongerFirstPiece()
    {
        var tokens = new[] { "<unk>", "<s>", "</s>", "a", "b", "c", "ab", "bc" };
        var result = new TokenTrie(tokens).BestSegmentations(HotwordCompiler.Scalars("abc"));
        Assert.Equal(new[] { "ab c", "a bc" }, result.Select(s => string.Join(" ", s.Select(t => tokens[t]))));
    }

    [Fact]
    public void SegmentationIgnoresSpecialAndReservedTokens()
    {
        var tokens = new[] { "<unk>", "<s>", "</s>", "<|zh|>", "<", "|", "z", "h", ">", "s" };
        var trie = new TokenTrie(tokens);
        Assert.Equal(6, trie.BestSegmentations(HotwordCompiler.Scalars("<|zh|>"))[0].Length);
        Assert.Equal(3, trie.BestSegmentations(HotwordCompiler.Scalars("<s>"))[0].Length);
    }

    [Fact]
    public void SequencesUseBestThenSecondBestSegmentations()
    {
        var tokens = MakeTokens();
        var result = Pieces(HotwordCompiler.SequencesFor("VoiceTyper", new TokenTrie(tokens)), tokens);
        Assert.Equal("▁Vo ice T y per", result[0]);
        Assert.Contains("▁voice ▁type r", result);
        Assert.True(result.Count <= HotwordCompiler.MaxSequencesPerSurface);
        Assert.Equal(result.Count, result.Distinct().Count());
    }

    [Fact]
    public void SingleTokenSurfaceIsTooShortAndUnsupportedIsReported()
    {
        var trie = new TokenTrie(MakeTokens("▁Voice"));
        Assert.Equal(HotwordCompiler.SurfaceKind.TooShort, HotwordCompiler.SequencesFor("李", trie).Kind);
        Assert.Equal(HotwordCompiler.SurfaceKind.TooShort, HotwordCompiler.SequencesFor("Voice", trie).Kind);
        Assert.Equal(HotwordCompiler.SurfaceKind.Unsupported, HotwordCompiler.SequencesFor("😀", trie).Kind);
        Assert.Equal(HotwordCompiler.SurfaceKind.Unsupported, HotwordCompiler.SequencesFor("张😀", trie).Kind);
    }

    [Fact]
    public void CompileReportStatusesAndTotalLimit()
    {
        var tokens = MakeTokens();
        var trie = new TokenTrie(tokens);
        var output = HotwordCompiler.Compile(new VocabularyConfig
        {
            Entries = { new("张三丰"), new("李"), new("😀"), new("Claude", "李") },
        }, trie);
        Assert.Equal(
            new[] { HotwordCompileReport.Status.Biased, HotwordCompileReport.Status.TooShort,
                HotwordCompileReport.Status.UnsupportedCharacters, HotwordCompileReport.Status.Biased },
            output.Report.Entries.Select(e => e.Status));
        Assert.Equal(HotwordCompileReport.Status.Biased, output.Report.StatusForTerm("claude"));
        Assert.Equal(new[] { "张", "三", "丰" }, output.Sequences[0].Select(t => tokens[t]));

        var many = HotwordCompiler.Compile(new VocabularyConfig
        {
            Entries = Enumerable.Range(0, 400)
                .Select(i => new VocabularyEntry($"q{i}x", Enumerable.Range(0, 10).Select(k => $"w{i}y{k}").ToArray())).ToList(),
        }, trie);
        Assert.Equal(HotwordCompiler.MaxTotalSequences, many.Sequences.Count);
        Assert.Equal(HotwordCompileReport.Status.Biased, many.Report.Entries[0].Status);
        Assert.Equal(HotwordCompileReport.Status.Truncated, many.Report.Entries[^1].Status);
    }

    // ─── 上下文图（与 macOS ContextGraphTests 对应）──────────────

    private static ContextGraph.State Walk(ContextGraph graph, params int[] tokens) =>
        tokens.Aggregate(ContextGraph.Root, graph.Step);

    [Fact]
    public void GraphScoringMatchesSpec()
    {
        var graph = new ContextGraph(new[] { new[] { 1, 2, 3 } }, 1.5, 4);
        Assert.Equal(1.5, graph.Bias(Walk(graph, 1)));
        Assert.Equal(3.0, graph.Bias(Walk(graph, 1, 2)));
        var done = Walk(graph, 1, 2, 3);
        Assert.Equal(4.5, done.Committed);
        Assert.Equal(1, done.Hits);
        // 中途失配撤回暂存分；停在半路的暂存分不计入 committed。
        Assert.Equal(0, graph.Bias(Walk(graph, 1, 2, 9)));
        Assert.Equal(0, Walk(graph, 1, 2).Committed);

        var locked = new ContextGraph(new[] { new[] { 1, 2 } }, 1.5, 4);
        Assert.Equal(3.0, Walk(locked, 1, 2, 7, 8).Committed);

        var nested = new ContextGraph(new[] { new[] { 1, 2 }, new[] { 1, 2, 3 } }, 1.5, 4);
        Assert.Equal(4.5, Walk(nested, 1, 2, 3).Committed);
        Assert.Equal(2, Walk(nested, 1, 2, 3).Hits);
        Assert.Equal(3.0, Walk(nested, 1, 2, 9).Committed);

        var deep = new ContextGraph(new[] { new[] { 1, 2, 3, 4, 5, 6 } }, 1.5, 4);
        Assert.Equal(6.0, deep.Bias(Walk(deep, 1, 2, 3, 4, 5)));

        var suffix = new ContextGraph(new[] { new[] { 1, 2, 3 }, new[] { 2, 4 } }, 1.5, 4);
        var jumped = Walk(suffix, 1, 2, 4);
        Assert.Equal(1, jumped.Hits);
        Assert.Equal(3.0, jumped.Committed);

        var sorted = new ContextGraph(new[] { new[] { 5, 1 }, new[] { 3, 2 }, new[] { 5, 4 }, new[] { 3, 2, 9 } }, 1.5, 4);
        Assert.Equal(new[] { 3, 5 }, sorted.RootTokens);
        Assert.Equal(new[] { 1, 4 }, sorted.ChildrenOf(Walk(sorted, 5)));
    }

    // ─── 真实模型（无模型时跳过）────────────────────────────────

    /// <summary>50 条与音频内容无关的词汇、最强档：输出与同一引擎的贪心结果逐字相同（VOC-D4 / §10.3）。</summary>
    [SkippableFact]
    public void IrrelevantVocabularyKeepsOutputIdentical()
    {
        var bundle = ModelLocator.Locate("");
        Skip.If(bundle is null, "本机未找到 SenseVoice 模型");
        var wav = Path.Combine(AppContext.BaseDirectory, "Fixtures", "speech_zh_en_mixed.wav");
        Skip.IfNot(File.Exists(wav), "缺少语音夹具");
        var (samples, _) = EndToEndRecognitionTests.LoadMonoWav(wav);

        using var engine = new SenseVoiceEngine(bundle!, AsrLanguage.Auto, 4);
        var greedy = engine.Recognize(samples);
        var report = engine.SetVocabulary(new VocabularyConfig
        {
            Bias = "strong",
            Entries = IrrelevantTerms.Select(t => new VocabularyEntry(t)).ToList(),
        });
        Assert.Equal(50, report!.Entries.Count);
        Assert.Equal(greedy, engine.Recognize(samples));
        Assert.Equal(DecodeStats.DecodeMode.Beam, engine.LastDecodeStats.Mode);
    }

    private static readonly string[] IrrelevantTerms =
    {
        "张三丰", "李四光", "王小波", "飞书", "钉钉", "企业微信", "Kubernetes", "PostgreSQL", "Redis", "Grafana",
        "Prometheus", "TensorFlow", "PyTorch", "Claude Code", "Anthropic", "OpenAI", "ChatGPT", "Gemini", "Copilot", "Cursor",
        "VSCode", "JetBrains", "IntelliJ", "Xcode", "SwiftUI", "AppKit", "WinForms", "ONNX", "SenseVoice", "Paraformer",
        "克劳德", "乔布斯", "马斯克", "黄仁勋", "雷军", "字节跳动", "阿里巴巴", "腾讯会议", "小红书", "哔哩哔哩",
        "深度求索", "通义千问", "文心一言", "豆包", "Kimi", "月之暗面", "智谱清言", "讯飞", "搜狗", "百度网盘",
    };
}
