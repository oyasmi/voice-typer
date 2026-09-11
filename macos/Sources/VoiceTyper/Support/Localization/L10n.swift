import Foundation

/// 界面语言。中文是默认语言，英文为对等的第二语言。
///
/// 刻意**不做**「跟随系统」：识别语言（`ASRLanguage`）已经是一个独立的语言选项，
/// 再加一个会随系统漂移的界面语言，用户很难判断"我现在看到的是哪一种、为什么变了"。
/// 显式两选一，读配置文件也一眼看得懂。
enum AppLanguage: String, Codable, CaseIterable, Sendable {
    case zh
    case en

    /// 语言名用它自己的语言书写（与系统语言选择器的惯例一致），不随当前界面语言变化。
    var displayName: String {
        switch self {
        case .zh: return "中文"
        case .en: return "English"
        }
    }
}

/// 运行期界面语言与中英文案表。
///
/// **键就是中文原文**：`L("录音中")`。这样做的理由是
/// 1. 中文是默认语言，查不到翻译时回落到键本身仍然是正确的中文界面，不会出现 `hud.recording`
///    这种键名泄漏到界面上；
/// 2. 新增文案只需在一处补英文，不必同时维护一张键名表；
/// 3. 覆盖率可测：`LocalizationTests` 扫描全部源码里的 `L(...)` / `LF(...)`，要求每条都能在
///    英文表里查到，漏翻会让测试失败，而不是等用户切到英文才发现。
///
/// 语言在进程启动早期由 `bootstrap()` 定下，运行期改设置需要重启才生效（设置页有明确提示）。
enum L10n {
    private static let lock = NSLock()
    /// `nonisolated(unsafe)` + 锁：文案会在识别、下载、LLM 等后台线程被读取，
    /// 不能假定只在主线程访问。
    nonisolated(unsafe) private static var storedLanguage: AppLanguage = .zh

    /// 界面语言在 `UserDefaults` 里的镜像。
    ///
    /// `config.yaml` 仍是唯一的事实来源，但菜单栏与主菜单在配置读出来之前就已经构造完成，
    /// 那时再改语言就来不及了。镜像值只用于启动瞬间取一个语言，每次加载配置都会被同步。
    static let defaultsKey = "ui.interfaceLanguage"

    static var current: AppLanguage {
        lock.lock()
        defer { lock.unlock() }
        return storedLanguage
    }

    /// 进程启动早期调用：从 `UserDefaults` 镜像取语言。
    static func bootstrap(defaults: UserDefaults = .standard) {
        guard let raw = defaults.string(forKey: defaultsKey), let language = AppLanguage(rawValue: raw) else {
            return
        }
        setLanguage(language)
    }

    /// 配置加载/保存后调用：以 `config.yaml` 为准，并同步镜像供下次启动使用。
    static func apply(_ language: AppLanguage, defaults: UserDefaults = .standard) {
        setLanguage(language)
        defaults.set(language.rawValue, forKey: defaultsKey)
    }

    static func setLanguage(_ language: AppLanguage) {
        lock.lock()
        storedLanguage = language
        lock.unlock()
    }

    /// 查表。查不到（尚未翻译）时回落到中文原文，界面不会因此出现空白或键名。
    static func string(_ zh: String) -> String {
        switch current {
        case .zh:
            return zh
        case .en:
            return englishTable[zh] ?? zh
        }
    }
}

/// 取一条本地化文案。参数就是中文原文。
func L(_ zh: String) -> String {
    L10n.string(zh)
}

/// 取一条带占位符的本地化文案。中文原文即格式串，例如 `LF("下载模型 %d%%", percent)`。
func LF(_ zhFormat: String, _ arguments: CVarArg...) -> String {
    String(format: L10n.string(zhFormat), arguments: arguments)
}
