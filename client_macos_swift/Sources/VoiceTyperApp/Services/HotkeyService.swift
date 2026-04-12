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

    private func handle(eventType: CGEventType, event: CGEvent) {
        guard let hotkey else {
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

    private static func keyCode(for key: String) -> CGKeyCode? {
        switch key.lowercased() {
        case "a": return CGKeyCode(kVK_ANSI_A)
        case "b": return CGKeyCode(kVK_ANSI_B)
        case "c": return CGKeyCode(kVK_ANSI_C)
        case "d": return CGKeyCode(kVK_ANSI_D)
        case "e": return CGKeyCode(kVK_ANSI_E)
        case "f": return CGKeyCode(kVK_ANSI_F)
        case "g": return CGKeyCode(kVK_ANSI_G)
        case "h": return CGKeyCode(kVK_ANSI_H)
        case "i": return CGKeyCode(kVK_ANSI_I)
        case "j": return CGKeyCode(kVK_ANSI_J)
        case "k": return CGKeyCode(kVK_ANSI_K)
        case "l": return CGKeyCode(kVK_ANSI_L)
        case "m": return CGKeyCode(kVK_ANSI_M)
        case "n": return CGKeyCode(kVK_ANSI_N)
        case "o": return CGKeyCode(kVK_ANSI_O)
        case "p": return CGKeyCode(kVK_ANSI_P)
        case "q": return CGKeyCode(kVK_ANSI_Q)
        case "r": return CGKeyCode(kVK_ANSI_R)
        case "s": return CGKeyCode(kVK_ANSI_S)
        case "t": return CGKeyCode(kVK_ANSI_T)
        case "u": return CGKeyCode(kVK_ANSI_U)
        case "v": return CGKeyCode(kVK_ANSI_V)
        case "w": return CGKeyCode(kVK_ANSI_W)
        case "x": return CGKeyCode(kVK_ANSI_X)
        case "y": return CGKeyCode(kVK_ANSI_Y)
        case "z": return CGKeyCode(kVK_ANSI_Z)
        case "0": return CGKeyCode(kVK_ANSI_0)
        case "1": return CGKeyCode(kVK_ANSI_1)
        case "2": return CGKeyCode(kVK_ANSI_2)
        case "3": return CGKeyCode(kVK_ANSI_3)
        case "4": return CGKeyCode(kVK_ANSI_4)
        case "5": return CGKeyCode(kVK_ANSI_5)
        case "6": return CGKeyCode(kVK_ANSI_6)
        case "7": return CGKeyCode(kVK_ANSI_7)
        case "8": return CGKeyCode(kVK_ANSI_8)
        case "9": return CGKeyCode(kVK_ANSI_9)
        case "space": return CGKeyCode(kVK_Space)
        case "tab": return CGKeyCode(kVK_Tab)
        case "enter": return CGKeyCode(kVK_Return)
        case "f1": return CGKeyCode(kVK_F1)
        case "f2": return CGKeyCode(kVK_F2)
        case "f3": return CGKeyCode(kVK_F3)
        case "f4": return CGKeyCode(kVK_F4)
        case "f5": return CGKeyCode(kVK_F5)
        case "f6": return CGKeyCode(kVK_F6)
        case "f7": return CGKeyCode(kVK_F7)
        case "f8": return CGKeyCode(kVK_F8)
        case "f9": return CGKeyCode(kVK_F9)
        case "f10": return CGKeyCode(kVK_F10)
        case "f11": return CGKeyCode(kVK_F11)
        case "f12": return CGKeyCode(kVK_F12)
        default:
            return nil
        }
    }
}
