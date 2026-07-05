import Foundation

// MARK: - 服务端消息结构

private enum ServerMessage {
    case partial(text: String, seq: Int)
    case final(text: String, asrElapsed: Double?, llmElapsed: Double?)
    case warning(code: String, message: String)
    case error(code: String, message: String)
    case unknown
}

private func parseServerMessage(_ raw: String) -> ServerMessage {
    guard let data = raw.data(using: .utf8),
          let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let type = json["type"] as? String
    else {
        return .unknown
    }

    switch type {
    case "partial":
        let text = json["text"] as? String ?? ""
        let seq = json["seq"] as? Int ?? 0
        return .partial(text: text, seq: seq)

    case "final":
        let text = json["text"] as? String ?? ""
        let asrElapsed = json["asrElapsed"] as? Double
        let llmElapsed = json["llmElapsed"] as? Double
        return .final(text: text, asrElapsed: asrElapsed, llmElapsed: llmElapsed)

    case "warning":
        let code = json["code"] as? String ?? "unknown"
        let message = json["message"] as? String ?? ""
        return .warning(code: code, message: message)

    case "error":
        let code = json["code"] as? String ?? "unknown"
        let message = json["message"] as? String ?? ""
        return .error(code: code, message: message)

    default:
        return .unknown
    }
}

// MARK: - StreamingASRClient

/// WebSocket 客户端，管理单次录音会话的全双工流式通信。
///
/// 生命周期：connect → (sendAudio × N → finalize) → [onFinal/onError 触发] → close
/// 每次录音新建一个实例，不复用。
@MainActor
final class StreamingASRClient {
    var onPartial: ((String) -> Void)?
    var onFinal: ((String) -> Void)?
    /// 非致命提示（如 feed_failed）。连接仍存活，finalize 可继续。
    var onWarning: ((String) -> Void)?
    var onError: ((String) -> Void)?

    private var task: URLSessionWebSocketTask?
    private var receiveLoopTask: Task<Void, Never>?
    /// finalize 后等待 final 帧的超时任务；收到 final/error 或 close 时取消。
    private var finalizeTimeoutTask: Task<Void, Never>?
    private var closed = false

    // MARK: - 连接

    func connect(server: ServerConfig, hotwords: [String], llmRecorrect: Bool) throws {
        var components = URLComponents()
        components.scheme = server.wsScheme
        components.host = server.host
        components.port = server.port
        components.path = "/recognize/stream"
        components.queryItems = [
            URLQueryItem(name: "llm_recorrect", value: llmRecorrect ? "true" : "false"),
        ]

        guard let url = components.url else {
            throw URLError(.badURL)
        }

        var request = URLRequest(url: url)
        request.timeoutInterval = 5.0
        let trimmedKey = server.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmedKey.isEmpty {
            request.setValue("Bearer \(trimmedKey)", forHTTPHeaderField: "Authorization")
        }

        let wsTask = URLSession.shared.webSocketTask(with: request)
        self.task = wsTask
        wsTask.resume()

        // 发送 start 帧
        let startPayload: [String: Any] = [
            "type": "start",
            "hotwords": hotwords.joined(separator: " "),
            "sample_rate": 16000,
        ]
        sendJSON(startPayload)

        // 启动接收循环
        receiveLoopTask = Task { [weak self] in
            await self?.receiveLoop()
        }
    }

    // MARK: - 发送

    func sendAudio(_ data: Data) {
        guard !closed else { return }
        task?.send(.data(data)) { error in
            if let error {
                AppLog.network.error("音频帧发送失败: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    /// 请求离线复识别的最终结果。
    /// - Parameter timeout: 等待 `final` 帧的最长时间；超时视为识别失败，触发 onError，
    ///   避免服务端卡住 / 网络半开时 HUD 永久停在"识别中"。传 <= 0 表示不设超时。
    func finalize(timeout: TimeInterval) {
        guard !closed else { return }
        sendJSON(["type": "finalize"])

        finalizeTimeoutTask?.cancel()
        guard timeout > 0 else { return }
        finalizeTimeoutTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
            guard !Task.isCancelled, let self, !self.closed else { return }
            AppLog.network.error("finalize 超时（\(timeout, privacy: .public)s），未收到 final")
            self.onError?("识别超时")
        }
    }

    func close() {
        guard !closed else { return }
        closed = true
        finalizeTimeoutTask?.cancel()
        finalizeTimeoutTask = nil
        receiveLoopTask?.cancel()
        receiveLoopTask = nil
        task?.cancel(with: .normalClosure, reason: nil)
        task = nil
    }

    // MARK: - 接收循环

    private func receiveLoop() async {
        guard let task else { return }

        while !Task.isCancelled {
            do {
                let message = try await task.receive()
                switch message {
                case .string(let raw):
                    handleServerMessage(raw)
                case .data(let data):
                    if let raw = String(data: data, encoding: .utf8) {
                        handleServerMessage(raw)
                    }
                @unknown default:
                    break
                }
            } catch {
                if !closed && !Task.isCancelled {
                    AppLog.network.error("WS receive 失败: \(error.localizedDescription, privacy: .public)")
                    onError?("连接中断")
                }
                break
            }
        }
    }

    @MainActor
    private func handleServerMessage(_ raw: String) {
        switch parseServerMessage(raw) {
        case .partial(let text, _):
            if !text.isEmpty {
                onPartial?(text)
            }
        case .final(let text, let asr, let llm):
            finalizeTimeoutTask?.cancel()
            finalizeTimeoutTask = nil
            AppLog.network.info("final asr=\(asr ?? 0, privacy: .public) llm=\(llm ?? 0, privacy: .public)")
            onFinal?(text)
        case .warning(let code, let message):
            AppLog.network.warning("服务端 warning [\(code, privacy: .public)]: \(message, privacy: .public)")
            onWarning?(message)
        case .error(_, let message):
            finalizeTimeoutTask?.cancel()
            finalizeTimeoutTask = nil
            AppLog.network.error("服务端错误: \(message, privacy: .public)")
            onError?(message)
        case .unknown:
            break
        }
    }

    // MARK: - 内部工具

    private func sendJSON(_ payload: [String: Any]) {
        guard !closed, let task else { return }
        guard let data = try? JSONSerialization.data(withJSONObject: payload),
              let raw = String(data: data, encoding: .utf8) else { return }
        task.send(.string(raw)) { error in
            if let error {
                AppLog.network.error("控制帧发送失败: \(error.localizedDescription, privacy: .public)")
            }
        }
    }
}
