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
}
