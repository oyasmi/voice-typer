import AppKit
import Foundation
import Yams

final class ConfigStore {
    private let fileManager: FileManager
    let configDirectoryURL: URL
    let configURL: URL

    init(fileManager: FileManager = .default) {
        self.fileManager = fileManager
        let homeDirectory = fileManager.homeDirectoryForCurrentUser
        self.configDirectoryURL = homeDirectory
            .appendingPathComponent(".config", isDirectory: true)
            .appendingPathComponent(AppConstants.configDirectoryName, isDirectory: true)
        self.configURL = configDirectoryURL.appendingPathComponent(AppConstants.configFileName)
    }

    func loadOrCreate() throws -> AppConfig {
        try ensureDefaultFiles()
        let content = try String(contentsOf: configURL, encoding: .utf8)

        do {
            return try YAMLDecoder().decode(AppConfig.self, from: content)
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

        let content = serializedYAML(for: config)
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
        """
        # VoiceTyper 客户端配置
        server:
          scheme: "http"        # http / https，对应 ws / wss
          host: "127.0.0.1"
          port: 6008
          timeout: 60
          api_key: ""
          llm_recorrect: true
          streaming: true       # false 走 HTTP 兼容模式

        hotkey:
          modifiers: []
          key: "fn"

        ui:
          opacity: 0.85
          width: 240
          height: 70
        """
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
        server:
          scheme: \(yamlString(config.server.httpScheme))
          host: \(yamlString(config.server.host))
          port: \(config.server.port)
          timeout: \(yamlNumber(config.server.timeout))
          api_key: \(yamlString(config.server.apiKey))
          llm_recorrect: \(config.server.llmRecorrect ? "true" : "false")
          streaming: \(config.server.streaming ? "true" : "false")
        hotkey:
        \(hotkeyModifiersBlock)
          key: \(yamlString(config.hotkey.key))
        ui:
          opacity: \(yamlNumber(config.ui.opacity))
          width: \(yamlNumber(config.ui.width))
          height: \(yamlNumber(config.ui.height))
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
            // 无论成败清理临时文件（成功路径中文件已被移走，removeItem 会静默失败）
            try? fileManager.removeItem(at: temporaryURL)
        }

        if fileManager.fileExists(atPath: url.path) {
            _ = try fileManager.replaceItemAt(url, withItemAt: temporaryURL)
        } else {
            try fileManager.moveItem(at: temporaryURL, to: url)
        }
    }
}
