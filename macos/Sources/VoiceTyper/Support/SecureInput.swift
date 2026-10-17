import AppKit
import Carbon
import Foundation
import IOKit

/// 系统「安全输入」（Secure Event Input）状态（R8-B2）。
///
/// 密码框、终端的「安全键盘输入」、1Password 等开启它时，事件 tap 收不到 `keyDown/keyUp`，
/// 组合键热键会静默失效；修饰键的 `flagsChanged` 通常仍能收到（待真机确认）。
enum SecureInput {
    static func isEnabled() -> Bool {
        IsSecureEventInputEnabled()
    }

    /// 开启安全输入的进程。尽力而为：读 IORegistry 根节点的 `IOConsoleUsers`，取当前控制台用户字典里的
    /// `kCGSSessionSecureInputPID`；任何一步失败返回 nil（字段是否存在需真机确认）。
    static func ownerPID() -> pid_t? {
        let root = IORegistryGetRootEntry(kIOMainPortDefault)
        guard root != 0 else { return nil }
        defer { IOObjectRelease(root) }
        guard let users = IORegistryEntryCreateCFProperty(
            root, "IOConsoleUsers" as CFString, kCFAllocatorDefault, 0
        )?.takeRetainedValue() as? [[String: Any]] else {
            return nil
        }
        let current = users.first { ($0["kCGSSessionOnConsoleKey"] as? Bool) == true } ?? users.first
        guard let pid = current?["kCGSSessionSecureInputPID"] as? Int, pid > 0 else { return nil }
        return pid_t(pid)
    }

    /// 当前热键下是否需要向用户说明安全输入（现算，成本是一次 IORegistry 读取）。
    @MainActor
    static func currentWarning(for hotkey: HotkeyConfig) -> String? {
        guard hotkey.kind == .combo, isEnabled() else { return nil }
        let name = ownerPID().flatMap { NSRunningApplication(processIdentifier: $0)?.localizedName }
        return warning(isEnabled: true, ownerName: name, hotkeyIsCombo: true)
    }

    /// 菜单头 / 设置页 / 引导共用的说明文案。`nil` = 不需要说明。
    ///
    /// 只有组合键热键会受影响：Fn 与单独修饰键走 `flagsChanged`，不依赖 keyDown。
    static func warning(isEnabled: Bool, ownerName: String?, hotkeyIsCombo: Bool) -> String? {
        guard isEnabled, hotkeyIsCombo else { return nil }
        return LF("「%@」开启了安全输入，组合键热键暂时收不到", ownerName ?? L("某个应用"))
    }
}
