import Foundation

/// 热词上下文图（VOC-4）：所有热词 token 序列建成一棵前缀树，再补 Aho–Corasick 失配指针。
/// 构建后不可变，可跨线程共享；束搜索里每个假设只携带一个很小的 `State`。
///
/// 打分语义（与 icefall / WeNet 的上下文偏置同构）：
/// - 正在匹配的热词每多对上一个 token，排名分 +b（`Dmax` 封顶），让它能在束里活到匹配完成；
/// - 中途失配，暂存分全部撤回：只对上前缀的路径拿不到任何净收益；
/// - 完整匹配后锁定为 `committed`；`张三` 锁定后仍可继续走到 `张三丰`。
///
/// 已知限制：一个热词是另一个热词的中缀或后缀、且二者重叠出现时，重叠部分可能被重复计分或漏计。
final class ContextGraph: Sendable {
    /// 假设携带的偏置状态。它是 token 序列的确定性函数，同一前缀从不同对齐到达时必然相同。
    struct State: Equatable, Sendable {
        var node: Int32 = 0
        /// 已完整匹配的热词锁定的分。
        var committed: Double = 0
        /// 当前路径上最近一次完整匹配时的 `S` 值（没有则为 0）。
        var anchor: Double = 0
        /// 完成匹配的次数。
        var hits: Int32 = 0
    }

    static let root: Int32 = 0

    /// `(节点 << 32) | token` → 子节点。
    private let edges: [Int: Int32]
    private let fail: [Int32]
    /// `S(node) = b × min(depth, Dmax)`。
    private let score: [Double]
    private let isEnd: [Bool]
    /// 每个节点的子节点 token，升序。束搜索据此把「正在匹配的热词的下一个 token」加入候选。
    private let childTokens: [[Int32]]
    /// 所有热词序列的首 token，升序、去重。
    let rootTokens: [Int32]

    var nodeCount: Int { isEnd.count }

    init(sequences: [[Int]], perTokenBias: Double, maxScoredDepth: Int) {
        var edges: [Int: Int32] = [:]
        var depth: [Int] = [0]
        var isEnd: [Bool] = [false]
        var childTokens: [[Int32]] = [[]]
        for sequence in sequences where !sequence.isEmpty {
            var node = 0
            for token in sequence {
                let key = Self.edgeKey(node, token)
                if let next = edges[key] {
                    node = Int(next)
                } else {
                    let next = isEnd.count
                    edges[key] = Int32(next)
                    depth.append(depth[node] + 1)
                    isEnd.append(false)
                    childTokens.append([])
                    childTokens[node].append(Int32(token))
                    node = next
                }
            }
            isEnd[node] = true
        }
        for index in childTokens.indices {
            childTokens[index].sort()
        }

        // BFS 补失配指针：fail(v) 指向「v 的路径的最长真后缀」在树中的节点。
        var fail = [Int32](repeating: 0, count: isEnd.count)
        var queue: [Int] = childTokens[0].map { Int(edges[Self.edgeKey(0, Int($0))]!) }
        var head = 0
        while head < queue.count {
            let parent = queue[head]
            head += 1
            for token in childTokens[parent] {
                let child = Int(edges[Self.edgeKey(parent, Int(token))]!)
                var candidate = Int(fail[parent])
                while candidate != 0, edges[Self.edgeKey(candidate, Int(token))] == nil {
                    candidate = Int(fail[candidate])
                }
                fail[child] = edges[Self.edgeKey(candidate, Int(token))] ?? 0
                queue.append(child)
            }
        }

        self.edges = edges
        self.fail = fail
        self.isEnd = isEnd
        self.childTokens = childTokens
        self.rootTokens = childTokens[0]
        self.score = depth.map { perTokenBias * Double(min($0, maxScoredDepth)) }
    }

    private static func edgeKey(_ node: Int, _ token: Int) -> Int {
        node << 32 | token
    }

    /// 正在匹配的热词的下一个 token（升序）。
    func children(of state: State) -> [Int32] {
        childTokens[Int(state.node)]
    }

    /// 排名用的偏置：`committed + S(node) − anchor`，后两项是「正在匹配、尚未完成」的暂存分。
    func bias(_ state: State) -> Double {
        state.committed + score[Int(state.node)] - state.anchor
    }

    /// 发射 token 后的状态转移。
    func step(_ state: State, token: Int) -> State {
        var next = state
        if let child = edges[Self.edgeKey(Int(state.node), token)] {
            next.node = child
        } else {
            // 沿失配链找第一个有子节点 token 的祖先；找不到回到根。暂存分全部撤回。
            var candidate = Int(state.node)
            var target: Int32 = Self.root
            while candidate != 0 {
                candidate = Int(fail[candidate])
                if let child = edges[Self.edgeKey(candidate, token)] {
                    target = child
                    break
                }
            }
            next.node = target
            next.anchor = 0
        }
        if isEnd[Int(next.node)] {
            let reached = score[Int(next.node)]
            next.committed += reached - next.anchor
            next.anchor = reached
            next.hits += 1
        }
        return next
    }
}
