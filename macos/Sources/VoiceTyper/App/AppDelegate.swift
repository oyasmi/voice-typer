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

    /// 点 Dock / Launchpad / 访达里的图标。
    ///
    /// 无条件把窗口带到前台，不再看 `hasVisibleWindows`：窗口"存在"不等于用户看得见，
    /// 它可能在另一个 Space、被别的窗口盖住，或者只是没被激活。此前 `flag == true`
    /// 时这里什么都不做，用户反复点图标毫无反应（B5）。
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        guard NSClassFromString("XCTestCase") == nil else { return false }
        coordinator.handleReopen()
        return false
    }
}
