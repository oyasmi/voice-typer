import Foundation

/// SenseVoice CTC 解码后的文本清洗。逐条对齐
/// `server/voice_typer_server/recognizer.py` 的 `SenseVoiceRecognizer._postprocess`
/// 与相关正则常量，两侧规则必须保持一致（见金标准测试 EndToEndRecognitionTests）。
enum TextPostprocessor {
    /// SenseVoice 把语言/情感/事件/文本规整信息以 <|xxx|> 形式混在 CTC 输出里，剥离之。
    private static let richTagPattern = try! NSRegularExpression(pattern: "<\\|[^|]*\\|>")
    private static let whitespacePattern = try! NSRegularExpression(pattern: "\\s+")

    // CJK 范围：符号区 　-〿、扩展A 㐀-䶿、基本区 一-鿿、全角 ＀-￯
    // 对应 Python 侧 _CJK = "　-〿㐀-䶿一-鿿＀-￯"
    private static let cjkClass = "\\u3000-\\u303F\\u3400-\\u4DBF\\u4E00-\\u9FFF\\uFF00-\\uFFEF"

    /// SentencePiece 的词首标记 ▁ 会在中英交界处留下不一致的空格；统一去掉与 CJK 相邻的空格。
    private static let spaceAfterCJKPattern = try! NSRegularExpression(
        pattern: "(?<=[\(cjkClass)])\\s+(?=[0-9A-Za-z])"
    )
    private static let spaceBeforeCJKPattern = try! NSRegularExpression(
        pattern: "(?<=[0-9A-Za-z])\\s+(?=[\(cjkClass)])"
    )

    /// 判断结果里是否有"实字"：0-9A-Za-z + CJK 基本区 + 假名 + 谚文音节。
    /// 对应 Python 侧 _SUBSTANTIVE_RE = "[0-9A-Za-z一-鿿぀-ヿ가-힯]"
    private static let substantivePattern = try! NSRegularExpression(
        pattern: "[0-9A-Za-z\\u4E00-\\u9FFF\\u3040-\\u30FF\\uAC00-\\uD7A3]"
    )

    /// 输入接近静音时 SenseVoice 常吐一个孤立的句号，这种纯标点结果没有任何上屏价值，直接丢弃。
    static func process(_ raw: String) -> String {
        var text = raw.replacingOccurrences(of: "▁", with: " ")
        text = replace(richTagPattern, in: text, with: "")
        text = replace(whitespacePattern, in: text, with: " ")
            .trimmingCharacters(in: .whitespaces)
        text = replace(spaceAfterCJKPattern, in: text, with: "")
        text = replace(spaceBeforeCJKPattern, in: text, with: "")
        return matches(substantivePattern, in: text) ? text : ""
    }

    private static func replace(_ regex: NSRegularExpression, in text: String, with template: String) -> String {
        let range = NSRange(text.startIndex..<text.endIndex, in: text)
        return regex.stringByReplacingMatches(in: text, range: range, withTemplate: template)
    }

    private static func matches(_ regex: NSRegularExpression, in text: String) -> Bool {
        let range = NSRange(text.startIndex..<text.endIndex, in: text)
        return regex.firstMatch(in: text, range: range) != nil
    }
}
