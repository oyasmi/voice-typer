import AppKit
import ApplicationServices
import AVFoundation
import CoreGraphics
import Foundation

enum PermissionKind: CaseIterable {
    case microphone
    case accessibility
    case inputMonitoring

    var title: String {
        switch self {
        case .microphone:
            return "麦克风"
        case .accessibility:
            return "辅助功能"
        case .inputMonitoring:
            return "输入监控"
        }
    }
}

enum PermissionStatus: Equatable {
    case authorized
    case denied
    case notDetermined

    var displayText: String {
        switch self {
        case .authorized:
            return "已授权"
        case .denied:
            return "未授权"
        case .notDetermined:
            return "待请求"
        }
    }
}

struct PermissionSnapshot: Equatable {
    var microphone: PermissionStatus
    var accessibility: PermissionStatus
    var inputMonitoring: PermissionStatus

    var allRequiredGranted: Bool {
        microphone == .authorized &&
        accessibility == .authorized &&
        inputMonitoring == .authorized
    }

    func status(for kind: PermissionKind) -> PermissionStatus {
        switch kind {
        case .microphone:
            return microphone
        case .accessibility:
            return accessibility
        case .inputMonitoring:
            return inputMonitoring
        }
    }
}

@MainActor
final class PermissionCenter {
    func snapshot() -> PermissionSnapshot {
        PermissionSnapshot(
            microphone: microphoneStatus(),
            accessibility: accessibilityStatus(),
            inputMonitoring: inputMonitoringStatus()
        )
    }

    func request(_ kind: PermissionKind) async -> PermissionStatus {
        switch kind {
        case .microphone:
            return await requestMicrophone()
        case .accessibility:
            _ = requestAccessibility()
            return accessibilityStatus()
        case .inputMonitoring:
            return requestInputMonitoring()
        }
    }

    func openSystemSettings(for kind: PermissionKind) {
        let targetURL: URL = switch kind {
        case .microphone:
            SystemSettingsURL.microphone
        case .accessibility:
            SystemSettingsURL.accessibility
        case .inputMonitoring:
            SystemSettingsURL.inputMonitoring
        }
        NSWorkspace.shared.open(targetURL)
    }

    private func microphoneStatus() -> PermissionStatus {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            return .authorized
        case .denied, .restricted:
            return .denied
        case .notDetermined:
            return .notDetermined
        @unknown default:
            return .denied
        }
    }

    private func accessibilityStatus() -> PermissionStatus {
        AXIsProcessTrusted() ? .authorized : .denied
    }

    private func inputMonitoringStatus() -> PermissionStatus {
        CGPreflightListenEventAccess() ? .authorized : .denied
    }

    private func requestMicrophone() async -> PermissionStatus {
        await withCheckedContinuation { continuation in
            AVCaptureDevice.requestAccess(for: .audio) { granted in
                continuation.resume(returning: granted ? .authorized : .denied)
            }
        }
    }

    private func requestAccessibility() -> Bool {
        let options = ["AXTrustedCheckOptionPrompt" as CFString: true] as CFDictionary
        return AXIsProcessTrustedWithOptions(options)
    }

    private func requestInputMonitoring() -> PermissionStatus {
        if inputMonitoringStatus() == .authorized {
            return .authorized
        }

        _ = CGRequestListenEventAccess()

        let status = inputMonitoringStatus()
        guard status != .authorized else {
            return .authorized
        }

        let alert = NSAlert()
        alert.alertStyle = .informational
        alert.messageText = "请在系统设置中允许“输入监控”"
        alert.informativeText = "macOS 通常不会直接弹出“输入监控”的授权窗口。点击“打开系统设置”后，请在“隐私与安全性 > 输入监控”中启用 VoiceTyper，然后返回本窗口。"
        alert.addButton(withTitle: "打开系统设置")
        alert.addButton(withTitle: "取消")

        if alert.runModal() == .alertFirstButtonReturn {
            openSystemSettings(for: .inputMonitoring)
        }

        return .denied
    }
}
