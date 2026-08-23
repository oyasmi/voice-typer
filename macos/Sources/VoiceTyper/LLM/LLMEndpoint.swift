import Foundation

/// 「测试校对」等只需要展示一句错误文案、不需要区分具体错误类型的场景使用（R3-13）：
/// `Result` 的 Failure 类型必须遵循 `Error`，纯 `String` 不满足，故用这个最简包装。
struct SimpleMessageError: Error, LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

/// 把用户填写的 LLM Base URL 结构化解析为最终请求地址，供设置页校验与
/// `LLMCorrector` 复用同一份规则，避免非法输入在请求期才触发强制解包崩溃（F-03）。
enum LLMEndpoint {
    enum LLMEndpointError: LocalizedError {
        case malformed
        /// 明文 HTTP 只允许本机或私有网络地址：识别文本与 `Authorization: Bearer <key>`
        /// 全程明文传输，若放行任意公网 host，用户填一个 `http://example.com` 就会把
        /// 两者都暴露给链路上的任何中间人（R2-07）。
        case insecurePlaintextHost(String)

        var errorDescription: String? {
            switch self {
            case .malformed:
                return "Base URL 格式不合法，请检查协议头（http/https）与地址。"
            case .insecurePlaintextHost(let host):
                return "\(host) 是公网地址，明文 HTTP 只允许本机或局域网地址，公网请使用 https://。"
            }
        }
    }

    /// 允许 https 用于任意 host；http 仅放行回环/私网地址（本机或局域网跑的
    /// Ollama/vLLM 等本地模型服务是常见场景，不应误伤）。
    /// 若输入已包含 `/chat/completions` 后缀则不重复追加。
    static func resolve(_ baseURL: String) -> Result<URL, LLMEndpointError> {
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
            return .failure(.malformed)
        }

        if scheme == "http", !isAllowedPlaintextHost(host) {
            return .failure(.insecurePlaintextHost(host))
        }

        if !components.path.hasSuffix("/chat/completions") {
            components.path += "/chat/completions"
        }
        guard let url = components.url else { return .failure(.malformed) }
        return .success(url)
    }

    /// 便利入口：忽略具体失败原因，仅返回可用的请求地址。保留是为了减少既有调用方
    /// （非设置页校验路径，不需要展示具体错误文案）的改动面。
    static func chatCompletionsURL(from baseURL: String) -> URL? {
        try? resolve(baseURL).get()
    }

    private static func isAllowedPlaintextHost(_ rawHost: String) -> Bool {
        var host = rawHost.lowercased()
        if host.hasPrefix("["), host.hasSuffix("]") {
            host = String(host.dropFirst().dropLast())
        }

        if host == "localhost" || host.hasSuffix(".localhost") || host.hasSuffix(".local") {
            return true
        }
        if host == "::1" {
            return true
        }
        if let ipv4 = IPv4Address(host) {
            return ipv4.isLoopback || ipv4.isPrivate || ipv4.isLinkLocal
        }
        return false
    }

    private struct IPv4Address {
        let octets: (UInt8, UInt8, UInt8, UInt8)

        init?(_ string: String) {
            let parts = string.split(separator: ".", omittingEmptySubsequences: false)
            guard parts.count == 4 else { return nil }
            var values: [UInt8] = []
            for part in parts {
                guard let value = UInt8(part) else { return nil }
                values.append(value)
            }
            octets = (values[0], values[1], values[2], values[3])
        }

        /// 127.0.0.0/8
        var isLoopback: Bool { octets.0 == 127 }
        /// 10/8、172.16/12、192.168/16
        var isPrivate: Bool {
            octets.0 == 10
                || (octets.0 == 172 && (16...31).contains(octets.1))
                || (octets.0 == 192 && octets.1 == 168)
        }
        /// 169.254/16（含 mDNS 场景下的链路本地地址）
        var isLinkLocal: Bool { octets.0 == 169 && octets.1 == 254 }
    }
}
