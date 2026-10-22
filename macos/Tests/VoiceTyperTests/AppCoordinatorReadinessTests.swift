import XCTest
@testable import VoiceTyper

/// `AppCoordinator` 的就绪判定拆成了几个纯函数（R3-12）：不需要构造协调器的全部依赖
/// （权限中心、状态栏、ASR 服务…）即可验证。
/// - `computeReadiness`：暂停 > 权限缺失 > 下载中 > 模型状态；
/// - `shouldInterruptDictation`：哪些未就绪必须拆掉进行中的听写；
/// - `displayState`：菜单栏如何把就绪度与听写阶段合成一个状态；
/// - `requiresControllerRebuild`：哪些设置变化必须重建控制器才会生效。
@MainActor
final class AppCoordinatorReadinessTests: XCTestCase {
    private let granted = PermissionSnapshot(microphone: .authorized, accessibility: .authorized, inputMonitoring: .authorized)
    private let denied = PermissionSnapshot(microphone: .notDetermined, accessibility: .denied, inputMonitoring: .denied)

    private func readiness(
        permissions: PermissionSnapshot? = nil,
        isPaused: Bool = false,
        isDownloadingModel: Bool = false,
        downloadProgress: Double = 0,
        asrState: ASRService.State
    ) -> AppState {
        AppCoordinator.computeReadiness(
            permissions: permissions ?? granted,
            isPaused: isPaused,
            isDownloadingModel: isDownloadingModel,
            downloadProgress: downloadProgress,
            asrState: asrState
        )
    }

    // MARK: - computeReadiness

    func testPausedTakesPriorityOverEverythingElse() {
        XCTAssertEqual(
            readiness(permissions: denied, isPaused: true, isDownloadingModel: true, downloadProgress: 0.5, asrState: .ready),
            .paused
        )
    }

    func testMissingPermissionsTakesPriorityOverDownloadAndModel() {
        XCTAssertEqual(
            readiness(permissions: denied, isDownloadingModel: true, downloadProgress: 0.5, asrState: .ready),
            .setupRequired
        )
    }

    func testDownloadingModelTakesPriorityOverASRState() {
        XCTAssertEqual(
            readiness(isDownloadingModel: true, downloadProgress: 0.42, asrState: .modelMissing),
            .downloadingModel(0.42)
        )
    }

    func testUnloadedAndLoadingBothMapToModelLoading() {
        XCTAssertEqual(readiness(asrState: .unloaded), .modelLoading)
        XCTAssertEqual(readiness(asrState: .loading), .modelLoading)
    }

    func testFailedStateBecomesErrorWithMessage() {
        XCTAssertEqual(readiness(asrState: .failed("模型损坏")), .error("模型加载失败: 模型损坏"))
    }

    /// 空闲卸载后仍算就绪：热键照常监听，下次按热键时按需加载（F-04）。
    func testReadyAndSuspendedForIdleAreBothIdle() {
        XCTAssertEqual(readiness(asrState: .ready), .idle)
        XCTAssertEqual(readiness(asrState: .suspendedForIdle), .idle)
    }

    // MARK: - shouldInterruptDictation

    /// 空闲卸载后首次按热键：makeSession() 一边加载模型一边录音，引擎就绪后回灌音频。
    /// 这时进入 `.loading` 不能拆掉听写，否则首段录音直接丢失。
    func testLoadingInParallelWithRecordingDoesNotInterrupt() {
        XCTAssertFalse(AppCoordinator.shouldInterruptDictation(readiness: .modelLoading, asrState: .loading))
        XCTAssertFalse(AppCoordinator.shouldInterruptDictation(readiness: .idle, asrState: .ready))
        XCTAssertFalse(AppCoordinator.shouldInterruptDictation(readiness: .idle, asrState: .suspendedForIdle))
    }

    /// 不对称：`.unloaded`（引擎被卸下、重新加载）不享有并行加载的保护。
    func testOtherUnreadyStatesInterrupt() {
        XCTAssertTrue(AppCoordinator.shouldInterruptDictation(readiness: .modelLoading, asrState: .unloaded))
        for state: AppState in [.setupRequired, .modelMissing, .downloadingModel(0.3), .paused, .error("模型加载失败: x")] {
            XCTAssertTrue(AppCoordinator.shouldInterruptDictation(readiness: state, asrState: .ready), "\(state)")
        }
    }

    // MARK: - displayState

    /// 核心不变量：进行中的听写永远优先显示——就绪度的任何变化都不会把它盖掉。
    func testDictationPhaseWinsOverReadiness() {
        for readiness: AppState in [.idle, .modelLoading, .error("模型下载失败: x"), .downloadingModel(0.5)] {
            XCTAssertEqual(
                AppCoordinator.displayState(readiness: readiness, phase: .recognizing, dictationError: nil),
                .recognizing
            )
        }
    }

    /// 听写错误只在就绪时短暂显示；它不是就绪度的一部分，不能盖住模型加载失败之类的长期状态。
    func testDictationErrorShowsOnlyWhenReady() {
        XCTAssertEqual(
            AppCoordinator.displayState(readiness: .idle, phase: nil, dictationError: "插入失败"),
            .error("插入失败")
        )
        XCTAssertEqual(
            AppCoordinator.displayState(readiness: .modelMissing, phase: nil, dictationError: "插入失败"),
            .modelMissing
        )
        XCTAssertEqual(AppCoordinator.displayState(readiness: .idle, phase: nil, dictationError: nil), .idle)
    }

    // MARK: - requiresControllerRebuild

    func testUIOnlyChangesDoNotRebuildController() {
        var updated = AppConfig()
        updated.ui.opacity = 0.5
        updated.ui.hudPosition = .bottomRight
        XCTAssertFalse(AppCoordinator.requiresControllerRebuild(from: AppConfig(), to: updated, llmAPIKeyChanged: false))
    }

    /// 输入设备在控制器构造时拿走：菜单栏切换麦克风不重建就永远不会生效。
    func testInputDeviceChangeRebuildsController() {
        var updated = AppConfig()
        updated.audio.inputDevice = "system"
        XCTAssertTrue(AppCoordinator.requiresControllerRebuild(from: AppConfig(), to: updated, llmAPIKeyChanged: false))
    }

    /// 只改 API Key（存在 Keychain，配置本身没变）也必须重建，否则校对一直用旧 Key。
    func testAPIKeyChangeRebuildsControllerOnlyWhenCorrectionIsEnabled() {
        var enabled = AppConfig()
        enabled.llm.enabled = true
        XCTAssertTrue(AppCoordinator.requiresControllerRebuild(from: enabled, to: enabled, llmAPIKeyChanged: true))
        XCTAssertFalse(AppCoordinator.requiresControllerRebuild(from: enabled, to: enabled, llmAPIKeyChanged: false))
        XCTAssertFalse(AppCoordinator.requiresControllerRebuild(from: AppConfig(), to: AppConfig(), llmAPIKeyChanged: true))
    }

    func testRecognitionHotkeyAndCorrectionChangesRebuildController() {
        var language = AppConfig()
        language.asr.language = .zh
        var hotkey = AppConfig()
        hotkey.hotkey.mode = .toggle
        var llm = AppConfig()
        llm.llm.model = "other"
        for updated in [language, hotkey, llm] {
            XCTAssertTrue(AppCoordinator.requiresControllerRebuild(from: AppConfig(), to: updated, llmAPIKeyChanged: false))
        }
    }

    /// 替换层在控制器构造时编译（VOC-7）：改词条或偏置强度都要重建才会生效。
    func testVocabularyChangeRebuildsController() {
        var entries = AppConfig()
        entries.vocabulary.entries = [VocabularyEntry(term: "Claude", aliases: ["克劳德"])]
        var bias = AppConfig()
        bias.vocabulary.bias = .strong
        for updated in [entries, bias] {
            XCTAssertTrue(AppCoordinator.requiresControllerRebuild(from: AppConfig(), to: updated, llmAPIKeyChanged: false))
        }
    }
}
