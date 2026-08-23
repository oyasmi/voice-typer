import Foundation
import Security

/// LLM API Key 的 Keychain 封装。不落配置文件——配置文件在 Application Support 下
/// 权限 0644，长期有效的密钥明文写进去不合适。
enum KeychainStore {
    private static let service = AppConstants.bundleIdentifier
    private static let account = "llm_api_key"

    static func loadLLMAPIKey() -> String {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
            // ad-hoc 签名每次构建的 cdhash 都会变化，可能与已保存条目的 ACL 信任记录不匹配；
            // 若不显式禁止，SecItemCopyMatching 会在主线程同步弹出系统级 Keychain 授权对话框
            // 阻塞 UI（这正是本项目此前排查到的"读 Keychain 卡住测试宿主"现象的产品态版本，
            // 见 ARCHITECTURE_REVIEW_ROUND2_TRIAGE.md R2-02）。显式禁止 UI 后查询会直接失败
            // 返回空字符串，而不是阻塞（R3-06）。
            kSecUseAuthenticationUI as String: kSecUseAuthenticationUIFail,
        ]
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        query.removeValue(forKey: kSecReturnData as String)
        guard status == errSecSuccess, let data = result as? Data,
              let key = String(data: data, encoding: .utf8) else {
            return ""
        }
        return key
    }

    /// 传入空字符串会删除已保存的密钥。
    @discardableResult
    static func saveLLMAPIKey(_ key: String) -> Bool {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        let baseQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]

        guard !trimmed.isEmpty else {
            let status = SecItemDelete(baseQuery as CFDictionary)
            return status == errSecSuccess || status == errSecItemNotFound
        }

        let data = Data(trimmed.utf8)
        let attributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock,
        ]

        let updateStatus = SecItemUpdate(baseQuery as CFDictionary, attributes as CFDictionary)
        if updateStatus == errSecItemNotFound {
            var addQuery = baseQuery
            attributes.forEach { addQuery[$0.key] = $0.value }
            let addStatus = SecItemAdd(addQuery as CFDictionary, nil)
            return addStatus == errSecSuccess
        }
        return updateStatus == errSecSuccess
    }
}
