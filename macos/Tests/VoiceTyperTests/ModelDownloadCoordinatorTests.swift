import XCTest
@testable import VoiceTyper

/// `ModelDownloadCoordinator` 的确定性编排测试（S-6）：
/// 验证首次自动触发单次约束、指数退避重试与耗尽告警、用户取消中断退避、手动重试清零退避计数。
@MainActor
final class ModelDownloadCoordinatorTests: XCTestCase {
    @MainActor
    private final class StubDownloader: ModelDownloading {
        var behavior: (@MainActor (@escaping (Double) -> Void) async throws -> Void)?
        private(set) var cancelCalled = false

        func downloadAll(onProgress: @escaping (Double) -> Void) async throws {
            if let behavior {
                try await behavior(onProgress)
            }
        }

        func cancel() {
            cancelCalled = true
        }
    }

    private struct DummyFailure: LocalizedError {
        var errorDescription: String? { "网络连接中断。" }
    }

    func testAutomaticPolicyStartsAtMostOncePerProcess() {
        var policy = AutomaticModelDownloadPolicy()
        XCTAssertFalse(policy.shouldStart(for: .ready, isDownloading: false))
        XCTAssertTrue(policy.shouldStart(for: .modelMissing, isDownloading: false))
        XCTAssertFalse(policy.shouldStart(for: .modelMissing, isDownloading: false), "同一进程内自动策略只触发一次")
    }

    func testBackoffRetriesExhaustThenManualRetryResetsCounter() async {
        var attempts = 0
        var sleptDelays: [TimeInterval] = []
        var exhaustedFlags: [Bool] = []
        var succeededCount = 0
        var shouldSucceed = false

        let coordinator = ModelDownloadCoordinator(
            retryDelays: [5, 20],
            downloaderFactory: {
                let stub = StubDownloader()
                stub.behavior = { onProgress in
                    attempts += 1
                    onProgress(0.25)
                    if shouldSucceed {
                        onProgress(1.0)
                        return
                    }
                    throw DummyFailure()
                }
                return stub
            },
            sleep: { delay in
                sleptDelays.append(delay)
            }
        )
        coordinator.isModelStillMissing = { true }
        coordinator.onFailed = { _, exhausted in
            exhaustedFlags.append(exhausted)
        }
        coordinator.onSucceeded = {
            succeededCount += 1
        }

        coordinator.startAutomaticIfNeeded(for: .modelMissing)
        try? await Task.sleep(nanoseconds: 80_000_000)

        // 首次 + 2 次自动退避重试 = 共 3 次尝试。
        XCTAssertEqual(attempts, 3)
        XCTAssertEqual(sleptDelays, [5, 20])
        XCTAssertEqual(exhaustedFlags, [false, false, true])
        XCTAssertFalse(coordinator.isDownloading)
        XCTAssertEqual(succeededCount, 0)

        // 用户手动点重试：退避计数归零，成功后置位 pendingReadyNotice。
        shouldSucceed = true
        coordinator.start(isAutomaticRetry: false)
        try? await Task.sleep(nanoseconds: 50_000_000)

        XCTAssertEqual(attempts, 4)
        XCTAssertEqual(coordinator.retryAttempt, 0)
        XCTAssertNil(coordinator.errorMessage)
        XCTAssertTrue(coordinator.pendingReadyNotice)
        XCTAssertEqual(succeededCount, 1)
    }

    func testCancelStopsActiveDownloaderAndPreventsPendingRetry() async {
        var createdDownloaders: [StubDownloader] = []
        var cancelledCallbackCount = 0

        let coordinator = ModelDownloadCoordinator(
            retryDelays: [5],
            downloaderFactory: {
                let stub = StubDownloader()
                stub.behavior = { _ in
                    try? await Task.sleep(nanoseconds: 40_000_000)
                    throw ModelDownloader.DownloadError.cancelled
                }
                createdDownloaders.append(stub)
                return stub
            },
            sleep: { _ in }
        )
        coordinator.onCancelled = { cancelledCallbackCount += 1 }

        coordinator.start()
        try? await Task.sleep(nanoseconds: 10_000_000)
        coordinator.cancel()
        try? await Task.sleep(nanoseconds: 60_000_000)

        XCTAssertEqual(createdDownloaders.count, 1)
        XCTAssertTrue(createdDownloaders[0].cancelCalled)
        XCTAssertEqual(cancelledCallbackCount, 1)
        XCTAssertNil(coordinator.errorMessage, "用户主动取消不应留下失败错误文案")
    }
}
