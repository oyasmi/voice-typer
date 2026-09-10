import XCTest
@testable import VoiceTyper

/// `AppCoordinator.computeTargetState` 是从 `reevaluateReadiness()` 拆出的纯决策函数
/// （R3-12）：不需要构造 `AppCoordinator` 的全部依赖（权限中心、状态栏、ASR 服务…）
/// 即可验证优先级——暂停 > 权限缺失 > 下载中 > ASR 状态，且就绪态不覆盖进行中的听写。
@MainActor
final class AppCoordinatorReadinessTests: XCTestCase {
    private let granted = PermissionSnapshot(microphone: .authorized, accessibility: .authorized, inputMonitoring: .authorized)
    private let denied = PermissionSnapshot(microphone: .notDetermined, accessibility: .denied, inputMonitoring: .denied)

    func testPausedTakesPriorityOverEverythingElse() {
        let target = AppCoordinator.computeTargetState(
            permissions: denied,
            isPaused: true,
            isDownloadingModel: true,
            downloadProgress: 0.5,
            asrState: .ready,
            previous: .idle
        )
        XCTAssertEqual(target, .paused)
    }

    func testMissingPermissionsTakesPriorityOverDownloadAndModel() {
        let target = AppCoordinator.computeTargetState(
            permissions: denied,
            isPaused: false,
            isDownloadingModel: true,
            downloadProgress: 0.5,
            asrState: .ready,
            previous: .idle
        )
        XCTAssertEqual(target, .setupRequired)
    }

    func testDownloadingModelTakesPriorityOverASRState() {
        let target = AppCoordinator.computeTargetState(
            permissions: granted,
            isPaused: false,
            isDownloadingModel: true,
            downloadProgress: 0.42,
            asrState: .modelMissing,
            previous: .idle
        )
        XCTAssertEqual(target, .downloadingModel(0.42))
    }

    func testUnloadedAndLoadingBothMapToModelLoadingFromInactivePrevious() {
        XCTAssertEqual(
            AppCoordinator.computeTargetState(
                permissions: granted, isPaused: false, isDownloadingModel: false,
                downloadProgress: 0, asrState: .unloaded, previous: .idle
            ),
            .modelLoading
        )
        XCTAssertEqual(
            AppCoordinator.computeTargetState(
                permissions: granted, isPaused: false, isDownloadingModel: false,
                downloadProgress: 0, asrState: .loading, previous: .idle
            ),
            .modelLoading
        )
    }

    /// 空闲卸载后首次按热键：makeSession() 异步加载模型的同时立刻开始录音，模型进入
    /// .loading 时上一轮 AppState 已是活动听写态。此时 .loading 必须保留 previous，
    /// 否则 reevaluateReadiness() 会因"进行中的听写被未就绪覆盖"而 stop()，丢掉首段音频。
    func testLoadingPreservesActiveDictationStates() {
        for active: AppState in [.recording, .recognizing, .inserting] {
            let target = AppCoordinator.computeTargetState(
                permissions: granted, isPaused: false, isDownloadingModel: false,
                downloadProgress: 0, asrState: .loading, previous: active
            )
            XCTAssertEqual(target, active, "听写进行中的 .loading 不应被覆盖为 .modelLoading")
        }
    }

    /// 非活动 / 启动态下的 .loading 仍应进入 .modelLoading（等待引擎加载完成）。
    func testLoadingBecomesModelLoadingFromInactivePrevious() {
        for previous: AppState in [.idle, .booting, .error("x"), .paused, .setupRequired] {
            let target = AppCoordinator.computeTargetState(
                permissions: granted, isPaused: false, isDownloadingModel: false,
                downloadProgress: 0, asrState: .loading, previous: previous
            )
            XCTAssertEqual(target, .modelLoading)
        }
    }

    /// 不对称：.unloaded（引擎从未加载）即使 previous 是活动听写态，也仍返回 .modelLoading。
    /// 只有 .loading 才享有"加载与录音并行"的保护。
    func testUnloadedDoesNotPreserveActiveDictation() {
        for active: AppState in [.recording, .recognizing, .inserting] {
            let target = AppCoordinator.computeTargetState(
                permissions: granted, isPaused: false, isDownloadingModel: false,
                downloadProgress: 0, asrState: .unloaded, previous: active
            )
            XCTAssertEqual(target, .modelLoading, ".unloaded 不参与活动听写保护")
        }
    }

    func testModelMissingMapsDirectly() {
        let target = AppCoordinator.computeTargetState(
            permissions: granted, isPaused: false, isDownloadingModel: false,
            downloadProgress: 0, asrState: .modelMissing, previous: .idle
        )
        XCTAssertEqual(target, .modelMissing)
    }

    func testFailedStateBecomesErrorWithMessage() {
        let target = AppCoordinator.computeTargetState(
            permissions: granted, isPaused: false, isDownloadingModel: false,
            downloadProgress: 0, asrState: .failed("模型损坏"), previous: .idle
        )
        XCTAssertEqual(target, .error("模型加载失败: 模型损坏"))
    }

    func testReadyBecomesIdleWhenPreviouslyIdle() {
        let target = AppCoordinator.computeTargetState(
            permissions: granted, isPaused: false, isDownloadingModel: false,
            downloadProgress: 0, asrState: .ready, previous: .idle
        )
        XCTAssertEqual(target, .idle)
    }

    /// 核心不变量：模型就绪/空闲卸载不应打断正在进行的听写——权限检查、空闲卸载计时器
    /// 触发的状态变化都可能在录音期间发生，若覆盖成 `.idle` 会让 UI 与实际录音状态脱节。
    func testReadyPreservesActiveDictationStates() {
        for active: AppState in [.recording, .recognizing, .inserting] {
            let target = AppCoordinator.computeTargetState(
                permissions: granted, isPaused: false, isDownloadingModel: false,
                downloadProgress: 0, asrState: .suspendedForIdle, previous: active
            )
            XCTAssertEqual(target, active, "听写进行中不应被就绪态覆盖")
        }
    }

    func testReadyBecomesIdleWhenPreviouslyInTransientOrErrorState() {
        for previous: AppState in [.booting, .error("x"), .paused, .setupRequired] {
            let target = AppCoordinator.computeTargetState(
                permissions: granted, isPaused: false, isDownloadingModel: false,
                downloadProgress: 0, asrState: .ready, previous: previous
            )
            XCTAssertEqual(target, .idle)
        }
    }
}
