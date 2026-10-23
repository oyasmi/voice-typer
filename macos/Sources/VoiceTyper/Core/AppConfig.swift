import Foundation

struct AppConfig: Codable {
    var asr: ASRConfig
    var llm: LLMConfig
    var hotkey: HotkeyConfig
    var audio: AudioConfig
    var ui: UIConfig
    var vocabulary: VocabularyConfig

    init(
        asr: ASRConfig = .init(),
        llm: LLMConfig = .init(),
        hotkey: HotkeyConfig = .init(),
        audio: AudioConfig = .init(),
        ui: UIConfig = .init(),
        vocabulary: VocabularyConfig = .init()
    ) {
        self.asr = asr
        self.llm = llm
        self.hotkey = hotkey
        self.audio = audio
        self.ui = ui
        self.vocabulary = vocabulary
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.asr = try container.decodeIfPresent(ASRConfig.self, forKey: .asr) ?? .init()
        self.llm = try container.decodeIfPresent(LLMConfig.self, forKey: .llm) ?? .init()
        self.hotkey = try container.decodeIfPresent(HotkeyConfig.self, forKey: .hotkey) ?? .init()
        self.audio = try container.decodeIfPresent(AudioConfig.self, forKey: .audio) ?? .init()
        self.ui = try container.decodeIfPresent(UIConfig.self, forKey: .ui) ?? .init()
        self.vocabulary = try container.decodeIfPresent(VocabularyConfig.self, forKey: .vocabulary) ?? .init()
    }

    enum CodingKeys: String, CodingKey {
        case asr
        case llm
        case hotkey
        case audio
        case ui
        case vocabulary
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
        config.vocabulary = config.vocabulary.validated()
        return config
    }

    /// `SettingsViewModel.applyHotkey` 已经在 UI 路径校验过"主键是否支持"与"非 fn 主键
    /// 必须搭配至少一个修饰键"，但手改过的 config.yaml 会绕过 UI：一个 `key: "d"` 且不带修饰键的
    /// 配置会让任何 App 里正常敲字母 d 都触发一次录音（`HotkeyService` 只检查键名是否受支持，
    /// R4-05）。这里是第二道防线，越界回落到默认热键 fn，不静默接受。
    private static func validatedHotkey(_ hotkey: HotkeyConfig) -> HotkeyConfig {
        let key = hotkey.key.lowercased()
        // 回落只针对"按哪个键"这件事；`mode`（按住 / 切换）是独立且始终合法的用户选择，
        // 不应被一个非法键名连坐重置。
        guard HotkeyService.isSupportedKey(key) else {
            AppLog.app.warning("配置字段 hotkey.key 不支持(\(hotkey.key, privacy: .public))，已回落为默认热键 fn")
            return HotkeyConfig(mode: hotkey.mode)
        }
        // 单独修饰键（右 ⌘ 等）本身就是完整热键；手改配置里多写的 modifiers 会让识别器永远
        // 对不上，忽略并记 warning。
        if ModifierHotkey(rawValue: key) != nil {
            if !hotkey.modifiers.isEmpty {
                AppLog.app.warning("配置字段 hotkey.modifiers 与单独修饰键(\(key, privacy: .public))同时出现，已忽略 modifiers")
            }
            return HotkeyConfig(modifiers: [], key: key, mode: hotkey.mode)
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
        case .auto: return L("自动")
        case .zh: return L("中文")
        case .en: return L("英文")
        case .yue: return L("粤语")
        case .ja: return L("日语")
        case .ko: return L("韩语")
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
    /// 0 = 常驻不卸载（**默认 0**：模型常驻约 510MB 内存，换来按热键零等待；
    /// 内存紧张的机器可改回定时卸载）
    var idleUnloadMinutes: Int
    /// 启动时就把模型载入内存。
    ///
    /// **默认 true**：多数用户是"每天高频使用"的场景，启动即加载能让首次按热键零等待。
    /// 关掉之后首次按热键才加载，且加载与录音并行（`ASRService.makeSession`），
    /// 用户通常正在说第一句话，感知延迟也接近于零；适合"今天可能一次都不用听写、
    /// 且在意常驻 ~510MB 内存"的场景。
    var preloadOnLaunch: Bool

    init(
        language: ASRLanguage = .auto,
        threads: Int = 0,
        modelDir: String = "",
        idleUnloadMinutes: Int = 0,
        preloadOnLaunch: Bool = true
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
        self.idleUnloadMinutes = try container.decodeIfPresent(Int.self, forKey: .idleUnloadMinutes) ?? 0
        self.preloadOnLaunch = try container.decodeIfPresent(Bool.self, forKey: .preloadOnLaunch) ?? true
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
/// `VoiceTyperController.minimumRecordingNanos` 当成误触丢弃的一次轻碰，变成一段
/// 用户毫无察觉就开始了的持续录音——误触的代价从"什么都没发生"升级为"一直在录"，
/// 这是比手指累更糟的失败模式。宁可让用户显式选一次模式。
enum HotkeyMode: String, Codable, CaseIterable {
    /// 按住热键录音，松开结束。
    case hold
    /// 按一次开始录音，再按一次结束。
    case toggle

    var displayName: String {
        switch self {
        case .hold: return L("按住说话")
        case .toggle: return L("按一次开始，再按一次结束")
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

    /// 热键是单独的修饰键（右 ⌘ 等）：单击即触发，按住它再按别的键则作为快捷键照常使用。
    var isModifierOnly: Bool { ModifierHotkey(rawValue: key.lowercased()) != nil }

    var kind: DictationMetrics.HotkeyKind {
        if key.lowercased() == "fn" { return .fn }
        return isModifierOnly ? .modifier : .combo
    }

    var displayString: String {
        if key.lowercased() == "fn" {
            return "Fn🌐"
        }
        if let modifier = ModifierHotkey(rawValue: key.lowercased()) {
            return modifier.displayName
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

/// 录音输入设备。
struct AudioConfig: Codable, Equatable {
    /// `auto`（默认：戴蓝牙耳机时改用内置麦克风）/ `system`（严格跟随系统默认输入）/
    /// 设备 UID（为后续的手选设备预留，暂无界面）。
    var inputDevice: String

    init(inputDevice: String = "auto") {
        self.inputDevice = inputDevice
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let raw = try container.decodeIfPresent(String.self, forKey: .inputDevice) ?? "auto"
        // 空串与缺失一样回落默认，避免手改配置留空后落成"指定一个名叫空串的设备"。
        self.inputDevice = raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "auto" : raw
    }

    enum CodingKeys: String, CodingKey {
        case inputDevice = "input_device"
    }
}

/// HUD 浮窗的落点。
enum HUDPosition: String, Codable, CaseIterable {
    /// 屏幕底部居中（默认）。
    case bottomCenter = "bottom_center"
    /// 屏幕右下角。
    case bottomRight = "bottom_right"
    /// 跟随输入位置（插入点）就近显示；取不到输入位置时跟随鼠标。
    /// 配置值仍是 `near_cursor`（兼容旧配置），显示名与行为在 R8-E3 改为跟随插入点。
    case nearCursor = "near_cursor"
    /// 不显示浮窗。
    ///
    /// 语义是"不显示**过程**"，不是"什么都不显示"：错误与"没有识别到内容"这类
    /// 提示仍会浮出来。那些信息在别处拿不到（菜单栏图标只有一个红点），
    /// 一并静音会把一个可配置项变成一个静默失败的陷阱。
    case hidden

    var displayName: String {
        switch self {
        case .bottomCenter: return L("底部居中")
        case .bottomRight: return L("右下角")
        case .nearCursor: return L("跟随输入位置")
        case .hidden: return L("不显示（仅出错时提示）")
        }
    }
}

struct UIConfig: Codable, Equatable {
    var opacity: Double
    var hudPosition: HUDPosition
    /// 界面语言。默认中文；改动需要重启应用才全面生效（设置页已明示）。
    var interfaceLanguage: AppLanguage

    init(
        opacity: Double = 0.85,
        hudPosition: HUDPosition = .bottomCenter,
        interfaceLanguage: AppLanguage = .zh
    ) {
        self.opacity = opacity
        self.hudPosition = hudPosition
        self.interfaceLanguage = interfaceLanguage
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.opacity = try container.decodeIfPresent(Double.self, forKey: .opacity) ?? 0.85
        let rawPosition = try container.decodeIfPresent(String.self, forKey: .hudPosition)
            ?? HUDPosition.bottomCenter.rawValue
        self.hudPosition = HUDPosition(rawValue: rawPosition) ?? .bottomCenter
        // 无法识别的语言回落中文，与本文件其余字段的容错一致。
        let rawLanguage = try container.decodeIfPresent(String.self, forKey: .interfaceLanguage)
            ?? AppLanguage.zh.rawValue
        self.interfaceLanguage = AppLanguage(rawValue: rawLanguage) ?? .zh
    }

    enum CodingKeys: String, CodingKey {
        case opacity
        case hudPosition = "hud_position"
        case interfaceLanguage = "interface_language"
    }
}

/// 识别偏置强度（VOC-D5）。只影响识别层偏置；替换层与 LLM 词表不受它控制。
enum VocabularyBias: String, Codable, CaseIterable {
    case off
    case light
    case standard
    case strong

    var displayName: String {
        switch self {
        case .off: return L("关")
        case .light: return L("轻")
        case .standard: return L("标准")
        case .strong: return L("强")
        }
    }
}

/// 一条自定义词汇（VOC-D2）：`term` 是正确写法，`aliases` 是「听起来像 / 常被识别成」的写法。
struct VocabularyEntry: Codable, Equatable {
    var term: String
    var aliases: [String]

    init(term: String, aliases: [String] = []) {
        self.term = term
        self.aliases = aliases
    }

    /// 容错：手写 YAML 时允许 `- 张三丰` 这种只有 term 的简写，`aliases` 也允许写成单个字符串。
    init(from decoder: Decoder) throws {
        if let single = try? decoder.singleValueContainer().decode(String.self) {
            self.init(term: single)
            return
        }
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let term = try container.decodeIfPresent(String.self, forKey: .term) ?? ""
        let aliases: [String]
        if let list = try? container.decodeIfPresent([String].self, forKey: .aliases) {
            aliases = list
        } else if let one = try? container.decodeIfPresent(String.self, forKey: .aliases) {
            aliases = [one]
        } else {
            aliases = []
        }
        self.init(term: term, aliases: aliases)
    }

    enum CodingKeys: String, CodingKey {
        case term
        case aliases
    }
}

/// 自定义词汇表（VOC-1）。词条文本只存在 config.yaml，日志里只记条数（VOC-D9）。
struct VocabularyConfig: Codable, Equatable {
    static let maxEntries = 200
    static let maxAliasesPerEntry = 10
    static let maxSurfaceLength = 50

    var bias: VocabularyBias
    var entries: [VocabularyEntry]

    init(bias: VocabularyBias = .standard, entries: [VocabularyEntry] = []) {
        self.bias = bias
        self.entries = entries
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let rawBias = try container.decodeIfPresent(String.self, forKey: .bias) ?? VocabularyBias.standard.rawValue
        // 未知值回落 standard，与本文件其余枚举字段的容错一致。
        self.bias = VocabularyBias(rawValue: rawBias) ?? .standard
        self.entries = try container.decodeIfPresent([VocabularyEntry].self, forKey: .entries) ?? []
    }

    enum CodingKeys: String, CodingKey {
        case bias
        case entries
    }

    /// 识别层是否需要编译上下文图：有词条且强度不是「关」。
    var biasesRecognition: Bool { bias != .off && !entries.isEmpty }

    /// §4.2 的逐条校验：去空白、去空、去重、合并同名 term、截断超限部分。越界只截断，warning 只记条数。
    /// - Parameter logging: 设置页的草稿每次渲染都会规整一遍（空白新行必然被丢弃），传 false 免得刷屏日志。
    func validated(logging: Bool = true) -> VocabularyConfig {
        var merged: [VocabularyEntry] = []
        var indexByKey: [String: Int] = [:]
        var truncatedSurfaces = 0
        var droppedEntries = 0
        var mergedEntries = 0

        for entry in entries {
            let term = Self.normalizedSurface(entry.term, truncatedCount: &truncatedSurfaces)
            guard !term.isEmpty else {
                droppedEntries += 1
                continue
            }
            let aliases = entry.aliases.map { Self.normalizedSurface($0, truncatedCount: &truncatedSurfaces) }
            let key = term.lowercased()
            if let index = indexByKey[key] {
                mergedEntries += 1
                merged[index].aliases.append(contentsOf: aliases)
            } else {
                indexByKey[key] = merged.count
                merged.append(VocabularyEntry(term: term, aliases: aliases))
            }
        }

        var truncatedAliases = 0
        for index in merged.indices {
            let termKey = merged[index].term.lowercased()
            var seen: Set<String> = [termKey]
            var kept: [String] = []
            for alias in merged[index].aliases where !alias.isEmpty {
                guard seen.insert(alias.lowercased()).inserted else { continue }
                kept.append(alias)
            }
            if kept.count > Self.maxAliasesPerEntry {
                truncatedAliases += kept.count - Self.maxAliasesPerEntry
                kept.removeLast(kept.count - Self.maxAliasesPerEntry)
            }
            merged[index].aliases = kept
        }

        let overflow = max(0, merged.count - Self.maxEntries)
        if overflow > 0 {
            merged.removeLast(overflow)
        }

        if logging, droppedEntries + mergedEntries + truncatedSurfaces + truncatedAliases + overflow > 0 {
            AppLog.app.warning(
                "词汇表已规整：空词条 \(droppedEntries, privacy: .public) 条、合并重复 \(mergedEntries, privacy: .public) 条、截断过长写法 \(truncatedSurfaces, privacy: .public) 个、截断多余 alias \(truncatedAliases, privacy: .public) 个、超出上限 \(overflow, privacy: .public) 条"
            )
        }
        return VocabularyConfig(bias: bias, entries: merged)
    }

    /// 去首尾空白；内部的换行、制表等空白收敛成一个半角空格（写回 YAML 时不必转义控制字符）；超长截断。
    private static func normalizedSurface(_ raw: String, truncatedCount: inout Int) -> String {
        let collapsed = raw
            .split(whereSeparator: { $0.isWhitespace })
            .joined(separator: " ")
        guard collapsed.count > maxSurfaceLength else { return collapsed }
        truncatedCount += 1
        return String(collapsed.prefix(maxSurfaceLength)).trimmingCharacters(in: .whitespaces)
    }
}
