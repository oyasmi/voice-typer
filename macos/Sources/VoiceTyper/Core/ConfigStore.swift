import AppKit
import Foundation
import Yams

final class ConfigStore {
    private let fileManager: FileManager
    private let legacyConfigURLOverride: URL?
    let configDirectoryURL: URL
    let configURL: URL

    /// - Parameters:
    ///   - baseDirectoryOverride: 仅供测试使用，跳过真实的 Application Support 目录。
    ///   - legacyConfigURLOverride: 仅供测试使用，跳过真实的 `~/.config/voice_typer/config.yaml`。
    init(
        fileManager: FileManager = .default,
        baseDirectoryOverride: URL? = nil,
        legacyConfigURLOverride: URL? = nil
    ) {
        self.fileManager = fileManager
        self.legacyConfigURLOverride = legacyConfigURLOverride
        let appSupport = baseDirectoryOverride
            ?? fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? fileManager.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support")
        self.configDirectoryURL = appSupport.appendingPathComponent(AppConstants.appSupportDirectoryName, isDirectory: true)
        self.configURL = configDirectoryURL.appendingPathComponent(AppConstants.configFileName)
    }

    func loadOrCreate() throws -> AppConfig {
        if !fileManager.fileExists(atPath: configURL.path) {
            try ensureDefaultFiles()
            if let migrated = ConfigMigrator.migrateFromLegacyClientConfig(
                fileManager: fileManager,
                legacyURLOverride: legacyConfigURLOverride
            ) {
                try save(config: migrated)
                return migrated
            }
        }
        try ensureDefaultFiles()
        let content = try String(contentsOf: configURL, encoding: .utf8)

        do {
            return try YAMLDecoder().decode(AppConfig.self, from: content).validated()
        } catch {
            throw NSError(
                domain: AppConstants.bundleIdentifier,
                code: 1002,
                userInfo: [
                    NSLocalizedDescriptionKey: "配置文件解析失败，请检查 \(configURL.path)",
                    NSUnderlyingErrorKey: error,
                ]
            )
        }
    }

    func save(config: AppConfig) throws {
        try ensureDefaultFiles()
        let content = serializedYAML(for: config.validated())
        try writeAtomically(content: content, to: configURL)
    }

    func ensureDefaultFiles() throws {
        try fileManager.createDirectory(
            at: configDirectoryURL,
            withIntermediateDirectories: true,
            attributes: nil
        )

        if !fileManager.fileExists(atPath: configURL.path) {
            try defaultConfigYAML().write(to: configURL, atomically: true, encoding: .utf8)
        }
    }

    func openConfigDirectory() {
        NSWorkspace.shared.open(configDirectoryURL)
    }

    private func defaultConfigYAML() -> String {
        serializedYAML(for: AppConfig())
    }

    private func serializedYAML(for config: AppConfig) -> String {
        let hotkeyModifiersBlock: String
        if config.hotkey.modifiers.isEmpty {
            hotkeyModifiersBlock = "  modifiers: []"
        } else {
            let modifiers = config.hotkey.modifiers
                .map { "    - \(yamlString($0))" }
                .joined(separator: "\n")
            hotkeyModifiersBlock = "  modifiers:\n\(modifiers)"
        }

        return """
        asr:
          language: \(yamlString(config.asr.language.rawValue))
          threads: \(config.asr.threads)
          model_dir: \(yamlString(config.asr.modelDir))
          idle_unload_minutes: \(config.asr.idleUnloadMinutes)
        llm:
          enabled: \(config.llm.enabled ? "true" : "false")
          base_url: \(yamlString(config.llm.baseURL))
          model: \(yamlString(config.llm.model))
          temperature: \(yamlNumber(config.llm.temperature))
          max_tokens: \(config.llm.maxTokens)
          timeout: \(yamlNumber(config.llm.timeout))
        hotkey:
        \(hotkeyModifiersBlock)
          key: \(yamlString(config.hotkey.key))
        ui:
          opacity: \(yamlNumber(config.ui.opacity))
        """
    }

    private func yamlString(_ value: String) -> String {
        let escaped = value
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
        return "\"\(escaped)\""
    }

    private func yamlNumber(_ value: Double) -> String {
        let roundedValue = value.rounded()
        if abs(value - roundedValue) < 0.000_000_1 {
            return String(Int(roundedValue))
        }

        let formatter = NumberFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.numberStyle = .decimal
        formatter.usesGroupingSeparator = false
        formatter.minimumFractionDigits = 0
        formatter.maximumFractionDigits = 16
        return formatter.string(from: NSNumber(value: value)) ?? String(value)
    }

    private func writeAtomically(content: String, to url: URL) throws {
        let temporaryURL = url.deletingLastPathComponent()
            .appendingPathComponent(".\(url.lastPathComponent).tmp")
        try content.write(to: temporaryURL, atomically: true, encoding: .utf8)

        defer {
            try? fileManager.removeItem(at: temporaryURL)
        }

        if fileManager.fileExists(atPath: url.path) {
            _ = try fileManager.replaceItemAt(url, withItemAt: temporaryURL)
        } else {
            try fileManager.moveItem(at: temporaryURL, to: url)
        }
    }
}
