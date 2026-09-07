import XCTest
@testable import VoiceTyper

/// 引导「试一试」这一步的判定逻辑。它是引导的价值所在——把"按了没反应"翻译成
/// "断在哪一环"——所以判定本身必须有测试覆盖，而不是只靠手工点一遍。
@MainActor
final class OnboardingViewModelTests: XCTestCase {
    private let granted = PermissionSnapshot(
        microphone: .authorized, accessibility: .authorized, inputMonitoring: .authorized
    )
    private let denied = PermissionSnapshot(
        microphone: .notDetermined, accessibility: .denied, inputMonitoring: .denied
    )

    private func makeTrialReadyViewModel() -> OnboardingViewModel {
        let vm = OnboardingViewModel()
        vm.permissions = granted
        vm.asrState = .ready
        vm.step = .trial
        return vm
    }

    // MARK: - 就绪条件

    func testTrialRequiresBothPermissionsAndModel() {
        let vm = OnboardingViewModel()
        vm.permissions = denied
        vm.asrState = .ready
        XCTAssertFalse(vm.canRunTrial)
        XCTAssertEqual(vm.trialBlockingHint, "还有系统权限没有授予，请回到上一步完成授权。")

        vm.permissions = granted
        vm.asrState = .modelMissing
        XCTAssertFalse(vm.canRunTrial)
        XCTAssertEqual(vm.trialBlockingHint, "语音模型还没准备好，请等待或回到上一步重试下载。")

        vm.asrState = .ready
        XCTAssertTrue(vm.canRunTrial)
        XCTAssertNil(vm.trialBlockingHint)
    }

    /// 空闲卸载后的引擎在语义上仍然可用（下次录音会自动加载），不该被判成"模型没准备好"。
    func testSuspendedForIdleCountsAsReady() {
        let vm = OnboardingViewModel()
        vm.permissions = granted
        vm.asrState = .suspendedForIdle
        XCTAssertTrue(vm.isModelReady)
        XCTAssertTrue(vm.canRunTrial)
    }

    func testGrantedPermissionCountReflectsSnapshot() {
        let vm = OnboardingViewModel()
        vm.permissions = PermissionSnapshot(
            microphone: .authorized, accessibility: .denied, inputMonitoring: .authorized
        )
        XCTAssertEqual(vm.grantedPermissionCount, 2)
    }

    // MARK: - 试用状态机

    func testSuccessfulTrialFlow() {
        let vm = makeTrialReadyViewModel()
        vm.handle(.recordingStarted)
        XCTAssertEqual(vm.trial, .recording)
        vm.handle(.level(0.2))
        vm.handle(.recognizing)
        XCTAssertEqual(vm.trial, .recognizing)
        vm.handle(.inserted("你好世界"))
        XCTAssertEqual(vm.trial, .succeeded("你好世界"))
    }

    /// 空结果 + 录音期间几乎没有电平 → 判为"没采到声音"，把用户指向输入设备。
    func testEmptyResultWithSilenceIsReportedAsNoSpeech() {
        let vm = makeTrialReadyViewModel()
        vm.handle(.recordingStarted)
        vm.handle(.level(0.0005))
        vm.handle(.recognizing)
        vm.handle(.emptyResult)
        XCTAssertEqual(vm.trial, .noSpeech)
    }

    /// 空结果但确实采到了声音 → 不是设备问题，提示改成"说清楚一点再试"。
    func testEmptyResultWithAudibleLevelIsReportedAsRecognitionFailure() {
        let vm = makeTrialReadyViewModel()
        vm.handle(.recordingStarted)
        vm.handle(.level(0.35))
        vm.handle(.recognizing)
        vm.handle(.emptyResult)
        guard case .failed(let message) = vm.trial else {
            return XCTFail("采到声音但没识别出文字应报告为 .failed，实际是 \(vm.trial)")
        }
        XCTAssertTrue(message.contains("没有识别出文字"))
    }

    /// 每次重新开始录音都要清掉上一轮的峰值，否则上一轮的声音会把这一轮的静音"救活"。
    func testPeakLevelResetsBetweenAttempts() {
        let vm = makeTrialReadyViewModel()
        vm.handle(.recordingStarted)
        vm.handle(.level(0.9))
        vm.handle(.recognizing)
        vm.handle(.emptyResult)

        vm.handle(.recordingStarted)
        vm.handle(.level(0.0001))
        vm.handle(.recognizing)
        vm.handle(.emptyResult)
        XCTAssertEqual(vm.trial, .noSpeech)
    }

    func testEventsOutsideTrialStepAreIgnored() {
        let vm = OnboardingViewModel()
        vm.permissions = granted
        vm.asrState = .ready
        vm.step = .welcome
        vm.handle(.recordingStarted)
        vm.handle(.inserted("不该被记成试用结果"))
        XCTAssertEqual(vm.trial, .idle, "用户在别的步骤顺手按热键不应污染试用状态")
    }

    func testSwitchingStepResetsTrial() {
        let vm = makeTrialReadyViewModel()
        vm.handle(.recordingStarted)
        vm.handle(.inserted("成功过一次"))
        XCTAssertEqual(vm.trial, .succeeded("成功过一次"))

        vm.goBack()
        XCTAssertEqual(vm.step, .model)
        XCTAssertEqual(vm.trial, .idle)
    }

    func testFailureAndCancellationArePassedThrough() {
        let vm = makeTrialReadyViewModel()
        vm.handle(.failed("插入失败，已复制到剪贴板"))
        XCTAssertEqual(vm.trial, .failed("插入失败，已复制到剪贴板"))

        vm.handle(.cancelled)
        XCTAssertEqual(vm.trial, .cancelled)

        vm.handle(.blocked("模型还在下载"))
        XCTAssertEqual(vm.trial, .blocked("模型还在下载"))
    }

    // MARK: - 完成标记

    func testCompletionRecordIsScopedToFlowVersion() {
        let suiteName = "OnboardingViewModelTests.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suiteName) else {
            return XCTFail("无法创建临时 UserDefaults suite")
        }
        defer { defaults.removePersistentDomain(forName: suiteName) }

        XCTAssertFalse(OnboardingRecord.isCompleted(defaults: defaults))
        OnboardingRecord.markCompleted(defaults: defaults)
        XCTAssertTrue(OnboardingRecord.isCompleted(defaults: defaults))
    }
}
