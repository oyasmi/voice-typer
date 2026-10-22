import Foundation

/// 自定义词汇的确定性替换（VOC-7）：把识别结果里的 alias 换成 term，并把 term 的大小写、空格、连字符变体
/// 统一成 term 的写法（`voice typer` → `VoiceTyper`）。由 `VocabularyConfig` 编译出的不可变值，`apply` 是纯函数。
///
/// 匹配规则（§6.1）：
/// - 汉字 / 假名 / 谚文等非 ASCII 字符逐字匹配；与 ASCII 段交界处允许 0–1 个空白。
/// - ASCII 字母不区分大小写；写法首尾若是 ASCII 字母数字，要求原文对应位置外侧不是 `[A-Za-z0-9]`（词边界）。
/// - ASCII 段内「小写→大写」「字母↔数字」处、写法里 ASCII 段之间的空格或连字符处，允许 0–1 个空白或连字符。
///
/// 选取与幂等（§6.2）：一轮内在原文上找出全部匹配，按起点升序、同起点取最长、不重叠贪心选取，一次性拼出结果，
/// 替换产物不参与本轮匹配。同起点同长度时 term 自身的模式优先于 alias，所以已是正确写法的文本永远保持原样
/// （`Claude Code` 不会被 alias `Claude` 改成 `Claude Code Code`）。替换会让新的相邻关系出现
/// （`克劳德code` → `Claude code`，下一轮才能被 `Claude Code` 统一），因此 `apply` 重复整轮直到结果不再变化
/// （最多 `maxRounds` 轮），保证 `apply(apply(x)) == apply(x)`。
struct VocabularyReplacer: Sendable {
    static let empty = VocabularyReplacer(VocabularyConfig())
    static let maxRounds = 4

    /// 全部 term，按词汇表顺序；供 LLM 词表（VOC-8）使用。
    let terms: [String]
    private let patterns: [Pattern]
    /// 模式首字符（ASCII 已转小写）→ 以它开头的模式下标，按优先级排列。
    private let patternsByFirstScalar: [UInt32: [Int]]

    var isEmpty: Bool { patterns.isEmpty }

    init(_ vocabulary: VocabularyConfig) {
        var patterns: [Pattern] = []
        for entry in vocabulary.entries {
            let target = Array(entry.term.unicodeScalars)
            guard !target.isEmpty else { continue }
            for (index, surface) in ([entry.term] + entry.aliases).enumerated() {
                if let pattern = Pattern(surface: surface, target: target, isTerm: index == 0) {
                    patterns.append(pattern)
                }
            }
        }
        var byFirst: [UInt32: [Int]] = [:]
        for (index, pattern) in patterns.enumerated() {
            guard case .literal(let first) = pattern.units[0] else { continue }
            byFirst[first.value, default: []].append(index)
        }
        self.terms = vocabulary.entries.map(\.term).filter { !$0.isEmpty }
        self.patterns = patterns
        self.patternsByFirstScalar = byFirst
    }

    func apply(_ text: String) -> String {
        guard !isEmpty, !text.isEmpty else { return text }
        var current = Array(text.unicodeScalars)
        for _ in 0..<Self.maxRounds {
            let next = applyOnce(current)
            if next == current { break }
            current = next
        }
        return String(String.UnicodeScalarView(current))
    }

    // MARK: - 一轮替换

    private func applyOnce(_ text: [Unicode.Scalar]) -> [Unicode.Scalar] {
        var output: [Unicode.Scalar] = []
        output.reserveCapacity(text.count)
        var index = 0
        while index < text.count {
            guard let match = longestMatch(in: text, at: index) else {
                output.append(text[index])
                index += 1
                continue
            }
            let end = match.end
            let target = patterns[match.pattern].target
            // CJK 写法换成英文 term 时不能和相邻的英文粘在一起（`克劳德code` → `Claude code`）。
            if let previous = output.last, Self.isASCIIAlphanumeric(previous), Self.isASCIIAlphanumeric(target[0]) {
                output.append(" ")
            }
            output.append(contentsOf: target)
            if end < text.count, Self.isASCIIAlphanumeric(text[end]), Self.isASCIIAlphanumeric(target[target.count - 1]) {
                output.append(" ")
            }
            index = end
        }
        return output
    }

    /// 同起点取最长；同长度时 term 模式优先于 alias 模式，其余按词汇表顺序。
    private func longestMatch(in text: [Unicode.Scalar], at start: Int) -> (pattern: Int, end: Int)? {
        guard let candidates = patternsByFirstScalar[Self.folded(text[start]).value] else { return nil }
        var best: (pattern: Int, end: Int)?
        for index in candidates {
            guard let end = patterns[index].match(in: text, at: start) else { continue }
            guard let current = best else {
                best = (index, end)
                continue
            }
            if end > current.end || (end == current.end && patterns[index].isTerm && !patterns[current.pattern].isTerm) {
                best = (index, end)
            }
        }
        return best
    }

    // MARK: - 模式

    fileprivate enum Unit: Equatable {
        /// 一个字符；ASCII 字母已转小写，与原文比较时同样转小写。
        case literal(Unicode.Scalar)
        /// 0–1 个空白；`allowsHyphen` 时也可以是连字符。
        case optionalSeparator(allowsHyphen: Bool)
    }

    fileprivate struct Pattern: Sendable {
        let units: [Unit]
        let target: [Unicode.Scalar]
        let isTerm: Bool
        let needsLeadingBoundary: Bool
        let needsTrailingBoundary: Bool

        init?(surface: String, target: [Unicode.Scalar], isTerm: Bool) {
            let scalars = Array(surface.unicodeScalars)
            var units: [Unit] = []
            var index = 0
            // 上一个非空白字符（原样，未转小写），用来决定两字符之间允许什么分隔。
            var previous: Unicode.Scalar?
            var pendingSpace = false
            while index < scalars.count {
                let scalar = scalars[index]
                if scalar.properties.isWhitespace {
                    pendingSpace = true
                    index += 1
                    continue
                }
                let isASCII = scalar.isASCII
                if let previous, case .literal? = units.last {
                    let previousWasASCII = previous.isASCII
                    if scalar == "-", index + 1 < scalars.count,
                       VocabularyReplacer.isASCIIAlphanumeric(previous),
                       VocabularyReplacer.isASCIIAlphanumeric(scalars[index + 1]) {
                        // 两个 ASCII 字母数字之间的连字符是分隔符，不是字面量：`GPT-4o` 也匹配 `GPT 4o`、`GPT4o`。
                        units.append(.optionalSeparator(allowsHyphen: true))
                        pendingSpace = false
                        index += 1
                        continue
                    }
                    if pendingSpace {
                        units.append(.optionalSeparator(allowsHyphen: previousWasASCII && isASCII))
                    } else if previousWasASCII != isASCII {
                        units.append(.optionalSeparator(allowsHyphen: false))
                    } else if HotwordCompiler.splitPoints([previous, scalar]) == [1] {
                        units.append(.optionalSeparator(allowsHyphen: true))
                    }
                }
                pendingSpace = false
                units.append(.literal(VocabularyReplacer.folded(scalar)))
                previous = scalar
                index += 1
            }
            guard case .literal(let first)? = units.first, case .literal(let last)? = units.last else { return nil }
            self.units = units
            self.target = target
            self.isTerm = isTerm
            self.needsLeadingBoundary = VocabularyReplacer.isASCIIAlphanumeric(first)
            self.needsTrailingBoundary = VocabularyReplacer.isASCIIAlphanumeric(last)
        }

        /// 在 `start` 处尝试匹配，返回匹配终点（不含）；多种走法时取最长。
        func match(in text: [Unicode.Scalar], at start: Int) -> Int? {
            if needsLeadingBoundary, start > 0, VocabularyReplacer.isASCIIAlphanumeric(text[start - 1]) {
                return nil
            }
            return matchUnits(from: 0, in: text, at: start)
        }

        private func matchUnits(from unitIndex: Int, in text: [Unicode.Scalar], at position: Int) -> Int? {
            guard unitIndex < units.count else {
                if needsTrailingBoundary, position < text.count, VocabularyReplacer.isASCIIAlphanumeric(text[position]) {
                    return nil
                }
                return position
            }
            switch units[unitIndex] {
            case .literal(let expected):
                guard position < text.count, VocabularyReplacer.folded(text[position]) == expected else { return nil }
                return matchUnits(from: unitIndex + 1, in: text, at: position + 1)
            case .optionalSeparator(let allowsHyphen):
                var best = matchUnits(from: unitIndex + 1, in: text, at: position)
                if position < text.count,
                   text[position].properties.isWhitespace || (allowsHyphen && text[position] == "-"),
                   let consumed = matchUnits(from: unitIndex + 1, in: text, at: position + 1) {
                    best = max(best ?? consumed, consumed)
                }
                return best
            }
        }
    }

    // MARK: - 字符分类

    fileprivate static func isASCIIAlphanumeric(_ scalar: Unicode.Scalar) -> Bool {
        HotwordCompiler.isASCIIAlphanumeric(scalar)
    }

    fileprivate static func folded(_ scalar: Unicode.Scalar) -> Unicode.Scalar {
        HotwordCompiler.asciiLowercased(scalar)
    }
}
