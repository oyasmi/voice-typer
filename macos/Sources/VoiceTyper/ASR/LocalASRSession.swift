import Foundation

/// 单次录音会话的本地识别接缝层。接口**刻意与旧的 `StreamingASRClient`（WebSocket 客户端）
/// 逐字一致**，使 `VoiceTyperController` 的四个回调闭包可以原样搬运，只需把
/// `client.connect(server:llmRecorrect:)` 换成 `asrService.makeSession(llmCorrector:)`。
///
/// 职责映射（对照服务端 `app.StreamRecognizeHandler`，见 macos/DESIGN.md §5.2）：
/// - 有预览在跑就跳过（`previewInFlight`），跳过的音频在下次预览一并处理
/// - partial 只在文本变化时下发
/// - `isFinalizing` 后不再补发 partial
/// - 预览异常 → `onWarning`，会话继续
/// - 300 秒会话上限 → 一次性 `onWarning`，之后静默丢弃新音频
/// - finalize → 离线整段识别 → （可选）LLM 纠错 → `onFinal`
@MainActor
final class LocalASRSession {
    var onPartial: ((String) -> Void)?
    var onFinal: ((String) -> Void)?
    var onWarning: ((String) -> Void)?
    var onError: ((String) -> Void)?

    private static let maxSessionSamples = 300 * 16000

    private let asrQueue: DispatchQueue
    private let engineAccessor: () -> (any SenseVoiceRecognizing)?
    private let llmCorrector: LLMCorrector?

    private var buffer: RecognitionBuffer?
    /// 引擎尚未加载完成时，音频先攒在这里；引擎就绪后的第一次 sendAudio 会把它们一并灌入 buffer。
    private var pendingAudio: [Float] = []

    private var previewInFlight = false
    private var isFinalizing = false
    private var capped = false
    private var closed = false
    private var lastPreview = ""
    private var finalizeWatchdog: Task<Void, Never>?

    init(asrQueue: DispatchQueue, engineAccessor: @escaping () -> (any SenseVoiceRecognizing)?, llmCorrector: LLMCorrector?) {
        self.asrQueue = asrQueue
        self.engineAccessor = engineAccessor
        self.llmCorrector = llmCorrector
    }

    func sendAudio(_ data: Data) {
        guard !closed, !isFinalizing else { return }
        guard data.count % 4 == 0 else {
            onWarning?("音频帧长度 \(data.count) 不是 4 的倍数，已丢弃")
            return
        }
        guard !data.isEmpty else { return }

        let samples: [Float] = data.withUnsafeBytes { raw in
            Array(raw.bindMemory(to: Float.self))
        }

        ensureBufferIfPossible()
        guard let buffer else {
            // 引擎仍在加载：先攒着，下次 sendAudio（或 finalize）时补上。
            pendingAudio.append(contentsOf: samples)
            return
        }

        if buffer.sampleCount >= Self.maxSessionSamples {
            if !capped {
                capped = true
                onWarning?("录音已达 \(Self.maxSessionSamples / 16000) 秒上限，后续音频不再录入")
            }
            return
        }

        buffer.append(samples)
        schedulePreview()
    }

    /// - Parameter timeout: 等待识别完成的最长时间；本地无网络往返，这里纯粹是防止推理卡死的
    ///   看门狗。传 <= 0 表示不设超时。
    func finalize(timeout: TimeInterval) {
        guard !closed, !isFinalizing else { return }
        isFinalizing = true
        finalizeWatchdog?.cancel()

        if timeout > 0 {
            finalizeWatchdog = Task { [weak self] in
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                guard !Task.isCancelled, let self, !self.closed else { return }
                AppLog.asr.error("finalize 超时（\(timeout, privacy: .public)s）")
                self.onError?("识别超时")
            }
        }

        ensureBufferIfPossible()
        guard let buffer else {
            // 引擎仍未就绪（极短录音、模型刚好还没加载完）：等一小段时间重试，
            // 而不是立即报错——preload 已经在后台跑，多数情况下几百毫秒内就绪。
            waitForEngineThenFinalize()
            return
        }

        runFinalize(on: buffer)
    }

    func close() {
        guard !closed else { return }
        closed = true
        finalizeWatchdog?.cancel()
        finalizeWatchdog = nil
        buffer = nil
    }

    // MARK: - 私有实现

    private func ensureBufferIfPossible() {
        guard buffer == nil, let engine = engineAccessor() else { return }
        let newBuffer = RecognitionBuffer(engine: engine)
        if !pendingAudio.isEmpty {
            newBuffer.append(pendingAudio)
            pendingAudio.removeAll()
        }
        buffer = newBuffer
    }

    private func schedulePreview() {
        guard !isFinalizing, !previewInFlight, let buffer else { return }
        previewInFlight = true
        asrQueue.async { [weak self] in
            let result = Result { try buffer.preview() }
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.previewInFlight = false
                guard !self.closed, !self.isFinalizing else { return }
                switch result {
                case .success(let text):
                    if !text.isEmpty, text != self.lastPreview {
                        self.lastPreview = text
                        self.onPartial?(text)
                    }
                case .failure(let error):
                    AppLog.asr.warning("预览识别异常: \(String(describing: error), privacy: .public)")
                    self.onWarning?("\(error.localizedDescription)")
                }
            }
        }
    }

    private func waitForEngineThenFinalize(attempt: Int = 0) {
        // 引擎加载耗时约 0.85s；每 100ms 探测一次，最多等 5s——超过这个时间基本
        // 意味着模型加载失败，交给外层 finalizeWatchdog 的超时兜底报错。
        guard attempt < 50, !closed else {
            if !closed {
                isFinalizing = false
                onError?("识别引擎尚未就绪")
            }
            return
        }
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 100_000_000)
            guard let self, !self.closed else { return }
            self.ensureBufferIfPossible()
            if let buffer = self.buffer {
                self.runFinalize(on: buffer)
            } else {
                self.waitForEngineThenFinalize(attempt: attempt + 1)
            }
        }
    }

    private func runFinalize(on buffer: RecognitionBuffer) {
        asrQueue.async { [weak self] in
            let result = Result { try buffer.finalize() }
            Task { @MainActor [weak self] in
                guard let self, !self.closed else { return }
                self.finalizeWatchdog?.cancel()
                self.finalizeWatchdog = nil

                switch result {
                case .success(let text):
                    self.completeWithASRText(text)
                case .failure(let error):
                    AppLog.asr.error("离线复识别失败: \(String(describing: error), privacy: .public)")
                    self.onError?(error.localizedDescription)
                }
            }
        }
    }

    /// 拿到 ASR 原文后：若启用了 LLM 纠错，先把原文通过 onPartial 顶到 HUD 上
    /// （让用户立刻看到结果，纠错中不会像"卡住"），再异步纠错，最终以纠错结果调用 onFinal。
    /// 这一步在跨进程架构下没有意义（要多一次网络往返），进程内是免费的。
    private func completeWithASRText(_ text: String) {
        guard let llmCorrector, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            onFinal?(text)
            return
        }

        onPartial?(text)
        Task { @MainActor [weak self] in
            guard let self, !self.closed else { return }
            let corrected = await llmCorrector.correct(text)
            guard !self.closed else { return }
            self.onFinal?(corrected)
        }
    }
}
