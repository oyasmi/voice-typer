using System.Text.RegularExpressions;

namespace VoiceTyper.Asr;

/// <summary>
/// SenseVoice CTC 解码后的文本清洗。C# 直译自
/// <c>macos/Sources/VoiceTyper/ASR/TextPostprocessor.swift</c>，逐条对齐
/// <c>server/voice_typer_server/recognizer.py</c> 的 <c>SenseVoiceRecognizer._postprocess</c>，
/// 两侧规则必须保持一致。
/// </summary>
internal static class TextPostprocessor
{
    /// <summary>SenseVoice 把语言/情感/事件/文本规整信息以 &lt;|xxx|&gt; 形式混在 CTC 输出里，剥离之。</summary>
    private static readonly Regex RichTagPattern = new(@"<\|[^|]*\|>", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    // CJK 范围：符号区 3000-303F、扩展A 3400-4DBF、基本区 4E00-9FFF、全角 FF00-FFEF
    // 对应 Python 侧 _CJK = "　-〿㐀-䶿一-鿿＀-￯"
    private const string CjkClass = "　-〿㐀-䶿一-鿿＀-￯";

    /// <summary>SentencePiece 的词首标记 ▁ 会在中英交界处留下不一致的空格；统一去掉与 CJK 相邻的空格。</summary>
    private static readonly Regex SpaceAfterCjkPattern =
        new($"(?<=[{CjkClass}])\\s+(?=[0-9A-Za-z])", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforeCjkPattern =
        new($"(?<=[0-9A-Za-z])\\s+(?=[{CjkClass}])", RegexOptions.Compiled);

    /// <summary>
    /// 判断结果里是否有"实字"：0-9A-Za-z + CJK 基本区 + 假名 + 谚文音节。
    /// 对应 Python 侧 _SUBSTANTIVE_RE = "[0-9A-Za-z一-鿿぀-ヿ가-힯]"
    /// </summary>
    private static readonly Regex SubstantivePattern =
        new(@"[0-9A-Za-z一-鿿぀-ヿ가-힣]", RegexOptions.Compiled);

    /// <summary>输入接近静音时 SenseVoice 常吐一个孤立的句号，这种纯标点结果没有任何上屏价值，直接丢弃。</summary>
    public static string Process(string raw)
    {
        var text = raw.Replace("▁", " ");
        text = RichTagPattern.Replace(text, "");
        text = WhitespacePattern.Replace(text, " ").Trim();
        text = SpaceAfterCjkPattern.Replace(text, "");
        text = SpaceBeforeCjkPattern.Replace(text, "");
        return SubstantivePattern.IsMatch(text) ? text : "";
    }
}
