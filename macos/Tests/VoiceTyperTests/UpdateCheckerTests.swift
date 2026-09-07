import XCTest
@testable import VoiceTyper

/// 版本号解析与比较的纯逻辑测试（不联网）。
/// 这两个函数一旦出错，后果是"明明有新版本却说已是最新"或反过来，用户没有别的手段发现。
final class UpdateCheckerTests: XCTestCase {
    func testParsesCommonTagShapes() {
        XCTAssertEqual(UpdateChecker.versionComponents(from: "3.2.1"), [3, 2, 1])
        XCTAssertEqual(UpdateChecker.versionComponents(from: "v3.2.1"), [3, 2, 1])
        XCTAssertEqual(UpdateChecker.versionComponents(from: "macos-3.2.1-beta"), [3, 2, 1])
        XCTAssertEqual(UpdateChecker.versionComponents(from: "v10"), [10])
        XCTAssertEqual(UpdateChecker.versionComponents(from: "0.0.0-dev"), [0, 0, 0])
    }

    func testReturnsNilWhenNoDigitsPresent() {
        XCTAssertNil(UpdateChecker.versionComponents(from: ""))
        XCTAssertNil(UpdateChecker.versionComponents(from: "beta"))
        XCTAssertNil(UpdateChecker.versionComponents(from: "v."))
    }

    func testStopsAtFirstNumberRun() {
        // 只取第一串点分数字，后面的噪声不参与比较。
        XCTAssertEqual(UpdateChecker.versionComponents(from: "3.2.1 (build 4567)"), [3, 2, 1])
        XCTAssertEqual(UpdateChecker.versionComponents(from: "3..2"), [3])
    }

    func testComparisonTreatsMissingComponentsAsZero() {
        XCTAssertEqual(UpdateChecker.compare([3, 2], [3, 2, 0]), .orderedSame)
        XCTAssertEqual(UpdateChecker.compare([3, 2, 1], [3, 2, 0]), .orderedDescending)
        XCTAssertEqual(UpdateChecker.compare([3, 2, 0], [3, 2, 1]), .orderedAscending)
        XCTAssertEqual(UpdateChecker.compare([3, 10, 0], [3, 9, 9]), .orderedDescending)
        XCTAssertEqual(UpdateChecker.compare([4], [3, 99, 99]), .orderedDescending)
    }
}
