import CryptoKit
import Foundation

/// 记录"某个 LLM 接口是否拒绝 `thinking` 字段"，避免每次校对都白挨一次 400 再重发。
///
/// 这是应用状态，不是用户配置，所以存 UserDefaults 而不进 `config.yaml`；
/// 控制器每次保存配置都会重建 `LLMCorrector`，因此缓存不能只放在实例里。
protocol LLMCapabilityStoring: AnyObject, Sendable {
    func isThinkingParameterUnsupported(fingerprint: String) -> Bool
    func setThinkingParameterUnsupported(_ unsupported: Bool, fingerprint: String)
}

/// 接口指纹：`SHA-256(请求地址 + "\n" + 模型名)` 的前 16 位十六进制。
/// **不含 API Key**：换 Key 不改变接口能力，也避免把密钥的任何派生物写进 UserDefaults。
enum LLMCapabilityFingerprint {
    static func make(chatURL: URL, model: String) -> String {
        let digest = SHA256.hash(data: Data("\(chatURL.absoluteString)\n\(model)".utf8))
        return digest.prefix(8).map { String(format: "%02x", $0) }.joined()
    }
}

final class UserDefaultsLLMCapabilityStore: LLMCapabilityStoring, @unchecked Sendable {
    static let shared = UserDefaultsLLMCapabilityStore()

    private static let key = "llm.thinkingParameterUnsupported"
    /// 最多保留的指纹数，超出丢最旧的。
    private static let maxEntries = 32

    private let defaults: UserDefaults
    private let lock = NSLock()

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func isThinkingParameterUnsupported(fingerprint: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return (defaults.stringArray(forKey: Self.key) ?? []).contains(fingerprint)
    }

    func setThinkingParameterUnsupported(_ unsupported: Bool, fingerprint: String) {
        lock.lock(); defer { lock.unlock() }
        var entries = (defaults.stringArray(forKey: Self.key) ?? []).filter { $0 != fingerprint }
        if unsupported {
            entries.append(fingerprint)
            if entries.count > Self.maxEntries {
                entries.removeFirst(entries.count - Self.maxEntries)
            }
        }
        defaults.set(entries, forKey: Self.key)
    }
}
