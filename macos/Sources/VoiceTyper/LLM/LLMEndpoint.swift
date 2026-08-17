import Foundation

/// 把用户填写的 LLM Base URL 结构化解析为最终请求地址，供设置页校验与
/// `LLMCorrector` 复用同一份规则，避免非法输入在请求期才触发强制解包崩溃（F-03）。
enum LLMEndpoint {
    /// 允许 https；也显式允许 http（回环地址等本地模型服务场景）。
    /// 若输入已包含 `/chat/completions` 后缀则不重复追加。
    static func chatCompletionsURL(from baseURL: String) -> URL? {
        var trimmed = baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        while trimmed.hasSuffix("/") {
            trimmed.removeLast()
        }
        guard !trimmed.isEmpty,
              !trimmed.contains(where: { $0.isWhitespace }),
              var components = URLComponents(string: trimmed),
              let scheme = components.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let host = components.host,
              !host.isEmpty
        else {
            return nil
        }

        if !components.path.hasSuffix("/chat/completions") {
            components.path += "/chat/completions"
        }
        return components.url
    }
}
