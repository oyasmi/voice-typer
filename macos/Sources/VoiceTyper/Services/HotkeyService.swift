import Carbon.HIToolbox
import CoreGraphics
import Foundation

enum HotkeyServiceError: LocalizedError {
    case unsupportedKey(String)
    case inputMonitoringDenied
    case startupTimedOut
    case startupFailed(String)

    var errorDescription: String? {
        switch self {
        case .unsupportedKey(let key):
            return "不支持的热键: \(key)"
        case .inputMonitoringDenied:
            return "输入监控权限缺失，无法启动热键监听"
        case .startupTimedOut:
            return "热键监听启动超时"
        case .startupFailed(let message):
            return message
        }
    }
}

private final class StartupResultBox: @unchecked Sendable {
    let semaphore = DispatchSemaphore(value: 0)
    var error: Error?
}

/// 事件 tap 回调上下文。通过 weak 引用避免悬空指针崩溃。
/// 内存由 `Unmanaged.passRetained` 管理，在 worker 线程退出时释放。
private final class TapContext: @unchecked Sendable {
    weak var service: HotkeyService?
    init(_ service: HotkeyService) {
        self.service = service
    }
}

final class HotkeyService: @unchecked Sendable {
    /// 热键按下回调。保证在主线程触发。
    var onPress: (() -> Void)?
    /// 热键松开回调。保证在主线程触发。
    var onRelease: (() -> Void)?
    /// 录音进行中按下 Esc 的取消回调。保证在主线程触发。
    var onCancel: (() -> Void)?

    private var eventTap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    private var runLoop: CFRunLoop?
    private var workerThread: Thread?
    private var hotkey: HotkeyConfig?
    private var isActive = false
    private var isRunning = false
    private let shutdownSemaphore = DispatchSemaphore(value: 0)

    func start(with hotkey: HotkeyConfig) throws {
        stop()
        if hotkey.key.lowercased() != "fn", Self.keyCode(for: hotkey.key) == nil {
            throw HotkeyServiceError.unsupportedKey(hotkey.key)
        }

        self.hotkey = hotkey
        let context = TapContext(self)
        let startupBox = StartupResultBox()
        self.workerThread = Thread { [weak self] in
            self?.runEventLoop(startupBox: startupBox, context: context)
        }
        workerThread?.name = "VoiceTyper.HotkeyService"
        workerThread?.start()

        if startupBox.semaphore.wait(timeout: .now() + 2) == .timedOut {
            stop()
            throw HotkeyServiceError.startupTimedOut
        }

        if let error = startupBox.error {
            stop()
            throw error
        }

        isRunning = true
    }

    func stop() {
        // 禁用 tap 阻止新事件进入回调
        if let eventTap {
            CGEvent.tapEnable(tap: eventTap, enable: false)
        }

        // 通知 worker 线程退出 RunLoop
        if let runLoop {
            CFRunLoopStop(runLoop)
        }

        // 等待 worker 线程完成清理（超时 2 秒防止死锁）
        if workerThread != nil {
            _ = shutdownSemaphore.wait(timeout: .now() + 2)
        }

        // worker 线程已退出，安全清理主线程侧引用
        eventTap = nil
        runLoopSource = nil
        self.runLoop = nil
        workerThread = nil
        hotkey = nil
        isActive = false
        isRunning = false
    }

    private func runEventLoop(startupBox: StartupResultBox, context: TapContext) {
        let mask =
            (1 << CGEventType.keyDown.rawValue) |
            (1 << CGEventType.keyUp.rawValue) |
            (1 << CGEventType.flagsChanged.rawValue)

        // 使用 passRetained 确保 context 在事件 tap 存活期间不会被释放
        let contextPtr = Unmanaged.passRetained(context).toOpaque()

        let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .listenOnly,
            eventsOfInterest: CGEventMask(mask),
            callback: { _, type, event, userInfo in
                guard let userInfo else {
                    return Unmanaged.passUnretained(event)
                }
                let ctx = Unmanaged<TapContext>.fromOpaque(userInfo).takeUnretainedValue()
                // 系统在回调超时等情况下会禁用 tap；若不重新启用，全局热键会静默失效
                // 直到重启应用。这里检测到禁用事件立即恢复。
                if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
                    ctx.service?.reenableTap()
                    return Unmanaged.passUnretained(event)
                }
                ctx.service?.handle(eventType: type, event: event)
                return Unmanaged.passUnretained(event)
            },
            userInfo: contextPtr
        )

        guard let tap else {
            AppLog.hotkey.error("无法创建事件监听，通常意味着输入监控权限缺失")
            Unmanaged<TapContext>.fromOpaque(contextPtr).release()
            startupBox.error = HotkeyServiceError.inputMonitoringDenied
            startupBox.semaphore.signal()
            shutdownSemaphore.signal()
            return
        }

        self.eventTap = tap
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        guard let source else {
            CFMachPortInvalidate(tap)
            Unmanaged<TapContext>.fromOpaque(contextPtr).release()
            startupBox.error = HotkeyServiceError.startupFailed("无法创建热键事件源")
            startupBox.semaphore.signal()
            shutdownSemaphore.signal()
            return
        }

        self.runLoopSource = source
        let currentRunLoop = CFRunLoopGetCurrent()
        self.runLoop = currentRunLoop

        CFRunLoopAddSource(currentRunLoop, source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        startupBox.semaphore.signal()
        CFRunLoopRun()

        // worker 线程退出，执行所有本地资源清理
        CFRunLoopRemoveSource(currentRunLoop, source, .commonModes)
        CFMachPortInvalidate(tap)
        Unmanaged<TapContext>.fromOpaque(contextPtr).release()
        shutdownSemaphore.signal()
    }

    /// 重新启用被系统禁用的事件 tap。运行在 tap 回调线程（worker 线程），
    /// 与 `eventTap` 的赋值同线程，无需额外同步。
    fileprivate func reenableTap() {
        guard let eventTap else { return }
        AppLog.hotkey.warning("事件监听被系统禁用，已重新启用")
        CGEvent.tapEnable(tap: eventTap, enable: true)
    }

    private func handle(eventType: CGEventType, event: CGEvent) {
        guard let hotkey else {
            return
        }

        // 录音进行中（热键仍处于激活态）按 Esc → 取消本次录音。
        // 放在热键分支之前，Fn 与组合键两种模式都能触发。tap 为 listenOnly，
        // 不吞事件，Esc 仍会照常传递给前台应用。
        if isActive,
           eventType == .keyDown,
           event.getIntegerValueField(.keyboardEventKeycode) == Int64(kVK_Escape) {
            DispatchQueue.main.async { [weak self] in
                self?.onCancel?()
            }
            return
        }

        if hotkey.key.lowercased() == "fn" {
            handleFn(eventType: eventType, event: event)
            return
        }

        guard let targetKeyCode = Self.keyCode(for: hotkey.key) else {
            return
        }

        switch eventType {
        case .keyDown:
            let matchesModifiers = modifiersMatch(
                expected: hotkey.modifiers,
                flags: event.flags
            )
            let matchesKey = event.getIntegerValueField(.keyboardEventKeycode) == Int64(targetKeyCode)

            if matchesModifiers && matchesKey && !isActive {
                isActive = true
                DispatchQueue.main.async { [weak self] in
                    self?.onPress?()
                }
            }
        case .keyUp:
            let matchesKey = event.getIntegerValueField(.keyboardEventKeycode) == Int64(targetKeyCode)
            if matchesKey && isActive {
                isActive = false
                DispatchQueue.main.async { [weak self] in
                    self?.onRelease?()
                }
            }
        default:
            break
        }
    }

    private func handleFn(eventType: CGEventType, event: CGEvent) {
        guard eventType == .flagsChanged else {
            return
        }

        let isFnPressed = event.flags.contains(.maskSecondaryFn)
        if isFnPressed && !isActive {
            isActive = true
            DispatchQueue.main.async { [weak self] in
                self?.onPress?()
            }
        } else if !isFnPressed && isActive {
            isActive = false
            DispatchQueue.main.async { [weak self] in
                self?.onRelease?()
            }
        }
    }

    private func modifiersMatch(expected: [String], flags: CGEventFlags) -> Bool {
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

    /// 支持作为热键主键的键名 → 虚拟键码。"fn" 不在此表中，走独立的
    /// flagsChanged 处理路径（见 `handleFn`）。
    private static let keyCodeMap: [String: CGKeyCode] = [
        "a": CGKeyCode(kVK_ANSI_A), "b": CGKeyCode(kVK_ANSI_B), "c": CGKeyCode(kVK_ANSI_C),
        "d": CGKeyCode(kVK_ANSI_D), "e": CGKeyCode(kVK_ANSI_E), "f": CGKeyCode(kVK_ANSI_F),
        "g": CGKeyCode(kVK_ANSI_G), "h": CGKeyCode(kVK_ANSI_H), "i": CGKeyCode(kVK_ANSI_I),
        "j": CGKeyCode(kVK_ANSI_J), "k": CGKeyCode(kVK_ANSI_K), "l": CGKeyCode(kVK_ANSI_L),
        "m": CGKeyCode(kVK_ANSI_M), "n": CGKeyCode(kVK_ANSI_N), "o": CGKeyCode(kVK_ANSI_O),
        "p": CGKeyCode(kVK_ANSI_P), "q": CGKeyCode(kVK_ANSI_Q), "r": CGKeyCode(kVK_ANSI_R),
        "s": CGKeyCode(kVK_ANSI_S), "t": CGKeyCode(kVK_ANSI_T), "u": CGKeyCode(kVK_ANSI_U),
        "v": CGKeyCode(kVK_ANSI_V), "w": CGKeyCode(kVK_ANSI_W), "x": CGKeyCode(kVK_ANSI_X),
        "y": CGKeyCode(kVK_ANSI_Y), "z": CGKeyCode(kVK_ANSI_Z),
        "0": CGKeyCode(kVK_ANSI_0), "1": CGKeyCode(kVK_ANSI_1), "2": CGKeyCode(kVK_ANSI_2),
        "3": CGKeyCode(kVK_ANSI_3), "4": CGKeyCode(kVK_ANSI_4), "5": CGKeyCode(kVK_ANSI_5),
        "6": CGKeyCode(kVK_ANSI_6), "7": CGKeyCode(kVK_ANSI_7), "8": CGKeyCode(kVK_ANSI_8),
        "9": CGKeyCode(kVK_ANSI_9),
        "space": CGKeyCode(kVK_Space), "tab": CGKeyCode(kVK_Tab), "enter": CGKeyCode(kVK_Return),
        "f1": CGKeyCode(kVK_F1), "f2": CGKeyCode(kVK_F2), "f3": CGKeyCode(kVK_F3),
        "f4": CGKeyCode(kVK_F4), "f5": CGKeyCode(kVK_F5), "f6": CGKeyCode(kVK_F6),
        "f7": CGKeyCode(kVK_F7), "f8": CGKeyCode(kVK_F8), "f9": CGKeyCode(kVK_F9),
        "f10": CGKeyCode(kVK_F10), "f11": CGKeyCode(kVK_F11), "f12": CGKeyCode(kVK_F12),
    ]

    private static func keyCode(for key: String) -> CGKeyCode? {
        keyCodeMap[key.lowercased()]
    }

    /// 虚拟键码 → 支持的热键主键名（供设置界面的按键录制器反查）。
    /// 不在支持表中的键返回 nil。"fn" 走独立路径，不在此表中。
    static func keyName(for keyCode: UInt16) -> String? {
        keyCodeMap.first { $0.value == CGKeyCode(keyCode) }?.key
    }

    /// 该键名是否可作为热键（供设置界面在保存前做校验，避免落盘无法启动的配置）。
    /// "fn" 视为合法（独立处理路径）。
    static func isSupportedKey(_ key: String) -> Bool {
        let normalized = key.lowercased()
        return normalized == "fn" || keyCodeMap[normalized] != nil
    }
}
