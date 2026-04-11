import Foundation

struct AppConfig: Codable {
    var server: ServerConfig
    var hotkey: HotkeyConfig
    var hotwordFiles: [String]
    var ui: UIConfig

    init(
        server: ServerConfig = .init(),
        hotkey: HotkeyConfig = .init(),
        hotwordFiles: [String] = [AppConstants.defaultHotwordsFileName],
        ui: UIConfig = .init()
    ) {
        self.server = server
        self.hotkey = hotkey
        self.hotwordFiles = hotwordFiles
        self.ui = ui
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.server = try container.decodeIfPresent(ServerConfig.self, forKey: .server) ?? .init()
        self.hotkey = try container.decodeIfPresent(HotkeyConfig.self, forKey: .hotkey) ?? .init()
        self.hotwordFiles = try container.decodeIfPresent([String].self, forKey: .hotwordFiles) ?? [AppConstants.defaultHotwordsFileName]
        self.ui = try container.decodeIfPresent(UIConfig.self, forKey: .ui) ?? .init()
    }

    enum CodingKeys: String, CodingKey {
        case server
        case hotkey
        case hotwordFiles = "hotword_files"
        case ui
    }
}

struct ServerConfig: Codable {
    var host: String
    var port: Int
    var timeout: Double
    var apiKey: String
    var llmRecorrect: Bool

    init(
        host: String = "127.0.0.1",
        port: Int = 6008,
        timeout: Double = 60.0,
        apiKey: String = "",
        llmRecorrect: Bool = true
    ) {
        self.host = host
        self.port = port
        self.timeout = timeout
        self.apiKey = apiKey
        self.llmRecorrect = llmRecorrect
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.host = try container.decodeIfPresent(String.self, forKey: .host) ?? "127.0.0.1"
        self.port = try container.decodeIfPresent(Int.self, forKey: .port) ?? 6008
        self.timeout = try container.decodeIfPresent(Double.self, forKey: .timeout) ?? 60.0
        self.apiKey = try container.decodeIfPresent(String.self, forKey: .apiKey) ?? ""
        self.llmRecorrect = try container.decodeIfPresent(Bool.self, forKey: .llmRecorrect) ?? true
    }

    enum CodingKeys: String, CodingKey {
        case host
        case port
        case timeout
        case apiKey = "api_key"
        case llmRecorrect = "llm_recorrect"
    }
}

struct HotkeyConfig: Codable {
    var modifiers: [String]
    var key: String

    init(modifiers: [String] = [], key: String = "fn") {
        self.modifiers = modifiers
        self.key = key
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.modifiers = try container.decodeIfPresent([String].self, forKey: .modifiers) ?? []
        self.key = try container.decodeIfPresent(String.self, forKey: .key) ?? "fn"
    }

    var displayString: String {
        if key.lowercased() == "fn" {
            return "Fn🌐"
        }
        let parts = modifiers + [key]
        return parts.map { $0.uppercased() }.joined(separator: "+")
    }
}

struct UIConfig: Codable {
    var opacity: Double
    var width: Double
    var height: Double

    init(opacity: Double = 0.85, width: Double = 240, height: Double = 70) {
        self.opacity = opacity
        self.width = width
        self.height = height
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.opacity = try container.decodeIfPresent(Double.self, forKey: .opacity) ?? 0.85
        self.width = try container.decodeIfPresent(Double.self, forKey: .width) ?? 240
        self.height = try container.decodeIfPresent(Double.self, forKey: .height) ?? 70
    }
}
