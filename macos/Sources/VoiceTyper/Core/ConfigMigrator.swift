import Foundation
import Yams

/// 首次启动一次性迁移：若新配置路径（Application Support）尚无配置文件，
/// 而旧客户端（client_macos_swift / VoiceTyperClient）的 `~/.config/voice_typer/config.yaml`
/// 存在，则继承其中的 hotkey 与 ui.opacity —— 老用户换过来热键不用重设。
///
/// server 段（地址/端口/API Key/streaming）在新 App 里没有对应概念，直接丢弃。
enum ConfigMigrator {
    private struct LegacyConfig: Decodable {
        struct Hotkey: Decodable {
            var modifiers: [String]?
            var key: String?
        }
        struct UI: Decodable {
            var opacity: Double?
        }
        var hotkey: Hotkey?
        var ui: UI?
    }

    /// - Parameter legacyURLOverride: 仅供测试使用，跳过真实的 `~/.config/voice_typer/config.yaml`。
    static func migrateFromLegacyClientConfig(
        fileManager: FileManager,
        legacyURLOverride: URL? = nil
    ) -> AppConfig? {
        let legacyURL = legacyURLOverride ?? fileManager.homeDirectoryForCurrentUser
            .appendingPathComponent(".config", isDirectory: true)
            .appendingPathComponent("voice_typer", isDirectory: true)
            .appendingPathComponent("config.yaml")

        guard fileManager.fileExists(atPath: legacyURL.path),
              let content = try? String(contentsOf: legacyURL, encoding: .utf8),
              let legacy = try? YAMLDecoder().decode(LegacyConfig.self, from: content) else {
            return nil
        }

        var config = AppConfig()
        if let hotkey = legacy.hotkey {
            config.hotkey = HotkeyConfig(
                modifiers: hotkey.modifiers ?? [],
                key: hotkey.key ?? "fn"
            )
        }
        if let opacity = legacy.ui?.opacity {
            config.ui.opacity = opacity
        }
        AppLog.app.info("已从旧客户端配置迁移热键与 HUD 透明度: \(legacyURL.path, privacy: .public)")
        return config
    }
}
