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
    case unsupportedHotkeyKey(String)

    var errorDescription: String? {
        switch self {
        case .unsupportedHotkeyKey(let key):
            return "不支持的主键 \(key)。可用：字母 a–z、数字 0–9、space、tab、enter、F1–F12。"
        }
    }
}

/// 设置窗口各 SwiftUI 分页共享的可观察状态。
/// 由 `SetupWindowController` 创建并注入回调，AppKit 侧更新它、SwiftUI 侧观察它。
@MainActor
@Observable
final class SettingsViewModel {
    // MARK: 权限 / 引擎状态（由控制器推送）
    var permissions = PermissionSnapshot(microphone: .notDetermined, accessibility: .denied, inputMonitoring: .denied)
    var asrState: ASRService.State = .unloaded
    /// nil 表示当前未在下载。
    var downloadProgress: Double?
    var engineStatus = "检查中"
    var hotkeyDisplay = "Fn🌐"

    // MARK: 识别（草稿，显式保存）
    var language: ASRLanguage = .auto
    var modelActionBusy = false

    // MARK: 智能纠错（草稿，显式保存）
    var llmEnabled = false
    var llmBaseURL = ""
    var llmAPIKey = ""
    var llmModel = "gpt-4o-mini"
    var llmTemperature = 0.0
    var llmTimeout = 5.0
    var recognitionMessage = ""
    var recognitionMessageKind: SettingsMessageKind = .info
    var recognitionBusy = false

    // MARK: 热键（捕获即保存）
    var hotkeyConfig = HotkeyConfig()
    var hotkeyMessage = ""
    var hotkeyMessageKind: SettingsMessageKind = .info

    // MARK: 通用
    var launchAtLogin = false
    var hudOpacity = 0.85
    var idleUnloadMinutes = 10
    var generalMessage = ""
    var generalMessageKind: SettingsMessageKind = .info

    // MARK: 注入的回调
    var onRequestPermission: ((PermissionKind) -> Void)?
    var onOpenSystemSettings: ((PermissionKind) -> Void)?
    var onSaveConfig: ((AppConfig) async throws -> Void)?
    var onSuspendHotkey: ((Bool) -> Void)?
    var onPreviewHUDOpacity: ((Double) -> Void)?
    var onToggleLaunchAtLogin: ((Bool) -> Void)?
    var onStartModelDownload: (() -> Void)?
    var onCancelModelDownload: (() -> Void)?
    var onReloadModel: (() -> Void)?
    var onTestLLMCorrection: ((LLMConfig, String) async -> Bool)?

    /// 已落盘的基线配置。即时保存（热键/透明度）以它为基准，避免带上未保存的识别草稿。
    private(set) var loadedConfig = AppConfig()

    // MARK: - 加载

    func load(config: AppConfig, launchAtLogin: Bool) {
        loadedConfig = config
        language = config.asr.language
        llmEnabled = config.llm.enabled
        llmBaseURL = config.llm.baseURL
        llmAPIKey = KeychainStore.loadLLMAPIKey()
        llmModel = config.llm.model
        llmTemperature = config.llm.temperature
        llmTimeout = config.llm.timeout
        hotkeyConfig = config.hotkey
        hotkeyDisplay = config.hotkey.displayString
        hudOpacity = config.ui.opacity
        idleUnloadMinutes = config.asr.idleUnloadMinutes
        self.launchAtLogin = launchAtLogin

        recognitionMessage = ""
        hotkeyMessage = ""
    }

    // MARK: - 识别 / 纠错

    func testLLMCorrection() {
        let draft = draftLLMConfig()
        guard draft.enabled, !draft.baseURL.trimmingCharacters(in: .whitespaces).isEmpty else {
            recognitionMessage = "请先启用智能纠错并填写 Base URL。"
            recognitionMessageKind = .error
            return
        }
        guard LLMEndpoint.chatCompletionsURL(from: draft.baseURL) != nil else {
            recognitionMessage = "Base URL 格式不合法，请检查协议头（http/https）与地址。"
            recognitionMessageKind = .error
            return
        }
        recognitionBusy = true
        recognitionMessage = "正在测试纠错…"
        recognitionMessageKind = .info
        let apiKey = llmAPIKey
        Task { [weak self] in
            guard let self else { return }
            let ok = await self.onTestLLMCorrection?(draft, apiKey) ?? false
            self.recognitionBusy = false
            self.recognitionMessage = ok
                ? "纠错测试成功：模型正常响应并返回了修正结果。"
                : "纠错测试未通过，请检查 Base URL / API Key / 模型名是否正确。"
            self.recognitionMessageKind = ok ? .success : .error
        }
    }

    func saveRecognitionSettings() {
        let draftLLM = draftLLMConfig()
        if draftLLM.enabled, LLMEndpoint.chatCompletionsURL(from: draftLLM.baseURL) == nil {
            recognitionMessage = "Base URL 格式不合法，请检查协议头（http/https）与地址，设置未保存。"
            recognitionMessageKind = .error
            return
        }
        var config = loadedConfig
        config.asr.language = language
        config.llm = draftLLM
        recognitionBusy = true
        recognitionMessage = "正在保存并应用设置…"
        recognitionMessageKind = .info
        let apiKey = llmAPIKey
        let previousAPIKey = KeychainStore.loadLLMAPIKey()
        Task { [weak self] in
            guard let self else { return }
            // 先写 Keychain、检查返回值（原先被丢弃）：写失败必须中止，
            // 否则界面显示"已保存"而纠错永远 401（F-13）。
            guard KeychainStore.saveLLMAPIKey(apiKey) else {
                self.recognitionMessage = "API Key 写入 Keychain 失败，设置未保存。"
                self.recognitionMessageKind = .error
                self.recognitionBusy = false
                return
            }
            do {
                try await self.onSaveConfig?(config)
                self.loadedConfig = config
                self.recognitionMessage = "设置已保存并生效。"
                self.recognitionMessageKind = .success
            } catch {
                // YAML 写入失败：尽力把 Keychain 回滚到保存前的值，避免两者状态不一致。
                _ = KeychainStore.saveLLMAPIKey(previousAPIKey)
                self.recognitionMessage = "保存失败：\(error.localizedDescription)"
                self.recognitionMessageKind = .error
            }
            self.recognitionBusy = false
        }
    }

    private func draftLLMConfig() -> LLMConfig {
        LLMConfig(
            enabled: llmEnabled,
            baseURL: llmBaseURL.trimmingCharacters(in: .whitespacesAndNewlines),
            model: llmModel.trimmingCharacters(in: .whitespacesAndNewlines),
            temperature: llmTemperature,
            maxTokens: loadedConfig.llm.maxTokens,
            timeout: llmTimeout
        )
    }

    func startModelDownload() {
        modelActionBusy = true
        onStartModelDownload?()
    }

    func cancelModelDownload() {
        onCancelModelDownload?()
    }

    func reloadModel() {
        modelActionBusy = true
        onReloadModel?()
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
                // 不静默吞掉异常（AGENTS.md）：失败不更新 loadedConfig（避免以错误基线覆盖
                // 后续即时保存），并把失败告知用户，而不是只吞掉。
                self.generalMessage = "HUD 透明度保存失败：\(error.localizedDescription)"
                self.generalMessageKind = .error
            }
        }
    }

    func commitIdleUnloadMinutes() {
        var updated = loadedConfig
        updated.asr.idleUnloadMinutes = idleUnloadMinutes
        Task { [weak self] in
            guard let self else { return }
            do {
                try await self.onSaveConfig?(updated)
                self.loadedConfig = updated
            } catch {
                self.generalMessage = "空闲卸载时长保存失败：\(error.localizedDescription)"
                self.generalMessageKind = .error
            }
        }
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        launchAtLogin = enabled
        onToggleLaunchAtLogin?(enabled)
    }
}
