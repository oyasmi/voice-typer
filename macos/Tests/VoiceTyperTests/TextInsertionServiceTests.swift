import XCTest
@testable import VoiceTyper

/// R2-05a 回归：AX 控件返回的 `CFRange` 不可信，非法值必须被识别出来而不是直接
/// 喂给 `NSString.replacingCharacters` 触发 `NSRangeException`（Swift 无法捕获，直接崩溃）。
final class TextInsertionServiceTests: XCTestCase {
    func testValidRangeWithinBoundsIsAccepted() {
        let range = TextInsertionService.validInsertionRange(CFRange(location: 2, length: 3), in: 10)
        XCTAssertEqual(range, NSRange(location: 2, length: 3))
    }

    func testZeroLengthRangeAtEndIsAccepted() {
        let range = TextInsertionService.validInsertionRange(CFRange(location: 10, length: 0), in: 10)
        XCTAssertEqual(range, NSRange(location: 10, length: 0))
    }

    func testKCFNotFoundLocationIsRejected() {
        XCTAssertNil(TextInsertionService.validInsertionRange(CFRange(location: kCFNotFound, length: 0), in: 10))
    }

    func testNegativeLocationIsRejected() {
        XCTAssertNil(TextInsertionService.validInsertionRange(CFRange(location: -1, length: 2), in: 10))
    }

    func testNegativeLengthIsRejected() {
        XCTAssertNil(TextInsertionService.validInsertionRange(CFRange(location: 0, length: -1), in: 10))
    }

    func testLocationBeyondLengthIsRejected() {
        XCTAssertNil(TextInsertionService.validInsertionRange(CFRange(location: 11, length: 0), in: 10))
    }

    func testRangeOverflowingLengthIsRejected() {
        XCTAssertNil(TextInsertionService.validInsertionRange(CFRange(location: 8, length: 5), in: 10))
    }
}
