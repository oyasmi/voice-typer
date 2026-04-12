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
        guard let url = URL(string: "http://\(server.host):\(server.port)/recognize?llm_recorrect=\(server.llmRecorrect ? "true" : "false")") else {
            throw URLError(.badURL)
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.httpBody = audioData
        request.timeoutInterval = server.timeout
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")

        if !hotwords.isEmpty {
            request.setValue(hotwords.joined(separator: " "), forHTTPHeaderField: "X-Hotwords")
        }

        applyAuthorizationIfNeeded(to: &request, server: server)

        let (data, response) = try await session.data(for: request)

        if let httpResponse = response as? HTTPURLResponse,
           !(200..<300).contains(httpResponse.statusCode) {
            let body = String(data: data, encoding: .utf8) ?? ""
            throw NSError(
                domain: AppConstants.bundleIdentifier,
                code: httpResponse.statusCode,
                userInfo: [NSLocalizedDescriptionKey: "服务端返回 \(httpResponse.statusCode): \(body)"]
            )
        }

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
