import AppKit
import Foundation

@MainActor
final class VoiceTyperController {
    private let config: AppConfig
    private let asrService: ASRService
    private let llmCorrector: LLMCorrector?
    private let hotkeyService: HotkeyService
    private let audioCaptureService: AudioCaptureService
    private let textInsertionService: TextInsertionService

    private var asrSession: LocalASRSession?
    private var isRecording = false
    private var isRunning = false
    private var previewText = ""
    private var recordingStartedAt: Date?

    /// 录音时长低于此阈值的会话直接丢弃，避免误触上传无意义音频。
    /// 单进程架构下这不再是"省流量"的约定，纯粹是防误触。
    private static let minimumRecordingDuration: TimeInterval = 0.3

    var onStateChange: ((AppState) -> Void)?
    var onRecognizedText: ((String) -> Void)?
    var onPreviewUpdate: ((String) -> Void)?
    /// 非致命提示（预览失败等），UI 可短暂闪烁状态但不打断录音。
    var onPreviewWarning: ((String) -> Void)?
    /// 用户主动取消（录音中按 Esc）。与 .idle 区分，便于 UI 给出"已取消"提示。
    var onCancelled: (() -> Void)?
    /// 录音期间的实时音量电平（0…1 量级），供 HUD 波形显示。保证在主线程触发。
    var onAudioLevel: ((Float) -> Void)?
    var isStarted: Bool { isRunning }

    init(
        config: AppConfig,
        asrService: ASRService,
        hotkeyService: HotkeyService = HotkeyService(),
        audioCaptureService: AudioCaptureService = AudioCaptureService(),
        textInsertionService: TextInsertionService = TextInsertionService()
    ) {
        self.config = config
        self.asrService = asrService
        self.hotkeyService = hotkeyService
        self.audioCaptureService = audioCaptureService
        self.textInsertionService = textInsertionService

        if config.llm.enabled, !config.llm.baseURL.trimmingCharacters(in: .whitespaces).isEmpty {
            self.llmCorrector = LLMCorrector(config: LLMCorrector.Config(
                baseURL: config.llm.baseURL,
                apiKey: KeychainStore.loadLLMAPIKey(),
                model: config.llm.model,
                temperature: config.llm.temperature,
                maxTokens: config.llm.maxTokens,
                timeout: config.llm.timeout
            ))
        } else {
            self.llmCorrector = nil
        }
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
        // 电平回调在音频线程触发，跳回主线程再转发给 UI。
        // 音频引擎仅在录音期间运行，故无需按会话单独装卸此回调。
        audioCaptureService.onLevel = { [weak self] level in
            Task { @MainActor [weak self] in self?.onAudioLevel?(level) }
        }
        try hotkeyService.start(with: config.hotkey)
        isRunning = true
        onStateChange?(.idle)
    }

    func stop() {
        hotkeyService.stop()
        audioCaptureService.stopWithoutResult()
        teardownASRSession()
        isRunning = false
        isRecording = false
    }

    // MARK: - 录音流程

    private func beginRecording() {
        guard isRunning, !isRecording else { return }
        recordingStartedAt = Date()
        beginStreamingRecording()
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

        // stop() 触发 onTailChunk → sendAudio(tail) + finalize()
        audioCaptureService.stop()
    }

    /// 停止采集、丢弃尾音、关闭识别会话、清空预览。
    /// 不做状态转换，由调用方决定回到 idle 还是发"已取消"提示。
    private func resetRecording() {
        audioCaptureService.stopWithoutResult()
        teardownASRSession()
        previewText = ""
        onPreviewUpdate?("")
        recordingStartedAt = nil
        isRecording = false
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

    // MARK: - 识别路径

    private func beginStreamingRecording() {
        let session = asrService.makeSession(llmCorrector: llmCorrector)

        // partial 是全量预览文本，直接替换：本地识别引擎对已累积音频整段/滑窗重跑，
        // 后一次结果会修正前一次的文字，增量语义无法表达这种回溯修改。
        session.onPartial = { [weak self] text in
            guard let self else { return }
            self.previewText = text
            self.onPreviewUpdate?(self.previewText)
        }

        session.onFinal = { [weak self, weak session] text in
            guard let self else { return }
            if let s = session, self.asrSession === s {
                self.previewText = ""
                self.onPreviewUpdate?("")
                self.teardownASRSession()
                self.handleFinalText(text)
            } else {
                // 旧会话在后台完成：直接插入，不影响当前会话状态。
                let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !trimmed.isEmpty, self.isRunning else { return }
                _ = self.textInsertionService.insert(text: trimmed)
                self.onRecognizedText?(trimmed)
            }
        }

        session.onWarning = { [weak self, weak session] message in
            guard let self else { return }
            if let s = session, self.asrSession === s {
                self.onPreviewWarning?(message)
            }
        }

        session.onError = { [weak self, weak session] message in
            guard let self else { return }
            AppLog.asr.error("识别错误: \(message, privacy: .public)")
            if let s = session, self.asrSession === s {
                self.audioCaptureService.stopWithoutResult()
                self.teardownASRSession()
                self.previewText = ""
                self.onPreviewUpdate?("")
                self.isRecording = false
                self.onStateChange?(.error(message))
            }
        }

        audioCaptureService.onChunk = { [weak session] data in
            Task { @MainActor [weak session] in
                session?.sendAudio(data)
            }
        }

        audioCaptureService.onTailChunk = { [weak self, weak session] data in
            Task { @MainActor [weak self, weak session] in
                guard let self else { return }
                if !data.isEmpty {
                    session?.sendAudio(data)
                }
                // 本地推理没有网络往返，但仍设看门狗防止模型卡死导致 HUD 永久停在"识别中"。
                session?.finalize(timeout: 30)
                self.onStateChange?(.recognizing)
            }
        }

        do {
            try audioCaptureService.start()
        } catch {
            AppLog.audio.error("开始录音失败: \(error.localizedDescription, privacy: .public)")
            session.close()
            onStateChange?(.error("开始录音失败"))
            return
        }

        asrSession = session
        isRecording = true
        previewText = ""
        onStateChange?(.recording)
    }

    // MARK: - 公共处理

    /// 插入最终文本并更新状态。调用方负责在调用前完成会话拆解（teardownASRSession）。
    private func handleFinalText(_ text: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)

        guard !trimmed.isEmpty else {
            if !isRecording {
                onStateChange?(.idle)
            }
            return
        }

        guard isRunning else { return }

        onStateChange?(.inserting)
        let inserted = textInsertionService.insert(text: trimmed)
        if inserted {
            onRecognizedText?(trimmed)
            if !isRecording {
                onStateChange?(.idle)
            }
        } else {
            // 插入失败兜底：把结果写入剪贴板，避免长听写内容彻底丢失。
            textInsertionService.copyToClipboard(text: trimmed)
            AppLog.app.error("文本插入失败，已复制到剪贴板")
            onStateChange?(.error("插入失败，已复制到剪贴板，可手动粘贴"))
        }
    }

    private func teardownASRSession() {
        asrSession?.close()
        asrSession = nil
        asrService.sessionEnded()
        audioCaptureService.onChunk = nil
        audioCaptureService.onTailChunk = nil
    }
}
