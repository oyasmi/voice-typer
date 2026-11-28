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

    /// 词汇替换后的一致性过滤（HINT-5.3 / HINT-D7）：
    /// 1. `surface` 不再出现在替换后的文本中（已被确定性替换）→ 丢弃并计入 `droppedByVocab`；
    /// 2. `surface` 精确等于词汇表中任一 `term`（不区分大小写）→ 丢弃并计入 `droppedByVocab`；
    /// 3. 备选逐项过一遍 `apply` 规整 alias，去重并剔除与 `surface` 相同项；
    /// 4. 防御性过滤含 `<`、`>`、`\n`、`\r` 的异常字符串。
    func reconcileHints(
        _ hints: [AcousticHint],
        inReplacedText text: String
    ) -> (active: [AcousticHint], droppedByVocab: Int) {
        guard !hints.isEmpty else { return ([], 0) }
        let loweredTerms = Set(terms.map { $0.lowercased() })
        var active: [AcousticHint] = []
        active.reserveCapacity(hints.count)
        var droppedByVocab = 0

        for hint in hints {
            if Self.containsUnsafeTagChars(hint.surface) { continue }
            if !text.contains(hint.surface) || loweredTerms.contains(hint.surface.lowercased()) {
                droppedByVocab += 1
                continue
            }
            var seen = Set<String>()
            var cleanedAlts: [String] = []
            cleanedAlts.reserveCapacity(hint.alternatives.count)
            for alt in hint.alternatives {
                let normalized = apply(alt)
                guard !normalized.isEmpty,
                      !Self.containsUnsafeTagChars(normalized),
                      normalized != hint.surface,
                      seen.insert(normalized).inserted
                else { continue }
                cleanedAlts.append(normalized)
            }
            guard !cleanedAlts.isEmpty else { continue }
            active.append(AcousticHint(
                surface: hint.surface,
                alternatives: cleanedAlts,
                bestMargin: hint.bestMargin
            ))
        }
        return (active, droppedByVocab)
    }

    private static func containsUnsafeTagChars(_ value: String) -> Bool {
        value.contains("<") || value.contains(">") || value.contains("\n") || value.contains("\r")
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
        ///
        /// 使用确定性 NFA 位置集合按 unit 逐步推进（S-2）：每步只可能保持原位或前进 1 格，
        /// 候选终点始终严格递增且自然去重，避免可选分隔符与字面连字符交错时产生指数级递归回溯，
        /// 最坏复杂度 O(|units| × |match|)。
        func match(in text: [Unicode.Scalar], at start: Int) -> Int? {
            if needsLeadingBoundary, start > 0, VocabularyReplacer.isASCIIAlphanumeric(text[start - 1]) {
                return nil
            }
            var positions = [start]
            var nextPositions: [Int] = []
            for unit in units {
                nextPositions.removeAll(keepingCapacity: true)
                switch unit {
                case .literal(let expected):
                    for pos in positions where pos < text.count && VocabularyReplacer.folded(text[pos]) == expected {
                        nextPositions.append(pos + 1)
                    }
                case .optionalSeparator(let allowsHyphen):
                    for pos in positions {
                        if nextPositions.last != pos {
                            nextPositions.append(pos)
                        }
                        if pos < text.count,
                           text[pos].properties.isWhitespace || (allowsHyphen && text[pos] == "-") {
                            nextPositions.append(pos + 1)
                        }
                    }
                }
                if nextPositions.isEmpty {
                    return nil
                }
                swap(&positions, &nextPositions)
            }
            for pos in positions.reversed() {
                if needsTrailingBoundary, pos < text.count, VocabularyReplacer.isASCIIAlphanumeric(text[pos]) {
                    continue
                }
                return pos
            }
            return nil
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
