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

    func testCenteredScaleOfOneIsIdentity() {
        XCTAssertTrue(CATransform3DIsIdentity(HUDMotion.centeredScale(1, in: CGRect(x: 0, y: 0, width: 120, height: 40))))
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

    func testFullMotionUsesSpringAndInfiniteShimmer() throws {
        HUDMotion.reduceMotionProvider = { false }
        let bounds = CGRect(x: 0, y: 0, width: 100, height: 20)

        XCTAssertNotNil(HUDMotion.exhaleAnimation(bounds: bounds))

        let group = try XCTUnwrap(HUDMotion.popInAnimation(bounds: bounds) as? CAAnimationGroup)
        let spring = try XCTUnwrap(group.animations?.compactMap { $0 as? CASpringAnimation }.first)
        XCTAssertEqual(spring.damping, HUDMotion.springDamping)
        XCTAssertEqual(spring.stiffness, HUDMotion.springStiffness)
        XCTAssertEqual(spring.mass, HUDMotion.springMass)

        let mask = try XCTUnwrap(HUDMotion.shimmerMask(for: bounds))
        XCTAssertEqual(mask.colors?.count, 3)
        let sweep = try XCTUnwrap(mask.animation(forKey: "shimmer") as? CABasicAnimation)
        XCTAssertEqual(sweep.keyPath, "locations")
        XCTAssertEqual(sweep.repeatCount, .infinity)
        XCTAssertEqual(sweep.duration, HUDMotion.shimmerDuration)
    }
}
