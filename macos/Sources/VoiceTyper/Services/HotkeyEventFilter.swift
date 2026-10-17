import Carbon.HIToolbox
import CoreGraphics
import Foundation

/// 「事件 → 动作」的纯值类型状态机（R8-B1）：不依赖 `CGEvent`，可脱离系统单测。
///
/// `HotkeyService` 只负责把 `CGEvent` 转成 `Input`、把 `Action` 派发到主线程、按 `consume`
/// 决定回调返回 `nil`（吞掉）还是原事件。与 `ModifierTapRecognizer` 同风格，
/// **只在事件 tap 的 worker 线程上使用**。
///
/// 吞键规则（只吞被 VoiceTyper 消费的按键，R8-D-4）：
/// - 组合键主键的 `keyDown`（含自动重复）与对应 `keyUp`；
/// - 本次触发了 `.cancel` 的 Esc 的 `keyDown` 与对应 `keyUp`；
/// - `flagsChanged`（Fn、单独修饰键、任何修饰键）、鼠标事件、其他按键**永不吞**——
///   吞掉修饰键变化会造成系统修饰键状态错乱。
///
/// 配对不变量：吞了 `keyDown` 就必须吞对应的 `keyUp`；放行了 `keyDown` 就必须放行 `keyUp`。
struct HotkeyEventFilter {
    enum Input {
        case keyDown(keyCode: Int64, flags: UInt64, isRepeat: Bool)
        case keyUp(keyCode: Int64)
        case flagsChanged(keyCode: Int64, flags: UInt64)
        case mouseDown
    }

    enum Action: Equatable {
        case press
        case release
        case cancel
        case gestureCancelled
    }

    struct Decision: Equatable {
        var actions: [Action] = []
        /// 是否不让事件继续传给前台应用。
        var consume = false
    }

    private let hotkey: HotkeyConfig
    private let targetKeyCode: Int64?
    private let isFn: Bool
    private var modifierRecognizer: ModifierTapRecognizer?
    /// 组合键 / Fn 的激活态。
    private var isActive = false
    /// 已吞掉 `keyDown` 的键码：其自动重复与 `keyUp` 必须同样被吞，保证配对。
    private var consumedKeyCodes: Set<Int64> = []

    init(hotkey: HotkeyConfig) {
        self.hotkey = hotkey
        self.modifierRecognizer = ModifierHotkey(rawValue: hotkey.key.lowercased()).map(ModifierTapRecognizer.init)
        self.isFn = hotkey.key.lowercased() == "fn"
        self.targetKeyCode = HotkeyService.keyCodeValue(for: hotkey.key)
    }

    mutating func handle(_ input: Input, acceptsCancelWhenInactive: Bool) -> Decision {
        switch input {
        case .keyDown(let keyCode, _, let isRepeat) where consumedKeyCodes.contains(keyCode):
            // 同一次物理按下的自动重复：继续吞。非重复的 keyDown 说明上一次的 keyUp 丢了
            // （tap 被系统短暂禁用等），清掉陈旧记录后按新的按下处理。
            if isRepeat { return Decision(actions: [], consume: true) }
            consumedKeyCodes.remove(keyCode)
        case .keyUp(let keyCode):
            // keyUp 照常走一遍逻辑（组合键的 release 在这里产生），再按配对不变量决定吞不吞。
            let wasConsumed = consumedKeyCodes.remove(keyCode) != nil
            var decision = route(input, acceptsCancelWhenInactive: acceptsCancelWhenInactive)
            decision.consume = decision.consume || wasConsumed
            return decision
        default:
            break
        }
        return route(input, acceptsCancelWhenInactive: acceptsCancelWhenInactive)
    }

    private mutating func route(_ input: Input, acceptsCancelWhenInactive: Bool) -> Decision {
        // 按 Esc → 取消本次听写。两种情况下生效：
        // (a) 热键仍处于激活态（录音中）；
        // (b) 已松开但控制器打开了 `acceptsCancelWhenInactive`（识别中，结果尚未上屏）。
        // 放在热键分支之前，Fn 与组合键两种模式都能触发。
        // 单独修饰键模式下"按住中"以识别器为准（含已作废但尚未抬起的阶段）。
        let isHolding = modifierRecognizer?.isHolding ?? isActive
        if isHolding || acceptsCancelWhenInactive,
           case .keyDown(let keyCode, _, _) = input,
           keyCode == Int64(kVK_Escape) {
            // 这个 Esc 同样要喂给识别器使手势作废，否则松开修饰键时会多发一个 `.ended`，
            // toggle 模式下会被误当作一次"单击"而开始新录音。
            _ = modifierRecognizer?.handle(.keyDown)
            consumedKeyCodes.insert(keyCode)
            return Decision(actions: [.cancel], consume: true)
        }

        if modifierRecognizer != nil {
            return handleModifier(input)
        }
        if isFn {
            return handleFn(input)
        }
        return handleCombo(input)
    }

    /// 把事件转成识别器输入：`.began → press`、`.ended → release`、`.cancelled → gestureCancelled`。
    /// "toggle 模式只有干净单击才算"由控制器处理。
    private mutating func handleModifier(_ input: Input) -> Decision {
        let recognizerInput: ModifierTapRecognizer.Input
        switch input {
        case .flagsChanged(let keyCode, let flags):
            recognizerInput = .flagsChanged(keyCode: keyCode, flags: flags)
        case .keyDown:
            recognizerInput = .keyDown
        case .mouseDown:
            recognizerInput = .mouseDown
        case .keyUp:
            return Decision()
        }
        guard let output = modifierRecognizer?.handle(recognizerInput) else { return Decision() }
        switch output {
        case .began: return Decision(actions: [.press])
        case .ended: return Decision(actions: [.release])
        case .cancelled: return Decision(actions: [.gestureCancelled])
        }
    }

    private mutating func handleFn(_ input: Input) -> Decision {
        guard case .flagsChanged(_, let flags) = input else { return Decision() }
        let isFnPressed = CGEventFlags(rawValue: flags).contains(.maskSecondaryFn)
        if isFnPressed && !isActive {
            isActive = true
            return Decision(actions: [.press])
        } else if !isFnPressed && isActive {
            isActive = false
            return Decision(actions: [.release])
        }
        return Decision()
    }

    private mutating func handleCombo(_ input: Input) -> Decision {
        guard let targetKeyCode else { return Decision() }
        switch input {
        case .keyDown(let keyCode, let flags, _):
            guard keyCode == targetKeyCode else { return Decision() }
            if isActive {
                // 已激活时同一个主键再来 = 自动重复（此时修饰键可能已经松开），不能漏给前台应用。
                consumedKeyCodes.insert(keyCode)
                return Decision(actions: [], consume: true)
            }
            if Self.modifiersMatch(expected: hotkey.modifiers, flags: CGEventFlags(rawValue: flags)) {
                isActive = true
                consumedKeyCodes.insert(keyCode)
                return Decision(actions: [.press], consume: true)
            }
        case .keyUp(let keyCode):
            if keyCode == targetKeyCode && isActive {
                isActive = false
                return Decision(actions: [.release])
            }
        default:
            break
        }
        return Decision()
    }

    static func modifiersMatch(expected: [String], flags: CGEventFlags) -> Bool {
        let normalized = Set(expected.map { $0.lowercased() })
        let expectedCommand = normalized.contains("cmd") || normalized.contains("command")
        let expectedControl = normalized.contains("ctrl") || normalized.contains("control")
        let expectedOption = normalized.contains("alt") || normalized.contains("option")
        let expectedShift = normalized.contains("shift")

        return expectedCommand == flags.contains(.maskCommand) &&
            expectedControl == flags.contains(.maskControl) &&
            expectedOption == flags.contains(.maskAlternate) &&
            expectedShift == flags.contains(.maskShift)
    }
}
