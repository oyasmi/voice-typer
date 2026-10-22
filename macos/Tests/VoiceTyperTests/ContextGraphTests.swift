import XCTest
@testable import VoiceTyper

/// VOC-4：上下文图的逐步打分。token id 用小整数即可，图不关心词表。
final class ContextGraphTests: XCTestCase {
    private let b = 1.5

    private func walk(_ graph: ContextGraph, _ tokens: [Int]) -> ContextGraph.State {
        tokens.reduce(ContextGraph.State()) { graph.step($0, token: $1) }
    }

    func testMatchingTokensAddBiasAndCompletionLocksIt() {
        let graph = ContextGraph(sequences: [[1, 2, 3]], perTokenBias: b, maxScoredDepth: 4)
        XCTAssertEqual(graph.bias(walk(graph, [1])), 1.5)
        XCTAssertEqual(graph.bias(walk(graph, [1, 2])), 3.0)
        let done = walk(graph, [1, 2, 3])
        XCTAssertEqual(done.committed, 4.5)
        XCTAssertEqual(graph.bias(done), 4.5)
        XCTAssertEqual(done.hits, 1)
    }

    /// 中途失配，暂存分全部撤回：只对上前缀的路径没有任何净收益。
    func testMismatchWithdrawsPendingBias() {
        let graph = ContextGraph(sequences: [[1, 2, 3]], perTokenBias: b, maxScoredDepth: 4)
        let failed = walk(graph, [1, 2, 9])
        XCTAssertEqual(graph.bias(failed), 0)
        XCTAssertEqual(failed.committed, 0)
        XCTAssertEqual(failed.hits, 0)
    }

    /// 终稿只计 committed：停在半路的暂存分不算数。
    func testFinalScoreExcludesPendingBias() {
        let graph = ContextGraph(sequences: [[1, 2, 3]], perTokenBias: b, maxScoredDepth: 4)
        let halfway = walk(graph, [1, 2])
        XCTAssertEqual(graph.bias(halfway), 3.0)
        XCTAssertEqual(halfway.committed, 0)
    }

    func testCompletedMatchStaysLockedAfterLaterMismatch() {
        let graph = ContextGraph(sequences: [[1, 2]], perTokenBias: b, maxScoredDepth: 4)
        let state = walk(graph, [1, 2, 7, 8])
        XCTAssertEqual(state.committed, 3.0)
        XCTAssertEqual(graph.bias(state), 3.0)
    }

    /// `张三` 与 `张三丰` 同时在表：`张三` 锁定后仍可继续走到 `张三丰`，只补差额。
    func testNestedHotwords() {
        let graph = ContextGraph(sequences: [[1, 2], [1, 2, 3]], perTokenBias: b, maxScoredDepth: 4)
        let short = walk(graph, [1, 2])
        XCTAssertEqual(short.committed, 3.0)
        XCTAssertEqual(short.hits, 1)
        let long = walk(graph, [1, 2, 3])
        XCTAssertEqual(long.committed, 4.5)
        XCTAssertEqual(long.hits, 2)
        // 张三 之后失配：张三 的分保留。
        XCTAssertEqual(walk(graph, [1, 2, 9]).committed, 3.0)
    }

    /// Dmax 封顶：长词暂存分最多 4b，避免中途失配时撤回过多。
    func testPendingBiasIsCappedAtMaxScoredDepth() {
        let graph = ContextGraph(sequences: [[1, 2, 3, 4, 5, 6]], perTokenBias: b, maxScoredDepth: 4)
        XCTAssertEqual(graph.bias(walk(graph, [1, 2, 3, 4, 5])), 6.0)
        XCTAssertEqual(walk(graph, [1, 2, 3, 4, 5, 6]).committed, 6.0)
    }

    /// AC 失配跳转：`1 2` 之后来了 `4`，跳到 `2 4` 继续匹配；之前的暂存分撤回，新匹配重新计分。
    func testFailureLinkJumpsToSuffixMatch() {
        let graph = ContextGraph(sequences: [[1, 2, 3], [2, 4]], perTokenBias: b, maxScoredDepth: 4)
        let state = walk(graph, [1, 2, 4])
        XCTAssertEqual(state.hits, 1)
        XCTAssertEqual(state.committed, 3.0)
        XCTAssertEqual(graph.bias(state), 3.0)
    }

    func testRootAndChildTokensAreSortedAndUnique() {
        let graph = ContextGraph(sequences: [[5, 1], [3, 2], [5, 4], [3, 2, 9]], perTokenBias: b, maxScoredDepth: 4)
        XCTAssertEqual(graph.rootTokens, [3, 5])
        XCTAssertEqual(graph.children(of: walk(graph, [5])), [1, 4])
        XCTAssertEqual(graph.children(of: walk(graph, [3, 2])), [9])
        XCTAssertEqual(graph.children(of: ContextGraph.State()), [3, 5])
    }
}
