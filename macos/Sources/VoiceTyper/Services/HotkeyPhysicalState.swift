import CoreGraphics
import Foundation

/// 热键此刻在物理上是否仍被按住（R8-B3 卡键看门狗用）。
///
/// 事件 tap 偶尔会丢事件（被系统短暂禁用、睡眠唤醒…），于是「松开」永远没到，hold 模式的录音
/// 会一直录下去。看门狗每 250ms 读一次修饰键的物理状态，连续两次判定已松开就按松键处理。
enum HotkeyPhysicalState {
    /// 当前修饰键状态（含设备相关的左右位）。
    static func currentFlags() -> UInt64 {
        CGEventSource.flagsState(.combinedSessionState).rawValue
    }

    /// - Fn：`maskSecondaryFn`。
    /// - 单独修饰键：设备相关的左右位（复用 `ModifierHotkey` 的位定义）。
    /// - 组合键：所需修饰键都还按着——**不检查主键**：被吞的主键不一定更新物理状态，
    ///   而主键松开时本来就有 keyUp 事件，丢的主要是修饰键侧的事件。
    static func isHeld(_ hotkey: HotkeyConfig, flags: UInt64) -> Bool {
        let key = hotkey.key.lowercased()
        if let modifier = ModifierHotkey(rawValue: key) {
            return flags & modifier.deviceMask != 0
        }
        if key == "fn" {
            return CGEventFlags(rawValue: flags).contains(.maskSecondaryFn)
        }
        let required = Set(hotkey.modifiers.map { $0.lowercased() })
        let cgFlags = CGEventFlags(rawValue: flags)
        if (required.contains("cmd") || required.contains("command")) && !cgFlags.contains(.maskCommand) { return false }
        if (required.contains("ctrl") || required.contains("control")) && !cgFlags.contains(.maskControl) { return false }
        if (required.contains("alt") || required.contains("option")) && !cgFlags.contains(.maskAlternate) { return false }
        if required.contains("shift") && !cgFlags.contains(.maskShift) { return false }
        return true
    }
}
