import AppKit
import QuartzCore
import XCTest
@testable import VoiceTyper

@MainActor
final class HUDMotionTests: XCTestCase {
    private var originalProvider: (() -> Bool)?

    override func setUp() async throws {
        originalProvider = HUDMotion.reduceMotionProvider
    }

    override func tearDown() async throws {
        if let originalProvider { HUDMotion.reduceMotionProvider = originalProvider }
    }

    func testCenteredScaleKeepsCenterFixedAndPullsCornersInward() {
        let bounds = CGRect(x: 0, y: 0, width: 100, height: 40)
        let transform = CATransform3DGetAffineTransform(HUDMotion.centeredScale(0.5, in: bounds))

        let center = CGPoint(x: 50, y: 20).applying(transform)
        XCTAssertEqual(center.x, 50, accuracy: 1e-9)
        XCTAssertEqual(center.y, 20, accuracy: 1e-9)

        let corner = CGPoint(x: 0, y: 0).applying(transform)
        XCTAssertEqual(corner.x, 25, accuracy: 1e-9)
        XCTAssertEqual(corner.y, 10, accuracy: 1e-9)
    }

    func testReduceMotionDisablesScaleSpringAndShimmer() {
        HUDMotion.reduceMotionProvider = { true }
        let bounds = CGRect(x: 0, y: 0, width: 100, height: 20)

        XCTAssertNil(HUDMotion.exhaleAnimation(bounds: bounds))
        XCTAssertNil(HUDMotion.shimmerMask(for: bounds))

        let popIn = HUDMotion.popInAnimation(bounds: bounds)
        let fade = popIn as? CABasicAnimation
        XCTAssertEqual(fade?.keyPath, "opacity", "减少动态效果时只允许透明度过渡")
        XCTAssertLessThanOrEqual(popIn.duration, HUDMotion.reducedMotionDuration)

        let rise = HUDMotion.textRiseAnimation()
        XCTAssertEqual((rise as? CABasicAnimation)?.keyPath, "opacity")
    }

}
