import AppKit
import Foundation
import Yams

final class ConfigStore {
    private let fileManager: FileManager
    let configDirectoryURL: URL
    let configURL: URL
    let defaultHotwordsURL: URL

    init(fileManager: FileManager = .default) {
        self.fileManager = fileManager
        let homeDirectory = fileManager.homeDirectoryForCurrentUser
        self.configDirectoryURL = homeDirectory
            .appendingPathComponent(".config", isDirectory: true)
            .appendingPathComponent(AppConstants.configDirectoryName, isDirectory: true)
        self.configURL = configDirectoryURL.appendingPathComponent(AppConstants.configFileName)
        self.defaultHotwordsURL = configDirectoryURL.appendingPathComponent(AppConstants.defaultHotwordsFileName)
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

        var normalizedConfig = config
        normalizedConfig.hotwordFiles = normalizedHotwordFiles(from: config.hotwordFiles)

        let content = serializedYAML(for: normalizedConfig)
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

        if !fileManager.fileExists(atPath: defaultHotwordsURL.path) {
            try defaultHotwordsContent().write(to: defaultHotwordsURL, atomically: true, encoding: .utf8)
        }
    }

    func openConfigDirectory() {
        NSWorkspace.shared.open(configDirectoryURL)
    }

    func loadHotwords(using config: AppConfig) -> [String] {
        var words: [String] = []
        var seen = Set<String>()

        for relativePath in config.hotwordFiles {
            let url = resolveHotwordURL(for: relativePath)
            guard let content = try? String(contentsOf: url, encoding: .utf8) else {
                continue
            }

            for line in content.components(separatedBy: .newlines) {
                let word = line.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !word.isEmpty, !word.hasPrefix("#"), !seen.contains(word) else {
                    continue
                }
                seen.insert(word)
                words.append(word)
            }
        }

        return words
    }

    func loadManagedHotwordsText(using config: AppConfig) throws -> String {
        try ensureDefaultFiles()

        let url = managedHotwordsURL(using: config)
        if !fileManager.fileExists(atPath: url.path) {
            try writeAtomically(content: defaultHotwordsContent(), to: url)
        }

        return try String(contentsOf: url, encoding: .utf8)
    }

    func saveManagedHotwordsText(_ text: String, using config: AppConfig) throws {
        try ensureDefaultFiles()
        let url = managedHotwordsURL(using: config)
        let normalized = normalizeHotwordsText(text)
        try writeAtomically(content: normalized, to: url)
    }

    func managedHotwordsURL(using config: AppConfig) -> URL {
        let hotwordFiles = normalizedHotwordFiles(from: config.hotwordFiles)
        let managedPath = hotwordFiles.first ?? AppConstants.defaultHotwordsFileName
        return resolveHotwordURL(for: managedPath)
    }

    func additionalHotwordFileCount(using config: AppConfig) -> Int {
        let managedURL = managedHotwordsURL(using: config).standardizedFileURL
        let uniqueURLs = Set(
            normalizedHotwordFiles(from: config.hotwordFiles)
                .map { resolveHotwordURL(for: $0).standardizedFileURL }
        )
        return uniqueURLs.filter { $0 != managedURL }.count
    }

    private func resolveHotwordURL(for path: String) -> URL {
        let expanded = NSString(string: path).expandingTildeInPath
        if expanded.hasPrefix("/") {
            return URL(fileURLWithPath: expanded)
        }
        return configDirectoryURL.appendingPathComponent(path)
    }

    private func defaultConfigYAML() -> String {
        """
        # VoiceTyper 客户端配置
        server:
          host: "127.0.0.1"
          port: 6008
          timeout: 60
          api_key: ""
          llm_recorrect: true

        hotkey:
          modifiers: []
          key: "fn"

        hotword_files:
          - "hotwords.txt"

        ui:
          opacity: 0.85
          width: 240
          height: 70
        """
    }

    private func defaultHotwordsContent() -> String {
        """
        # VoiceTyper 用户词库
        FunASR
        OpenAI
        ChatGPT
        """
    }

    private func normalizedHotwordFiles(from hotwordFiles: [String]) -> [String] {
        var normalized: [String] = [AppConstants.defaultHotwordsFileName]
        var seen = Set<String>(normalized)

        for path in hotwordFiles {
            let trimmed = path.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty, !seen.contains(trimmed) else {
                continue
            }
            normalized.append(trimmed)
            seen.insert(trimmed)
        }

        return normalized
    }

    private func normalizeHotwordsText(_ text: String) -> String {
        let normalizedNewlines = text.replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
        let trimmed = normalizedNewlines.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "" : "\(trimmed)\n"
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

        let hotwordFilesBlock = config.hotwordFiles
            .map { "  - \(yamlString($0))" }
            .joined(separator: "\n")

        return """
        server:
          host: \(yamlString(config.server.host))
          port: \(config.server.port)
          timeout: \(yamlNumber(config.server.timeout))
          api_key: \(yamlString(config.server.apiKey))
          llm_recorrect: \(config.server.llmRecorrect ? "true" : "false")
        hotkey:
        \(hotkeyModifiersBlock)
          key: \(yamlString(config.hotkey.key))
        hotword_files:
        \(hotwordFilesBlock)
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

        if fileManager.fileExists(atPath: url.path) {
            _ = try fileManager.replaceItemAt(url, withItemAt: temporaryURL)
        } else {
            try fileManager.moveItem(at: temporaryURL, to: url)
        }
    }
}
