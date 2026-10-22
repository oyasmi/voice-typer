using System.Reflection;
using System.Text.Json;
using VoiceTyper.Asr.Hotword;
using VoiceTyper.Core;
using VoiceTyper.Llm;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// VOC-1 / VOC-7 / VOC-8 / VOC-10：词汇配置、确定性替换、LLM 词表与度量字段。替换规则用例来自与 macOS 共享的
/// <c>vocabulary_cases.json</c>（csproj 里 Link 自 <c>macos/Tests/VoiceTyperTests/Fixtures/</c>）。
/// </summary>
public class VocabularyTests
{
    private sealed record Case(string Input, string Expected, string? Formatted, string Note);
    private sealed record Group(string Name, List<VocabularyEntry> Vocabulary, List<Case> Cases);

    private static List<Group> LoadGroups()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vocabulary_cases.json");
        Assert.True(File.Exists(path), $"共享夹具缺失：{path}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        return document.RootElement.GetProperty("groups").EnumerateArray().Select(group => new Group(
            group.GetProperty("name").GetString()!,
            group.GetProperty("vocabulary").EnumerateArray().Select(entry => new VocabularyEntry
            {
                Term = entry.GetProperty("term").GetString()!,
                Aliases = entry.TryGetProperty("aliases", out var aliases)
                    ? aliases.EnumerateArray().Select(a => a.GetString()!).ToList()
                    : new List<string>(),
            }).ToList(),
            group.GetProperty("cases").EnumerateArray().Select(item => new Case(
                item.GetProperty("input").GetString()!,
                item.GetProperty("expected").GetString()!,
                item.TryGetProperty("formatted", out var formatted) ? formatted.GetString() : null,
                item.GetProperty("note").GetString()!)).ToList())).ToList();
    }

    private static VocabularyReplacer Replacer(List<VocabularyEntry> entries) =>
        new(new VocabularyConfig { Entries = entries }.Validated());

    // ─── 替换层 ───────────────────────────────────────────────

    [Fact]
    public void SharedReplacementFixtureCases()
    {
        var groups = LoadGroups();
        Assert.True(groups.Sum(g => g.Cases.Count) >= 27, "规格要求的初始用例不得减少");
        foreach (var group in groups)
        {
            var replacer = Replacer(group.Vocabulary);
            foreach (var item in group.Cases)
            {
                var output = replacer.Apply(item.Input);
                Assert.True(output == item.Expected, $"{group.Name}：{item.Note}（实际 {output}）");
                Assert.True(replacer.Apply(output) == output, $"{group.Name} 不幂等：{item.Note}");
                if (item.Formatted is { } formatted)
                {
                    Assert.Equal(formatted, OutputFormatter.FormatForOutput(item.Input, replacer));
                }
            }
        }
    }

    /// <summary>性质测试：term、alias、它们的小写形式与常见填充片段随机拼接，Apply 都幂等。</summary>
    [Fact]
    public void ApplyIsIdempotentOnRandomConcatenations()
    {
        ulong state = 7;
        int Next()
        {
            state = unchecked(state * 6_364_136_223_846_793_005UL + 1_442_695_040_888_963_407UL);
            return (int)(state >> 33);
        }
        foreach (var group in LoadGroups())
        {
            var replacer = Replacer(group.Vocabulary);
            var pieces = new List<string> { "的", "用", " ", "-", "a", "x1", "，", "code", "Code", "voice", "typer", "4o", "gpt" };
            foreach (var entry in group.Vocabulary)
            {
                pieces.Add(entry.Term);
                pieces.Add(entry.Term.ToLowerInvariant());
                pieces.AddRange(entry.Aliases);
            }
            for (int i = 0; i < 2000; i++)
            {
                var text = string.Concat(Enumerable.Range(0, 1 + Next() % 8).Select(_ => pieces[Next() % pieces.Count]));
                var once = replacer.Apply(text);
                Assert.True(replacer.Apply(once) == once, $"{group.Name}：「{text}」");
            }
        }
    }

    [Fact]
    public void EmptyVocabularyIsNoOpAndTermsKeepOrder()
    {
        Assert.True(VocabularyReplacer.Empty.IsEmpty);
        Assert.Equal("用克劳德写 voice typer", VocabularyReplacer.Empty.Apply("用克劳德写 voice typer"));
        Assert.Equal("用 Python 写", OutputFormatter.FormatForOutput("用Python写", VocabularyReplacer.Empty));
        var replacer = Replacer(new() { new("Claude", "克劳德"), new("张三丰") });
        Assert.Equal(new[] { "Claude", "张三丰" }, replacer.Terms);
        Assert.Equal("用 Claude 写", OutputFormatter.FormatForOutput("用克劳德写", replacer));
    }

    // ─── 配置（与 macOS AppConfigTests 的 VOC-1 段对应）─────────────

    [Fact]
    public void ValidationNormalizesEntries()
    {
        var vocabulary = new VocabularyConfig
        {
            Bias = "light",
            Entries =
            {
                new("  Claude  ", "克劳德", " claude ", "", "克劳德", "KLD"),
                new("   "),
                new("claude", "kld", "可劳德"),
                new("a\nb\tc"),
            },
        }.Validated();
        Assert.Equal(VocabularyBias.Light, vocabulary.BiasValue);
        Assert.Equal(new[] { new VocabularyEntry("Claude", "克劳德", "KLD", "可劳德"), new VocabularyEntry("a b c") }, vocabulary.Entries);
    }

    [Fact]
    public void ValidationTruncatesOverflow()
    {
        var entries = new List<VocabularyEntry> { new(new string('长', 60), Enumerable.Range(0, 15).Select(i => $"别名{i}").ToArray()) };
        entries.AddRange(Enumerable.Range(0, 250).Select(i => new VocabularyEntry($"词{i}")));
        var vocabulary = new VocabularyConfig { Entries = entries }.Validated();
        Assert.Equal(VocabularyConfig.MaxEntries, vocabulary.Entries.Count);
        Assert.Equal(VocabularyConfig.MaxSurfaceLength, vocabulary.Entries[0].Term.Length);
        Assert.Equal(VocabularyConfig.MaxAliasesPerEntry, vocabulary.Entries[0].Aliases.Count);
        Assert.Equal("词198", vocabulary.Entries[^1].Term);
    }

    [Fact]
    public void YamlDecodingIsTolerantAndDefaultsWhenMissing()
    {
        var missing = ConfigStore.Deserializer.Deserialize<AppConfig>("hotkey:\n  key: right_ctrl\n").Validated();
        Assert.Equal(VocabularyBias.Standard, missing.Vocabulary.BiasValue);
        Assert.Empty(missing.Vocabulary.Entries);

        const string yaml = """
            vocabulary:
              bias: extreme
              entries:
                - 张三丰
                - term: "Claude"
                  aliases: "克劳德"
                - term: "VoiceTyper"
                  aliases: []
                  note: 未知字段忽略
                - aliases: ["没有 term"]
            """;
        var vocabulary = ConfigStore.Deserializer.Deserialize<AppConfig>(yaml).Validated().Vocabulary;
        Assert.Equal(VocabularyBias.Standard, vocabulary.BiasValue);
        Assert.Equal(new[] { new VocabularyEntry("张三丰"), new VocabularyEntry("Claude", "克劳德"), new VocabularyEntry("VoiceTyper") },
            vocabulary.Entries);
    }

    [Fact]
    public void SerializeYamlRoundTripsVocabularyAndCloneKeepsIt()
    {
        var config = new AppConfig();
        config.Vocabulary = new VocabularyConfig
        {
            Bias = "strong",
            Entries = { new("Claude", "克劳德", "可劳德"), new("张三丰"), new("say \"hi\" \\ ok", "x: y", "- z", "#tag") },
        };
        var method = typeof(ConfigStore).GetMethod("SerializeYaml", BindingFlags.NonPublic | BindingFlags.Static)!;
        var yaml = (string)method.Invoke(null, new object[] { config })!;
        var parsed = ConfigStore.Deserializer.Deserialize<AppConfig>(yaml).Validated();
        Assert.True(parsed.Vocabulary.ContentEquals(config.Vocabulary));
        // 设置窗口以 Clone 出的草稿保存：漏掉词汇段会让任何一次保存都清空词汇表。
        Assert.True(config.Clone().Vocabulary.ContentEquals(config.Vocabulary));
    }

    // ─── LLM 词表（VOC-8）──────────────────────────────────────

    private static LlmCorrector.Config PayloadConfig() => new()
    {
        ChatCompletionsUrl = new Uri("https://stub.invalid/v1/chat/completions"),
        ApiKey = "k",
        Model = "gpt-4o-mini",
        Temperature = 0,
        MaxTokens = 800,
        Timeout = 5,
    };

    private static List<string> Contents(Dictionary<string, object> payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("content").GetString()!).ToList();

    [Fact]
    public void GlossaryOnlyChangesLastUserMessage()
    {
        var plain = Contents(LlmCorrector.BuildPayload(PayloadConfig(), "SYS", "用克劳德写", includeThinking: true));
        var withGlossary = Contents(LlmCorrector.BuildPayload(PayloadConfig(), "SYS", "用克劳德写", includeThinking: true,
            new[] { "Claude", "VoiceTyper", "张三丰" }));
        Assert.Equal(plain.SkipLast(1), withGlossary.SkipLast(1));
        Assert.Equal("<glossary>Claude、VoiceTyper、张三丰</glossary>\n<asr_text>\n用克劳德写\n</asr_text>", withGlossary[^1]);

        var empty = JsonSerializer.Serialize(LlmCorrector.BuildPayload(PayloadConfig(), "SYS", "原文", false, Array.Empty<string>()));
        var legacy = JsonSerializer.Serialize(LlmCorrector.BuildPayload(PayloadConfig(), "SYS", "原文", false));
        Assert.Equal(legacy, empty);
    }

    // ─── 度量（VOC-10）────────────────────────────────────────

    [Fact]
    public void DecodeFieldsAppendedAtEnd()
    {
        var metrics = new DictationMetrics();
        Assert.EndsWith("decode_mode=- vocab_hits=- decode_ms=-", metrics.SummaryLine());
        metrics.Timings.DecodeMode = DecodeStats.DecodeMode.Beam;
        metrics.Timings.HotwordHits = 2;
        metrics.Timings.DecodeTicks = System.Diagnostics.Stopwatch.Frequency * 13 / 10_000; // 1.3ms
        Assert.EndsWith("decode_mode=beam vocab_hits=2 decode_ms=1.3", metrics.SummaryLine());
    }
}
