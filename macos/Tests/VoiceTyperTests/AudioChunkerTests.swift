import XCTest
@testable import VoiceTyper

/// `AudioChunker` 是从 `AudioCaptureService` 拆出的纯逻辑分帧器（R3-01 修复的一部分），
/// 脱离 AVAudioEngine 即可验证"凑满即吐出 chunk、尾音靠 drain() 取走"这条确定性规则。
final class AudioChunkerTests: XCTestCase {
    func testEmitsChunkExactlyWhenBufferReachesSize() {
        var chunker = AudioChunker(chunkSamples: 4)
        let chunks = chunker.append([1, 2, 3, 4])
        XCTAssertEqual(chunks, [[1, 2, 3, 4]])
    }

    func testAccumulatesAcrossMultipleAppendsBeforeEmitting() {
        var chunker = AudioChunker(chunkSamples: 4)
        XCTAssertEqual(chunker.append([1, 2]), [])
        let chunks = chunker.append([3, 4, 5])
        XCTAssertEqual(chunks, [[1, 2, 3, 4]])
        XCTAssertEqual(chunker.drain(), [5])
    }

    func testEmitsMultipleChunksFromOneLargeAppend() {
        var chunker = AudioChunker(chunkSamples: 3)
        let chunks = chunker.append([1, 2, 3, 4, 5, 6, 7])
        XCTAssertEqual(chunks, [[1, 2, 3], [4, 5, 6]])
        XCTAssertEqual(chunker.drain(), [7])
    }

    func testDrainReturnsRemainderAndClearsBuffer() {
        var chunker = AudioChunker(chunkSamples: 100)
        _ = chunker.append([1, 2, 3])
        XCTAssertEqual(chunker.drain(), [1, 2, 3])
        XCTAssertEqual(chunker.drain(), [], "drain 之后缓冲区必须清空，重复调用不能吐出同一批样本")
    }

    func testDrainOnEmptyBufferReturnsEmpty() {
        var chunker = AudioChunker(chunkSamples: 10)
        XCTAssertEqual(chunker.drain(), [])
    }
}
