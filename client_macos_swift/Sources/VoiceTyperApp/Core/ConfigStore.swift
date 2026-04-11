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
}
