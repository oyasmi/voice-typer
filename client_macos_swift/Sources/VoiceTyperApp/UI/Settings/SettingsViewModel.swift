import Foundation
import SwiftUI

/// 设置窗口内联提示的语义类别。
enum SettingsMessageKind {
    case info
    case success
    case error

    var color: Color {
        switch self {
        case .info:
            return .secondary
        case .success:
            return .green
        case .error:
            return .red
        }
    }
}

private enum SettingsValidationError: LocalizedError {
    case emptyHost
    case invalidPort
    case unsupportedHotkeyKey(String)

    var errorDescription: String? {
        switch self {
        case .emptyHost:
            return "服务地址不能为空。"
        case .invalidPort:
            return "端口必须是 1 到 65535 之间的数字。"
        case .unsupportedHotkeyKey(let key):
            return "不支持的主键“\(key)”。可用：字母 a–z、数字 0–9、space、tab、enter、F1–F12。"
        }
    }
}

/// 设置窗口各 SwiftUI 分页共享的可观察状态。
/// 由 `SetupWindowController` 创建并注入回调，AppKit 侧更新它、SwiftUI 侧观察它。
@MainActor
@Observable
final class SettingsViewModel {
    // MARK: 权限 / 服务状态（由控制器推送）
    var permissions = PermissionSnapshot(microphone: .notDetermined, accessibility: .denied, inputMonitoring: .denied)
    var serviceReady = false
    var serverStatus = "检查中"
    var hotkeyDisplay = "Fn🌐"

    // MARK: 连接（草稿，显式保存）
    var scheme = "http"
    var host = ""
    var port = ""
    var apiKey = ""
    var streaming = true
    var llmRecorrect = false
    var connectionMessage = ""
    var connectionMessageKind: SettingsMessageKind = .info
    var connectionBusy = false

    // MARK: 热键（捕获即保存）
    var hotkeyConfig = HotkeyConfig()
    var hotkeyMessage = ""
    var hotkeyMessageKind: SettingsMessageKind = .info

    // MARK: 热词（自动保存）
    var hotwordsText = ""
    var additionalHotwordFileCount = 0
    var hotwordsMessage = ""
    var hotwordsMessageKind: SettingsMessageKind = .info

    // MARK: 通用
    var launchAtLogin = false
    var hudOpacity = 0.85

    // MARK: 注入的回调
    var onRequestPermission: ((PermissionKind) -> Void)?
    var onOpenSystemSettings: ((PermissionKind) -> Void)?
    var onRetryServerCheck: (() -> Void)?
    var onTestServerConnection: ((ServerConfig) async -> Bool)?
    var onSaveConfig: ((AppConfig) async throws -> Void)?
    var onSaveHotwords: ((String) async throws -> Void)?
    var onSuspendHotkey: ((Bool) -> Void)?
    var onPreviewHUDOpacity: ((Double) -> Void)?
    var onToggleLaunchAtLogin: ((Bool) -> Void)?

    /// 已落盘的基线配置。即时保存（热键/透明度）以它为基准，避免带上未保存的连接草稿。
    private(set) var loadedConfig = AppConfig()
    private var savedHotwordsText = ""
    private var hotwordsSaveTask: Task<Void, Never>?

    // MARK: - 加载

    func load(config: AppConfig, managedHotwordsText: String, additionalHotwordFileCount: Int, launchAtLogin: Bool) {
        loadedConfig = config
        scheme = config.server.httpScheme
        host = config.server.host
        port = String(config.server.port)
        apiKey = config.server.apiKey
        streaming = config.server.streaming
        llmRecorrect = config.server.llmRecorrect
        hotkeyConfig = config.hotkey
        hotkeyDisplay = config.hotkey.displayString
        hudOpacity = config.ui.opacity
        self.launchAtLogin = launchAtLogin

        hotwordsText = managedHotwordsText
        savedHotwordsText = managedHotwordsText
        self.additionalHotwordFileCount = additionalHotwordFileCount

        connectionMessage = ""
        hotkeyMessage = ""
        hotwordsMessage = ""
    }

    // MARK: - 连接

    private func draftServerConfig() throws -> ServerConfig {
        let trimmedHost = host.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedHost.isEmpty else {
            throw SettingsValidationError.emptyHost
        }
        guard let portValue = Int(port.trimmingCharacters(in: .whitespacesAndNewlines)),
              (1...65535).contains(portValue) else {
            throw SettingsValidationError.invalidPort
        }
        return ServerConfig(
            scheme: scheme,
            host: trimmedHost,
            port: portValue,
            timeout: loadedConfig.server.timeout,
            apiKey: apiKey,
            llmRecorrect: llmRecorrect,
            streaming: streaming
        )
    }

    func testConnection() {
        do {
            let server = try draftServerConfig()
            connectionBusy = true
            connectionMessage = "正在测试连接…"
            connectionMessageKind = .info
            Task { [weak self] in
                guard let self else { return }
                let ready = await self.onTestServerConnection?(server) ?? false
                self.connectionBusy = false
                self.connectionMessage = ready
                    ? "连接成功，识别服务已就绪。"
                    : "连接失败，请检查服务地址、端口和服务端状态。"
                self.connectionMessageKind = ready ? .success : .error
            }
        } catch {
            connectionMessage = error.localizedDescription
            connectionMessageKind = .error
        }
    }

    func saveConnection() {
        do {
            let server = try draftServerConfig()
            var config = loadedConfig
            config.server = server
            connectionBusy = true
            connectionMessage = "正在保存并应用设置…"
            connectionMessageKind = .info
            Task { [weak self] in
                guard let self else { return }
                do {
                    try await self.onSaveConfig?(config)
                    self.loadedConfig = config
                    self.connectionMessage = "设置已保存并生效。"
                    self.connectionMessageKind = .success
                } catch {
                    self.connectionMessage = "保存失败：\(error.localizedDescription)"
                    self.connectionMessageKind = .error
                }
                self.connectionBusy = false
            }
        } catch {
            connectionMessage = error.localizedDescription
            connectionMessageKind = .error
        }
    }

    // MARK: - 热键

    /// 录制器捕获到新热键后调用：校验、落盘、更新显示。
    func applyHotkey(_ config: HotkeyConfig) {
        let key = config.key.lowercased()
        guard HotkeyService.isSupportedKey(key) else {
            hotkeyMessage = SettingsValidationError.unsupportedHotkeyKey(config.key).localizedDescription
            hotkeyMessageKind = .error
            return
        }
        hotkeyConfig = config
        hotkeyDisplay = config.displayString
        var updated = loadedConfig
        updated.hotkey = config
        Task { [weak self] in
            guard let self else { return }
            do {
                // 保存会重建并重启控制器（即从录制态恢复热键监听）。
                try await self.onSaveConfig?(updated)
                self.loadedConfig = updated
                self.hotkeyMessage = "热键已更新为 \(config.displayString)。"
                self.hotkeyMessageKind = .success
            } catch {
                self.hotkeyMessage = "保存失败：\(error.localizedDescription)"
                self.hotkeyMessageKind = .error
                // 保存失败时控制器可能仍处于挂起态，显式恢复以免热键失灵。
                self.onSuspendHotkey?(false)
            }
        }
    }

    func resetHotkeyToFn() {
        applyHotkey(HotkeyConfig(modifiers: [], key: "fn"))
    }

    /// 录制开始：挂起全局热键监听，避免录制时按 Fn 当场触发录音。
    func beginHotkeyRecording() {
        onSuspendHotkey?(true)
        hotkeyMessage = "正在录制：按下想要的快捷键（Esc 取消，Delete 恢复默认）。"
        hotkeyMessageKind = .info
    }

    /// 录制取消：恢复原热键监听。
    func cancelHotkeyRecording() {
        onSuspendHotkey?(false)
        hotkeyMessage = ""
    }

    // MARK: - 热词

    var hotwordCount: Int {
        hotwordsText
            .components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty && !$0.hasPrefix("#") }
            .count
    }

    /// 文本变化时调用：防抖 1s 后自动保存。
    func hotwordsChanged() {
        guard hotwordsText != savedHotwordsText else { return }
        hotwordsMessage = "编辑中…"
        hotwordsMessageKind = .info
        hotwordsSaveTask?.cancel()
        hotwordsSaveTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            guard let self, !Task.isCancelled else { return }
            await self.saveHotwordsNow()
        }
    }

    private func saveHotwordsNow() async {
        let text = hotwordsText
        do {
            try await onSaveHotwords?(text)
            savedHotwordsText = text
            hotwordsMessage = "已保存 · 词条数 \(hotwordCount)"
            hotwordsMessageKind = .success
        } catch {
            hotwordsMessage = "保存失败：\(error.localizedDescription)"
            hotwordsMessageKind = .error
        }
    }

    // MARK: - 通用

    func hudOpacityPreview(_ value: Double) {
        hudOpacity = value
        onPreviewHUDOpacity?(value)
    }

    func commitHUDOpacity() {
        var updated = loadedConfig
        updated.ui.opacity = hudOpacity
        Task { [weak self] in
            guard let self else { return }
            do {
                try await self.onSaveConfig?(updated)
                self.loadedConfig = updated
            } catch {
                // 透明度保存失败不打扰用户，仅记录到内联消息位（通用页无固定消息位，静默）。
            }
        }
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        launchAtLogin = enabled
        onToggleLaunchAtLogin?(enabled)
    }
}
