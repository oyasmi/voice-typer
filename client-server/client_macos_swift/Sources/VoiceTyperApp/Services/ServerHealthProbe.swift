import Foundation

/// `/health` 探测结果。仅保留客户端当前会消费的字段；
/// 服务端实际返回更多元信息（asr_model、device 等），需要时再扩展这里即可。
struct ServerHealthResult {
    let ready: Bool
    let version: String?

    static let unreachable = ServerHealthResult(ready: false, version: nil)
}

/// 统一的 `/health` 探测入口。所有调用方都走这里，避免 controller / coordinator
/// 维护两份近似实现而漂移。支持 scheme（http/https）配置。
enum ServerHealthProbe {
    static func check(server: ServerConfig, timeout: TimeInterval = 5.0) async -> ServerHealthResult {
        guard let url = URL(string: "\(server.httpScheme)://\(server.host):\(server.port)/health") else {
            return .unreachable
        }
        var request = URLRequest(url: url)
        request.timeoutInterval = timeout
        let trimmed = server.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty {
            request.setValue("Bearer \(trimmed)", forHTTPHeaderField: "Authorization")
        }

        do {
            let (data, _) = try await URLSession.shared.data(for: request)
            guard let payload = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                return .unreachable
            }
            return ServerHealthResult(
                ready:   payload["ready"] as? Bool ?? false,
                version: payload["version"] as? String
            )
        } catch {
            AppLog.network.error("健康检查失败: \(error.localizedDescription, privacy: .public)")
            return .unreachable
        }
    }
}
