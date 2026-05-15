import Foundation

/// HTTP 客户端，用于非流式（批量）语音识别（POST /recognize）。
final class ASRClient {
    private let server: ServerConfig

    init(server: ServerConfig) {
        self.server = server
    }

    func recognize(audioData: Data, hotwords: String, llmRecorrect: Bool) async throws -> String {
        guard let url = URL(string: "http://\(server.host):\(server.port)/recognize?llm_recorrect=\(llmRecorrect)") else {
            throw URLError(.badURL)
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.timeoutInterval = server.timeout

        let trimmedKey = server.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmedKey.isEmpty {
            request.setValue("Bearer \(trimmedKey)", forHTTPHeaderField: "Authorization")
        }

        if !hotwords.isEmpty {
            let encoded = hotwords.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? hotwords
            request.setValue(encoded, forHTTPHeaderField: "X-Hotwords")
        }

        request.httpBody = audioData

        let (data, response) = try await URLSession.shared.data(for: request)

        if let httpResponse = response as? HTTPURLResponse, httpResponse.statusCode != 200 {
            let body = String(data: data, encoding: .utf8) ?? ""
            throw NSError(
                domain: "ASRClient",
                code: httpResponse.statusCode,
                userInfo: [NSLocalizedDescriptionKey: "服务错误 \(httpResponse.statusCode): \(body)"]
            )
        }

        guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let text = json["text"] as? String else {
            throw NSError(
                domain: "ASRClient",
                code: -1,
                userInfo: [NSLocalizedDescriptionKey: "响应格式无效"]
            )
        }

        return text
    }
}
