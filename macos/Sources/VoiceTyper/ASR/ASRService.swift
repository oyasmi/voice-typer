import Foundation

/// 空闲卸载的计时器抽象，供测试注入可控实现，避免依赖真实 wall-clock 等待（F-16）。
protocol IdleUnloadScheduling: AnyObject {
    /// 安排一次性回调；再次调用会取消前一次尚未触发的安排。
    func schedule(afterSeconds interval: TimeInterval, _ handler: @escaping () -> Void)
    func cancel()
}

final class TimerIdleUnloadScheduler: IdleUnloadScheduling {
    private var timer: Timer?

    func schedule(afterSeconds interval: TimeInterval, _ handler: @escaping () -> Void) {
        cancel()
        timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: false) { _ in handler() }
    }

    func cancel() {
        timer?.invalidate()
        timer = nil
    }
}

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
        /// 空闲超时后的有意卸载：与 `.unloaded`（尚未加载/加载失败前）区分，
        /// 避免状态变化被无条件转发时又反向触发自动预加载（F-04）。
        /// 语义上仍视为"就绪"——热键监听正常，下次 `makeSession()` 时按需加载。
        case suspendedForIdle
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
    nonisolated(unsafe) private var engine: (any SenseVoiceRecognizing)?

    private var config = ASRConfig()
    private let idleScheduler: IdleUnloadScheduling
    private var loadGeneration = 0

    private let modelLocate: (String) -> ModelBundle?
    private let engineFactory: (ModelBundle, ASRLanguage, Int) throws -> any SenseVoiceRecognizing

    /// - Parameters:
    ///   - idleScheduler: 仅供测试注入可控实现，默认使用真实 Timer。
    ///   - modelLocate: 仅供测试注入假模型定位，默认 `ModelLocator.locate`。
    ///   - engineFactory: 仅供测试注入假引擎（无需真实 ONNX 模型），默认构造 `SenseVoiceEngine`。
    init(
        idleScheduler: IdleUnloadScheduling = TimerIdleUnloadScheduler(),
        modelLocate: @escaping (String) -> ModelBundle? = { ModelLocator.locate(explicitDir: $0) },
        engineFactory: @escaping (ModelBundle, ASRLanguage, Int) throws -> any SenseVoiceRecognizing =
            { try SenseVoiceEngine(bundle: $0, language: $1, threads: $2) }
    ) {
        self.idleScheduler = idleScheduler
        self.modelLocate = modelLocate
        self.engineFactory = engineFactory
    }

    /// 配置变化时调用：语言变化直接热更新引擎；模型目录/线程数变化触发重新加载。
    func updateConfig(_ newConfig: ASRConfig) {
        let languageChanged = config.language != newConfig.language
        let reloadNeeded = config.modelDir != newConfig.modelDir || config.threads != newConfig.threads
        config = newConfig

        if languageChanged {
            // 在 asrQueue 闭包内部读取 engine，而不是在调用方（MainActor）线程的
            // 捕获列表里读取——engine 只应在 asrQueue 上被访问（N-01）。
            asrQueue.async { [weak self] in
                self?.engine?.setLanguage(newConfig.language)
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
        await unloadNow(dueToIdle: false)
        await load()
    }

    /// 引擎当前实例（供 LocalASRSession 在 asrQueue 上读取）。可从任意线程调用；
    /// 写入永远在 asrQueue 上发生，读取用同一队列串行化即可保证一致性——
    /// 这个 getter 本身只应从 asrQueue 闭包内部调用。
    nonisolated func currentEngine() -> (any SenseVoiceRecognizing)? {
        engine
    }

    private func load() async {
        loadGeneration += 1
        let generation = loadGeneration

        guard let bundle = modelLocate(config.modelDir) else {
            state = .modelMissing
            return
        }
        state = .loading

        let language = config.language
        let threads = config.threads
        let engineFactory = engineFactory

        let result: Result<any SenseVoiceRecognizing, Error> = await withCheckedContinuation { continuation in
            asrQueue.async {
                do {
                    let built = try engineFactory(bundle, language, threads)
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

    /// - Parameter dueToIdle: true 表示由空闲计时器触发的有意卸载，卸载完成后
    ///   进入 `.suspendedForIdle` 而非 `.unloaded`，避免被无差别转发的状态变化
    ///   误判为"尚未加载"从而立即触发自动预加载（F-04）。`reload()` 内部调用时传 false。
    private func unloadNow(dueToIdle: Bool) async {
        idleScheduler.cancel()
        // 无条件 hop 到 asrQueue 判空，不在 MainActor 上读 engine（N-01）；
        // 队列为空转的成本可忽略。
        let hadEngine: Bool = await withCheckedContinuation { continuation in
            asrQueue.async { [weak self] in
                let hadEngine = self?.engine != nil
                self?.engine = nil
                continuation.resume(returning: hadEngine)
            }
        }
        guard hadEngine else { return }
        if state == .ready {
            state = dueToIdle ? .suspendedForIdle : .unloaded
        }
    }

    private func scheduleIdleUnloadIfNeeded() {
        idleScheduler.cancel()
        guard config.idleUnloadMinutes > 0 else { return }
        let interval = TimeInterval(config.idleUnloadMinutes * 60)
        idleScheduler.schedule(afterSeconds: interval) { [weak self] in
            Task { @MainActor in await self?.unloadNow(dueToIdle: true) }
        }
    }

    /// 创建一次录音会话。若引擎当前未加载（首次使用、或刚从空闲卸载中恢复），
    /// 会异步触发重新加载并与录音并行——用户通常正在说第一句话，
    /// 加载耗时（约 0.85s）对感知延迟几乎不可见。
    func makeSession(llmCorrector: LLMCorrector?) -> LocalASRSession {
        if state != .ready && state != .loading {
            Task { await preload() }
        }
        idleScheduler.cancel() // 录音期间不应触发空闲卸载；结束后由 makeSession 之外的下一次调度重新安排
        return LocalASRSession(asrQueue: asrQueue, engineAccessor: { [weak self] in self?.currentEngine() }, llmCorrector: llmCorrector)
    }

    /// 录音会话结束后由调用方（VoiceTyperController）调用，重新安排空闲卸载计时。
    func sessionEnded() {
        scheduleIdleUnloadIfNeeded()
    }
}
