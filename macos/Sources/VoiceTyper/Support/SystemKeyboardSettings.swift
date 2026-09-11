import Foundation

/// macOS「系统设置 → 键盘 → 按下🌐键」的取值。
///
/// 为什么需要它：本应用默认热键是 Fn🌐，而 `HotkeyService` 的事件 tap 是
/// `.listenOnly`（**故意**不吞事件，否则会破坏所有依赖这些按键的正常输入）。
/// 结果是按住 Fn 说话、松开的一瞬间，系统自己的 Fn 行为**也会照常触发**——
/// 默认配置下就是弹出表情面板或切走输入法。用户看到的现象是"一说话就蹦出 Emoji 面板"，
/// 而这和 VoiceTyper 的代码毫无关系，靠自己几乎不可能定位到是键盘设置的问题。
///
/// 因此在引导与设置页主动检测并给出直达按钮，而不是等用户自己撞上去。
enum FnKeyUsage: Equatable {
    /// 不执行任何操作 —— 与 Fn 热键无冲突，这是我们推荐的设置。
    case doNothing
    case changeInputSource
    case showEmojiAndSymbols
    case startDictation
    /// 读到了一个当前系统版本里未知的取值。
    case unknown(Int)
    /// 该偏好项不存在（从未改过，或系统版本不提供）。此时系统默认行为随机型/版本而异，
    /// 无法断定安全，一律按"可能冲突"处理。
    case unspecified

    /// 是否与 Fn 热键冲突。`unspecified` 保守地算作冲突：宁可多提示一次，
    /// 也好过让用户对着弹出的表情面板一头雾水。
    var conflictsWithFnHotkey: Bool {
        switch self {
        case .doNothing:
            return false
        case .changeInputSource, .showEmojiAndSymbols, .startDictation, .unknown, .unspecified:
            return true
        }
    }

    /// 当前设置的中文描述，用于把提示落到用户在系统设置里真正看到的那几个字上。
    var displayName: String {
        switch self {
        case .doNothing: return L("不执行任何操作")
        case .changeInputSource: return L("更改输入法")
        case .showEmojiAndSymbols: return L("显示表情与符号")
        case .startDictation: return L("开始听写")
        case .unknown(let raw): return LF("未知设置（%d）", raw)
        case .unspecified: return L("系统默认")
        }
    }
}

enum SystemKeyboardSettings {
    /// 「按下🌐键」的取值键名。这是一个未公开的偏好项，因此所有分支都必须能容忍
    /// "读不到"或"读到没见过的值"，绝不能据此阻断任何流程——它只用来决定要不要多显示
    /// 一条提示。
    private static let fnUsageKey = "AppleFnUsageType"

    /// 实测（macOS 15）该项写在 `com.apple.HIToolbox`，而非 NSGlobalDomain。
    /// 老系统上曾出现在全局域，因此两个域都查一遍：先 HIToolbox，读不到再退回全局域。
    private static let fnUsageDomains = ["com.apple.HIToolbox", kCFPreferencesAnyApplication as String]

    /// 读取当前的「按下🌐键」设置。
    ///
    /// 用 `CFPreferences` 而不是 `UserDefaults.standard`：用户会在引导页与系统设置之间
    /// 来回切换，需要每次都读到最新值。`CFPreferencesAppSynchronize` 会丢弃本进程缓存，
    /// 让紧随其后的读取反映用户刚刚在系统设置里做的修改。
    static func fnKeyUsage() -> FnKeyUsage {
        var raw: Int?
        for domainName in fnUsageDomains {
            let domain = domainName as CFString
            _ = CFPreferencesAppSynchronize(domain)
            if let value = CFPreferencesCopyAppValue(fnUsageKey as CFString, domain),
               let number = value as? NSNumber {
                raw = number.intValue
                break
            }
        }
        guard let intValue = raw else {
            return .unspecified
        }
        switch intValue {
        case 0: return .doNothing
        case 1: return .changeInputSource
        case 2: return .showEmojiAndSymbols
        case 3: return .startDictation
        case let other: return .unknown(other)
        }
    }

    /// 仅在当前热键确实是 Fn 时才需要提示；用户改成组合键后这条提示应当消失。
    static func fnConflictWarning(for hotkey: HotkeyConfig) -> String? {
        guard hotkey.key.lowercased() == "fn" else { return nil }
        let usage = fnKeyUsage()
        guard usage.conflictsWithFnHotkey else { return nil }
        let current = usage == .unspecified
            ? L("当前为系统默认设置")
            : LF("当前是「%@」", usage.displayName)
        return LF("「系统设置 → 键盘 → 按下🌐键」建议设为「不执行任何操作」（%@）。否则按住 Fn 说话时，系统自己的 Fn 功能也会同时触发——松手的一瞬间可能弹出表情面板或切走输入法。", current)
    }
}
