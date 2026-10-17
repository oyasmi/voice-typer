import XCTest
@testable import VoiceTyper

/// R8-A2：协调器只持有一个文本插入服务。`pendingRestore`（待恢复的用户原剪贴板）住在它里面，
/// 每次保存设置重建控制器都新建一个，会让待恢复的原剪贴板随之丢失。
@MainActor
final class AppCoordinatorInsertionServiceTests: XCTestCase {
    func testRebuiltControllersShareTheSingleTextInsertionService() {
        let coordinator = AppCoordinator()
        let first = coordinator.makeController()
        let second = coordinator.makeController()

        XCTAssertTrue(first.textInsertionService === coordinator.textInsertionService)
        XCTAssertTrue(second.textInsertionService === coordinator.textInsertionService)
        XCTAssertEqual(
            ObjectIdentifier(first.textInsertionService), ObjectIdentifier(second.textInsertionService)
        )
    }
}
