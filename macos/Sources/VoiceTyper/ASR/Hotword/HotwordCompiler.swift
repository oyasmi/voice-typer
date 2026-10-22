import Foundation

/// 词汇编译报告（VOC-3）：逐条给出识别层的生效状态，供设置页「状态」列显示。
/// 报告里有词条文本，只在内存里交给界面，**不得写入日志**（VOC-D9）。
struct HotwordCompileReport: Equatable, Sendable {
    enum Status: String, Sendable {
        /// 至少一条 token 序列进入上下文图。
        case biased
        /// 写法只有 1 个 token（单个汉字、单个词片），不参与偏置，只做替换（VOC-D6）。
        case tooShort = "too_short"
        /// 含词表里没有的字符（生僻扩展区汉字、emoji、全角符号等）。
        case unsupportedCharacters = "unsupported"
        /// 序列总数已达上限，本条没能入图。
        case truncated
    }

    struct Entry: Equatable, Sendable {
        let term: String
        let status: Status
    }

    var entries: [Entry]
    /// 入图的 token 序列总数。
    var sequenceCount: Int
    var compileNanos: UInt64

    func status(forTerm term: String) -> Status? {
        let key = term.lowercased()
        return entries.first { $0.term.lowercased() == key }?.status
    }

    func count(of status: Status) -> Int {
        entries.reduce(0) { $0 + ($1.status == status ? 1 : 0) }
    }
}

/// 模型词表的字符级前缀树，用来把热词写法切成 token（VOC-3 步骤 2）。
/// 只收普通 token：排除 id 0–2（`<unk>` `<s>` `</s>`）与 `<|…|>` 特殊 token。
/// 一个引擎只建一次（约 25k token），之后不可变。
final class TokenTrie: Sendable {
    /// `(节点 << 21) | 标量值` → 子节点。Unicode 标量最大 0x10FFFF，21 位放得下。
    private let edges: [Int: Int32]
    /// 节点 → 以该节点结尾的 token id；不是 token 结尾为 -1。
    private let tokenAtNode: [Int32]

    init(tokens: [String]) {
        var edges: [Int: Int32] = [:]
        var tokenAtNode: [Int32] = [-1]
        for (id, token) in tokens.enumerated() where !Self.isExcluded(id: id, token: token) {
            var node = 0
            for scalar in token.unicodeScalars {
                let key = node << 21 | Int(scalar.value)
                if let next = edges[key] {
                    node = Int(next)
                } else {
                    let next = tokenAtNode.count
                    tokenAtNode.append(-1)
                    edges[key] = Int32(next)
                    node = next
                }
            }
            // 词表里同一字符串出现两次时保留第一个 id。
            if tokenAtNode[node] < 0 {
                tokenAtNode[node] = Int32(id)
            }
        }
        self.edges = edges
        self.tokenAtNode = tokenAtNode
    }

    static func isSpecial(_ token: String) -> Bool {
        token.hasPrefix("<|") && token.hasSuffix("|>")
    }

    static func isExcluded(id: Int, token: String) -> Bool {
        id <= 2 || token.isEmpty || isSpecial(token)
    }

    /// token 数最少的前 `limit` 优切分；token 数相同时，逐段比较片段长度，先出现更长片段者优先
    /// （近似 BPE 的贪心合并）。有字符在词表里找不到时返回空数组。
    func bestSegmentations(of text: [Unicode.Scalar], limit: Int = 2) -> [[Int]] {
        guard !text.isEmpty else { return [] }
        let n = text.count
        // best[i] 是 text[i...] 的前 limit 优切分。
        var best = [[Segmentation]](repeating: [], count: n + 1)
        best[n] = [Segmentation(ids: [], lengths: [])]
        for start in stride(from: n - 1, through: 0, by: -1) {
            var candidates: [Segmentation] = []
            var node = 0
            var end = start
            while end < n, let next = edges[node << 21 | Int(text[end].value)] {
                node = Int(next)
                end += 1
                let id = tokenAtNode[node]
                guard id >= 0 else { continue }
                for rest in best[end] {
                    candidates.append(Segmentation(ids: [Int(id)] + rest.ids, lengths: [end - start] + rest.lengths))
                }
            }
            candidates.sort(by: Segmentation.ranksBefore)
            best[start] = Array(candidates.prefix(limit))
        }
        return best[0].map(\.ids)
    }

    private struct Segmentation {
        var ids: [Int]
        var lengths: [Int]

        static func ranksBefore(_ a: Segmentation, _ b: Segmentation) -> Bool {
            if a.ids.count != b.ids.count { return a.ids.count < b.ids.count }
            for (x, y) in zip(a.lengths, b.lengths) where x != y {
                return x > y
            }
            return false
        }
    }
}

/// 把词汇表编译成上下文图要用的 token 序列（VOC-3）。纯函数，可在任意线程调用。
enum HotwordCompiler {
    /// SentencePiece 的词首标记。
    static let wordBoundary: Unicode.Scalar = "\u{2581}"
    static let maxSequencesPerSurface = 8
    static let maxTotalSequences = 4000

    struct Output {
        var sequences: [[Int]]
        var report: HotwordCompileReport
    }

    static func compile(_ vocabulary: VocabularyConfig, trie: TokenTrie, now: MonotonicClock = systemMonotonicClock) -> Output {
        let startedAt = now()
        var collected: [[Int]] = []
        var entries: [HotwordCompileReport.Entry] = []
        entries.reserveCapacity(vocabulary.entries.count)

        for entry in vocabulary.entries {
            var added = false
            var hitLimit = false
            var sawTooShort = false
            for surface in [entry.term] + entry.aliases {
                switch sequences(for: surface, trie: trie) {
                case .unsupported:
                    break
                case .tooShort:
                    sawTooShort = true
                case .sequences(let found):
                    for sequence in found {
                        guard collected.count < maxTotalSequences else {
                            hitLimit = true
                            break
                        }
                        collected.append(sequence)
                        added = true
                    }
                }
            }
            let status: HotwordCompileReport.Status
            if added {
                status = .biased
            } else if hitLimit {
                status = .truncated
            } else if sawTooShort {
                status = .tooShort
            } else {
                status = .unsupportedCharacters
            }
            entries.append(.init(term: entry.term, status: status))
        }

        let report = HotwordCompileReport(
            entries: entries, sequenceCount: collected.count, compileNanos: now() &- startedAt
        )
        return Output(sequences: collected, report: report)
    }

    enum SurfaceResult: Equatable {
        case sequences([[Int]])
        case tooShort
        case unsupported
    }

    /// 一个写法的 token 序列：变体按 `rawVariants` 的优先级逐个切分，先取各自的最优切分，再补次优切分；
    /// 去重、丢弃单 token 序列后最多取 8 条。
    static func sequences(for surface: String, trie: TokenTrie) -> SurfaceResult {
        var best: [[Int]] = []
        var secondBest: [[Int]] = []
        for (index, variant) in rawVariants(of: surface).enumerated() {
            let found = trie.bestSegmentations(of: Array(variant.unicodeScalars))
            guard let first = found.first else { continue }
            // 原写法本身就是一个 token：模型能直接输出它，其余大小写变体的偏置只会把结果推向别的写法（VOC-D6）。
            if index == 0, first.count < 2 { return .tooShort }
            best.append(first)
            secondBest.append(contentsOf: found.dropFirst())
        }
        guard !best.isEmpty else { return .unsupported }

        var result: [[Int]] = []
        for sequence in best + secondBest where sequence.count >= 2 && !result.contains(sequence) {
            result.append(sequence)
            if result.count == maxSequencesPerSurface { break }
        }
        return result.isEmpty ? .tooShort : .sequences(result)
    }

    // MARK: - 原始串变体（VOC-3 步骤 1）

    private enum SegmentKind {
        /// 连续 ASCII 字母数字。
        case alphanumeric
        /// 连续 ASCII 标点（`. - + @ #` 等）。
        case punctuation
        /// 单个非 ASCII 字符：汉字、假名、谚文及其他。
        case wide
    }

    private struct Segment {
        let kind: SegmentKind
        let scalars: [Unicode.Scalar]
        /// 原写法里该段之前有没有空格。
        let spaceBefore: Bool
    }

    private enum LetterCase {
        case original
        case lower
        case capitalized
    }

    /// 写法 → 模型可能输出的原始 token 串，按可能性从高到低排列、已去重。
    ///
    /// - 词首 `▁`：空格之后的 ASCII 段一定带；串首、汉字之后的 ASCII 段两种都生成——
    ///   中文句子里的英文词前面通常是汉字，模型这时常常不输出 `▁`（`，audio▁driven`，实测）。
    /// - 拆词：ASCII 段在「小写→大写」「字母↔数字」处拆开，拆开处补 `▁`（`VoiceTyper` → `▁Voice▁Typer`）。
    /// - 大小写：原样、全小写、首字母大写。
    /// - 顺序：原样合写 → 原样拆词 → 小写拆词 → 小写合写 → 首字母大写（合写、拆词），每一种先带 `▁` 再不带。
    static func rawVariants(of surface: String) -> [String] {
        let segments = segment(surface)
        guard !segments.isEmpty else { return [] }
        let forms: [(split: Bool, letterCase: LetterCase)] = [
            (false, .original), (true, .original), (true, .lower),
            (false, .lower), (false, .capitalized), (true, .capitalized),
        ]
        var variants: [String] = []
        for form in forms {
            for leadingBoundary in [true, false] {
                let raw = render(segments, split: form.split, letterCase: form.letterCase, ambiguousBoundary: leadingBoundary)
                if !variants.contains(raw) {
                    variants.append(raw)
                }
            }
        }
        return variants
    }

    private static func segment(_ surface: String) -> [Segment] {
        var segments: [Segment] = []
        let scalars = Array(surface.unicodeScalars)
        var index = 0
        var spaceBefore = false
        while index < scalars.count {
            let scalar = scalars[index]
            if scalar.properties.isWhitespace {
                spaceBefore = true
                index += 1
                continue
            }
            let segmentKind = kind(of: scalar)
            var end = index + 1
            if segmentKind != .wide {
                while end < scalars.count, !scalars[end].properties.isWhitespace, kind(of: scalars[end]) == segmentKind {
                    end += 1
                }
            }
            segments.append(Segment(kind: segmentKind, scalars: Array(scalars[index..<end]), spaceBefore: spaceBefore))
            spaceBefore = false
            index = end
        }
        return segments
    }

    private static func kind(of scalar: Unicode.Scalar) -> SegmentKind {
        guard scalar.isASCII else { return .wide }
        return isASCIIAlphanumeric(scalar) ? .alphanumeric : .punctuation
    }

    private static func render(
        _ segments: [Segment], split: Bool, letterCase: LetterCase, ambiguousBoundary: Bool
    ) -> String {
        var output = String.UnicodeScalarView()
        var previous: SegmentKind?
        for segment in segments {
            guard segment.kind != .wide else {
                output.append(contentsOf: segment.scalars)
                previous = .wide
                continue
            }
            let ambiguous = previous == nil || (previous == .wide && !segment.spaceBefore)
            if segment.spaceBefore || (ambiguous && ambiguousBoundary) {
                output.append(wordBoundary)
            }
            if segment.kind == .alphanumeric {
                let pieces = casedPieces(segment.scalars, split: split, letterCase: letterCase)
                for (index, piece) in pieces.enumerated() {
                    if index > 0 { output.append(wordBoundary) }
                    output.append(contentsOf: piece)
                }
            } else {
                output.append(contentsOf: segment.scalars)
            }
            previous = segment.kind
        }
        return String(output)
    }

    /// ASCII 段内部可以拆开的位置：小写→大写、字母↔数字。
    static func splitPoints(_ scalars: [Unicode.Scalar]) -> [Int] {
        guard scalars.count > 1 else { return [] }
        return (1..<scalars.count).filter { index in
            let a = scalars[index - 1], b = scalars[index]
            return (isASCIILower(a) && isASCIIUpper(b))
                || (isASCIILetter(a) && isASCIIDigit(b))
                || (isASCIIDigit(a) && isASCIILetter(b))
        }
    }

    private static func casedPieces(
        _ scalars: [Unicode.Scalar], split: Bool, letterCase: LetterCase
    ) -> [[Unicode.Scalar]] {
        var pieces: [[Unicode.Scalar]] = [scalars]
        if split {
            let bounds = [0] + splitPoints(scalars) + [scalars.count]
            pieces = (0..<(bounds.count - 1)).map { Array(scalars[bounds[$0]..<bounds[$0 + 1]]) }
        }
        switch letterCase {
        case .original:
            return pieces
        case .lower:
            return pieces.map { $0.map(asciiLowercased) }
        case .capitalized:
            return pieces.map { piece in
                piece.enumerated().map { $0.offset == 0 ? asciiUppercased($0.element) : asciiLowercased($0.element) }
            }
        }
    }

    // MARK: - ASCII 字符分类（不受系统区域设置影响）

    static func isASCIIUpper(_ scalar: Unicode.Scalar) -> Bool { (0x41...0x5A).contains(scalar.value) }
    static func isASCIILower(_ scalar: Unicode.Scalar) -> Bool { (0x61...0x7A).contains(scalar.value) }
    static func isASCIIDigit(_ scalar: Unicode.Scalar) -> Bool { (0x30...0x39).contains(scalar.value) }
    static func isASCIILetter(_ scalar: Unicode.Scalar) -> Bool { isASCIIUpper(scalar) || isASCIILower(scalar) }
    static func isASCIIAlphanumeric(_ scalar: Unicode.Scalar) -> Bool { isASCIILetter(scalar) || isASCIIDigit(scalar) }

    static func asciiLowercased(_ scalar: Unicode.Scalar) -> Unicode.Scalar {
        isASCIIUpper(scalar) ? Unicode.Scalar(scalar.value + 0x20)! : scalar
    }

    static func asciiUppercased(_ scalar: Unicode.Scalar) -> Unicode.Scalar {
        isASCIILower(scalar) ? Unicode.Scalar(scalar.value - 0x20)! : scalar
    }
}
