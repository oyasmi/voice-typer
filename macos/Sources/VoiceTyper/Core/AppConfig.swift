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

    /// 把手改 YAML 或历史脏数据里越界的值夹逼回合法范围，避免 `timeout: -1`、
    /// `opacity: 5.0`、`threads: 9999`、`idle_unload_minutes: -5` 之类的值进入运行态（F-13）。
    /// 越界时记一条 warning，不静默——但也不因此拒绝整份配置。
    func validated() -> AppConfig {
        var config = self
        config.asr.threads = clamp(config.asr.threads, 0, 32, field: "asr.threads")
        config.asr.idleUnloadMinutes = clamp(config.asr.idleUnloadMinutes, 0, 24 * 60, field: "asr.idle_unload_minutes")
        config.llm.temperature = clamp(config.llm.temperature, 0, 2, field: "llm.temperature")
        config.llm.maxTokens = clamp(config.llm.maxTokens, 64, 8192, field: "llm.max_tokens")
        config.llm.timeout = clamp(config.llm.timeout, 1, 120, field: "llm.timeout")
        config.ui.opacity = clamp(config.ui.opacity, 0.1, 1.0, field: "ui.opacity")
        config.hotkey = Self.validatedHotkey(config.hotkey)
        return config
    }

    /// `SettingsViewModel.applyHotkey` 已经在 UI 路径校验过"主键是否支持"与"非 fn 主键
    /// 必须搭配至少一个修饰键"，但设置窗口不是改配置的唯一入口——菜单里就有「打开配置
    /// 目录」，README 也鼓励手改 YAML。不经 UI 直接写一个 `key: "d"` 且不带修饰键的配置
    /// 完全可达：`HotkeyService` 只检查键名是否受支持，不要求修饰键非空，结果是在任何
    /// App 里正常打字敲字母 d 都会触发一次录音（R4-05）。这里补上手改配置文件绕过 UI
    /// 校验的第二道防线，越界回落到默认热键 fn，不静默接受。
    private static func validatedHotkey(_ hotkey: HotkeyConfig) -> HotkeyConfig {
        let key = hotkey.key.lowercased()
        // 回落只针对"按哪个键"这件事；`mode`（按住 / 切换）是独立且始终合法的用户选择，
        // 不应被一个非法键名连坐重置。
        guard HotkeyService.isSupportedKey(key) else {
            AppLog.app.warning("配置字段 hotkey.key 不支持(\(hotkey.key, privacy: .public))，已回落为默认热键 fn")
            return HotkeyConfig(mode: hotkey.mode)
        }
        guard key == "fn" || !hotkey.modifiers.isEmpty else {
            AppLog.app.warning("配置字段 hotkey 未搭配修饰键(\(hotkey.key, privacy: .public))，已回落为默认热键 fn")
            return HotkeyConfig(mode: hotkey.mode)
        }
        return hotkey
    }
}

private func clamp<T: Comparable>(_ value: T, _ lower: T, _ upper: T, field: String) -> T {
    guard value < lower || value > upper else { return value }
    let clamped = min(max(value, lower), upper)
    AppLog.app.warning("配置字段 \(field, privacy: .public) 越界(\(String(describing: value), privacy: .public))，已夹逼为 \(String(describing: clamped), privacy: .public)")
    return clamped
}

/// 浮点重载：`value < lower || value > upper` 对 NaN 两边都是 false，会让 NaN 原样穿透
/// 通用版本（例如 `llm.temperature = .nan` 会让 JSONSerialization 抛错，每次校对都
/// 静默回落原文；`ui.opacity = .nan` 会污染窗口 alpha）。非有限数值直接重置为下限（R2-12）。
private func clamp<T: Comparable & FloatingPoint>(_ value: T, _ lower: T, _ upper: T, field: String) -> T {
    guard value.isFinite else {
        AppLog.app.warning("配置字段 \(field, privacy: .public) 不是有限数值(\(String(describing: value), privacy: .public))，已重置为 \(String(describing: lower), privacy: .public)")
        return lower
    }
    guard value < lower || value > upper else { return value }
    let clamped = min(max(value, lower), upper)
    AppLog.app.warning("配置字段 \(field, privacy: .public) 越界(\(String(describing: value), privacy: .public))，已夹逼为 \(String(describing: clamped), privacy: .public)")
    return clamped
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

    /// SenseVoice 词表里的语言 id，与 client-server/server/voice_typer_server/recognizer.py:_SENSEVOICE_LID 保持一致。
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

struct ASRConfig: Codable, Equatable {
    var language: ASRLanguage
    /// 0 = 自动（min(4, 核数)）
    var threads: Int
    /// 留空 = 按 ModelLocator 优先级自动定位
    var modelDir: String
    /// 0 = 常驻不卸载
    var idleUnloadMinutes: Int
    /// 启动时就把模型载入内存。
    ///
    /// **默认 false**：常驻 ~510MB 的识别引擎对一个开机自启的菜单栏应用是很重的代价，
    /// 而这份代价在"今天一次都没用听写"的日子里是纯浪费——旧行为是启动即加载、
    /// `idleUnloadMinutes` 到点再卸载，等于每次开机都白花一次 0.85s 加载与十分钟 510MB。
    /// 关掉之后首次按热键才加载，且加载与录音并行（`ASRService.makeSession`），
    /// 用户通常正在说第一句话，感知延迟接近于零。
    /// 打开它适合"每天都高频使用、且不在意常驻内存"的场景。
    var preloadOnLaunch: Bool

    init(
        language: ASRLanguage = .auto,
        threads: Int = 0,
        modelDir: String = "",
        idleUnloadMinutes: Int = 10,
        preloadOnLaunch: Bool = false
    ) {
        self.language = language
        self.threads = threads
        self.modelDir = modelDir
        self.idleUnloadMinutes = idleUnloadMinutes
        self.preloadOnLaunch = preloadOnLaunch
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let rawLanguage = try container.decodeIfPresent(String.self, forKey: .language) ?? "auto"
        self.language = ASRLanguage(rawValue: rawLanguage) ?? .auto
        self.threads = try container.decodeIfPresent(Int.self, forKey: .threads) ?? 0
        self.modelDir = try container.decodeIfPresent(String.self, forKey: .modelDir) ?? ""
        self.idleUnloadMinutes = try container.decodeIfPresent(Int.self, forKey: .idleUnloadMinutes) ?? 10
        self.preloadOnLaunch = try container.decodeIfPresent(Bool.self, forKey: .preloadOnLaunch) ?? false
    }

    enum CodingKeys: String, CodingKey {
        case language
        case threads
        case modelDir = "model_dir"
        case idleUnloadMinutes = "idle_unload_minutes"
        case preloadOnLaunch = "preload_on_launch"
    }
}

/// LLM 校对配置。api_key 不落此结构 —— 存 Keychain，见 KeychainStore。
struct LLMConfig: Codable, Equatable {
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

/// 热键的触发语义。
///
/// 默认 `hold`（按住说话）不变；`toggle` 是为长听写（写邮件、写文档）准备的：
/// 手指不必一直按着，按一次开始、再按一次结束。
///
/// 刻意**不做**"短按 toggle / 长按 hold"的自动判别：那会把今天被
/// `VoiceTyperController.minimumRecordingDuration` 当成误触丢弃的一次轻碰，变成一段
/// 用户毫无察觉就开始了的持续录音——误触的代价从"什么都没发生"升级为"一直在录"，
/// 这是比手指累更糟的失败模式。宁可让用户显式选一次模式。
enum HotkeyMode: String, Codable, CaseIterable {
    /// 按住热键录音，松开结束。
    case hold
    /// 按一次开始录音，再按一次结束。
    case toggle

    var displayName: String {
        switch self {
        case .hold: return "按住说话"
        case .toggle: return "按一次开始，再按一次结束"
        }
    }
}

struct HotkeyConfig: Codable, Equatable {
    var modifiers: [String]
    var key: String
    var mode: HotkeyMode

    init(modifiers: [String] = [], key: String = "fn", mode: HotkeyMode = .hold) {
        self.modifiers = modifiers
        self.key = key
        self.mode = mode
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.modifiers = try container.decodeIfPresent([String].self, forKey: .modifiers) ?? []
        self.key = try container.decodeIfPresent(String.self, forKey: .key) ?? "fn"
        // 无法识别的 mode 回落 hold，而不是解码失败——与本文件其余字段的容错一致。
        let rawMode = try container.decodeIfPresent(String.self, forKey: .mode) ?? HotkeyMode.hold.rawValue
        self.mode = HotkeyMode(rawValue: rawMode) ?? .hold
    }

    var displayString: String {
        if key.lowercased() == "fn" {
            return "Fn🌐"
        }
        let parts = modifiers + [key]
        return parts.map { $0.uppercased() }.joined(separator: "+")
    }

    enum CodingKeys: String, CodingKey {
        case modifiers
        case key
        case mode
    }
}

/// HUD 浮窗的落点。
enum HUDPosition: String, Codable, CaseIterable {
    /// 屏幕底部居中（默认）。
    case bottomCenter = "bottom_center"
    /// 屏幕右下角。
    case bottomRight = "bottom_right"
    /// 跟随鼠标位置，就近显示。
    case nearCursor = "near_cursor"
    /// 不显示浮窗。
    ///
    /// 语义是"不显示**过程**"，不是"什么都不显示"：错误与"没有识别到内容"这类
    /// 提示仍会浮出来。那些信息在别处拿不到（菜单栏图标只有一个红点），
    /// 一并静音会把一个可配置项变成一个静默失败的陷阱。
    case hidden

    var displayName: String {
        switch self {
        case .bottomCenter: return "底部居中"
        case .bottomRight: return "右下角"
        case .nearCursor: return "跟随光标"
        case .hidden: return "不显示（仅出错时提示）"
        }
    }
}

struct UIConfig: Codable, Equatable {
    var opacity: Double
    var hudPosition: HUDPosition

    init(opacity: Double = 0.85, hudPosition: HUDPosition = .bottomCenter) {
        self.opacity = opacity
        self.hudPosition = hudPosition
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.opacity = try container.decodeIfPresent(Double.self, forKey: .opacity) ?? 0.85
        let rawPosition = try container.decodeIfPresent(String.self, forKey: .hudPosition)
            ?? HUDPosition.bottomCenter.rawValue
        self.hudPosition = HUDPosition(rawValue: rawPosition) ?? .bottomCenter
    }

    enum CodingKeys: String, CodingKey {
        case opacity
        case hudPosition = "hud_position"
    }
}
