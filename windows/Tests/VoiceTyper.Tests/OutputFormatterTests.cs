using System.Text.Json;
using VoiceTyper.Core;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// R8-F2：中英文之间统一加空格。用例来自与 macOS 共享的 <c>text_format_cases.json</c>
/// （csproj 里 Link 自 <c>macos/Tests/VoiceTyperTests/Fixtures/</c>）。两平台规则只能一起改。
/// </summary>
public class OutputFormatterTests
{
    private sealed record Case(string Input, string Expected, string Note);

    private static List<Case> LoadCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "text_format_cases.json");
        // 必需夹具：缺失说明 csproj 的 Link 失效或夹具被移走，必须失败而不是静默跳过。
        Assert.True(File.Exists(path), $"共享夹具缺失：{path}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        return document.RootElement.GetProperty("cjk_latin_spacing").EnumerateArray()
            .Select(item => new Case(
                item.GetProperty("input").GetString()!,
                item.GetProperty("expected").GetString()!,
                item.GetProperty("note").GetString()!))
            .ToList();
    }

    [Fact]
    public void SharedFixtureCases()
    {
        var cases = LoadCases();
        Assert.True(cases.Count >= 19, "规格要求的初始用例不得减少");
        foreach (var item in cases)
        {
            Assert.True(
                OutputFormatter.ApplyCjkLatinSpacing(item.Input) == item.Expected,
                $"用例失败：{item.Note}（输入 {item.Input}）");
        }
    }

    /// <summary>对每条期望值再跑一次结果不变：LLM 已经加过空格的文本不能被二次改动。</summary>
    [Fact]
    public void FormattingIsIdempotent()
    {
        foreach (var item in LoadCases())
        {
            Assert.True(
                OutputFormatter.ApplyCjkLatinSpacing(item.Expected) == item.Expected,
                $"不幂等：{item.Note}");
        }
    }

    [Fact]
    public void SurrogatePairsAreTreatedAsOtherCharacters()
    {
        // 代理项对（如 𠀀 U+20000）不属于 BMP 汉字集合，与 Swift 按标量扫描的结果一致：不加空格。
        Assert.Equal("𠀀Python", OutputFormatter.ApplyCjkLatinSpacing("𠀀Python"));
    }
}
