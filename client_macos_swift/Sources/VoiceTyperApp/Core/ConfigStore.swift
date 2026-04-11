import AppKit
import Foundation

final class ConfigStore {
    let configDirectoryURL: URL
    let configURL: URL
    let defaultHotwordsURL: URL

    init(fileManager: FileManager = .default) {
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
        return parseConfig(from: content)
    }

    func ensureDefaultFiles() throws {
        try FileManager.default.createDirectory(
            at: configDirectoryURL,
            withIntermediateDirectories: true,
            attributes: nil
        )

        if !FileManager.default.fileExists(atPath: configURL.path) {
            try defaultConfigYAML().write(to: configURL, atomically: true, encoding: .utf8)
        }

        if !FileManager.default.fileExists(atPath: defaultHotwordsURL.path) {
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
          timeout: 60.0
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

    private func parseConfig(from content: String) -> AppConfig {
        var config = AppConfig()
        var section = ""
        var currentListKey = ""

        for rawLine in content.components(separatedBy: .newlines) {
            let trimmed = rawLine.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty, !trimmed.hasPrefix("#") else {
                continue
            }

            if !rawLine.hasPrefix(" "), trimmed.hasSuffix(":") {
                section = String(trimmed.dropLast())
                currentListKey = ""
                if section == "hotword_files" {
                    config.hotwordFiles = []
                }
                continue
            }

            if trimmed.hasPrefix("- ") {
                let value = unquote(String(trimmed.dropFirst(2)))
                if section == "hotword_files" || currentListKey == "hotword_files" {
                    config.hotwordFiles.append(value)
                } else if section == "hotkey", currentListKey == "modifiers" {
                    config.hotkey.modifiers.append(value)
                }
                continue
            }

            let parts = trimmed.split(separator: ":", maxSplits: 1).map(String.init)
            guard parts.count == 2 else {
                continue
            }

            let key = parts[0]
            let value = unquote(parts[1].trimmingCharacters(in: .whitespaces))
            currentListKey = key

            switch (section, key) {
            case ("server", "host"):
                config.server.host = value
            case ("server", "port"):
                config.server.port = Int(value) ?? config.server.port
            case ("server", "timeout"):
                config.server.timeout = Double(value) ?? config.server.timeout
            case ("server", "api_key"):
                config.server.apiKey = value
            case ("server", "llm_recorrect"):
                config.server.llmRecorrect = parseBool(value, default: config.server.llmRecorrect)
            case ("hotkey", "key"):
                config.hotkey.key = value
            case ("hotkey", "modifiers"):
                if value == "[]" {
                    config.hotkey.modifiers = []
                } else if !value.isEmpty {
                    config.hotkey.modifiers = value
                        .split(separator: ",")
                        .map { unquote($0.trimmingCharacters(in: .whitespacesAndNewlines)) }
                        .filter { !$0.isEmpty }
                }
            case ("ui", "opacity"):
                config.ui.opacity = Double(value) ?? config.ui.opacity
            case ("ui", "width"):
                config.ui.width = Double(value) ?? config.ui.width
            case ("ui", "height"):
                config.ui.height = Double(value) ?? config.ui.height
            default:
                break
            }
        }

        if config.hotwordFiles.isEmpty {
            config.hotwordFiles = [AppConstants.defaultHotwordsFileName]
        }

        return config
    }

    private func parseBool(_ rawValue: String, default defaultValue: Bool) -> Bool {
        switch rawValue.lowercased() {
        case "true", "yes", "1":
            return true
        case "false", "no", "0":
            return false
        default:
            return defaultValue
        }
    }

    private func unquote(_ value: String) -> String {
        if value.hasPrefix("\""), value.hasSuffix("\""), value.count >= 2 {
            return String(value.dropFirst().dropLast())
        }
        return value
    }
}
