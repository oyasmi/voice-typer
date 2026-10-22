import Foundation

/// 跨线程读写的一次性标志：在 MainActor 上置位，asrQueue 上的推理闭包在跑之前查询（N-01）。
private final class CancelFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var flag = false

    var isSet: Bool {
        lock.lock(); defer { lock.unlock() }
        return flag
    }

    func set() {
        lock.lock(); flag = true; lock.unlock()
    }
}

/// 引擎还没加载完时用户正在等待的阶段（R8-A1），用于让浮窗说明「在等模型」。
enum EngineWaitStage: Equatable {
    /// 录音中已攒满约 1s 音频，引擎仍未就绪。
    case recording
    /// 松键后引擎仍未就绪（约 300ms 后触发）。
    case finalizing
}

/// 单次录音会话的本地识别接缝层：接收流式音频、驱动滑窗预览、录音结束后离线整段
/// 复识别、可选 LLM 校对，通过四个回调把结果交给 `VoiceTyperController`。
///
/// 行为：
/// - 有预览在跑就跳过（`previewInFlight`），跳过的音频在下次预览一并处理
/// - partial 只在文本变化时下发
/// - `stopPreviews()` / `isFinalizing` 后不再发起、也不再补发 partial
/// - 预览异常 → `onWarning`，会话继续
/// - 单段会话上限 → 一次性 `onWarning`，之后静默丢弃新音频
/// - finalize → 离线整段识别 → （可选）LLM 校对 → `onFinal`
@MainActor
final class LocalASRSession {
    var onPartial: ((String) -> Void)?
    var onFinal: ((String) -> Void)?
    var onWarning: ((String) -> Void)?
    var onError: ((String) -> Void)?
    /// 达到单段录音上限时触发一次：只发 `onWarning` 无法让用户知道后续说的话已经不会被
    /// 录入（HUD 的警告闪烁只持续 1.2s，随后恢复"录音中"，用户完全无法察觉自己在对着一个
    /// 已停止收音的会话继续说话）。调用方应据此立即结束本次录音、把已录到的内容正常上屏，
    /// 而不是任由用户继续说下去、内容却被静默丢弃（R3-03）。
    var onSessionCapped: (() -> Void)?
    /// ASR 已出结果、开始等待 LLM 校对时触发一次。
    /// 没有这个信号时 HUD 会一直停在"识别中"，用户不知道自己在等的是本地推理还是网络。
    var onCorrectionStarted: (() -> Void)?
    /// 引擎未就绪而用户在等待时触发，每个阶段至多一次。
    var onEngineWait: ((EngineWaitStage) -> Void)?

    /// 单段录音上限（秒）：桌面听写场景 5 分钟不是合理假设，且更长的会话意味着更大的
    /// finalize 峰值内存与耗时。若要恢复到 300 秒，需先补 60/90/300 秒的峰值 RSS
    /// 与 finalize 耗时实测（F-07b）。浮窗的上限倒计时也读它（R8-C3）。
    static let maxSessionSeconds = 120
    private static let maxSessionSamples = maxSessionSeconds * 16000

    /// 等引擎加载的上限：慢机或磁盘繁忙时加载可能远超 0.85s；真正失败由 `engineUnavailable`
    /// 立即发现，不必靠等满时间来判断（R8-A1）。
    private static let engineWaitMaxAttempts = 600
    private static let engineWaitPollNanos: UInt64 = 100_000_000
    private static let engineWaitNoticeAttempt = 3
    private static let engineWaitRecordingNoticeSamples = 16_000

    private let asrQueue: DispatchQueue
    private let engineAccessor: () -> (any SenseVoiceRecognizing)?
    private let llmCorrector: LLMCorrector?
    /// 词汇替换层快照（VOC-7）。听写中不会改配置（`applyConfig` 会拒绝重建控制器），快照即可。
    private let vocabulary: VocabularyReplacer
    /// 引擎已确定不可能就绪（加载失败 / 模型缺失）。为 true 时 finalize 立即失败，不再空等。
    private let engineUnavailable: () -> Bool
    /// 单调时钟；会在 asrQueue 上被调用，必须线程安全（默认实现满足）。
    private let now: MonotonicClock

    /// 分阶段打点，会话收尾前由控制器读取并入 `DictationMetrics`。
    private(set) var timings = ASRSessionTimings()
    private var hasReceivedAudio = false
    private var engineWaitStartedAt: UInt64?

    private var buffer: RecognitionBuffer?
    /// 引擎尚未加载完成时，音频先攒在这里；引擎就绪后的第一次 sendAudio 会把它们一并灌入 buffer。
    private var pendingAudio: [Float] = []

    private var previewInFlight = false
    /// 自上一次预览**开始**以来累计的新样本数（R8-D1）。
    private var samplesSinceLastPreviewStart = 0
    /// 预览的最早允许开始时刻 = 上次预览结束 + 上次耗时 / 2，使稳态推理占空比不超过 2/3。
    /// 不引入定时器：下一个采集块（最迟 200ms 后）到达时会再检查一次。
    private var previewNotBefore: UInt64 = 0
    /// 预览之间至少累计这么多新样本（300ms @ 16kHz）。
    private static let previewMinimumNewSamples = 4_800
    /// 自上一次预览以来，是否收到过能量高于静音阈值的音频。
    ///
    /// SenseVoice 非流式，预览靠"对已累积音频重跑"实现；两次预览之间全是静音时，
    /// 重跑一遍窗口只会得到和上次一样的结果，纯属浪费，而用户思考停顿在口述里占比并不低。
    /// 跳过它对结果没有任何影响：松手后的 `finalize()` 永远对完整音频整段重跑。
    private var hasSpeechSinceLastPreview = false
    private var isFinalizing = false
    /// 用户已松键（或采集已结束），不再发起新的预览：此后的预览结果反正会被丢弃，
    /// 却会占住串行的 asrQueue，让终稿排在它后面（`final_wait`）。
    private var previewsStopped = false
    /// 与 `previewsStopped` 同步置位，供已入队、尚未开跑的预览闭包短路。
    private let previewStopFlag = CancelFlag()
    private var capped = false
    private var closed = false
    /// 与 `closed` 同步置位，供 asrQueue 闭包在跑推理前短路判断（F-07d）：
    /// `close()` 之后已入队的闭包仍会执行，但不应该真的跑一遍 CTC 解码只为丢弃结果。
    /// 用锁保护的标志跨线程读写，取代此前的 `nonisolated(unsafe)`（N-01）。
    private let cancelFlag = CancelFlag()
    private var lastPreview = ""
    private var finalizeWatchdog: Task<Void, Never>?
    /// `finalize(timeout:)` 给定的看门狗时长；真正开表在推理派发到 asrQueue 的时刻（`runFinalize`）。
    private var finalizeTimeout: TimeInterval = 0
    private var notifiedRecordingWait = false
    private var notifiedFinalizingWait = false
    private var correctionTask: Task<Void, Never>?
    private var correctionOriginalText = ""
    /// 正在等 LLM 校对。控制器据此决定再按热键是否跳过校对（R8-C2）。
    private(set) var isCorrecting = false

    init(
        asrQueue: DispatchQueue,
        engineAccessor: @escaping () -> (any SenseVoiceRecognizing)?,
        llmCorrector: LLMCorrector?,
        vocabulary: VocabularyReplacer = .empty,
        engineUnavailable: @escaping () -> Bool = { false },
        now: @escaping MonotonicClock = systemMonotonicClock
    ) {
        self.asrQueue = asrQueue
        self.engineAccessor = engineAccessor
        self.llmCorrector = llmCorrector
        self.vocabulary = vocabulary
        self.engineUnavailable = engineUnavailable
        self.now = now
    }

    func sendAudio(_ samples: [Float]) {
        guard !closed, !isFinalizing else { return }
        guard !samples.isEmpty else { return }

        ensureBufferIfPossible()
        if !hasReceivedAudio {
            hasReceivedAudio = true
            timings.coldAtStart = (buffer == nil)
        }
        guard let buffer else {
            // 引擎仍在加载：先攒着，下次 sendAudio（或 finalize）时补上。
            // pendingAudio 同样严格不超过单段上限——引擎长时间不就绪时不能无限积累
            // （R3-03 同源）；单个超大 chunk 也只追加剩余容量内的部分。
            let accepted = acceptWithinCap(samples, currentCount: pendingAudio.count)
            pendingAudio.append(contentsOf: accepted)
            timings.receivedSamples += accepted.count
            if !notifiedRecordingWait, pendingAudio.count >= Self.engineWaitRecordingNoticeSamples {
                notifiedRecordingWait = true
                onEngineWait?(.recording)
            }
            return
        }

        let accepted = acceptWithinCap(samples, currentCount: buffer.sampleCount)
        guard !accepted.isEmpty else { return }
        timings.receivedSamples += accepted.count
        samplesSinceLastPreviewStart += accepted.count

        if Self.containsSpeech(accepted) {
            hasSpeechSinceLastPreview = true
        }
        buffer.append(accepted)
        schedulePreview()
    }

    /// 统一的单段上限裁剪：pendingAudio 与已创建 buffer 共用。
    /// - 返回可安全追加的样本前缀，保证 `currentCount + 返回值.count <= maxSessionSamples`，
    ///   即单个 chunk 也不会跨越上限。
    /// - 仅当本次 chunk **严格超过**剩余容量时触发一次 `onWarning` + `onSessionCapped`
    ///   （控制器据此走与松键相同的收尾路径）；恰好填满不触发，保留"下一入口才触发"的
    ///   既有兼容语义。
    private func acceptWithinCap(_ samples: [Float], currentCount: Int) -> [Float] {
        let remaining = max(0, Self.maxSessionSamples - currentCount)
        guard samples.count > remaining else { return samples }
        triggerCapOnce()
        return Array(samples.prefix(remaining))
    }

    private func triggerCapOnce() {
        guard !capped else { return }
        capped = true
        onWarning?(LF("录音已达 %d 秒上限，自动结束本次听写", Self.maxSessionSamples / 16000))
        onSessionCapped?()
    }

    /// - Parameter timeout: 推理开始后等待识别完成的最长时间；本地无网络往返，这里纯粹是防止
    ///   推理卡死的看门狗。传 <= 0 表示不设超时。计时从推理真正派发到 asrQueue 起算，
    ///   **不含**等待引擎加载的时间（那段另有上限）（R8-A1）。
    func finalize(timeout: TimeInterval) {
        guard !closed, !isFinalizing else { return }
        isFinalizing = true
        timings.finalizeStartedAt = now()
        finalizeWatchdog?.cancel()
        finalizeTimeout = timeout

        ensureBufferIfPossible()
        guard let buffer else {
            // 引擎仍未就绪（极短录音、模型刚好还没加载完）：等一小段时间重试，
            // 而不是立即报错——preload 已经在后台跑，多数情况下几百毫秒内就绪。
            engineWaitStartedAt = now()
            waitForEngineThenFinalize()
            return
        }

        runFinalize(on: buffer)
    }

    /// 松键（含补录开始）或采集结束时调用：不再发起新的预览，已入队但还没开跑的那次也跳过。
    /// 已经在跑的预览无法中断（ORT 绑定没有 `SetTerminate`，见 DESIGN.md §11.1）。
    func stopPreviews() {
        guard !previewsStopped else { return }
        previewsStopped = true
        previewStopFlag.set()
    }

    func close() {
        guard !closed else { return }
        closed = true
        cancelFlag.set()
        previewStopFlag.set()
        finalizeWatchdog?.cancel()
        finalizeWatchdog = nil
        correctionTask?.cancel()
        correctionTask = nil
        isCorrecting = false
        buffer = nil
    }

    /// 校对中用户选择跳过：取消网络请求，立即以识别原文收尾（R8-C2）。
    /// 返回是否真的跳过了；不在校对中（或已收尾）返回 false。
    @discardableResult
    func skipCorrection() -> Bool {
        guard isCorrecting, !closed else { return false }
        isCorrecting = false
        correctionTask?.cancel()
        correctionTask = nil
        timings.llmCompletedAt = now()
        timings.llmResult = .skipped
        onFinal?(correctionOriginalText)
        return true
    }

    // MARK: - 私有实现

    private func ensureBufferIfPossible() {
        guard buffer == nil, let engine = engineAccessor() else { return }
        let newBuffer = RecognitionBuffer(engine: engine, reservedSampleCapacity: Self.maxSessionSamples)
        if !pendingAudio.isEmpty {
            samplesSinceLastPreviewStart += pendingAudio.count
            newBuffer.append(pendingAudio)
            pendingAudio.removeAll()
        }
        buffer = newBuffer
    }

    /// 这一段音频里是否有可能是语音（线性 RMS 超过静音阈值）。
    /// 判断只用于"要不要跑这次预览"，判错的代价上限是多跑或少跑一次预览，
    /// 最终文本不受影响——因此刻意用最简单的能量门限，不引入真正的 VAD 模型。
    nonisolated static func containsSpeech(_ samples: [Float]) -> Bool {
        guard !samples.isEmpty else { return false }
        var sumSquares: Float = 0
        for sample in samples {
            sumSquares += sample * sample
        }
        let rms = (sumSquares / Float(samples.count)).squareRoot()
        return rms >= AppConstants.silenceRMSThreshold
    }

    private func schedulePreview() {
        guard !isFinalizing, !previewsStopped, !previewInFlight, let buffer else { return }
        // 这一轮没有新的语音：跳过整窗重跑，HUD 保持上一次的预览文本。
        guard hasSpeechSinceLastPreview else {
            timings.previewSkipped += 1
            return
        }
        // 事件驱动的调度（R8-D1）：新样本不足 300ms、或还没到 `previewNotBefore` 时先不跑，
        // 保留 `hasSpeechSinceLastPreview`，等下一个采集块到达再检查。
        guard samplesSinceLastPreviewStart >= Self.previewMinimumNewSamples,
              now() >= previewNotBefore else { return }
        hasSpeechSinceLastPreview = false
        samplesSinceLastPreviewStart = 0
        previewInFlight = true
        let clock = now
        let requestedAt = clock()
        let stopFlag = previewStopFlag
        asrQueue.async { [weak self] in
            // 松键后才轮到的预览直接放弃：previewInFlight 不必复位，此后不会再发起预览。
            if stopFlag.isSet { return }
            let startedAt = clock()
            let result = Result { try buffer.preview() }
            let elapsed = clock() &- startedAt
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.previewInFlight = false
                let deliveredAt = self.now()
                self.previewNotBefore = deliveredAt &+ elapsed / 2
                self.timings.previewRuns += 1
                self.timings.previewMaxNanos = max(self.timings.previewMaxNanos, elapsed)
                self.timings.previewDeliveredNanos &+= deliveredAt &- requestedAt
                self.timings.previewDeliveredCount += 1
                guard !self.closed, !self.isFinalizing, !self.previewsStopped else { return }
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
        // 引擎加载耗时约 0.85s（慢机更久）；每 100ms 探测一次，最多等 60s。加载已确定失败
        // （`engineUnavailable`）时立即报错，不再空等（R8-A1）。
        if !closed, engineUnavailable() {
            AppLog.asr.error("识别引擎不可用，终稿立即失败")
            isFinalizing = false
            onError?(L("识别引擎尚未就绪"))
            return
        }
        guard attempt < Self.engineWaitMaxAttempts, !closed else {
            if !closed {
                isFinalizing = false
                onError?(L("识别引擎尚未就绪"))
            }
            return
        }
        if attempt >= Self.engineWaitNoticeAttempt, !notifiedFinalizingWait {
            notifiedFinalizingWait = true
            onEngineWait?(.finalizing)
        }
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: Self.engineWaitPollNanos)
            guard let self, !self.closed else { return }
            self.ensureBufferIfPossible()
            if let buffer = self.buffer {
                if let waitStart = self.engineWaitStartedAt {
                    self.timings.engineWaitNanos = self.now() &- waitStart
                }
                self.runFinalize(on: buffer)
            } else {
                self.waitForEngineThenFinalize(attempt: attempt + 1)
            }
        }
    }

    private func runFinalize(on buffer: RecognitionBuffer) {
        startFinalizeWatchdog()
        let clock = now
        timings.finalizeDispatchedAt = clock()
        asrQueue.async { [weak self] in
            if self?.cancelFlag.isSet == true { return }
            let inferenceStartedAt = clock()
            let result = Result { try buffer.finalize() }
            // 同在 asrQueue 上、紧跟终稿推理之后读取，不会被预览覆盖（预览已在松键时停止）。
            let decodeStats = buffer.lastDecodeStats
            Task { @MainActor [weak self] in
                guard let self, !self.closed else { return }
                self.timings.finalizeInferenceStartedAt = inferenceStartedAt
                self.timings.decodeMode = decodeStats.mode
                self.timings.hotwordHits = decodeStats.hotwordHits
                self.timings.decodeNanos = decodeStats.decodeNanos
                self.finalizeWatchdog?.cancel()
                self.finalizeWatchdog = nil

                switch result {
                case .success(let text):
                    self.timings.asrCompletedAt = self.now()
                    self.completeWithASRText(text)
                case .failure(let error):
                    AppLog.asr.error("离线复识别失败: \(String(describing: error), privacy: .public)")
                    self.onError?(error.localizedDescription)
                }
            }
        }
    }

    private func startFinalizeWatchdog() {
        let timeout = finalizeTimeout
        guard timeout > 0 else { return }
        finalizeWatchdog?.cancel()
        finalizeWatchdog = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
            guard !Task.isCancelled, let self, !self.closed else { return }
            AppLog.asr.error("finalize 超时（\(timeout, privacy: .public)s）")
            self.timings.finalizeTimedOut = true
            self.onError?(L("识别超时"))
        }
    }

    /// 拿到 ASR 原文后：先做词汇替换（VOC-7），若启用了 LLM 校对，再把文本通过 onPartial 顶到 HUD 上
    /// （让用户立刻看到结果，校对中不会像"卡住"），再异步校对，最终以校对结果调用 onFinal。
    /// 这一步在跨进程架构下没有意义（要多一次网络往返），进程内是免费的。
    ///
    /// LLM 收到的是替换后的文本（有意放宽 R8「LLM 输入保持 ASR 原貌」）：替换只改词条，
    /// few-shot 示例描述的「中英文紧挨着」的形态不受影响。
    private func completeWithASRText(_ asrText: String) {
        let text = vocabulary.apply(asrText)
        guard let llmCorrector, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            timings.llmResult = .off
            onFinal?(text)
            return
        }

        onPartial?(text)
        onCorrectionStarted?()
        timings.llmStartedAt = now()
        correctionOriginalText = text
        isCorrecting = true
        correctionTask = Task { @MainActor [weak self] in
            guard let self, !self.closed else { return }
            let report = await llmCorrector.correctWithReport(text, glossary: self.vocabulary.terms)
            // 已被用户跳过（skipCorrection 已用原文收尾）或会话已关闭：迟到的结果一律忽略。
            guard !self.closed, self.isCorrecting else { return }
            self.isCorrecting = false
            self.correctionTask = nil
            let outcome = report.outcome
            self.timings.llmCompletedAt = self.now()
            self.timings.llmRetriedWithoutThinking = (report.thinkingParameter == .rejectedThenOmitted)
            if case .fellBack = outcome {
                self.timings.llmResult = .fellBack
                // 回落原因交给控制器，在「已输入 · 未校对」里说明（R8-C1）；
                // 不再发 onWarning——它会被紧随其后的成功提示盖掉。
                self.timings.llmFallbackReason = report.fallbackReason
            }
            if case .corrected = outcome { self.timings.llmResult = .corrected }
            self.onFinal?(outcome.text)
        }
    }
}
