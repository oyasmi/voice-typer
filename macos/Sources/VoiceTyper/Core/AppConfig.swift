import Foundation

struct AppConfig: Codable {
    var asr: ASRConfig
    var llm: LLMConfig
    var hotkey: HotkeyConfig
    var ui: UIConfig

    init(
        asr: ASRConfig = .init(),
        llm: LLMConfig = .init(),
        hotkey: HotkeyConfig = .init(),
        ui: UIConfig = .init()
    ) {
        self.asr = asr
        self.llm = llm
        self.hotkey = hotkey
        self.ui = ui
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.asr = try container.decodeIfPresent(ASRConfig.self, forKey: .asr) ?? .init()
        self.llm = try container.decodeIfPresent(LLMConfig.self, forKey: .llm) ?? .init()
        self.hotkey = try container.decodeIfPresent(HotkeyConfig.self, forKey: .hotkey) ?? .init()
        self.ui = try container.decodeIfPresent(UIConfig.self, forKey: .ui) ?? .init()
    }

    enum CodingKeys: String, CodingKey {
        case asr
        case llm
        case hotkey
        case ui
    }
}

/// 支持的 SenseVoice 识别语言。与 recognizer.SenseVoiceRecognizer 的
/// _SENSEVOICE_LID 表一一对应。
enum ASRLanguage: String, Codable, CaseIterable {
    case auto, zh, en, yue, ja, ko

    var displayName: String {
        switch self {
        case .auto: return "自动"
        case .zh: return "中文"
        case .en: return "英文"
        case .yue: return "粤语"
        case .ja: return "日语"
        case .ko: return "韩语"
        }
    }

    /// SenseVoice 词表里的语言 id，与 server/recognizer.py:_SENSEVOICE_LID 保持一致。
    var tokenID: Int32 {
        switch self {
        case .auto: return 0
        case .zh: return 3
        case .en: return 4
        case .yue: return 7
        case .ja: return 11
        case .ko: return 12
        }
    }
}

struct ASRConfig: Codable {
    var language: ASRLanguage
    /// 0 = 自动（min(4, 核数)）
    var threads: Int
    /// 留空 = 按 ModelLocator 优先级自动定位
    var modelDir: String
    /// 0 = 常驻不卸载
    var idleUnloadMinutes: Int

    init(
        language: ASRLanguage = .auto,
        threads: Int = 0,
        modelDir: String = "",
        idleUnloadMinutes: Int = 10
    ) {
        self.language = language
        self.threads = threads
        self.modelDir = modelDir
        self.idleUnloadMinutes = idleUnloadMinutes
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let rawLanguage = try container.decodeIfPresent(String.self, forKey: .language) ?? "auto"
        self.language = ASRLanguage(rawValue: rawLanguage) ?? .auto
        self.threads = try container.decodeIfPresent(Int.self, forKey: .threads) ?? 0
        self.modelDir = try container.decodeIfPresent(String.self, forKey: .modelDir) ?? ""
        self.idleUnloadMinutes = try container.decodeIfPresent(Int.self, forKey: .idleUnloadMinutes) ?? 10
    }

    enum CodingKeys: String, CodingKey {
        case language
        case threads
        case modelDir = "model_dir"
        case idleUnloadMinutes = "idle_unload_minutes"
    }
}

/// LLM 纠错配置。api_key 不落此结构 —— 存 Keychain，见 KeychainStore。
struct LLMConfig: Codable {
    var enabled: Bool
    var baseURL: String
    var model: String
    var temperature: Double
    var maxTokens: Int
    var timeout: Double

    init(
        enabled: Bool = false,
        baseURL: String = "",
        model: String = "gpt-4o-mini",
        temperature: Double = 0.0,
        maxTokens: Int = 800,
        timeout: Double = 5.0
    ) {
        self.enabled = enabled
        self.baseURL = baseURL
        self.model = model
        self.temperature = temperature
        self.maxTokens = maxTokens
        self.timeout = timeout
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.enabled = try container.decodeIfPresent(Bool.self, forKey: .enabled) ?? false
        self.baseURL = try container.decodeIfPresent(String.self, forKey: .baseURL) ?? ""
        self.model = try container.decodeIfPresent(String.self, forKey: .model) ?? "gpt-4o-mini"
        self.temperature = try container.decodeIfPresent(Double.self, forKey: .temperature) ?? 0.0
        self.maxTokens = try container.decodeIfPresent(Int.self, forKey: .maxTokens) ?? 800
        self.timeout = try container.decodeIfPresent(Double.self, forKey: .timeout) ?? 5.0
    }

    enum CodingKeys: String, CodingKey {
        case enabled
        case baseURL = "base_url"
        case model
        case temperature
        case maxTokens = "max_tokens"
        case timeout
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

    init(opacity: Double = 0.85) {
        self.opacity = opacity
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.opacity = try container.decodeIfPresent(Double.self, forKey: .opacity) ?? 0.85
    }
}
