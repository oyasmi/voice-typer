import AppKit
import Foundation

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let coordinator = AppCoordinator()

    func applicationDidFinishLaunching(_ notification: Notification) {
        // 测试宿主隔离，不是功能开关：hosted unit test 会真实启动这个 App，
        // 若不短路会读写用户真实的配置目录、探测 TCC 权限、弹出 Keychain 授权对话框
        // （R2-02）。真正的功能测试改为直接构造被测类型，不依赖启动整个 App。
        guard NSClassFromString("XCTestCase") == nil else { return }
        coordinator.start()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag {
            coordinator.openSetupWindow()
        }
        return false
    }
}
