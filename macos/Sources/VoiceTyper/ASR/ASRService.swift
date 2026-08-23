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
    /// 引擎引用本身的读写用 `engineLock` 保护，而不是靠"只在 asrQueue 上访问"的约定——
    /// `currentEngine()` 会被 `LocalASRSession` 从 MainActor 直接调用（不经过 asrQueue），
    /// 此前那条约定实际已被违反，属于无同步的跨线程读写（N-01）。`nonisolated(unsafe)`
    /// 退出 actor 隔离检查，真正的同步靠下面 `currentEngine()`/`setEngine()` 里的锁，
    /// 不再是"约定"。引擎推理仍然只在 asrQueue 上串行执行，这一点不变。
    private let engineLock = NSLock()
    private nonisolated(unsafe) var _engine: (any SenseVoiceRecognizing)?

    private var config = ASRConfig()
    private let idleScheduler: IdleUnloadScheduling
    private var loadGeneration = 0
    /// `preload()`/`reload()` 的互斥标志：两者都可能各自触发 `load()`，若不guard，
    /// `unloadNow` 把 state 置 `.unloaded` 触发的 `onStateChange` 会被 `AppCoordinator`
    /// 无差别转发进 `reevaluateReadiness()`，其 `.unloaded` 分支又发起一次独立的
    /// `preload()`——两次 `load()` 会各自构建一份 ~500MB 的 ORT session，在谁都还没被
    /// `setEngine()` 替换掉之前短暂同时存活，峰值内存可翻倍（R3-02）。
    private var isLoadInFlight = false
    /// 加载进行中又收到一次加载请求（例如 reload 时 preload 恰好也被触发）：
    /// 不重入执行，只记一次"完成后再跑一遍"，确保最新配置最终生效，而不是静默丢弃。
    private var reloadRequestedWhileLoading = false

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
            asrQueue.async { [weak self] in
                self?.currentEngine()?.setLanguage(newConfig.language)
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
        await requestLoad(unloadFirst: false)
    }

    func reload() async {
        await requestLoad(unloadFirst: true)
    }

    /// `preload()`/`reload()` 的统一入口：若已有加载在跑，只记一个"完成后再跑一次"的
    /// 标记，绝不发起第二次 `load()`（R3-02）。
    private func requestLoad(unloadFirst: Bool) async {
        guard !isLoadInFlight else {
            reloadRequestedWhileLoading = true
            return
        }
        isLoadInFlight = true
        if unloadFirst {
            await unloadNow(dueToIdle: false)
        }
        await load()
        isLoadInFlight = false

        if reloadRequestedWhileLoading {
            reloadRequestedWhileLoading = false
            // 合并的请求按"重新加载"处理：确保加载期间发生的最新配置变化最终会生效，
            // 而不是被静默吞掉。
            await requestLoad(unloadFirst: true)
        }
    }

    /// 引擎当前实例。可从任意线程调用（`LocalASRSession` 会在 MainActor 上直接读取，
    /// 而写入发生在 asrQueue）——引用本身由 `engineLock` 保护，可安全跨线程访问。
    nonisolated func currentEngine() -> (any SenseVoiceRecognizing)? {
        engineLock.lock()
        defer { engineLock.unlock() }
        return _engine
    }

    /// 写入引擎引用；只应在 asrQueue 上调用，与推理串行化的约定保持一致。
    /// - Returns: 写入前是否已有旧引擎实例。
    @discardableResult
    private nonisolated func setEngine(_ newEngine: (any SenseVoiceRecognizing)?) -> Bool {
        engineLock.lock()
        defer { engineLock.unlock() }
        let hadPrevious = _engine != nil
        _engine = newEngine
        return hadPrevious
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
            asrQueue.async { [weak self] in self?.setEngine(built) }
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
        // hop 到 asrQueue 只是为了让"卸载"与"推理"在时序上不交叉；引擎引用本身
        // 已经用 engineLock 保护，跨线程读写是安全的。
        let hadEngine: Bool = await withCheckedContinuation { continuation in
            asrQueue.async { [weak self] in
                continuation.resume(returning: self?.setEngine(nil) ?? false)
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
