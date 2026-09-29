import Foundation

/// 可单独作为热键的修饰键（`hotkey.key` 的取值）。
///
/// 刻意不支持：左 ⌘（参与的快捷键太多）、Shift（中文输入法里单击 Shift 切换中英文，冲突
/// 无法避免）、左 ⌃（部分用户用它做 Emacs 风格编辑）。
enum ModifierHotkey: String, CaseIterable {
    case rightCommand = "right_command"
    case rightOption = "right_option"
    case leftOption = "left_option"
    case rightControl = "right_control"

    /// 虚拟键码（NSEvent / CGEvent 的 keyCode）：靠它区分左右，不能只看通用修饰位。
    var keyCode: Int64 {
        switch self {
        case .rightCommand: return 54
        case .rightOption: return 61
        case .leftOption: return 58
        case .rightControl: return 62
        }
    }

    /// 该键在 CGEventFlags 低位里的"设备相关"标志（IOKit `NX_DEVICE*KEYMASK`）。
    var deviceMask: UInt64 {
        switch self {
        case .rightCommand: return 0x10
        case .rightOption: return 0x40
        case .leftOption: return 0x20
        case .rightControl: return 0x2000
        }
    }

    /// 对应的通用修饰位（`CGEventFlags.maskCommand` 等）。
    var genericMask: UInt64 {
        switch self {
        case .rightCommand: return 0x100000
        case .rightOption, .leftOption: return 0x80000
        case .rightControl: return 0x40000
        }
    }

    var displayName: String {
        switch self {
        case .rightCommand: return L("右 ⌘")
        case .rightOption: return L("右 ⌥")
        case .leftOption: return L("左 ⌥")
        case .rightControl: return L("右 ⌃")
        }
    }

    /// 通用修饰位里"会让一次单击作废"的全集：⌘ ⌥ ⌃ ⇧ Fn。
    /// 不含 Caps Lock（`0x10000`）与小键盘位（`0x200000`）——它们是状态位而非"按住了另一个键"。
    static let relevantGenericMask: UInt64 = 0x100000 | 0x80000 | 0x40000 | 0x20000 | 0x800000

    /// 全部左右侧修饰键的设备位（⌃ ⇧ ⌘ ⌥ 的左右）。
    static let allDeviceMasks: UInt64 = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x40 | 0x2000

    static func from(keyCode: Int64) -> ModifierHotkey? {
        allCases.first { $0.keyCode == keyCode }
    }
}

/// 单独修饰键"干净单击"的识别状态机：纯值类型，无副作用，不依赖 `CGEvent`，可脱离系统单测。
///
/// - 开始：目标键按下，且此刻没有任何其他修饰键被按住 → `.began`
/// - 作废：按住期间出现任意 keyDown、鼠标按下或另一个修饰键 → `.cancelled`（每次手势最多一次）
/// - 结束：目标键抬起；仍干净则 `.ended`，已作废则无输出
struct ModifierTapRecognizer {
    enum Input {
        case flagsChanged(keyCode: Int64, flags: UInt64)
        case keyDown
        case mouseDown
    }

    enum Output: Equatable {
        case began
        case cancelled
        case ended
    }

    private enum State {
        case idle
        case holdingClean
        case invalidated
    }

    let target: ModifierHotkey
    private var state = State.idle

    init(target: ModifierHotkey) {
        self.target = target
    }

    /// 目标键是否处于按住（含已作废但尚未抬起）。Esc 取消的生效窗口以它为准。
    var isHolding: Bool { state != .idle }

    mutating func handle(_ input: Input) -> Output? {
        switch (state, input) {
        case (.idle, .flagsChanged(let keyCode, let flags)):
            guard keyCode == target.keyCode,
                  flags & target.deviceMask != 0,
                  othersAreClear(in: flags) else { return nil }
            state = .holdingClean
            return .began

        case (.idle, _):
            return nil

        case (.holdingClean, .keyDown), (.holdingClean, .mouseDown):
            state = .invalidated
            return .cancelled

        case (.holdingClean, .flagsChanged(let keyCode, let flags)):
            if keyCode == target.keyCode {
                guard flags & target.deviceMask == 0 else { return nil }
                state = .idle
                return .ended
            }
            guard !othersAreClear(in: flags) else { return nil }
            state = .invalidated
            return .cancelled

        case (.invalidated, .flagsChanged(let keyCode, let flags)):
            if keyCode == target.keyCode, flags & target.deviceMask == 0 {
                state = .idle
            }
            return nil

        case (.invalidated, _):
            return nil
        }
    }

    /// 除目标键自身外没有其他修饰键：通用位只允许目标对应的那一位，
    /// 且所有其他左右侧设备位都为 0（同一修饰键的另一侧同样算"其他"）。
    private func othersAreClear(in flags: UInt64) -> Bool {
        let extraGeneric = flags & ModifierHotkey.relevantGenericMask & ~target.genericMask
        let extraDevice = flags & ModifierHotkey.allDeviceMasks & ~target.deviceMask
        return extraGeneric == 0 && extraDevice == 0
    }
}
