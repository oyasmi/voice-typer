using VoiceTyper.Asr.Hotword;
using VoiceTyper.Core;
using VoiceTyper.UI;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// VOC-9：设置页「词汇」不依赖 WinForms 的逻辑（行 ↔ 配置、状态列、从最近结果添加），与 macOS
/// <c>SettingsViewModelVocabularyTests</c> 同一组语义。界面线程上的表格行为见 <c>SetupFormTests</c>。
/// 文案可能被并行的语言测试切换，这里只断言种类与结构，不断言提示文字。
/// </summary>
public class VocabularySettingsTests
{
    [Fact]
    public void RowsRoundTripAndAcceptEveryAliasSeparator()
    {
        var row = new VocabularyRow(" Claude ", "克劳德, 可劳德，科劳德、 ");
        Assert.Equal(new VocabularyEntry(" Claude ", "克劳德", "可劳德", "科劳德"), row.ToEntry());

        var restored = VocabularyRow.FromEntry(new VocabularyEntry("Claude", "克劳德", "可劳德"));
        Assert.Equal(new[] { "克劳德", "可劳德" }, restored.ToEntry().Aliases);
    }

    [Fact]
    public void BuildConfigNormalizesLikeSavedConfig()
    {
        var rows = new[]
        {
            new VocabularyRow("张三丰", ""),
            new VocabularyRow("  ", "空白新行"),
            new VocabularyRow("claude", "克劳德"),
            new VocabularyRow("Claude", "可劳德"),
        };
        var config = VocabularyRows.BuildConfig(VocabularyBias.Strong, rows);
        Assert.Equal(VocabularyBias.Strong, config.BiasValue);
        Assert.Equal(new[] { new VocabularyEntry("张三丰"), new VocabularyEntry("claude", "克劳德", "可劳德") }, config.Entries);
        // 与落盘时的规整一致：载入已保存的配置再原样读回，不能被判为「有改动」。
        var reloaded = VocabularyRows.BuildConfig(config.BiasValue, VocabularyRows.FromConfig(config));
        Assert.True(reloaded.ContentEquals(config));
    }

    [Fact]
    public void StatusColumnDistinguishesUnloadedUnsavedAndCompiled()
    {
        Assert.Equal(VocabularyRowStatus.Kind.Empty, VocabularyRows.Status("  ", null, VocabularyBias.Standard).Status);
        Assert.Equal(VocabularyRowStatus.Kind.Unknown, VocabularyRows.Status("李", null, VocabularyBias.Standard).Status);

        var report = new HotwordCompileReport
        {
            Entries = new[]
            {
                ("李", HotwordCompileReport.Status.TooShort),
                ("张三丰", HotwordCompileReport.Status.Biased),
                ("🙂", HotwordCompileReport.Status.UnsupportedCharacters),
            },
        };
        Assert.Equal(VocabularyRowStatus.Kind.Warning, VocabularyRows.Status("李", report, VocabularyBias.Standard).Status);
        Assert.Equal(VocabularyRowStatus.Kind.Active, VocabularyRows.Status(" 张三丰 ", report, VocabularyBias.Standard).Status);
        Assert.Equal(VocabularyRowStatus.Kind.Warning, VocabularyRows.Status("🙂", report, VocabularyBias.Light).Status);

        // 有报告却查不到：还没保存的草稿行，提示「保存后检查」而不是「模型加载后检查」。
        var unsaved = VocabularyRows.Status("还没保存", report, VocabularyBias.Standard);
        Assert.Equal(VocabularyRowStatus.Kind.Unknown, unsaved.Status);
        Assert.NotEqual(VocabularyRows.Status("还没保存", null, VocabularyBias.Standard).Reason, unsaved.Reason);

        // 已保存的强度为「关」：不建图，状态列不显示 ✓。
        Assert.Equal(VocabularyRowStatus.Kind.Unknown, VocabularyRows.Status("张三丰", report, VocabularyBias.Off).Status);
        Assert.Equal("✓", VocabularyRows.Status("张三丰", report, VocabularyBias.Strong).Symbol);
    }

    [Fact]
    public void AddFromHistoryAppendsOrMergesIntoExistingTerm()
    {
        var rows = new List<VocabularyRow> { new("Claude", "克劳德") };

        Assert.Equal(0, VocabularyRows.AddFromHistory(rows, " claude ", "可劳德"));
        Assert.Single(rows);
        Assert.Equal(new[] { "克劳德", "可劳德" }, rows[0].ToEntry().Aliases);

        Assert.Equal(1, VocabularyRows.AddFromHistory(rows, "张三丰", "张三峰"));
        Assert.Equal(new VocabularyEntry("张三丰", "张三峰"), rows[1].ToEntry());

        // 只差大小写的片段不作为 alias（替换层本来就不区分大小写）。
        Assert.Equal(2, VocabularyRows.AddFromHistory(rows, "VoiceTyper", "voicetyper"));
        Assert.Empty(rows[2].ToEntry().Aliases);

        Assert.Null(VocabularyRows.AddFromHistory(rows, "  ", "片段"));
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void AddFromHistoryRespectsEntryLimit()
    {
        var rows = Enumerable.Range(0, VocabularyConfig.MaxEntries).Select(i => new VocabularyRow($"词{i}", "")).ToList();
        Assert.Null(VocabularyRows.AddFromHistory(rows, "新词", "新次"));
        Assert.Equal(VocabularyConfig.MaxEntries, rows.Count);
        // 并入已有词条不受上限影响。
        Assert.Equal(0, VocabularyRows.AddFromHistory(rows, "词0", "次0"));
    }
}
