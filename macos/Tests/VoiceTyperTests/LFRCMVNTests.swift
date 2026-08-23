import XCTest
@testable import VoiceTyper

/// `applyLFR`/`applyCMVN` 的纯 Swift 结构性测试：不依赖模型或 Python 参考实现，
/// 任何机器都能跑。这两个函数此前只通过 `FbankParityTests`（需要真实模型，无模型环境
/// 下 XCTSkip）间接覆盖，而现有金标准夹具（0.62s 合成正弦，60 帧）恰好凑不出
/// `applyLFR` 的尾帧补齐分支——`framesLFR=10` 时最后一帧 `paddedT-start=9 ≥ m=7`，
/// 走的是普通分支，补齐逻辑一次都没被跑到过（R4-09）。这里用手算验证过的小数组
/// 直接覆盖尾帧补齐与左侧补齐两条路径。
final class LFRCMVNTests: XCTestCase {
    /// 构造第 i 帧为 `[Float(i)]`（1 维），便于从输出直接读出用了哪些原始帧拼接而成。
    private func indexedFeats(count: Int) -> [[Float]] {
        (0..<count).map { [Float($0)] }
    }

    /// T=13, m=7, n=6：leftPad=3，paddedT=16，framesLFR=ceil(13/6)=3。
    /// 前两帧（i=0,1）落在"够 m 帧"的普通分支；最后一帧（i=2，start=12）
    /// paddedT-start=4 < m=7，必须走尾帧补齐分支——补齐值应是 padded 的最后一帧
    /// （即原始序列的最后一帧，feats[12]=12）重复到凑满 7 个。
    func testTailFrameShorterThanMPadsWithLastFrame() {
        let feats = indexedFeats(count: 13)
        let lfr = applyLFR(feats, m: 7, n: 6)

        XCTAssertEqual(lfr.count, 3, "ceil(13/6) 应产出 3 个 LFR 帧")

        // i=0: start=0，padded[0...6] = [f0,f0,f0,f0,f1,f2,f3]（前 3 份是左侧补齐的复制）。
        XCTAssertEqual(lfr[0], [0, 0, 0, 0, 1, 2, 3])
        // i=1: start=6，padded[6...12] = [f3,f4,f5,f6,f7,f8,f9]。
        XCTAssertEqual(lfr[1], [3, 4, 5, 6, 7, 8, 9])
        // i=2: start=12，只剩 padded[12...15] = [f9,f10,f11,f12] 四帧真实数据，
        // 用最后一帧（f12=12）重复补齐 3 份凑满 7 个。
        XCTAssertEqual(lfr[2], [9, 10, 11, 12, 12, 12, 12])
    }

    /// T 恰好是 n 的整数倍且每帧都够 m：不应触发尾帧补齐分支，验证普通路径本身正确，
    /// 避免"改对了补齐分支、改坏了普通分支"这类回归不被发现。
    func testEveryFrameHasEnoughSamplesTakesNormalBranch() {
        let feats = indexedFeats(count: 12)
        let lfr = applyLFR(feats, m: 7, n: 6)

        XCTAssertEqual(lfr.count, 2, "ceil(12/6) 应产出 2 个 LFR 帧")
        XCTAssertEqual(lfr[0], [0, 0, 0, 0, 1, 2, 3])
        // i=1: start=6，paddedT=15，paddedT-start=9 ≥ m=7，走普通分支：
        // padded[6...12] = [f3,f4,f5,f6,f7,f8,f9]。
        XCTAssertEqual(lfr[1], [3, 4, 5, 6, 7, 8, 9])
    }

    /// 输出维度必须是 m × 原始特征维度；用 dim=2 的输入验证拼接顺序（先帧后维度）
    /// 而不是被 dim=1 的其它用例掩盖掉转置类 bug。
    func testOutputDimensionIsMTimesFeatureDimension() {
        let feats = (0..<8).map { [Float($0), Float($0) + 0.5] }
        let lfr = applyLFR(feats, m: 7, n: 6)

        XCTAssertEqual(lfr[0].count, 7 * 2)
        // 第一帧应是 padded[0..6] 逐帧展开：[f0,f0,f0,f0,f1,f2,f3]，每帧 2 维。
        XCTAssertEqual(lfr[0], [0, 0.5, 0, 0.5, 0, 0.5, 0, 0.5, 1, 1.5, 2, 2.5, 3, 3.5])
    }

    func testEmptyInputProducesEmptyOutput() {
        XCTAssertEqual(applyLFR([], m: 7, n: 6), [])
    }

    /// `out = (in + mean) * var`，逐维度、逐帧。
    func testApplyCMVNIsElementwiseAffineTransform() {
        let feats: [[Float]] = [[1, 2, 3], [4, 5, 6]]
        let stats = CMVNStats(means: [1, -1, 0], vars: [2, 3, 0.5])

        let normalized = applyCMVN(feats, stats: stats)

        let expectedRow0: [Float] = [(1 + 1) * 2, (2 + -1) * 3, (3 + 0) * 0.5]
        let expectedRow1: [Float] = [(4 + 1) * 2, (5 + -1) * 3, (6 + 0) * 0.5]
        XCTAssertEqual(normalized[0], expectedRow0)
        XCTAssertEqual(normalized[1], expectedRow1)
    }
}
