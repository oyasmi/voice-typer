import AppKit
import Foundation

@MainActor
final class VoiceTyperController {
    private enum Phase {
        case recording
        case recognizing
    }

    /// 一次听写的全部状态。控制器是它的唯一强持有者：一旦被下一段听写覆盖，
    /// 旧值必须先经过 `finish(_:)` 收尾，绝不能被静默替换（R2-01）。
    private final class Utterance {
        let session: LocalASRSession
        /// 录音开始时的前台应用 pid；插入前据此判断焦点是否已变化（F-10）。
        let expectedFrontmostPID: pid_t?
        let startedAt: Date
        var phase: Phase = .recording

        init(session: LocalASRSession, expectedFrontmostPID: pid_t?, startedAt: Date) {
            self.session = session
            self.expectedFrontmostPID = expectedFrontmostPID
            self.startedAt = startedAt
        }
    }

    private enum Outcome {
        case text(String)
        case failed(String)
        case cancelled
        case discarded
        /// stop()：控制器整体停止，不发任何状态变化。
        case shutdown
    }

    private let config: AppConfig
    private let asrService: ASRService
    private let llmCorrector: LLMCorrector?
    private let hotkeyService: HotkeyListening
    private let audioCaptureService: AudioCapturing
    private let textInsertionService: TextInserting

    private var active: Utterance?
    private var isRunning = false
    private var previewText = ""

    /// 派生自 `active`，不再是独立事实源：不支持重叠听写，同一时刻只可能有
    /// 一段录音在进行（AGENTS.md 的主流程本就是单段 Idle→Recording→Recognizing→Inserting）。
    private var isRecording: Bool { active?.phase == .recording }

    /// 录音时长低于此阈值的会话直接丢弃，避免处理误触产生的无意义音频。
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
        hotkeyService: HotkeyListening = HotkeyService(),
        audioCaptureService: AudioCapturing = AudioCaptureService(),
        textInsertionService: TextInserting = TextInsertionService()
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
                AppLog.llm.error("LLM Base URL 无法解析为合法请求地址，本次运行禁用智能校对")
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
        audioCaptureService.onDeviceChanged = { [weak self] in
            Task { @MainActor [weak self] in self?.handleDeviceChanged() }
        }
        try hotkeyService.start(with: config.hotkey)
        isRunning = true
        onStateChange?(.idle)
    }

    func stop() {
        hotkeyService.stop()
        audioCaptureService.stopWithoutResult()
        if active != nil {
            finish(.shutdown)
        }
        isRunning = false
    }

    /// 暂停全局热键监听但不销毁进行中的听写（R2-04）：设置页录制新热键、重新加载
    /// 模型等操作只应停掉按键 tap，不应该销毁用户正在说的内容。调用方须先确认
    /// 当前没有进行中的听写（`AppState.isActiveDictation`），否则应拒绝暂停请求。
    func suspendHotkeyListening() {
        hotkeyService.stop()
    }

    /// 恢复热键监听。控制器已 `start()` 过才有效。
    func resumeHotkeyListening() throws {
        guard isRunning else { return }
        try hotkeyService.start(with: config.hotkey)
    }

    // MARK: - 录音流程

    private func beginRecording() {
        guard isRunning else { return }
        guard active == nil else {
            onPreviewWarning?("上一段听写尚未完成")
            return
        }
        beginDictationSession()
    }

    private func finishRecording() {
        guard let utterance = active, utterance.phase == .recording else { return }

        // 短录音过滤：低于阈值的录音视为误触，立即取消。
        if Date().timeIntervalSince(utterance.startedAt) < Self.minimumRecordingDuration {
            AppLog.audio.info("录音时长低于阈值，已丢弃")
            audioCaptureService.stopWithoutResult()
            finish(.discarded)
            return
        }

        // stop() 触发 onTailChunk → sendAudio(tail) + finalize()，phase 在那里推进到 .recognizing。
        audioCaptureService.stop()
    }

    /// 用户在录音过程中按 Esc 主动取消，通过 `onCancelled` 让 UI 给出"已取消"提示。
    private func cancelByUser() {
        guard let utterance = active, utterance.phase == .recording else { return }
        audioCaptureService.stopWithoutResult()
        finish(.cancelled)
    }

    /// 录音期间输入设备变化：`AudioCaptureService` 已自行走过 `stop()` → `onTailChunk`
    /// 把已采到的音频交给当前会话继续识别（phase 已在那条路径推进到 .recognizing），
    /// 这里只需要把"设备变了"这件事明确告知用户（R2-03）。
    private func handleDeviceChanged() {
        guard active != nil else { return }
        onPreviewWarning?("输入设备已变化，本次录音已结束")
    }

    // MARK: - 识别路径

    private func beginDictationSession() {
        let session = asrService.makeSession(llmCorrector: llmCorrector)
        let expectedFrontmostPID = NSWorkspace.shared.frontmostApplication?.processIdentifier
        let utterance = Utterance(session: session, expectedFrontmostPID: expectedFrontmostPID, startedAt: Date())

        // partial 是全量预览文本，直接替换：本地识别引擎对已累积音频整段/滑窗重跑，
        // 后一次结果会修正前一次的文字，增量语义无法表达这种回溯修改。
        session.onPartial = { [weak self] text in
            guard let self else { return }
            self.previewText = text
            self.onPreviewUpdate?(self.previewText)
        }

        session.onFinal = { [weak self] text in
            self?.finish(.text(text))
        }

        session.onWarning = { [weak self] message in
            self?.onPreviewWarning?(message)
        }

        session.onError = { [weak self] message in
            AppLog.asr.error("识别错误: \(message, privacy: .public)")
            self?.finish(.failed(message))
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
                self.active?.phase = .recognizing
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
            asrService.sessionEnded()
            audioCaptureService.onChunk = nil
            audioCaptureService.onTailChunk = nil
            onStateChange?(.error("开始录音失败"))
            return
        }

        active = utterance
        previewText = ""
        onStateChange?(.recording)
    }

    // MARK: - 唯一收尾入口

    /// 所有终止路径（onFinal、onError、Esc 取消、短录音丢弃、stop()）都只调用这里。
    /// 靠"先取走 active 再处理"保证幂等：任何重复到达（例如已关闭会话的迟到回调）
    /// 直接返回，不会二次收尾。
    private func finish(_ outcome: Outcome) {
        guard let utterance = active else { return }
        active = nil
        utterance.session.close()
        asrService.sessionEnded()
        audioCaptureService.onChunk = nil
        audioCaptureService.onTailChunk = nil
        previewText = ""
        onPreviewUpdate?("")

        switch outcome {
        case .text(let text):
            handleFinalText(text, expectedFrontmostPID: utterance.expectedFrontmostPID)
        case .failed(let message):
            onStateChange?(.error(message))
        case .cancelled:
            if isRunning {
                AppLog.audio.info("用户取消录音")
                onCancelled?()
            }
        case .discarded:
            if isRunning {
                onStateChange?(.idle)
            }
        case .shutdown:
            break
        }
    }

    /// 插入最终文本并更新状态。调用方（`finish(_:)`）负责在调用前完成会话拆解。
    private func handleFinalText(_ text: String, expectedFrontmostPID: pid_t?) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)

        guard !trimmed.isEmpty else {
            onStateChange?(.idle)
            return
        }

        guard isRunning else { return }

        onStateChange?(.inserting)
        switch textInsertionService.insert(text: trimmed, expectedFrontmostPID: expectedFrontmostPID) {
        case .inserted:
            onRecognizedText?(trimmed)
            onStateChange?(.idle)
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
}
