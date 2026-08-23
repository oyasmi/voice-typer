import Foundation
import Security

/// LLM API Key 的 Keychain 封装。不落配置文件——配置文件在 Application Support 下
/// 权限 0644，长期有效的密钥明文写进去不合适。
enum KeychainStore {
    private static let service = AppConstants.bundleIdentifier
    private static let account = "llm_api_key"

    /// 读 Keychain 失败的原因。区分"从未保存过"（`.itemNotFound`，正常状态，不是错误）
    /// 与真正的读取失败（`.status`/`.malformedData`）——此前 `loadLLMAPIKey()` 把两者
    /// 都压成同一个空字符串返回值，导致"智能校对开着但从未生效"（ad-hoc 签名每次构建
    /// cdhash 都变，可能让已保存条目的 ACL 校验失败）在界面上完全不可见，用户只会看到
    /// 校对请求持续 401，却没有任何信号指向"读密钥本身就失败了"（R4-06）。
    enum KeychainReadError: Error {
        case status(OSStatus)
        case malformedData
    }

    static func loadLLMAPIKeyResult() -> Result<String, KeychainReadError> {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
            // ad-hoc 签名每次构建的 cdhash 都会变化，可能与已保存条目的 ACL 信任记录不匹配；
            // 若不显式禁止，SecItemCopyMatching 会在主线程同步弹出系统级 Keychain 授权对话框
            // 阻塞 UI（这正是本项目此前排查到的"读 Keychain 卡住测试宿主"现象的产品态版本，
            // R2-02）。显式禁止 UI 后查询会直接失败返回空字符串，而不是阻塞（R3-06）。
            kSecUseAuthenticationUI as String: kSecUseAuthenticationUIFail,
        ]
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound {
            return .success("") // 从未保存过：合法状态，不是错误。
        }
        guard status == errSecSuccess else {
            return .failure(.status(status))
        }
        guard let data = result as? Data, let key = String(data: data, encoding: .utf8) else {
            return .failure(.malformedData)
        }
        return .success(key)
    }

    /// 便利入口：不区分"从未保存"与"读取失败"，两者都返回空字符串。
    /// 需要区分二者的调用方（设置页展示、保存前的回滚判断）应改用 `loadLLMAPIKeyResult()`。
    static func loadLLMAPIKey() -> String {
        switch loadLLMAPIKeyResult() {
        case .success(let key):
            return key
        case .failure(let error):
            AppLog.app.error("读取 Keychain 中的 LLM API Key 失败: \(String(describing: error), privacy: .public)")
            return ""
        }
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
