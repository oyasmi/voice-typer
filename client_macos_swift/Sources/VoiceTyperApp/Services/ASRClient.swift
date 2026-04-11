import Foundation

struct RecognizeResponse: Decodable {
    let text: String
}

final class ASRClient: @unchecked Sendable {
    private let session: URLSession

    init(session: URLSession = .shared) {
        self.session = session
    }

    func healthCheck(server: ServerConfig) async -> Bool {
        guard let url = URL(string: "http://\(server.host):\(server.port)/health") else {
            return false
        }

        var request = URLRequest(url: url)
        request.timeoutInterval = 5.0
        applyAuthorizationIfNeeded(to: &request, server: server)

        do {
            let (data, _) = try await session.data(for: request)
            let payload = try JSONSerialization.jsonObject(with: data) as? [String: Any]
            return payload?["ready"] as? Bool ?? false
        } catch {
            AppLog.network.error("健康检查失败: \(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    func recognize(audioData: Data, hotwords: [String], server: ServerConfig) async throws -> String {
        guard let encodedHotwords = hotwords.joined(separator: " ").addingPercentEncoding(withAllowedCharacters: .alphanumerics),
              let url = URL(string: "http://\(server.host):\(server.port)/recognize?llm_recorrect=\(server.llmRecorrect ? "true" : "false")") else {
            throw URLError(.badURL)
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.httpBody = audioData
        request.timeoutInterval = server.timeout
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")

        if !hotwords.isEmpty {
            request.setValue(encodedHotwords, forHTTPHeaderField: "X-Hotwords")
        }

        applyAuthorizationIfNeeded(to: &request, server: server)

        let (data, _) = try await session.data(for: request)
        let decoded = try JSONDecoder().decode(RecognizeResponse.self, from: data)
        return decoded.text
    }

    private func applyAuthorizationIfNeeded(to request: inout URLRequest, server: ServerConfig) {
        let trimmed = server.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            return
        }
        request.setValue("Bearer \(trimmed)", forHTTPHeaderField: "Authorization")
    }
}
