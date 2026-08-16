import AppKit
import SwiftUI

/// 按键录制控件的底层 AppKit 视图。点击进入录制态，捕获下一个组合键或单独的 Fn。
/// 需要 AppKit 而非纯 SwiftUI，因为要监听 flagsChanged 才能捕获 Fn🌐 键。
@MainActor
final class HotkeyRecorderView: NSView {
    var onBeginRecording: (() -> Void)?
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
        isRecording = true
        onBeginRecording?()
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
            if code == escKeyCode {
                cancelRecording()
                return
            }
            if code == deleteKeyCode || code == forwardDeleteKeyCode {
                finish(HotkeyConfig(modifiers: [], key: "fn"))
                return
            }
            guard let name = HotkeyService.keyName(for: code) else {
                label.stringValue = "不支持该键，请重试"
                return
            }
            finish(HotkeyConfig(modifiers: modifiers(from: event.modifierFlags), key: name))
        case .flagsChanged:
            // 仅在按下单独的 Fn🌐 键（无其他修饰）时捕获。
            let flags = event.modifierFlags
            let others: NSEvent.ModifierFlags = [.command, .control, .option, .shift]
            if event.keyCode == fnKeyCode, flags.contains(.function), flags.isDisjoint(with: others) {
                finish(HotkeyConfig(modifiers: [], key: "fn"))
            }
        default:
            break
        }
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
        if let monitor {
            NSEvent.removeMonitor(monitor)
            self.monitor = nil
        }
    }

    private func updateAppearance() {
        label.stringValue = isRecording ? "按下快捷键…" : config.displayString
        label.textColor = isRecording ? .controlAccentColor : .labelColor
        layer?.borderColor = (isRecording ? NSColor.controlAccentColor : NSColor.separatorColor).cgColor
        layer?.borderWidth = isRecording ? 2 : 1
    }
}

/// SwiftUI 包装。
struct HotkeyRecorder: NSViewRepresentable {
    let config: HotkeyConfig
    let onBegin: () -> Void
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
