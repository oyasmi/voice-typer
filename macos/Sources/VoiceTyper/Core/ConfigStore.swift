import Foundation
import Yams

/// `config.yaml` 的读写。
///
/// **定位：这是应用的内部存储，不是面向用户的配置入口。** 所有配置项都由设置窗口提供
/// 界面，菜单里不再暴露「打开配置目录」，文档也不再引导用户手改 YAML——手改绕过了
/// `AppConfig.validated()` 之外的全部 UI 校验（例如热键必须搭配修饰键），且应用在运行期
/// 不监听该文件，改了也要重启才生效，是一条只会制造困惑的路径。
/// 保留容错解析（缺字段回落默认、越界夹逼）是为了历史配置与手工误改能自愈，
/// 不代表鼓励手改。整份文件读不出来时也不让应用停在错误态：另存一份备份、回落默认配置，
/// 由调用方告知用户（与 Windows 的 W5-04 一致）。
final class ConfigStore {
    /// 读不出来的配置文件另存为这个名字（与 config.yaml 同目录，覆盖上一份）。
    static let brokenBackupFileName = AppConstants.configFileName + ".broken"

    /// 最近一次 `loadOrCreate()` 是否因为 config.yaml 读不出来而回落了默认配置。
    /// 原文件保持不动，直到用户在设置里保存一次。
    private(set) var lastLoadFellBackToDefaults = false

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
        lastLoadFellBackToDefaults = false
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

        do {
            let content = try String(contentsOf: configURL, encoding: .utf8)
            return try YAMLDecoder().decode(AppConfig.self, from: content).validated()
        } catch {
            // 错误详情可能带出配置原文片段，日志里只记类型。
            AppLog.app.error("config.yaml 无法读取（\(String(describing: type(of: error)), privacy: .public)），已回落默认配置")
            backUpBrokenConfig()
            lastLoadFellBackToDefaults = true
            return AppConfig().validated()
        }
    }

    /// 把读不出来的 config.yaml 原样另存一份，供用户找回之前的设置。失败只记日志。
    private func backUpBrokenConfig() {
        let backupURL = configDirectoryURL.appendingPathComponent(Self.brokenBackupFileName)
        do {
            if fileManager.fileExists(atPath: backupURL.path) {
                try fileManager.removeItem(at: backupURL)
            }
            try fileManager.copyItem(at: configURL, to: backupURL)
        } catch {
            AppLog.app.error("备份无法读取的配置文件失败: \(error.localizedDescription, privacy: .public)")
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
          preload_on_launch: \(config.asr.preloadOnLaunch ? "true" : "false")
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
          mode: \(yamlString(config.hotkey.mode.rawValue))
        audio:
          input_device: \(yamlString(config.audio.inputDevice))
        ui:
          opacity: \(yamlNumber(config.ui.opacity))
          hud_position: \(yamlString(config.ui.hudPosition.rawValue))
          interface_language: \(yamlString(config.ui.interfaceLanguage.rawValue))
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
