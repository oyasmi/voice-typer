import Accelerate
import Foundation

/// 统一对齐后的发射 token（HINT-1），供 `AcousticHintExtractor` 消费。
struct AlignedToken: Equatable, Sendable {
    /// 词表中的 token id（已去 blank 与连续重复）。
    let id: Int
    /// 该 token 的代表性发射帧索引（0..<numFrames）。
    let peakFrame: Int
    /// true 表示该 token 属于已命中的热词区间，禁止为它生成备选（HINT-D5）。
    let protectedByHotword: Bool
}

/// 局部声学低置信度备选（HINT-5）。
/// **隐私红线**：不得写入主系统日志（`OSLog`）或 `DictationMetrics`。
struct AcousticHint: Equatable, Sendable {
    /// 清洗后的 1-best 局部片段（可能含上下文锚点）。
    let surface: String
    /// 按声学分差升序排列的备选片段（1...3 个，不含 surface 本身）。
    let alternatives: [String]
    /// 最强备选的 logit 分差；仅用于排序、离线评估与可选的独立诊断日志，不发送 LLM，不落主日志。
    let bestMargin: Double
}

/// 带局部声学备选的识别结果（HINT-5）。
struct RecognitionResult: Equatable, Sendable {
    let text: String
    let hints: [AcousticHint]
}

/// 局部低置信度声学备选提取器（HINT-1 – HINT-4）。
///
/// 纯逻辑模块，不依赖 AppKit / ONNX Runtime，由单元测试与共享夹具 `asr_hints_cases.json` 直接驱动。
/// 与 Windows `windows/Asr/AcousticHintExtractor.cs` 保持逐条对齐与同分确定性。
enum AcousticHintExtractor {
    /// 提取算法参数（HINT-4.5）；不进入 `config.yaml`。
    struct Parameters: Equatable, Sendable {
        var marginThreshold: Double = 1.15
        var maxHintsPerUtterance: Int = 5
        var maxAltsPerHint: Int = 3
        var maxMergedCJKLength: Int = 3
        var jointMarginMultiplier: Double = 1.5
        var frameTopK: Int = 4

        static let `default` = Parameters()
    }

    /// 词表静态元数据（HINT-2），引擎初始化后一次性预计算，与具体音频无关。
    struct TokenMetadata: Sendable {
        let count: Int
        let isExcluded: [Bool]
        let hasLeadingSpace: [Bool]
        let stripped: [String]
        let isSubstantive: [Bool]
        let isCJKChar: [Bool]
        let isASCIIWordPart: [Bool]
        let isPureFiller: [Bool]
        let isSingleASCIILetter: [Bool]
        let isPureASCII: [Bool]
        let isThirdPersonPronoun: [Bool]
        let isPureNumeral: [Bool]

        private static let pureFillers: Set<String> = ["呃", "嗯", "啊", "额", "哦", "噢", "唉", "哎"]
        private static let thirdPersonPronouns: Set<String> = ["他", "她", "它"]
        private static let numeralScalars: Set<Unicode.Scalar> = Set(
            "0123456789零〇一二两三四五六七八九十百千万亿".unicodeScalars
        )

        init(tokens: [String]) {
            let n = tokens.count
            var isExcluded = [Bool](repeating: false, count: n)
            var hasLeadingSpace = [Bool](repeating: false, count: n)
            var stripped = [String](repeating: "", count: n)
            var isSubstantive = [Bool](repeating: false, count: n)
            var isCJKChar = [Bool](repeating: false, count: n)
            var isASCIIWordPart = [Bool](repeating: false, count: n)
            var isPureFiller = [Bool](repeating: false, count: n)
            var isSingleASCIILetter = [Bool](repeating: false, count: n)
            var isPureASCII = [Bool](repeating: false, count: n)
            var isThirdPersonPronoun = [Bool](repeating: false, count: n)
            var isPureNumeral = [Bool](repeating: false, count: n)

            for i in 0..<n {
                let tok = tokens[i]
                isExcluded[i] = TokenTrie.isExcluded(id: i, token: tok)
                let leading = tok.hasPrefix("▁")
                hasLeadingSpace[i] = leading
                let s = leading ? String(tok.dropFirst()) : tok
                stripped[i] = s

                let scalars = Array(s.unicodeScalars)
                var hasSub = false
                var allASCII = !scalars.isEmpty
                var allASCIIAlnum = !scalars.isEmpty
                var allNumeral = !scalars.isEmpty
                for sc in scalars {
                    let v = sc.value
                    if v > 0x7F { allASCII = false }
                    if !Self.numeralScalars.contains(sc) { allNumeral = false }
                    let isAsciiAlnum = (v >= 0x30 && v <= 0x39)
                        || (v >= 0x41 && v <= 0x5A)
                        || (v >= 0x61 && v <= 0x7A)
                    if !isAsciiAlnum { allASCIIAlnum = false }
                    if isAsciiAlnum
                        || (v >= 0x4E00 && v <= 0x9FFF)
                        || (v >= 0x3040 && v <= 0x30FF)
                        || (v >= 0xAC00 && v <= 0xD7A3) {
                        hasSub = true
                    }
                }
                isSubstantive[i] = hasSub
                isPureASCII[i] = allASCII
                isASCIIWordPart[i] = allASCIIAlnum
                isPureNumeral[i] = allNumeral
                if scalars.count == 1 {
                    let v = scalars[0].value
                    isCJKChar[i] = (v >= 0x4E00 && v <= 0x9FFF)
                    isSingleASCIILetter[i] = (v >= 0x41 && v <= 0x5A) || (v >= 0x61 && v <= 0x7A)
                }
                isPureFiller[i] = Self.pureFillers.contains(s)
                isThirdPersonPronoun[i] = Self.thirdPersonPronouns.contains(s)
            }

            self.count = n
            self.isExcluded = isExcluded
            self.hasLeadingSpace = hasLeadingSpace
            self.stripped = stripped
            self.isSubstantive = isSubstantive
            self.isCJKChar = isCJKChar
            self.isASCIIWordPart = isASCIIWordPart
            self.isPureFiller = isPureFiller
            self.isSingleASCIILetter = isSingleASCIILetter
            self.isPureASCII = isPureASCII
            self.isThirdPersonPronoun = isThirdPersonPronoun
            self.isPureNumeral = isPureNumeral
        }
    }

    private static let scanBlock = 256

    /// 单路径 CTC Viterbi 强制对齐（HINT-1）：用于热词束搜索胜出路径。
    /// 只对跳过开头 `<|…|>` 标签后的 `contentTokens` 与有效帧对齐，并标记热词保护区间。
    /// 数值异常（NaN / 路径不可达）时返回 nil，调用方按安全阀回落空 hints。
    static func forcedAlign(
        logits: UnsafePointer<Float>,
        numFrames: Int,
        vocabSize: Int,
        tokens: [Int],
        tokenStrings: [String],
        graph: ContextGraph
    ) -> [AlignedToken]? {
        guard numFrames > 0, vocabSize > 0 else { return [] }

        // 跳过开头 `<|…|>` 特殊标签 token。
        var tokenStart = 0
        while tokenStart < tokens.count {
            let id = tokens[tokenStart]
            if id >= 0, id < tokenStrings.count, TokenTrie.isSpecial(tokenStrings[id]) {
                tokenStart += 1
            } else {
                break
            }
        }
        let contentTokens = Array(tokens[tokenStart...])
        let L = contentTokens.count
        guard L > 0 else { return [] }

        // 跳过开头 argmax 为 `<|…|>` 的标签帧。
        var startFrame = 0
        while startFrame < numFrames {
            let row = logits + startFrame * vocabSize
            var maxVal: Float = 0
            var maxIdx: vDSP_Length = 0
            vDSP_maxvi(row, 1, &maxVal, &maxIdx, vDSP_Length(vocabSize))
            if !maxVal.isFinite { return nil }
            let id = Int(maxIdx)
            if id >= 0, id < tokenStrings.count, TokenTrie.isSpecial(tokenStrings[id]) {
                startFrame += 1
            } else {
                break
            }
        }
        let T = numFrames - startFrame
        guard T > 0 else { return nil }

        // 扩展序列 s（长度 2L + 1）：偶数位 blank(0)，奇数位 contentTokens[i]。
        let S = 2 * L + 1
        var ext = [Int](repeating: 0, count: S)
        for i in 0..<L {
            let id = contentTokens[i]
            guard id >= 0, id < vocabSize else { return nil }
            ext[2 * i + 1] = id
        }

        var prevDP = [Double](repeating: -.infinity, count: S)
        var currDP = [Double](repeating: -.infinity, count: S)
        var backptr = [Int32](repeating: -1, count: T * S)

        let row0 = logits + startFrame * vocabSize
        let b0 = Double(row0[0])
        let y0 = Double(row0[ext[1]])
        guard b0.isFinite, y0.isFinite else { return nil }
        prevDP[0] = b0
        prevDP[1] = y0

        for t in 1..<T {
            let row = logits + (startFrame + t) * vocabSize
            let blankScore = Double(row[0])
            guard blankScore.isFinite else { return nil }
            for u in 0..<S {
                let emit: Double = (u & 1) == 0 ? blankScore : Double(row[ext[u]])
                if !emit.isFinite { return nil }
                var bestVal = prevDP[u]
                var bestPrev = u
                if u >= 1, prevDP[u - 1] > bestVal {
                    bestVal = prevDP[u - 1]
                    bestPrev = u - 1
                }
                if u >= 2, (u & 1) == 1, ext[u] != ext[u - 2], prevDP[u - 2] > bestVal {
                    bestVal = prevDP[u - 2]
                    bestPrev = u - 2
                }
                currDP[u] = bestVal + emit
                backptr[t * S + u] = Int32(bestPrev)
            }
            swap(&prevDP, &currDP)
        }

        var endState = S - 2
        if prevDP[S - 1] > prevDP[endState] {
            endState = S - 1
        }
        guard prevDP[endState].isFinite else { return nil }

        // 回溯并求每个 token 的非 blank 峰值核心帧。
        var bestFrameForToken = [Int](repeating: -1, count: L)
        var bestAdvForToken = [Float](repeating: -.infinity, count: L)
        var state = endState
        for t in stride(from: T - 1, through: 0, by: -1) {
            if (state & 1) == 1 {
                let tokenIdx = state / 2
                let absFrame = startFrame + t
                let row = logits + absFrame * vocabSize
                let adv = row[ext[state]] - row[0]
                // 倒序遍历：同分用 >= 以保留最早帧（HINT-1）。
                if adv >= bestAdvForToken[tokenIdx] {
                    bestAdvForToken[tokenIdx] = adv
                    bestFrameForToken[tokenIdx] = absFrame
                }
            }
            if t > 0 {
                let prev = Int(backptr[t * S + state])
                guard prev >= 0 else { return nil }
                state = prev
            }
        }

        let protected = graph.protectedMask(for: contentTokens)
        var aligned: [AlignedToken] = []
        aligned.reserveCapacity(L)
        for i in 0..<L {
            let pf = bestFrameForToken[i]
            guard pf >= 0 else { return nil }
            aligned.append(AlignedToken(id: contentTokens[i], peakFrame: pf, protectedByHotword: protected[i]))
        }
        return aligned
    }

    /// 数组便捷重载（供测试使用）。
    static func extract(
        logits: [Float],
        numFrames: Int,
        vocabSize: Int,
        aligned: [AlignedToken],
        fullText: String,
        metadata: TokenMetadata,
        parameters: Parameters = .default
    ) -> [AcousticHint] {
        logits.withUnsafeBufferPointer { buf in
            guard let base = buf.baseAddress else { return [] }
            return extract(
                logits: base,
                numFrames: numFrames,
                vocabSize: vocabSize,
                aligned: aligned,
                fullText: fullText,
                metadata: metadata,
                parameters: parameters
            )
        }
    }

    /// 零拷贝主入口：在 `logits` 底层内存上提取最多 `maxHintsPerUtterance` 处局部低置信度备选（HINT-3 / HINT-4）。
    static func extract(
        logits: UnsafePointer<Float>,
        numFrames: Int,
        vocabSize: Int,
        aligned: [AlignedToken],
        fullText: String,
        metadata: TokenMetadata,
        parameters: Parameters = .default
    ) -> [AcousticHint] {
        let n = aligned.count
        guard n > 0, numFrames > 0, vocabSize > 0, vocabSize == metadata.count, !fullText.isEmpty else {
            return []
        }

        // 步骤 0：单帧分块向量化扫描，收集各位置低置信度备选（HINT-3）。
        var frameAlts = [[(text: String, delta: Double)]](repeating: [], count: n)
        for i in 0..<n {
            let tok = aligned[i]
            let y = tok.id
            guard !tok.protectedByHotword,
                  y >= 0, y < vocabSize,
                  tok.peakFrame >= 0, tok.peakFrame < numFrames,
                  !metadata.isExcluded[y],
                  metadata.isSubstantive[y],
                  !metadata.isPureFiller[y]
            else { continue }

            let row = logits + tok.peakFrame * vocabSize
            let sy = row[y]
            guard sy.isFinite else { return [] }

            let syDouble = Double(sy)
            let cutoff = Float(syDouble - parameters.marginThreshold).nextDown
            let yStripped = metadata.stripped[y]
            let yPureASCII = metadata.isPureASCII[y]
            let yLower = yPureASCII ? yStripped.lowercased() : yStripped
            let yIsCJK = metadata.isCJKChar[y]
            let yIsPronoun = metadata.isThirdPersonPronoun[y]
            let yIsNumeral = metadata.isPureNumeral[y]

            var rawCands: [(delta: Double, id: Int, text: String)] = []
            rawCands.reserveCapacity(parameters.frameTopK + 2)

            var blockStart = 0
            while blockStart < vocabSize {
                let blockLen = min(Self.scanBlock, vocabSize - blockStart)
                var blockMax: Float = -.infinity
                vDSP_maxv(row + blockStart, 1, &blockMax, vDSP_Length(blockLen))
                if blockMax.isNaN { return [] }
                if blockMax >= cutoff {
                    let blockEnd = blockStart + blockLen
                    for k in blockStart..<blockEnd {
                        let v = row[k]
                        if v.isNaN { return [] }
                        if k == y || metadata.isExcluded[k] || !metadata.isSubstantive[k] { continue }
                        let delta = syDouble - Double(v)
                        if delta > parameters.marginThreshold { continue }
                        let kStripped = metadata.stripped[k]
                        if kStripped == yStripped { continue }
                        if yPureASCII, metadata.isPureASCII[k], kStripped.lowercased() == yLower { continue }
                        if yIsCJK, metadata.isSingleASCIILetter[k] { continue }
                        // 同音无声学区分度等价类过滤：「他/她/它」互换、数字写法互换。
                        if yIsPronoun, metadata.isThirdPersonPronoun[k] { continue }
                        if yIsNumeral, metadata.isPureNumeral[k] { continue }
                        rawCands.append((delta: delta, id: k, text: kStripped))
                    }
                }
                blockStart += blockLen
            }

            guard !rawCands.isEmpty else { continue }
            rawCands.sort { a, b in
                if a.delta != b.delta { return a.delta < b.delta }
                return a.id < b.id
            }

            let topSlice = rawCands.prefix(parameters.frameTopK)
            var seen = Set<String>()
            var deduped: [(text: String, delta: Double)] = []
            deduped.reserveCapacity(parameters.maxAltsPerHint)
            for item in topSlice {
                let norm = metadata.isPureASCII[item.id] ? item.text.lowercased() : item.text
                if seen.insert(norm).inserted {
                    deduped.append((text: item.text, delta: item.delta))
                    if deduped.count >= parameters.maxAltsPerHint { break }
                }
            }
            frameAlts[i] = deduped
        }

        // 步骤 1：词/段分组（HINT-4）。
        struct Candidate {
            var startIndex: Int
            var surface: String
            var alternatives: [String]
            var bestMargin: Double
        }

        var candidates: [Candidate] = []
        var singleCJKIndices: [Int] = []

        var i = 0
        while i < n {
            let yi = aligned[i].id
            if metadata.isASCIIWordPart[yi] {
                var j = i + 1
                while j < n,
                      metadata.isASCIIWordPart[aligned[j].id],
                      !metadata.hasLeadingSpace[aligned[j].id] {
                    j += 1
                }
                let hasProtected = (i..<j).contains { aligned[$0].protectedByHotword }
                if !hasProtected {
                    var lowPositions = (i..<j).filter { !frameAlts[$0].isEmpty }
                    if !lowPositions.isEmpty {
                        lowPositions.sort { a, b in
                            let da = frameAlts[a][0].delta
                            let db = frameAlts[b][0].delta
                            if da != db { return da < db }
                            return a < b
                        }
                        let chosen = Set(lowPositions.prefix(2))
                        let baseWord = (i..<j).map { metadata.stripped[aligned[$0].id] }.joined()
                        let baseLower = baseWord.lowercased()

                        var combos: [(word: String, delta: Double)] = [("", 0.0)]
                        for pos in i..<j {
                            let basePart = metadata.stripped[aligned[pos].id]
                            let opts: [(String, Double)] = chosen.contains(pos)
                                ? [(basePart, 0.0)] + frameAlts[pos].map { ($0.text, $0.delta) }
                                : [(basePart, 0.0)]
                            var nextCombos: [(word: String, delta: Double)] = []
                            nextCombos.reserveCapacity(combos.count * opts.count)
                            for c in combos {
                                for o in opts {
                                    nextCombos.append((c.word + o.0, c.delta + o.1))
                                }
                            }
                            combos = nextCombos
                        }

                        var bestByNorm: [String: (word: String, delta: Double)] = [:]
                        for combo in combos {
                            let key = combo.word.lowercased()
                            if key == baseLower { continue }
                            if isMalformedASCIIAlt(candidateLower: key, baseLower: baseLower) { continue }
                            if let prev = bestByNorm[key] {
                                if combo.delta < prev.delta || (combo.delta == prev.delta && combo.word < prev.word) {
                                    bestByNorm[key] = combo
                                }
                            } else {
                                bestByNorm[key] = combo
                            }
                        }
                        let ranked = bestByNorm.values.sorted { a, b in
                            if a.delta != b.delta { return a.delta < b.delta }
                            return a.word < b.word
                        }.prefix(parameters.maxAltsPerHint)

                        if let first = ranked.first {
                            candidates.append(Candidate(
                                startIndex: i,
                                surface: baseWord,
                                alternatives: ranked.map(\.word),
                                bestMargin: first.delta
                            ))
                        }
                    }
                }
                i = j
            } else if metadata.isCJKChar[yi], !aligned[i].protectedByHotword, !frameAlts[i].isEmpty {
                var j = i + 1
                while j < n,
                      (j - i) < parameters.maxMergedCJKLength,
                      metadata.isCJKChar[aligned[j].id],
                      !aligned[j].protectedByHotword,
                      !frameAlts[j].isEmpty {
                    j += 1
                }
                if j - i >= 2 {
                    let baseStr = (i..<j).map { metadata.stripped[aligned[$0].id] }.joined()
                    let jointLimit = parameters.jointMarginMultiplier * parameters.marginThreshold + 1e-9
                    var combos: [(word: String, delta: Double)] = [("", 0.0)]
                    for pos in i..<j {
                        let basePart = metadata.stripped[aligned[pos].id]
                        let opts: [(String, Double)] = [(basePart, 0.0)] + frameAlts[pos].prefix(2).map { ($0.text, $0.delta) }
                        var nextCombos: [(word: String, delta: Double)] = []
                        for c in combos {
                            for o in opts {
                                let sum = c.delta + o.1
                                if sum <= jointLimit {
                                    nextCombos.append((c.word + o.0, sum))
                                }
                            }
                        }
                        combos = nextCombos
                    }
                    var bestByStr: [String: Double] = [:]
                    for combo in combos where combo.word != baseStr {
                        if let prev = bestByStr[combo.word] {
                            if combo.delta < prev { bestByStr[combo.word] = combo.delta }
                        } else {
                            bestByStr[combo.word] = combo.delta
                        }
                    }
                    let ranked = bestByStr.sorted { a, b in
                        if a.value != b.value { return a.value < b.value }
                        return a.key < b.key
                    }.prefix(parameters.maxAltsPerHint)

                    if let first = ranked.first {
                        candidates.append(Candidate(
                            startIndex: i,
                            surface: baseStr,
                            alternatives: ranked.map(\.key),
                            bestMargin: first.value
                        ))
                    }
                } else {
                    singleCJKIndices.append(i)
                }
                i = j
            } else {
                if !aligned[i].protectedByHotword, !frameAlts[i].isEmpty {
                    let alts = frameAlts[i].prefix(parameters.maxAltsPerHint).map(\.text)
                    candidates.append(Candidate(
                        startIndex: i,
                        surface: metadata.stripped[yi],
                        alternatives: alts,
                        bestMargin: frameAlts[i][0].delta
                    ))
                }
                i += 1
            }
        }

        // 步骤 2：孤立单汉字上下文锚点扩展（HINT-4）。
        singleCJKIndices.sort { a, b in
            let da = frameAlts[a][0].delta
            let db = frameAlts[b][0].delta
            if da != db { return da < db }
            return a < b
        }
        var claimedAnchors = Set<Int>()
        for pos in singleCJKIndices {
            let baseChar = metadata.stripped[aligned[pos].id]
            let alts = frameAlts[pos].prefix(parameters.maxAltsPerHint).map(\.text)
            let margin = frameAlts[pos][0].delta
            let canRight = pos + 1 < n && metadata.isCJKChar[aligned[pos + 1].id]
            let canLeft = pos - 1 >= 0 && metadata.isCJKChar[aligned[pos - 1].id]

            var useLeft = false
            var useRight = false
            if canRight, !claimedAnchors.contains(pos + 1) {
                useRight = true
                claimedAnchors.insert(pos + 1)
            } else if canLeft, !claimedAnchors.contains(pos - 1) {
                useLeft = true
                claimedAnchors.insert(pos - 1)
            } else if canRight {
                useRight = true
            } else if canLeft {
                useLeft = true
            }

            func buildSurface(left: Bool, right: Bool, center: String) -> String {
                let lch = left ? metadata.stripped[aligned[pos - 1].id] : ""
                let rch = right ? metadata.stripped[aligned[pos + 1].id] : ""
                return lch + center + rch
            }

            var surface = buildSurface(left: useLeft, right: useRight, center: baseChar)
            if countOccurrences(of: surface, in: fullText) >= 2 {
                if useRight, canLeft {
                    useLeft = true
                    surface = buildSurface(left: useLeft, right: useRight, center: baseChar)
                } else if useLeft, canRight {
                    useRight = true
                    surface = buildSurface(left: useLeft, right: useRight, center: baseChar)
                }
            }

            let startIdx = useLeft ? pos - 1 : pos
            let expandedAlts = alts.map { buildSurface(left: useLeft, right: useRight, center: $0) }
            candidates.append(Candidate(
                startIndex: startIdx,
                surface: surface,
                alternatives: expandedAlts,
                bestMargin: margin
            ))
        }

        // 步骤 3：去重、按 bestMargin 截断前 maxHintsPerUtterance 条、按原文顺序重排（HINT-4）。
        var dedupBySurface: [String: Candidate] = [:]
        for cand in candidates where !cand.surface.isEmpty && !cand.alternatives.isEmpty {
            if let prev = dedupBySurface[cand.surface] {
                if cand.bestMargin < prev.bestMargin
                    || (cand.bestMargin == prev.bestMargin && cand.startIndex < prev.startIndex) {
                    dedupBySurface[cand.surface] = cand
                }
            } else {
                dedupBySurface[cand.surface] = cand
            }
        }

        var winners = Array(
            dedupBySurface.values.sorted { a, b in
                if a.bestMargin != b.bestMargin { return a.bestMargin < b.bestMargin }
                return a.startIndex < b.startIndex
            }.prefix(parameters.maxHintsPerUtterance)
        )
        winners.sort { $0.startIndex < $1.startIndex }

        return winners.map {
            AcousticHint(surface: $0.surface, alternatives: $0.alternatives, bestMargin: $0.bestMargin)
        }
    }

    /// 过滤英文 BPE 子词笛卡尔积产生的畸形叠词（如 `antigravityvity`、`HPpV` 类超长或连续叠字噪声）。
    private static func isMalformedASCIIAlt(candidateLower: String, baseLower: String) -> Bool {
        let cBytes = Array(candidateLower.utf8)
        let bLen = baseLower.utf8.count
        let cLen = cBytes.count
        if cLen > bLen + 2 { return true }
        if bLen >= 3, cLen < max(2, bLen - 2) { return true }
        // 连续 3 个相同字符（如 ggg / ppp）
        if cLen >= 3 {
            for idx in 2..<cLen {
                if cBytes[idx] == cBytes[idx - 1], cBytes[idx] == cBytes[idx - 2] {
                    return true
                }
            }
        }
        // 末尾出现长度 3...5 的立即重复子串（如 vityvity）
        for subLen in 3...5 {
            if cLen >= subLen * 2 {
                let tailStart = cLen - subLen
                let prevStart = tailStart - subLen
                var same = true
                for k in 0..<subLen {
                    if cBytes[prevStart + k] != cBytes[tailStart + k] {
                        same = false
                        break
                    }
                }
                if same { return true }
            }
        }
        return false
    }

    private static func countOccurrences(of needle: String, in haystack: String) -> Int {
        guard !needle.isEmpty else { return 0 }
        var count = 0
        var searchStart = haystack.startIndex
        while searchStart < haystack.endIndex,
              let range = haystack.range(of: needle, range: searchStart..<haystack.endIndex) {
            count += 1
            searchStart = range.upperBound
        }
        return count
    }
}
