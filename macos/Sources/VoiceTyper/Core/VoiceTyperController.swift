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

    /// 按录音发起顺序记账的听写结果队列：旧会话（LLM 纠错未返回时又开始下一次录音）
    /// 与当前会话都在此排队，只有队首已有结果时才出队提交，保证多段听写按录音顺序
    /// 上屏；同时统一走 `handleFinalText`，令旧会话也拥有插入失败复制剪贴板兜底与
    /// 错误上报，不再直接丢弃（F-05）。
    private struct PendingUtterance {
        let id: UInt64
        /// 录音开始时的前台应用 pid；插入前据此判断焦点是否已变化（F-10）。
        let expectedFrontmostPID: pid_t?
        /// nil 表示仍在识别/纠错中；非 nil 时即可提交（空串代表失败/丢弃，不插入文本）。
        var result: String?
    }
    private var pending: [PendingUtterance] = []
    private var nextUtteranceID: UInt64 = 0
    /// 当前会话对应的队列 id；录音被取消/丢弃（从未触发 finalize，因此永远不会收到
    /// onFinal/onError）时，靠它主动让出队列位置，避免后续段被永久卡住。
    private var currentUtteranceID: UInt64?

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

        if config.llm.enabled, let chatURL = LLMEndpoint.chatCompletionsURL(from: config.llm.baseURL) {
            self.llmCorrector = LLMCorrector(config: LLMCorrector.Config(
                chatCompletionsURL: chatURL,
                apiKey: KeychainStore.loadLLMAPIKey(),
                model: config.llm.model,
                temperature: config.llm.temperature,
                maxTokens: config.llm.maxTokens,
                timeout: config.llm.timeout
            ))
        } else {
            if config.llm.enabled {
                AppLog.llm.error("LLM Base URL 无法解析为合法请求地址，本次运行禁用智能纠错")
            }
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
        audioCaptureService.onFatalError = { [weak self] message in
            Task { @MainActor [weak self] in self?.onPreviewWarning?(message) }
        }
        try hotkeyService.start(with: config.hotkey)
        isRunning = true
        onStateChange?(.idle)
    }

    func stop() {
        hotkeyService.stop()
        audioCaptureService.stopWithoutResult()
        teardownASRSession()
        pending.removeAll()
        isRunning = false
        isRecording = false
    }

    // MARK: - 录音流程

    private func beginRecording() {
        guard isRunning, !isRecording else { return }
        recordingStartedAt = Date()
        beginDictationSession()
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
        // finalize 从未被触发，该会话永远不会回调 onFinal/onError，必须主动让出队列位置。
        if let id = currentUtteranceID {
            complete(utteranceID: id, result: "")
        }
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

    private func beginDictationSession() {
        let session = asrService.makeSession(llmCorrector: llmCorrector)

        let utteranceID = nextUtteranceID
        nextUtteranceID += 1
        let expectedFrontmostPID = NSWorkspace.shared.frontmostApplication?.processIdentifier
        pending.append(PendingUtterance(id: utteranceID, expectedFrontmostPID: expectedFrontmostPID, result: nil))
        currentUtteranceID = utteranceID

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
            }
            // 旧会话与当前会话统一走有序提交队列：只有排在前面的段都已有结果时才出队，
            // 保证多段听写按录音发起顺序上屏，且都获得 handleFinalText 里的插入失败
            // 兜底与状态收尾（F-05）。
            self.complete(utteranceID: utteranceID, result: text)
        }

        session.onWarning = { [weak self, weak session] message in
            guard let self else { return }
            if let s = session, self.asrSession === s {
                self.onPreviewWarning?(message)
            }
        }

        session.onError = { [weak self, weak session] message in
            guard let self else { return }
            let isCurrent = session.map { self.asrSession === $0 } ?? false
            if isCurrent {
                AppLog.asr.error("识别错误: \(message, privacy: .public)")
                self.audioCaptureService.stopWithoutResult()
                self.teardownASRSession()
                self.previewText = ""
                self.onPreviewUpdate?("")
                self.isRecording = false
                self.onStateChange?(.error(message))
            } else {
                AppLog.asr.error("旧会话识别错误（已被新录音顶替）: \(message, privacy: .public)")
            }
            // 失败也必须让出队列位置，否则后续更晚开始但更早识别完成的段会被永久卡住。
            self.complete(utteranceID: utteranceID, result: "")
        }

        audioCaptureService.onChunk = { [weak session] samples in
            Task { @MainActor [weak session] in
                session?.sendAudio(samples)
            }
        }

        audioCaptureService.onTailChunk = { [weak self, weak session] samples in
            Task { @MainActor [weak self, weak session] in
                guard let self else { return }
                if !samples.isEmpty {
                    session?.sendAudio(samples)
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
            currentUtteranceID = nil
            complete(utteranceID: utteranceID, result: "") // 释放队列位置，避免后续段被永久卡住
            onStateChange?(.error("开始录音失败"))
            return
        }

        asrSession = session
        isRecording = true
        previewText = ""
        onStateChange?(.recording)
    }

    // MARK: - 有序提交队列

    private func complete(utteranceID: UInt64, result: String) {
        guard let index = pending.firstIndex(where: { $0.id == utteranceID }) else { return }
        pending[index].result = result
        drainCommits()
    }

    /// 只要队首已有结果就按顺序出队提交，保证多段听写按录音发起顺序上屏。
    private func drainCommits() {
        while let first = pending.first, let result = first.result {
            pending.removeFirst()
            handleFinalText(result, expectedFrontmostPID: first.expectedFrontmostPID)
        }
    }

    // MARK: - 公共处理

    /// 插入最终文本并更新状态。调用方负责在调用前完成会话拆解（teardownASRSession）。
    private func handleFinalText(_ text: String, expectedFrontmostPID: pid_t?) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)

        guard !trimmed.isEmpty else {
            if !isRecording {
                onStateChange?(.idle)
            }
            return
        }

        guard isRunning else { return }

        onStateChange?(.inserting)
        switch textInsertionService.insert(text: trimmed, expectedFrontmostPID: expectedFrontmostPID) {
        case .inserted:
            onRecognizedText?(trimmed)
            if !isRecording {
                onStateChange?(.idle)
            }
        case .focusChanged:
            // 录音开始到插入之间前台应用已切换：不写入用户未预期的窗口，只复制到剪贴板。
            textInsertionService.copyToClipboard(text: trimmed)
            AppLog.app.warning("目标窗口已变化，插入已取消，改为复制到剪贴板")
            onStateChange?(.error("目标窗口已变化，结果已复制到剪贴板"))
        case .failed:
            // 插入失败兜底：把结果写入剪贴板，避免长听写内容彻底丢失。
            textInsertionService.copyToClipboard(text: trimmed)
            AppLog.app.error("文本插入失败，已复制到剪贴板")
            onStateChange?(.error("插入失败，已复制到剪贴板，可手动粘贴"))
        }
    }

    private func teardownASRSession() {
        asrSession?.close()
        asrSession = nil
        currentUtteranceID = nil
        asrService.sessionEnded()
        audioCaptureService.onChunk = nil
        audioCaptureService.onTailChunk = nil
    }
}
