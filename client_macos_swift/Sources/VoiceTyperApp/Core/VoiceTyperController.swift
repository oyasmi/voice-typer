import AppKit
import Foundation

@MainActor
final class VoiceTyperController {
    private let config: AppConfig
    private let hotwords: [String]
    private let hotkeyService: HotkeyService
    private let audioCaptureService: AudioCaptureService
    private let textInsertionService: TextInsertionService

    private var asrClient: StreamingASRClient?
    private var isRecording = false
    private var isRunning = false
    private var accumulatedPreview = ""
    private var batchAudioChunks: [Data] = []

    var onStateChange: ((AppState) -> Void)?
    var onRecognizedText: ((String) -> Void)?
    var onPreviewUpdate: ((String) -> Void)?
    var isStarted: Bool { isRunning }

    init(
        config: AppConfig,
        hotwords: [String],
        hotkeyService: HotkeyService = HotkeyService(),
        audioCaptureService: AudioCaptureService = AudioCaptureService(),
        textInsertionService: TextInsertionService = TextInsertionService()
    ) {
        self.config = config
        self.hotwords = hotwords
        self.hotkeyService = hotkeyService
        self.audioCaptureService = audioCaptureService
        self.textInsertionService = textInsertionService
    }

    func start() throws {
        guard !isRunning else { return }

        hotkeyService.onPress = { [weak self] in
            Task { @MainActor [weak self] in self?.beginRecording() }
        }
        hotkeyService.onRelease = { [weak self] in
            Task { @MainActor [weak self] in self?.finishRecording() }
        }
        try hotkeyService.start(with: config.hotkey)
        isRunning = true
        onStateChange?(.idle)
    }

    func stop() {
        hotkeyService.stop()
        audioCaptureService.stopWithoutResult()
        teardownASRClient()
        isRunning = false
        isRecording = false
    }

    func healthCheck() async -> Bool {
        guard let url = URL(string: "http://\(config.server.host):\(config.server.port)/health") else {
            return false
        }
        var request = URLRequest(url: url)
        request.timeoutInterval = 5.0
        let trimmedKey = config.server.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmedKey.isEmpty {
            request.setValue("Bearer \(trimmedKey)", forHTTPHeaderField: "Authorization")
        }
        do {
            let (data, _) = try await URLSession.shared.data(for: request)
            let payload = try JSONSerialization.jsonObject(with: data) as? [String: Any]
            return payload?["ready"] as? Bool ?? false
        } catch {
            AppLog.network.error("健康检查失败: \(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    // MARK: - 录音流程

    private func beginRecording() {
        guard isRunning, !isRecording else { return }

        if config.server.streaming {
            beginStreamingRecording()
        } else {
            beginBatchRecording()
        }
    }

    private func finishRecording() {
        guard isRecording else { return }
        isRecording = false
        // 流式：stop() 触发 onTailChunk → sendAudio(tail) + finalize()
        // 非流式：stop() 触发 onTailChunk → 累积尾音 → HTTP POST
        audioCaptureService.stop()
    }

    // MARK: - 流式路径

    private func beginStreamingRecording() {
        let client = StreamingASRClient()

        client.onPartial = { [weak self] fragment in
            guard let self else { return }
            self.accumulatedPreview += fragment
            self.onPreviewUpdate?(self.accumulatedPreview)
        }

        // [weak client] 而非 self.asrClient：onFinal 触发时 asrClient 可能已指向更新的会话，
        // 通过身份比较确认是否是当前会话，避免误关后续录音的 WS 连接。
        client.onFinal = { [weak self, weak client] text in
            guard let self else { return }
            if let c = client, self.asrClient === c {
                // 当前（最新）会话完成：清理预览、完整拆解、状态转换
                self.accumulatedPreview = ""
                self.onPreviewUpdate?("")
                self.teardownASRClient()
                self.handleFinalText(text)
            } else {
                // 旧会话在后台完成：静默关闭 + 直接插入，不影响当前会话状态
                client?.close()
                let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !trimmed.isEmpty, self.isRunning else { return }
                _ = self.textInsertionService.insert(text: trimmed)
                self.onRecognizedText?(trimmed)
            }
        }

        client.onError = { [weak self, weak client] message in
            guard let self else { return }
            AppLog.network.error("ASR 错误: \(message, privacy: .public)")
            if let c = client, self.asrClient === c {
                self.teardownASRClient()
                self.accumulatedPreview = ""
                self.onPreviewUpdate?("")
                self.onStateChange?(.error(message))
                self.isRecording = false
            } else {
                // 旧会话出错，不干扰当前会话
                client?.close()
            }
        }

        do {
            try client.connect(
                server: config.server,
                hotwords: hotwords,
                llmRecorrect: config.server.llmRecorrect
            )
        } catch {
            AppLog.network.error("WS 连接失败: \(error.localizedDescription, privacy: .public)")
            onStateChange?(.error("无法连接到识别服务"))
            return
        }

        // onChunk 在 AVAudioEngine 音频线程回调，需跳回 MainActor 再访问 @MainActor 的 client
        audioCaptureService.onChunk = { [weak client] data in
            Task { @MainActor [weak client] in
                client?.sendAudio(data)
            }
        }

        // onTailChunk 同理；sendAudio + finalize 需保证在同一 MainActor 执行块内顺序完成
        audioCaptureService.onTailChunk = { [weak self, weak client] data in
            Task { @MainActor [weak self, weak client] in
                if !data.isEmpty {
                    client?.sendAudio(data)
                }
                client?.finalize()
                self?.onStateChange?(.recognizing)
            }
        }

        do {
            try audioCaptureService.start()
        } catch {
            AppLog.audio.error("开始录音失败: \(error.localizedDescription, privacy: .public)")
            client.close()
            onStateChange?(.error("开始录音失败"))
            return
        }

        asrClient = client
        isRecording = true
        accumulatedPreview = ""
        onStateChange?(.recording)
    }

    // MARK: - 非流式路径

    private func beginBatchRecording() {
        batchAudioChunks = []

        audioCaptureService.onChunk = { [weak self] data in
            self?.batchAudioChunks.append(data)
        }

        audioCaptureService.onTailChunk = { [weak self] data in
            Task { @MainActor [weak self] in
                guard let self else { return }
                if !data.isEmpty {
                    self.batchAudioChunks.append(data)
                }
                await self.performBatchRecognition()
            }
        }

        do {
            try audioCaptureService.start()
        } catch {
            AppLog.audio.error("开始录音失败: \(error.localizedDescription, privacy: .public)")
            onStateChange?(.error("开始录音失败"))
            return
        }

        isRecording = true
        onStateChange?(.recording)
    }

    private func performBatchRecognition() async {
        onStateChange?(.recognizing)

        let combinedData = batchAudioChunks.reduce(Data(), +)
        batchAudioChunks = []

        guard !combinedData.isEmpty else {
            teardownASRClient()
            onStateChange?(.idle)
            return
        }

        let client = ASRClient(server: config.server)
        let hotwordsString = hotwords.joined(separator: " ")

        do {
            let text = try await client.recognize(
                audioData: combinedData,
                hotwords: hotwordsString,
                llmRecorrect: config.server.llmRecorrect
            )
            teardownASRClient()
            handleFinalText(text)
        } catch {
            teardownASRClient()
            AppLog.network.error("批量识别失败: \(error.localizedDescription, privacy: .public)")
            onStateChange?(.error("识别失败：\(error.localizedDescription)"))
        }
    }

    // MARK: - 公共处理

    /// 插入最终文本并更新状态。
    /// 调用方负责在调用前完成会话拆解（teardownASRClient / client.close）。
    private func handleFinalText(_ text: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)

        guard !trimmed.isEmpty else {
            if !isRecording { onStateChange?(.idle) }
            return
        }

        guard isRunning else { return }

        onStateChange?(.inserting)
        let inserted = textInsertionService.insert(text: trimmed)
        if inserted {
            onRecognizedText?(trimmed)
            // 若此时有新录音正在进行（并发会话），不覆盖 .recording 状态
            if !isRecording { onStateChange?(.idle) }
        } else {
            onStateChange?(.error("文本插入失败"))
        }
    }

    private func teardownASRClient() {
        asrClient?.close()
        asrClient = nil
        batchAudioChunks = []
        audioCaptureService.onChunk = nil
        audioCaptureService.onTailChunk = nil
    }
}
