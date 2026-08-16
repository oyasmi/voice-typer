import Foundation

/// 识别引擎的生命周期门面：定位模型 → 后台加载 → 提供录音会话 → 空闲卸载。
/// 所有耗时操作（模型加载、推理）都在 `asrQueue`（串行）上执行，与服务端
/// "单 worker executor" 的约束一致——ONNX session 与 fbank 前端都有可变状态，
/// 必须串行访问。
@MainActor
final class ASRService {
    enum State: Equatable {
        case unloaded
        case loading
        case ready
        /// 本机既无内置也无侧载模型副本，需要首启下载引导。
        case modelMissing
        case failed(String)
    }

    private(set) var state: State = .unloaded {
        didSet { onStateChange?(state) }
    }
    var onStateChange: ((State) -> Void)?

    private let asrQueue = DispatchQueue(label: "com.voicetyper.app.asr", qos: .userInitiated)
    /// 只应在 asrQueue 上读写；用 nonisolated(unsafe) 退出 MainActor 隔离检查，
    /// 由调用方（本文件）自行保证只在 asrQueue 上访问，与 AudioCaptureService
    /// 里 NSLock 保护可变缓冲的思路一致，这里改用串行队列本身做同步。
    nonisolated(unsafe) private var engine: SenseVoiceEngine?

    private var config = ASRConfig()
    private var idleTimer: Timer?
    private var loadGeneration = 0

    /// 配置变化时调用：语言变化直接热更新引擎；模型目录/线程数变化触发重新加载。
    func updateConfig(_ newConfig: ASRConfig) {
        let languageChanged = config.language != newConfig.language
        let reloadNeeded = config.modelDir != newConfig.modelDir || config.threads != newConfig.threads
        config = newConfig

        if languageChanged {
            asrQueue.async { [engine] in
                engine?.setLanguage(newConfig.language)
            }
        }
        if reloadNeeded {
            Task { await reload() }
        } else {
            scheduleIdleUnloadIfNeeded()
        }
    }

    func preload() async {
        guard state != .loading, state != .ready else { return }
        await load()
    }

    func reload() async {
        await unloadNow()
        await load()
    }

    /// 引擎当前实例（供 LocalASRSession 在 asrQueue 上读取）。可从任意线程调用；
    /// 写入永远在 asrQueue 上发生，读取用同一队列串行化即可保证一致性——
    /// 这个 getter 本身只应从 asrQueue 闭包内部调用。
    nonisolated func currentEngine() -> SenseVoiceEngine? {
        engine
    }

    private func load() async {
        loadGeneration += 1
        let generation = loadGeneration

        guard let bundle = ModelLocator.locate(explicitDir: config.modelDir) else {
            state = .modelMissing
            return
        }
        state = .loading

        let language = config.language
        let threads = config.threads

        let result: Result<SenseVoiceEngine, Error> = await withCheckedContinuation { continuation in
            asrQueue.async {
                do {
                    let built = try SenseVoiceEngine(bundle: bundle, language: language, threads: threads)
                    continuation.resume(returning: .success(built))
                } catch {
                    continuation.resume(returning: .failure(error))
                }
            }
        }

        guard generation == loadGeneration else { return } // 期间又发起了一次 reload，丢弃过期结果

        switch result {
        case .success(let built):
            asrQueue.async { [weak self] in self?.engine = built }
            state = .ready
            scheduleIdleUnloadIfNeeded()
        case .failure(let error):
            AppLog.asr.error("模型加载失败: \(String(describing: error), privacy: .public)")
            state = .failed(error.localizedDescription)
        }
    }

    private func unloadNow() async {
        idleTimer?.invalidate()
        idleTimer = nil
        guard engine != nil else { return }
        await withCheckedContinuation { continuation in
            asrQueue.async { [weak self] in
                self?.engine = nil
                continuation.resume()
            }
        }
        if state == .ready {
            state = .unloaded
        }
    }

    private func scheduleIdleUnloadIfNeeded() {
        idleTimer?.invalidate()
        idleTimer = nil
        guard config.idleUnloadMinutes > 0 else { return }
        let interval = TimeInterval(config.idleUnloadMinutes * 60)
        idleTimer = Timer.scheduledTimer(withTimeInterval: interval, repeats: false) { [weak self] _ in
            Task { @MainActor in await self?.unloadNow() }
        }
    }

    /// 创建一次录音会话。若引擎当前未加载（首次使用、或刚从空闲卸载中恢复），
    /// 会异步触发重新加载并与录音并行——用户通常正在说第一句话，
    /// 加载耗时（约 0.85s）对感知延迟几乎不可见。
    func makeSession(llmCorrector: LLMCorrector?) -> LocalASRSession {
        if state != .ready && state != .loading {
            Task { await preload() }
        }
        idleTimer?.invalidate()
        idleTimer = nil // 录音期间不应触发空闲卸载；结束后由 makeSession 之外的下一次调度重新安排
        return LocalASRSession(asrQueue: asrQueue, engineAccessor: { [weak self] in self?.currentEngine() }, llmCorrector: llmCorrector)
    }

    /// 录音会话结束后由调用方（VoiceTyperController）调用，重新安排空闲卸载计时。
    func sessionEnded() {
        scheduleIdleUnloadIfNeeded()
    }
}
