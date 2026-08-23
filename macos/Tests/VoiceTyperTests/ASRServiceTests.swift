import XCTest
@testable import VoiceTyper

/// 用假引擎 + 可控空闲调度器验证 ASRService 的生命周期，不依赖真实 ONNX 模型或
/// 真实 wall-clock 等待（F-16 回归测试，覆盖 F-04 修复）。
@MainActor
final class ASRServiceTests: XCTestCase {
    /// 捕获 schedule/cancel 调用，供测试手动触发回调，模拟计时器到期。
    private final class FakeIdleScheduler: IdleUnloadScheduling {
        private(set) var scheduleCount = 0
        private(set) var cancelCount = 0
        private(set) var lastInterval: TimeInterval?
        private var pendingHandler: (() -> Void)?

        func schedule(afterSeconds interval: TimeInterval, _ handler: @escaping () -> Void) {
            scheduleCount += 1
            lastInterval = interval
            pendingHandler = handler
        }

        func cancel() {
            cancelCount += 1
            pendingHandler = nil
        }

        /// 模拟计时器到期，只有仍处于"已安排且未取消"状态时才会触发，
        /// 与真实 Timer 的行为一致。
        func fire() {
            let handler = pendingHandler
            pendingHandler = nil
            handler?()
        }

        var hasPendingSchedule: Bool { pendingHandler != nil }
    }

    private final class FakeEngine: SenseVoiceRecognizing, @unchecked Sendable {
        func recognize(_ samples: [Float]) throws -> String { "" }
    }

    /// 记录 `setLanguage` 调用，供 R4-13 回归测试验证"加载期间发生的语言变化最终生效"。
    private final class LanguageTrackingFakeEngine: SenseVoiceRecognizing, @unchecked Sendable {
        private let lock = NSLock()
        private var _lastLanguage: ASRLanguage?

        func recognize(_ samples: [Float]) throws -> String { "" }

        func setLanguage(_ language: ASRLanguage) {
            lock.lock(); _lastLanguage = language; lock.unlock()
        }

        var lastLanguage: ASRLanguage? {
            lock.lock(); defer { lock.unlock() }
            return _lastLanguage
        }
    }

    private func fakeBundle() -> ModelBundle {
        let dummy = URL(fileURLWithPath: "/dev/null")
        return ModelBundle(
            modelDir: dummy, onnxFileURL: dummy, tokensURL: dummy, cmvnURL: dummy,
            quantized: true, fbankOptions: FbankOptions(), lfrM: 7, lfrN: 6
        )
    }

    private func makeService(
        scheduler: FakeIdleScheduler,
        idleUnloadMinutes: Int = 1
    ) -> ASRService {
        let service = ASRService(
            idleScheduler: scheduler,
            modelLocate: { [weak self] _ in self?.fakeBundle() },
            engineFactory: { _, _, _ in FakeEngine() }
        )
        // modelDir/threads 与 ASRConfig() 默认值保持一致，避免触发 updateConfig 内部的
        // 异步 reload()，与测试随后显式调用的 preload() 产生竞态。
        service.updateConfig(ASRConfig(language: .auto, threads: 0, modelDir: "", idleUnloadMinutes: idleUnloadMinutes))
        return service
    }

    func testIdleUnloadDoesNotAutoReload() async {
        let scheduler = FakeIdleScheduler()
        let service = makeService(scheduler: scheduler)
        await service.preload()
        XCTAssertEqual(service.state, .ready)
        XCTAssertTrue(scheduler.hasPendingSchedule)

        scheduler.fire() // 模拟第一次空闲超时
        // unloadNow 里有一次 asrQueue 往返，用极短 sleep 等待完成。
        try? await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(service.state, .suspendedForIdle, "卸载后必须是 suspendedForIdle 而不是 unloaded/ready")

        // 卸载后不应有新的自动加载被安排；即便再等一个计时周期也应保持 suspendedForIdle。
        XCTAssertFalse(scheduler.hasPendingSchedule, "空闲卸载完成后不应自己重新安排计时器（也不应自动重新加载）")
        try? await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(service.state, .suspendedForIdle, "两个计时周期内都不应自动回到 ready")
    }

    func testMakeSessionTriggersReloadAfterIdleUnload() async {
        let scheduler = FakeIdleScheduler()
        let service = makeService(scheduler: scheduler)
        await service.preload()
        scheduler.fire()
        try? await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(service.state, .suspendedForIdle)

        _ = service.makeSession(llmCorrector: nil)
        try? await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(service.state, .ready, "makeSession() 必须按需触发重新加载")
    }

    func testSessionInProgressIsNotUnloaded() async {
        let scheduler = FakeIdleScheduler()
        let service = makeService(scheduler: scheduler)
        await service.preload()
        XCTAssertTrue(scheduler.hasPendingSchedule)

        _ = service.makeSession(llmCorrector: nil)
        XCTAssertFalse(scheduler.hasPendingSchedule, "录音会话进行中不应有待触发的空闲卸载")

        service.sessionEnded()
        XCTAssertTrue(scheduler.hasPendingSchedule, "会话结束后应重新安排空闲卸载")
    }

    /// R4-13 回归：`updateConfig` 收到的语言变化若恰好落在 `load()` 已经取到旧语言快照、
    /// 但 `setEngine(built)` 还没执行完这段窗口内，此前会被随后的 `setEngine` 覆盖掉、
    /// 静默丢失，要等下一次 reload 才生效。用一个"构造耗时"的假引擎工厂制造这段窗口。
    func testLanguageChangedDuringLoadEndsUpAppliedToNewEngine() async {
        let scheduler = FakeIdleScheduler()
        let engine = LanguageTrackingFakeEngine()
        let service = ASRService(
            idleScheduler: scheduler,
            modelLocate: { [weak self] _ in self?.fakeBundle() },
            engineFactory: { _, _, _ in
                // 模拟真实 ORT session 构建耗时，给测试留出"加载仍在进行中改语言"的窗口。
                Thread.sleep(forTimeInterval: 0.1)
                return engine
            }
        )
        service.updateConfig(ASRConfig(language: .zh, threads: 0, modelDir: "", idleUnloadMinutes: 1))

        let loadTask = Task { await service.preload() }

        // 引擎工厂仍在其 0.1s 的模拟耗时里睡眠，此时改变语言：modelDir/threads 不变，
        // 只会走 updateConfig 里的语言热更新分支（asrQueue.async 里对 currentEngine()
        // 的 setLanguage 调用），不会触发新的 reload()。
        try? await Task.sleep(nanoseconds: 30_000_000)
        service.updateConfig(ASRConfig(language: .en, threads: 0, modelDir: "", idleUnloadMinutes: 1))

        await loadTask.value
        XCTAssertEqual(service.state, .ready)
        XCTAssertEqual(engine.lastLanguage, .en, "加载期间发生的语言变化最终应生效，而不是被构造时的旧语言覆盖")
    }
}
