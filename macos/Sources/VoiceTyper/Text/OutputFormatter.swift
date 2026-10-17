import Foundation

/// 识别文本上屏前的确定性格式化。位于 `TextPostprocessor`（识别契约）之后，
/// 不参与金标准比对；规则与 `Resources/correction.md` 第 7 条一致，
/// 与 Windows 共用 `Fixtures/text_format_cases.json`（R8-F2）。
enum OutputFormatter {
    /// 中文与英文单词之间补一个半角空格。只增不删，幂等。
    ///
    /// 规则（按 Unicode 标量做一次线性扫描，先切出 ASCII 词元再看两侧边界）：
    /// - 汉字集合：U+3400–4DBF、U+4E00–9FFF、U+F900–FAFF；全角标点不属于它，因此标点前后永不加空格。
    /// - ASCII 词元 = 连续的 ASCII 可见字符；至少含一个字母才算「英文单词」，纯数字 / 符号不算。
    /// - 汉字后接词元：词元首字符 ∈ `[A-Za-z0-9]` 才加；词元后接汉字：词元尾字符 ∈ `[A-Za-z0-9+#)\]]` 才加。
    /// - 含假名或谚文的整段原样返回：日韩排版不用这条规则，局部套用只会更乱。
    static func applyCJKLatinSpacing(_ text: String) -> String {
        let scalars = Array(text.unicodeScalars)
        guard !scalars.isEmpty, !scalars.contains(where: isKanaOrHangul) else { return text }

        var output = String.UnicodeScalarView()
        var index = 0
        while index < scalars.count {
            let scalar = scalars[index]
            guard isASCIIVisible(scalar) else {
                output.append(scalar)
                index += 1
                continue
            }

            var end = index
            while end < scalars.count, isASCIIVisible(scalars[end]) { end += 1 }
            let token = scalars[index..<end]
            defer { index = end }

            guard token.contains(where: isASCIILetter) else {
                output.append(contentsOf: token)
                continue
            }
            if index > 0, isHan(scalars[index - 1]), isAlphanumeric(token.first!) {
                output.append(" ")
            }
            output.append(contentsOf: token)
            if end < scalars.count, isHan(scalars[end]), isWordTail(token.last!) {
                output.append(" ")
            }
        }
        return String(output)
    }

    /// 上屏前的完整格式化链。R8-F1（自定义词汇）/ R8-F3（衔接空格）会在这里扩展。
    static func formatForOutput(_ text: String) -> String {
        applyCJKLatinSpacing(text)
    }

    // MARK: - 字符分类

    private static func isHan(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xF900...0xFAFF: return true
        default: return false
        }
    }

    private static func isKanaOrHangul(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x3040...0x30FF, 0x1100...0x11FF, 0x3130...0x318F, 0xAC00...0xD7A3: return true
        default: return false
        }
    }

    private static func isASCIIVisible(_ scalar: Unicode.Scalar) -> Bool {
        (0x21...0x7E).contains(scalar.value)
    }

    private static func isASCIILetter(_ scalar: Unicode.Scalar) -> Bool {
        (0x41...0x5A).contains(scalar.value) || (0x61...0x7A).contains(scalar.value)
    }

    private static func isAlphanumeric(_ scalar: Unicode.Scalar) -> Bool {
        isASCIILetter(scalar) || (0x30...0x39).contains(scalar.value)
    }

    private static func isWordTail(_ scalar: Unicode.Scalar) -> Bool {
        isAlphanumeric(scalar) || "+#)]".unicodeScalars.contains(scalar)
    }
}
