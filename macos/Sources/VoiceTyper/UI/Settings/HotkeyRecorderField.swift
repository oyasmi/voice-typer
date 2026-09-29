import AppKit
import SwiftUI

/// 按键录制控件的底层 AppKit 视图。点击进入录制态，捕获下一个组合键或单独的 Fn。
/// 需要 AppKit 而非纯 SwiftUI，因为要监听 flagsChanged 才能捕获 Fn🌐 键。
@MainActor
final class HotkeyRecorderView: NSView {
    /// 返回 false 表示挂起全局热键的请求被拒绝（例如正在听写中）：不得进入录制态。
    var onBeginRecording: (() -> Bool)?
    var onCapture: ((HotkeyConfig) -> Void)?
    var onCancelRecording: (() -> Void)?

    var config = HotkeyConfig() {
        didSet { updateAppearance() }
    }

    private(set) var isRecording = false
    private var monitor: Any?
    private let label = NSTextField(labelWithString: "")

    private let escKeyCode: UInt16 = 53
    private let deleteKeyCode: UInt16 = 51
    private let forwardDeleteKeyCode: UInt16 = 117
    private let fnKeyCode: UInt16 = 63

    /// 单独按下并抬起的"候选"修饰键：抬起前没有出现别的键，就把它录成单独修饰键热键。
    private var candidate: ModifierHotkey?
    /// 单独按下了一个不支持单独使用的修饰键（左 ⌘ / Shift / 左 ⌃），抬起时给出提示而不是静默忽略。
    private var pendingUnsupportedKeyCode: UInt16?
    /// 不支持单独使用的修饰键的设备位：左 ⌃ / 左 ⇧ / 右 ⇧ / 左 ⌘。
    private static let unsupportedModifierMasks: [UInt16: UInt64] = [59: 0x1, 56: 0x2, 60: 0x4, 55: 0x8]

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setup()
    }

    required init?(coder: NSCoder) { fatalError() }

    override var intrinsicContentSize: NSSize { NSSize(width: NSView.noIntrinsicMetric, height: 30) }

    override var acceptsFirstResponder: Bool { true }

    private func setup() {
        wantsLayer = true
        layer?.cornerRadius = 6
        layer?.borderWidth = 1
        layer?.borderColor = NSColor.separatorColor.cgColor
        layer?.backgroundColor = NSColor.controlBackgroundColor.cgColor

        label.font = .monospacedSystemFont(ofSize: 13, weight: .medium)
        label.alignment = .center
        label.translatesAutoresizingMaskIntoConstraints = false
        addSubview(label)
        NSLayoutConstraint.activate([
            label.centerXAnchor.constraint(equalTo: centerXAnchor),
            label.centerYAnchor.constraint(equalTo: centerYAnchor),
            label.leadingAnchor.constraint(greaterThanOrEqualTo: leadingAnchor, constant: 8),
            label.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor, constant: -8),
        ])
        updateAppearance()
    }

    override func mouseDown(with event: NSEvent) {
        if isRecording {
            cancelRecording()
        } else {
            startRecording()
        }
    }

    override func viewWillMove(toWindow newWindow: NSWindow?) {
        super.viewWillMove(toWindow: newWindow)
        // 窗口关闭或视图移除时若仍在录制，务必停止并恢复全局热键。
        if newWindow == nil, isRecording {
            cancelRecording()
        }
    }

    private func startRecording() {
        guard !isRecording else { return }
        guard onBeginRecording?() == true else { return }
        isRecording = true
        resetModifierCandidates()
        updateAppearance()
        window?.makeFirstResponder(self)
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .flagsChanged]) { [weak self] event in
            self?.handle(event)
            return nil  // 录制期间吞掉事件，避免误触发前台应用
        }
    }

    private func cancelRecording() {
        stopMonitor()
        isRecording = false
        updateAppearance()
        onCancelRecording?()
    }

    /// 捕获成功。不在此处触发"恢复热键"——保存新热键会重建并重启控制器，即完成恢复，
    /// 避免与恢复路径产生两条并发的 reevaluate 竞争。
    private func finish(_ config: HotkeyConfig) {
        stopMonitor()
        isRecording = false
        self.config = config
        updateAppearance()
        onCapture?(config)
    }

    private func handle(_ event: NSEvent) {
        switch event.type {
        case .keyDown:
            let code = event.keyCode
            // 候选期间收到 keyDown：用户在按组合键，按现有逻辑处理。
            resetModifierCandidates()
            if code == escKeyCode {
                cancelRecording()
                return
            }
            if code == deleteKeyCode || code == forwardDeleteKeyCode {
                finish(HotkeyConfig(modifiers: [], key: "fn"))
                return
            }
            guard let name = HotkeyService.keyName(for: code) else {
                label.stringValue = L("不支持该键，请重试")
                return
            }
            let mods = modifiers(from: event.modifierFlags)
            // 非 fn 主键必须至少带一个修饰键：热键 tap 是 listenOnly（不吞事件），裸键
            // 一旦生效，此后每次在任何应用里打这个字母都会同时触发录音，且用户很可能
            // 因此无法再用键盘正常操作、包括打开设置页改回来（R3-05）。
            guard !mods.isEmpty else {
                label.stringValue = L("请至少同时按住一个修饰键（⌘/⌃/⌥/⇧）")
                return
            }
            finish(HotkeyConfig(modifiers: mods, key: name))
        case .flagsChanged:
            // 仅在按下单独的 Fn🌐 键（无其他修饰）时捕获。
            let flags = event.modifierFlags
            let others: NSEvent.ModifierFlags = [.command, .control, .option, .shift]
            if event.keyCode == fnKeyCode {
                if flags.contains(.function), flags.isDisjoint(with: others) {
                    finish(HotkeyConfig(modifiers: [], key: "fn"))
                }
                return
            }
            handleModifierChange(event)
        default:
            break
        }
    }

    /// 单独修饰键的录制：按下受支持的修饰键（且无其他修饰）记为候选，抬起时才确认。
    /// 左右侧靠 keyCode 与设备位判断，不能只看 `modifierFlags`（左右 ⌘ 的通用位相同）。
    private func handleModifierChange(_ event: NSEvent) {
        let raw = UInt64(event.modifierFlags.rawValue)
        let code = event.keyCode

        if let candidate {
            if candidate.keyCode == Int64(code) {
                guard raw & candidate.deviceMask == 0 else { return }
                self.candidate = nil
                finish(HotkeyConfig(modifiers: [], key: candidate.rawValue))
            } else {
                // 又按了别的修饰键：用户在按组合键，放弃候选，等待后续的主键。
                resetModifierCandidates()
                label.stringValue = L("按下快捷键…")
            }
            return
        }

        if let pending = pendingUnsupportedKeyCode {
            if pending == code {
                guard raw & (Self.unsupportedModifierMasks[code] ?? 0) == 0 else { return }
                pendingUnsupportedKeyCode = nil
                label.stringValue = L("不支持单独使用该键，推荐右 ⌘ 或右 ⌥。")
            } else {
                pendingUnsupportedKeyCode = nil
            }
            return
        }

        let othersDown = { (own: UInt64) in raw & ModifierHotkey.allDeviceMasks & ~own != 0 }
        if let modifier = ModifierHotkey.from(keyCode: Int64(code)),
           raw & modifier.deviceMask != 0, !othersDown(modifier.deviceMask) {
            candidate = modifier
            label.stringValue = LF("松开以使用 %@，或继续按下组合键…", modifier.displayName)
        } else if let mask = Self.unsupportedModifierMasks[code], raw & mask != 0, !othersDown(mask) {
            pendingUnsupportedKeyCode = code
        }
    }

    private func resetModifierCandidates() {
        candidate = nil
        pendingUnsupportedKeyCode = nil
    }

    private func modifiers(from flags: NSEvent.ModifierFlags) -> [String] {
        let f = flags.intersection(.deviceIndependentFlagsMask)
        var result: [String] = []
        if f.contains(.control) { result.append("ctrl") }
        if f.contains(.option) { result.append("option") }
        if f.contains(.command) { result.append("command") }
        if f.contains(.shift) { result.append("shift") }
        return result
    }

    private func stopMonitor() {
        resetModifierCandidates()
        if let monitor {
            NSEvent.removeMonitor(monitor)
            self.monitor = nil
        }
    }

    private func updateAppearance() {
        label.stringValue = isRecording ? L("按下快捷键…") : config.displayString
        label.textColor = isRecording ? .controlAccentColor : .labelColor
        layer?.borderColor = (isRecording ? NSColor.controlAccentColor : NSColor.separatorColor).cgColor
        layer?.borderWidth = isRecording ? 2 : 1
    }
}

/// SwiftUI 包装。
struct HotkeyRecorder: NSViewRepresentable {
    let config: HotkeyConfig
    let onBegin: () -> Bool
    let onCapture: (HotkeyConfig) -> Void
    let onCancel: () -> Void

    func makeNSView(context: Context) -> HotkeyRecorderView {
        let view = HotkeyRecorderView()
        view.onBeginRecording = onBegin
        view.onCapture = onCapture
        view.onCancelRecording = onCancel
        view.config = config
        return view
    }

    func updateNSView(_ view: HotkeyRecorderView, context: Context) {
        view.onBeginRecording = onBegin
        view.onCapture = onCapture
        view.onCancelRecording = onCancel
        // 录制期间不要用外部 config 覆盖正在捕获的状态。
        if !view.isRecording {
            view.config = config
        }
    }
}
