import XCTest
@testable import VoiceTyper

/// R8-E2 / R8-E3 的纯计算：AX → Cocoa 坐标换算、选屏、插入点可信度、浮窗相对插入点的定位。
final class FocusGeometryTests: XCTestCase {
    func testCocoaRectFlipsYAroundThePrimaryScreen() {
        let ax = CGRect(x: 100, y: 50, width: 200, height: 300)
        let cocoa = FocusGeometry.cocoaRect(fromAX: ax, primaryScreenHeight: 1000)
        XCTAssertEqual(cocoa, CGRect(x: 100, y: 650, width: 200, height: 300))
        // 往返不变。
        XCTAssertEqual(FocusGeometry.cocoaRect(fromAX: cocoa, primaryScreenHeight: 1000), ax)
    }

    func testCocoaRectForWindowOnASecondaryScreenAbovePrimary() {
        // 副屏在主屏上方：AX 的 y 为负，换算后 Cocoa 的 y 超过主屏高度。
        let ax = CGRect(x: 0, y: -800, width: 400, height: 200)
        let cocoa = FocusGeometry.cocoaRect(fromAX: ax, primaryScreenHeight: 1000)
        XCTAssertEqual(cocoa.minY, 1600)
    }

    private let screens = [
        CGRect(x: 0, y: 0, width: 1000, height: 800),
        CGRect(x: 1000, y: 0, width: 1000, height: 800),
    ]

    func testScreenIndexPrefersTheCenterPoint() {
        XCTAssertEqual(FocusGeometry.screenIndex(for: CGRect(x: 100, y: 100, width: 200, height: 200), screenFrames: screens), 0)
        XCTAssertEqual(FocusGeometry.screenIndex(for: CGRect(x: 1200, y: 100, width: 200, height: 200), screenFrames: screens), 1)
    }

    func testScreenIndexFallsBackToLargestOverlapWhenCenterIsOutside() {
        // 中心在两屏之外（下方），但与第二屏相交面积更大。
        let rect = CGRect(x: 900, y: -300, width: 1000, height: 400)
        XCTAssertEqual(FocusGeometry.screenIndex(for: rect, screenFrames: screens), 1)
    }

    func testScreenIndexIsNilWhenNothingOverlaps() {
        XCTAssertNil(FocusGeometry.screenIndex(for: CGRect(x: 5000, y: 5000, width: 10, height: 10), screenFrames: screens))
        XCTAssertNil(FocusGeometry.screenIndex(for: .zero, screenFrames: []))
    }

    func testCaretPlausibility() {
        XCTAssertTrue(FocusGeometry.isPlausibleCaret(CGRect(x: 300, y: 300, width: 2, height: 18), screenFrames: screens))
        XCTAssertTrue(FocusGeometry.isPlausibleCaret(CGRect(x: 300, y: 300, width: 0, height: 18), screenFrames: screens),
                      "零宽的插入点是常态")
        XCTAssertFalse(FocusGeometry.isPlausibleCaret(.zero, screenFrames: screens), "全零 = 应用没给")
        XCTAssertFalse(FocusGeometry.isPlausibleCaret(CGRect(x: 9000, y: 9000, width: 2, height: 18), screenFrames: screens))
        XCTAssertFalse(FocusGeometry.isPlausibleCaret(CGRect(x: 300, y: 300, width: 2, height: 900), screenFrames: screens),
                       "整块控件的矩形不是插入点")
        XCTAssertFalse(FocusGeometry.isPlausibleCaret(CGRect(x: CGFloat.nan, y: 0, width: 1, height: 18), screenFrames: screens))
    }

    func testOriginNearCaretPlacesBelowWithGap() {
        let caret = CGRect(x: 500, y: 400, width: 0, height: 18)
        let origin = FocusGeometry.originNearCaret(
            caret: caret, size: CGSize(width: 400, height: 100),
            visible: screens[0], gap: 8, margin: 16
        )
        XCTAssertEqual(origin.x, 300)
        XCTAssertEqual(origin.y, 400 - 8 - 100)
    }

    func testOriginNearCaretFlipsAboveWhenThereIsNoRoomBelow() {
        let caret = CGRect(x: 500, y: 60, width: 0, height: 18)
        let origin = FocusGeometry.originNearCaret(
            caret: caret, size: CGSize(width: 400, height: 100),
            visible: screens[0], gap: 8, margin: 16
        )
        XCTAssertEqual(origin.y, 60 + 18 + 8, "放不下就翻到插入点上方")
    }
}
