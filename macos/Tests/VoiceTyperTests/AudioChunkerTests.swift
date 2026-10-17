import XCTest
@testable import VoiceTyper

/// `AudioChunker` 是从 `AudioCaptureService` 拆出的纯逻辑分帧器（R3-01 修复的一部分），
/// 脱离 AVAudioEngine 即可验证"凑满即吐出 chunk、尾音靠 drain() 取走"这条确定性规则。
/// 丢一个样本就会让整段识别错位，所以这条规则值得钉死。
final class AudioChunkerTests: XCTestCase {
    func testAccumulatesAcrossAppendsAndEmitsEveryFullChunk() {
        var chunker = AudioChunker(chunkSamples: 3)
        XCTAssertEqual(chunker.append([1, 2]), [])
        XCTAssertEqual(chunker.append([3, 4, 5, 6, 7]), [[1, 2, 3], [4, 5, 6]])
        XCTAssertEqual(chunker.drain(), [7], "不足一个 chunk 的尾音靠 drain() 取走")
    }

    func testDrainClearsTheBuffer() {
        var chunker = AudioChunker(chunkSamples: 100)
        XCTAssertEqual(chunker.drain(), [])
        _ = chunker.append([1, 2, 3])
        XCTAssertEqual(chunker.drain(), [1, 2, 3])
        XCTAssertEqual(chunker.drain(), [], "重复调用不能吐出同一批样本")
    }
}
