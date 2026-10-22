import Accelerate
import Foundation

/// 一次解码的统计（VOC-10），只含数字与枚举，可进入度量日志。
struct DecodeStats: Equatable, Sendable {
    enum Mode: String, Sendable {
        case greedy
        case beam
    }

    var mode: Mode
    /// 最优路径上完成热词匹配的次数；贪心解码恒为 0。
    var hotwordHits: Int
    var decodeNanos: UInt64

    static let greedy = DecodeStats(mode: .greedy, hotwordHits: 0, decodeNanos: 0)
}

/// 带热词偏置的 CTC 前缀束搜索（VOC-5）。
///
/// 不做 log-softmax：每个假设每帧恰好消费一个 token 或 blank，对整帧减同一个常数不改变任何假设之间的排名，
/// 因此只减去帧最大值（`ℓ_t[k] = x_t[k] − max_k x_t[k]`）。偏置 b 与门限 G 都是同一帧内的分差。
/// 累加器用 `Double`。
///
/// 迭代顺序（假设按排名、候选按 token id 升序、新前缀按首次出现）全部固定，不依赖哈希表的遍历顺序，
/// 同一输入在任何进程、任何平台上的结果都相同（Windows 移植须保持同一顺序）。
enum CTCPrefixBeamSearch {
    struct Parameters: Equatable, Sendable {
        /// 每 token 偏置 b。
        var perTokenBias: Double
        /// 声学门限 G：与该帧最高分差距超过 G 的 token 偏置再大也不考虑。
        var acousticGate: Double
        var beamWidth = 8
        var topK = 10
        var maxScoredDepth = 4

        /// 三档初值（VOC-6）。**全部是估算**，需用评估工具和真实录音校准，见 `DESIGN.md` §11.7。
        static func preset(_ bias: VocabularyBias) -> Parameters? {
            switch bias {
            case .off: return nil
            case .light: return Parameters(perTokenBias: 1.0, acousticGate: 4.0)
            case .standard: return Parameters(perTokenBias: 1.5, acousticGate: 5.0)
            case .strong: return Parameters(perTokenBias: 2.5, acousticGate: 7.0)
            }
        }
    }

    /// 词表里哪些 token 可以出现在候选里。与具体热词无关，一个引擎算一次。
    struct TokenClasses: Sendable {
        /// 普通 token：不是 blank / `<s>` / `</s>`，也不是 `<|…|>`。
        let isRegular: [Bool]
        /// `<|…|>` 特殊 token（开头的语言 / 情感 / 事件 / ITN 标签）。
        let isSpecial: [Bool]

        init(tokens: [String]) {
            isSpecial = tokens.map(TokenTrie.isSpecial)
            isRegular = tokens.indices.map { !TokenTrie.isExcluded(id: $0, token: tokens[$0]) }
        }
    }

    enum DecodeError: Error {
        /// 某一帧之后束为空（logits 含 NaN 等）。
        case beamExhausted
        /// 前缀回溯不到根。
        case brokenPrefixChain
    }

    struct Result: Equatable {
        /// 最优路径的 token 序列，含开头的标签 token，与贪心解码合并、去 blank 后的形态相同。
        var tokens: [Int]
        var hotwordHits: Int
    }

    static let blank = 0

    /// - Parameter logits: 指向 `[numFrames * vocabSize]` 行主序展平数据的指针（与 `CTCDecoder` 相同的零拷贝入口）。
    static func decode(
        logits: UnsafePointer<Float>,
        numFrames: Int,
        vocabSize: Int,
        graph: ContextGraph,
        classes: TokenClasses,
        parameters: Parameters
    ) throws -> Result {
        var search = Search(vocabSize: vocabSize, graph: graph, classes: classes, parameters: parameters)
        var frame = 0

        // 开头的标签帧：只要 argmax 是 `<|…|>` 就按贪心取值、不展开，标签 token 作为初始前缀。
        var prefix = 0
        var lastTag = -1
        while frame < numFrames {
            let argmax = rowArgmax(logits + frame * vocabSize, count: vocabSize)
            guard classes.isSpecial[argmax] else { break }
            if argmax != lastTag {
                prefix = search.child(of: prefix, token: argmax)
                lastTag = argmax
            }
            frame += 1
        }
        var beam = [Hypothesis(prefix: prefix, pb: lastTag >= 0 ? -.infinity : 0, pnb: lastTag >= 0 ? 0 : -.infinity)]

        while frame < numFrames {
            beam = try search.advance(beam, row: logits + frame * vocabSize)
            frame += 1
        }
        return try search.best(of: beam)
    }

    // MARK: - 实现

    struct Hypothesis {
        var prefix: Int
        /// 以 blank 结尾的对数概率。
        var pb: Double
        /// 以非 blank 结尾的对数概率。
        var pnb: Double

        var total: Double { logAddExp(pb, pnb) }
    }

    /// 前缀以「节点表 + (父节点, token) → 子节点」表示，扩展时不复制 token 数组。
    private struct Search {
        static let scanBlock = 256

        let vocabSize: Int
        let graph: ContextGraph
        let classes: TokenClasses
        let parameters: Parameters

        var parent: [Int32] = [-1]
        var token: [Int32] = [-1]
        var state: [ContextGraph.State] = [ContextGraph.State()]
        var childIndex: [Int: Int32] = [:]

        // 每帧复用的缓冲。
        var slotByPrefix: [Int: Int] = [:]
        var slots: [Hypothesis] = []
        var frameCandidates: [Int] = []
        var hypothesisCandidates: [Int] = []
        var topCandidates: [(token: Int, score: Float)] = []

        init(vocabSize: Int, graph: ContextGraph, classes: TokenClasses, parameters: Parameters) {
            self.vocabSize = vocabSize
            self.graph = graph
            self.classes = classes
            self.parameters = parameters
        }

        mutating func child(of prefix: Int, token k: Int) -> Int {
            let key = prefix &* vocabSize &+ k
            if let existing = childIndex[key] { return Int(existing) }
            let index = parent.count
            parent.append(Int32(prefix))
            token.append(Int32(k))
            state.append(graph.step(state[prefix], token: k))
            childIndex[key] = Int32(index)
            return index
        }

        func lastToken(of prefix: Int) -> Int { Int(token[prefix]) }

        func rank(_ hypothesis: Hypothesis) -> Double {
            hypothesis.total + graph.bias(state[hypothesis.prefix])
        }

        mutating func advance(_ beam: [Hypothesis], row: UnsafePointer<Float>) throws -> [Hypothesis] {
            let gate = parameters.acousticGate
            let blankScore = row[0]
            var maxNonBlank: Float = -.infinity
            vDSP_maxv(row + 1, 1, &maxNonBlank, vDSP_Length(vocabSize - 1))
            let frameMax = max(blankScore, maxNonBlank)

            // 跳帧：没有任何非 blank token 落在门限内，所有假设只做 blank 扩展。与候选集规则完全一致，不是近似。
            if Double(blankScore) - Double(maxNonBlank) > gate {
                return beam.map { Hypothesis(prefix: $0.prefix, pb: $0.total, pnb: -.infinity) }
            }

            let peak = Double(frameMax)
            // 门限统一按 ℓ = x − max 计算：ℓ ≥ −G。
            let admits = { [isRegular = classes.isRegular] (k: Int) -> Bool in
                isRegular[k] && Double(row[k]) - peak >= -gate
            }
            collectFrameCandidates(row: row, peak: peak, admits: admits)

            slotByPrefix.removeAll(keepingCapacity: true)
            slots.removeAll(keepingCapacity: true)
            let blankDelta = Double(blankScore) - peak

            for hypothesis in beam {
                let total = hypothesis.total
                accumulate(hypothesis.prefix, pb: total + blankDelta, pnb: -.infinity)

                // 候选 = 本帧公共候选 ∪ 正在匹配的热词的下一个 token ∪ 前缀末 token（停留），都受门限约束。
                hypothesisCandidates.removeAll(keepingCapacity: true)
                hypothesisCandidates.append(contentsOf: frameCandidates)
                let graphState = state[hypothesis.prefix]
                // 停在根上的假设，「下一个 token」就是全部热词首 token，本帧公共候选里已经有了。
                if graphState.node != ContextGraph.root {
                    for next in graph.children(of: graphState) where admits(Int(next)) {
                        hypothesisCandidates.append(Int(next))
                    }
                }
                let last = lastToken(of: hypothesis.prefix)
                if last >= 0, admits(last) {
                    hypothesisCandidates.append(last)
                }
                hypothesisCandidates.sort()

                var previous = -1
                for k in hypothesisCandidates where k != previous {
                    previous = k
                    let delta = Double(row[k]) - peak
                    if k == last {
                        // 停留在同一个 token；重复 token 之间必须隔 blank 才算新发射。
                        accumulate(hypothesis.prefix, pb: -.infinity, pnb: hypothesis.pnb + delta)
                        accumulate(child(of: hypothesis.prefix, token: k), pb: -.infinity, pnb: hypothesis.pb + delta)
                    } else {
                        accumulate(child(of: hypothesis.prefix, token: k), pb: -.infinity, pnb: total + delta)
                    }
                }
            }

            var ranked: [(rank: Double, hypothesis: Hypothesis)] = []
            ranked.reserveCapacity(slots.count)
            for slot in slots where slot.total > -.infinity {
                ranked.append((rank(slot), slot))
            }
            guard !ranked.isEmpty else { throw DecodeError.beamExhausted }
            ranked.sort { a, b in
                if a.rank != b.rank { return a.rank > b.rank }
                return lexicographicallyPrecedes(a.hypothesis.prefix, b.hypothesis.prefix)
            }
            return ranked.prefix(parameters.beamWidth).map { $0.hypothesis }
        }

        /// 本帧与假设无关的候选：门限内分数最高的 K 个普通 token ∪ 门限内的热词首 token。升序、去重。
        mutating func collectFrameCandidates(row: UnsafePointer<Float>, peak: Double, admits: (Int) -> Bool) {
            let limit = parameters.topK
            // 先用单精度粗筛（向下取整保证不漏），再交给 `admits` 按双精度精确判定。
            let floor = Float(peak - parameters.acousticGate).nextDown
            topCandidates.removeAll(keepingCapacity: true)
            // 分块扫描：整块最大值都在门限外就跳过。门限内的 token 每帧通常只有个位数，
            // 逐个比较 25k 个分数是解码的主要开销，分块后降到每帧约 100 次向量化求最大值。
            var blockStart = 1
            while blockStart < vocabSize {
                let blockEnd = min(blockStart + Self.scanBlock, vocabSize)
                var blockMax: Float = -.infinity
                vDSP_maxv(row + blockStart, 1, &blockMax, vDSP_Length(blockEnd - blockStart))
                if blockMax >= floor {
                    for k in blockStart..<blockEnd {
                        let score = row[k]
                        guard score >= floor, admits(k) else { continue }
                        if topCandidates.count == limit {
                            // 同分时保留 id 较小者，与「按分数降序、稳定」的选取一致。
                            guard score > topCandidates[limit - 1].score else { continue }
                            topCandidates.removeLast()
                        }
                        var position = topCandidates.count
                        while position > 0, topCandidates[position - 1].score < score { position -= 1 }
                        topCandidates.insert((k, score), at: position)
                    }
                }
                blockStart = blockEnd
            }

            frameCandidates.removeAll(keepingCapacity: true)
            frameCandidates.append(contentsOf: topCandidates.map { $0.token })
            for first in graph.rootTokens where admits(Int(first)) {
                frameCandidates.append(Int(first))
            }
            frameCandidates.sort()
            var unique = 0
            for index in frameCandidates.indices where index == 0 || frameCandidates[index] != frameCandidates[index - 1] {
                frameCandidates[unique] = frameCandidates[index]
                unique += 1
            }
            frameCandidates.removeLast(frameCandidates.count - unique)
        }

        mutating func accumulate(_ prefix: Int, pb: Double, pnb: Double) {
            if let slot = slotByPrefix[prefix] {
                slots[slot].pb = logAddExp(slots[slot].pb, pb)
                slots[slot].pnb = logAddExp(slots[slot].pnb, pnb)
            } else {
                slotByPrefix[prefix] = slots.count
                slots.append(Hypothesis(prefix: prefix, pb: pb, pnb: pnb))
            }
        }

        func tokens(of prefix: Int) throws -> [Int] {
            var result: [Int] = []
            var node = prefix
            while node > 0 {
                guard result.count < parent.count else { throw DecodeError.brokenPrefixChain }
                result.append(Int(token[node]))
                node = Int(parent[node])
            }
            guard node == 0 else { throw DecodeError.brokenPrefixChain }
            return result.reversed()
        }

        /// 同分时按前缀 token id 字典序（短前缀在前），保证可复现。只在分数完全相等时调用。
        func lexicographicallyPrecedes(_ a: Int, _ b: Int) -> Bool {
            guard a != b, let lhs = try? tokens(of: a), let rhs = try? tokens(of: b) else { return false }
            return lhs.lexicographicallyPrecedes(rhs)
        }

        /// 结束时撤回暂存分：`final = 声学分 + committed`。
        func best(of beam: [Hypothesis]) throws -> Result {
            var bestHypothesis: Hypothesis?
            var bestScore = -Double.infinity
            for hypothesis in beam {
                let score = hypothesis.total + state[hypothesis.prefix].committed
                if bestHypothesis == nil || score > bestScore
                    || (score == bestScore && lexicographicallyPrecedes(hypothesis.prefix, bestHypothesis!.prefix)) {
                    bestHypothesis = hypothesis
                    bestScore = score
                }
            }
            guard let bestHypothesis else { throw DecodeError.beamExhausted }
            return Result(
                tokens: try tokens(of: bestHypothesis.prefix),
                hotwordHits: Int(state[bestHypothesis.prefix].hits)
            )
        }
    }

    private static func rowArgmax(_ row: UnsafePointer<Float>, count: Int) -> Int {
        var maxValue: Float = 0
        var maxIndex: vDSP_Length = 0
        vDSP_maxvi(row, 1, &maxValue, &maxIndex, vDSP_Length(count))
        return Int(maxIndex)
    }

    static func logAddExp(_ a: Double, _ b: Double) -> Double {
        if a == -.infinity { return b }
        if b == -.infinity { return a }
        return max(a, b) + log1p(exp(-abs(a - b)))
    }
}
