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
    private var recordingStartedAt: Date?

    /// 录音时长低于此阈值的会话直接丢弃，避免误触上传无意义音频。
    /// 见仓库根目录 PROTOCOL.md "短录音过滤"。
    private static let minimumRecordingDuration: TimeInterval = 0.3

    var onStateChange: ((AppState) -> Void)?
    var onRecognizedText: ((String) -> Void)?
    var onPreviewUpdate: ((String) -> Void)?
    /// 非致命提示（流式 partial 失败等），UI 可短暂闪烁状态但不打断录音。
    var onPreviewWarning: ((String) -> Void)?
    /// 用户主动取消（录音中按 Esc）。与 .idle 区分，便于 UI 给出"已取消"提示。
    var onCancelled: (() -> Void)?
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
        hotkeyService.onCancel = { [weak self] in
            Task { @MainActor [weak self] in self?.cancelByUser() }
        }
        try hotkeyService.start(with: config.hotkey)
        isRunning = true
        // 预热音频引擎（不开麦、不亮指示灯），缩短首次按键的冷启动。
        audioCaptureService.prewarm()
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
        let result = await ServerHealthProbe.check(server: config.server)
        if let version = result.version {
            AppLog.network.info("服务端版本: \(version, privacy: .public)")
        }
        return result.ready
    }

    // MARK: - 录音流程

    private func beginRecording() {
        guard isRunning, !isRecording else { return }

        recordingStartedAt = Date()
        if config.server.streaming {
            beginStreamingRecording()
        } else {
            beginBatchRecording()
        }
    }

    private func finishRecording() {
        guard isRecording else { return }
        isRecording = false

        // 短录音过滤：低于阈值的录音视为误触，立即取消。
        if let startedAt = recordingStartedAt,
           Date().timeIntervalSince(startedAt) < Self.minimumRecordingDuration {
            AppLog.audio.info("录音时长低于阈值，已丢弃")
            cancelCurrentRecording()
            return
        }

        // 流式：stop() 触发 onTailChunk → sendAudio(tail) + finalize()
        // 非流式：stop() 触发 onTailChunk → 累积尾音 → HTTP POST
        audioCaptureService.stop()
    }

    /// 停止采集、丢弃尾音、关闭 WS、清空预览并重新预热引擎。
    /// 不做状态转换，由调用方决定回到 idle 还是发"已取消"提示。
    private func resetRecording() {
        audioCaptureService.stopWithoutResult()
        teardownASRClient()
        accumulatedPreview = ""
        onPreviewUpdate?("")
        recordingStartedAt = nil
        isRecording = false
        audioCaptureService.prewarm()
    }

    /// 取消当前录音并静默回到 idle（短录音过滤等内部触发）。
    private func cancelCurrentRecording() {
        resetRecording()
        if isRunning {
            onStateChange?(.idle)
        }
    }

    /// 用户在录音过程中按 Esc 主动取消，通过 `onCancelled` 让 UI 给出"已取消"提示。
    private func cancelByUser() {
        guard isRecording else { return }
        resetRecording()
        if isRunning {
            AppLog.audio.info("用户取消录音")
            onCancelled?()
        }
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

        client.onWarning = { [weak self, weak client] message in
            guard let self else { return }
            // 仅当前会话的 warning 才转发到 HUD；旧会话的提示直接丢弃。
            if let c = client, self.asrClient === c {
                self.onPreviewWarning?(message)
            }
        }

        client.onError = { [weak self, weak client] message in
            guard let self else { return }
            AppLog.network.error("ASR 错误: \(message, privacy: .public)")
            if let c = client, self.asrClient === c {
                // 停止采集，避免 WS 中途出错后 AVAudioEngine 继续运行、麦克风指示灯常亮，
                // 并防止残留音频混入下一次录音。
                self.audioCaptureService.stopWithoutResult()
                self.teardownASRClient()
                self.accumulatedPreview = ""
                self.onPreviewUpdate?("")
                self.isRecording = false
                self.audioCaptureService.prewarm()
                self.onStateChange?(.error(message))
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
                guard let self else { return }
                if !data.isEmpty {
                    client?.sendAudio(data)
                }
                // 传入超时：服务端卡住 / 网络半开时不至于让 HUD 永久停在"识别中"。
                client?.finalize(timeout: self.config.server.timeout)
                self.onStateChange?(.recognizing)
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
        let combinedData = batchAudioChunks.reduce(Data(), +)
        batchAudioChunks = []

        // 兜底：若实际音频不足阈值（采样数 = bytes / 4），同样丢弃。
        let sampleCount = combinedData.count / MemoryLayout<Float>.size
        let minimumSamples = Int(Self.minimumRecordingDuration * AppConstants.targetSampleRate)
        guard !combinedData.isEmpty, sampleCount >= minimumSamples else {
            teardownASRClient()
            audioCaptureService.prewarm()
            onStateChange?(.idle)
            return
        }

        onStateChange?(.recognizing)

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
            audioCaptureService.prewarm()
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
            if !isRecording {
                audioCaptureService.prewarm()
                onStateChange?(.idle)
            }
            return
        }

        guard isRunning else { return }

        onStateChange?(.inserting)
        let inserted = textInsertionService.insert(text: trimmed)
        if inserted {
            onRecognizedText?(trimmed)
            // 若此时有新录音正在进行（并发会话），不覆盖 .recording 状态
            if !isRecording {
                audioCaptureService.prewarm()
                onStateChange?(.idle)
            }
        } else {
            // 插入失败兜底：把结果写入剪贴板，避免长听写内容彻底丢失。
            textInsertionService.copyToClipboard(text: trimmed)
            AppLog.app.error("文本插入失败，已复制到剪贴板")
            onStateChange?(.error("插入失败，已复制到剪贴板，可手动粘贴"))
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
