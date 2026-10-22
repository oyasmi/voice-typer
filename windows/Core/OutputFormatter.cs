using System.Text;

namespace VoiceTyper.Core;

/// <summary>
/// 识别文本上屏前的确定性格式化（R8-F2）。位于 <see cref="Asr.TextPostprocessor"/>（识别契约）之后，
/// 不参与金标准比对。C# 直译自 <c>macos/Sources/VoiceTyper/Text/OutputFormatter.swift</c>；
/// 两平台规则只能一起改，用例共用 <c>macos/Tests/VoiceTyperTests/Fixtures/text_format_cases.json</c>
/// （见 <c>OutputFormatterTests</c>），规则细节见 <c>macos/docs/UX_R8_SPEC.md</c> R8-F2。
/// </summary>
internal static class OutputFormatter
{
    /// <summary>
    /// 中文与英文单词之间补一个半角空格。只增不删，幂等。
    /// <list type="bullet">
    /// <item>汉字集合：U+3400–4DBF、U+4E00–9FFF、U+F900–FAFF；全角标点不属于它，因此标点前后永不加空格。</item>
    /// <item>ASCII 词元 = 连续的 ASCII 可见字符（U+0021–007E）；至少含一个字母才算「英文单词」，纯数字 / 符号不算。</item>
    /// <item>汉字后接词元：词元首字符 ∈ [A-Za-z0-9] 才加；词元后接汉字：词元尾字符 ∈ [A-Za-z0-9+#)\]] 才加。</item>
    /// <item>含假名或谚文的整段原样返回：日韩排版不用这条规则，局部套用只会更乱。</item>
    /// </list>
    /// 汉字、假名、谚文与 ASCII 都在 BMP 内，按 UTF-16 码元扫描与 Swift 按 Unicode 标量扫描等价
    /// （代理项对不属于任何一类，两边都当作「其他字符」）。
    /// </summary>
    public static string ApplyCjkLatinSpacing(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var c in text)
        {
            if (IsKanaOrHangul(c)) return text;
        }

        var output = new StringBuilder(text.Length + 8);
        var index = 0;
        while (index < text.Length)
        {
            var c = text[index];
            if (!IsAsciiVisible(c))
            {
                output.Append(c);
                index++;
                continue;
            }

            var end = index;
            var hasLetter = false;
            while (end < text.Length && IsAsciiVisible(text[end]))
            {
                hasLetter |= IsAsciiLetter(text[end]);
                end++;
            }

            if (!hasLetter)
            {
                output.Append(text, index, end - index);
                index = end;
                continue;
            }

            if (index > 0 && IsHan(text[index - 1]) && IsAlphanumeric(text[index])) output.Append(' ');
            output.Append(text, index, end - index);
            if (end < text.Length && IsHan(text[end]) && IsWordTail(text[end - 1])) output.Append(' ');
            index = end;
        }
        return output.ToString();
    }

    /// <summary>
    /// 上屏前的完整格式化链（与 macOS 相同）：
    /// <code>
    /// CTC 解码（VOC-5）→ TextPostprocessor（识别契约，不变）
    ///   → VocabularyReplacer.Apply        —— LocalAsrSession.CompleteWithAsrText，LLM 之前
    ///   → LLM 纠错（附词表，VOC-8）
    ///   → FormatForOutput：
    ///         VocabularyReplacer.Apply    —— LLM 可能改回去；幂等，未启用 LLM 时是空操作
    ///       → ApplyCjkLatinSpacing（R8-F2）
    /// </code>
    /// 替换必须在加空格之前：<c>用克劳德写</c> 先变成 <c>用Claude写</c>，才能再补成 <c>用 Claude 写</c>。
    /// 预览不经过 CompleteWithAsrText，只在这里替换一次。
    /// </summary>
    public static string FormatForOutput(string text, VocabularyReplacer vocabulary) =>
        ApplyCjkLatinSpacing(vocabulary.Apply(text));

    private static bool IsHan(char c) =>
        (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF);

    private static bool IsKanaOrHangul(char c) =>
        (c >= 0x3040 && c <= 0x30FF) || (c >= 0x1100 && c <= 0x11FF) ||
        (c >= 0x3130 && c <= 0x318F) || (c >= 0xAC00 && c <= 0xD7A3);

    private static bool IsAsciiVisible(char c) => c >= 0x21 && c <= 0x7E;

    private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    private static bool IsAlphanumeric(char c) => IsAsciiLetter(c) || (c >= '0' && c <= '9');

    private static bool IsWordTail(char c) => IsAlphanumeric(c) || c is '+' or '#' or ')' or ']';
}
