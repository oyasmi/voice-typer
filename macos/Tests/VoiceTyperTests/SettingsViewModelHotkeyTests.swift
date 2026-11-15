import XCTest
@testable import VoiceTyper

/// 热键设置的落盘内容。
///
/// 这里只碰热键相关的入口，**刻意不调用 `SettingsViewModel.load(config:launchAtLogin:)`**：
/// 它会去读 Keychain，而 hosted 单元测试里那会触碰用户真实的钥匙串（同 `AppDelegate`
/// 里 R2-02 的理由）。直接给 `hotkeyConfig` 赋初值即可覆盖被测逻辑。
@MainActor
final class SettingsViewModelHotkeyTests: XCTestCase {
    /// 捕获 `onSaveConfig` 收到的配置。用引用类型，避免闭包捕获值语义的拷贝。
    private final class SaveRecorder {
        var saved: AppConfig?
        var callCount = 0
    }

    private func makeViewModel(
        initialHotkey: HotkeyConfig
    ) -> (SettingsViewModel, SaveRecorder) {
        let vm = SettingsViewModel()
        vm.hotkeyConfig = initialHotkey
        let recorder = SaveRecorder()
        vm.onSaveConfig = { config in
            recorder.saved = config
            recorder.callCount += 1
        }
        return (vm, recorder)
    }

    /// 给异步的保存 Task 一个运行机会。
    private func settle() async {
        try? await Task.sleep(nanoseconds: 120_000_000)
    }

    /// 回归钉子：录制器只知道"按了哪个键"，它构造的 `HotkeyConfig.mode` 永远是默认值。
    /// 若直接落盘，用户每换一次热键（录制 / 恢复为 Fn / 恢复为右 ⌘）就会被悄悄拽回「按住说话」。
    func testChangingHotkeyNeverResetsTriggerMode() async {
        let changes: [(String, (SettingsViewModel) -> Void, String, [String])] = [
            ("录制组合键", { $0.applyRecordedHotkey(HotkeyConfig(modifiers: ["ctrl"], key: "f2")) }, "f2", ["ctrl"]),
            ("录制单独修饰键", { $0.applyRecordedHotkey(HotkeyConfig(modifiers: [], key: "right_command")) }, "right_command", []),
            ("恢复为 Fn", { $0.resetHotkeyToFn() }, "fn", []),
            ("恢复为右 ⌘", { $0.resetHotkeyToRightCommand() }, "right_command", []),
        ]
        for (name, change, key, modifiers) in changes {
            let (vm, recorder) = makeViewModel(
                initialHotkey: HotkeyConfig(modifiers: ["alt"], key: "space", mode: .toggle)
            )
            change(vm)
            await settle()

            XCTAssertEqual(recorder.callCount, 1, name)
            XCTAssertEqual(recorder.saved?.hotkey.key, key, name)
            XCTAssertEqual(recorder.saved?.hotkey.modifiers, modifiers, name)
            XCTAssertEqual(recorder.saved?.hotkey.mode, .toggle, "\(name)：换热键不应把触发方式重置为默认值")
            XCTAssertEqual(vm.hotkeyMessageKind, .success, name)
        }
    }

    func testCommitAudioInputPolicyPersistsChoiceAndRevertsOnFailure() async {
        let vm = SettingsViewModel()
        var saved: AppConfig?
        vm.onSaveConfig = { saved = $0 }

        vm.audioInputPolicy = .systemDefault
        vm.commitAudioInputPolicy()
        await settle()
        XCTAssertEqual(saved?.audio.inputDevice, "system")

        struct SaveFailure: Error {}
        vm.onSaveConfig = { _ in throw SaveFailure() }
        vm.audioInputPolicy = .automatic
        vm.commitAudioInputPolicy()
        await settle()
        XCTAssertEqual(vm.audioInputPolicy, .systemDefault, "保存失败必须退回真实生效的值")
        XCTAssertEqual(vm.generalMessageKind, .error)
    }

    func testApplyHotkeyModeChangesOnlyTheMode() async {
        let (vm, recorder) = makeViewModel(
            initialHotkey: HotkeyConfig(modifiers: ["ctrl"], key: "f2", mode: .hold)
        )

        vm.applyHotkeyMode(.toggle)
        await settle()

        XCTAssertEqual(recorder.saved?.hotkey.key, "f2")
        XCTAssertEqual(recorder.saved?.hotkey.modifiers, ["ctrl"])
        XCTAssertEqual(recorder.saved?.hotkey.mode, .toggle)
        XCTAssertEqual(vm.hotkeyMessageKind, .success)
    }

    func testApplyHotkeyModeIsNoOpWhenUnchanged() async {
        let (vm, recorder) = makeViewModel(
            initialHotkey: HotkeyConfig(modifiers: [], key: "fn", mode: .hold)
        )

        vm.applyHotkeyMode(.hold)
        await settle()

        XCTAssertEqual(recorder.callCount, 0, "没有实际变化不应触发一次控制器重建")
    }

    /// 裸主键（无修饰键）必须在落盘前被拦下——热键 tap 不吞事件，裸键一旦生效，
    /// 之后在任何应用里打这个字母都会同时触发录音。
    func testBareKeyWithoutModifierIsRejectedBeforeSaving() async {
        let (vm, recorder) = makeViewModel(
            initialHotkey: HotkeyConfig(modifiers: [], key: "fn", mode: .hold)
        )

        vm.applyRecordedHotkey(HotkeyConfig(modifiers: [], key: "d"))
        await settle()

        XCTAssertEqual(recorder.callCount, 0)
        XCTAssertEqual(vm.hotkeyMessageKind, .error)
        XCTAssertEqual(vm.hotkeyConfig.key, "fn", "被拒绝的热键不应改变界面上显示的当前值")
    }

    /// 保存失败（例如正在听写中）必须把界面回退到真实生效的值，
    /// 否则 Picker / 录制框会显示一个其实没有生效的设置。
    func testFailedSaveRevertsDisplayedHotkey() async {
        let vm = SettingsViewModel()
        vm.hotkeyConfig = HotkeyConfig(modifiers: [], key: "fn", mode: .hold)
        struct SaveFailure: LocalizedError {
            var errorDescription: String? { "正在听写中" }
        }
        vm.onSaveConfig = { _ in throw SaveFailure() }

        vm.applyRecordedHotkey(HotkeyConfig(modifiers: ["ctrl"], key: "f2"))
        await settle()

        XCTAssertEqual(vm.hotkeyConfig.key, "fn")
        XCTAssertEqual(vm.hotkeyDisplay, "Fn🌐")
        XCTAssertEqual(vm.hotkeyMessageKind, .error)
    }

    /// S-4 回归：识别语言作为「语音模型」分组下的即时保存项，切换立即落盘；保存失败回退到已生效语言。
    func testCommitLanguagePersistsImmediatelyAndRevertsOnFailure() async {
        let vm = SettingsViewModel()
        var saved: AppConfig?
        vm.onSaveConfig = { saved = $0 }

        vm.language = .en
        vm.commitLanguage()
        await settle()
        XCTAssertEqual(saved?.asr.language, .en)

        struct SaveFailure: Error {}
        vm.onSaveConfig = { _ in throw SaveFailure() }
        vm.language = .ja
        vm.commitLanguage()
        await settle()
        XCTAssertEqual(vm.language, .en, "识别语言保存失败必须回退显示已生效的值")
        XCTAssertEqual(vm.modelMessageKind, .error)
    }

    /// S-4 回归：空闲卸载时长保存失败时必须回退 Picker 显示值（此前漏写回退）。
    func testCommitIdleUnloadMinutesRevertsOnFailure() async {
        let vm = SettingsViewModel()
        vm.onSaveConfig = { _ in }
        vm.idleUnloadMinutes = 10
        vm.commitIdleUnloadMinutes()
        await settle()

        struct SaveFailure: Error {}
        vm.onSaveConfig = { _ in throw SaveFailure() }
        vm.idleUnloadMinutes = 30
        vm.commitIdleUnloadMinutes()
        await settle()
        XCTAssertEqual(vm.idleUnloadMinutes, 10, "空闲卸载时长保存失败必须回退到上次生效的值")
        XCTAssertEqual(vm.modelMessageKind, .error)
    }

    /// S-4 回归：即时保存项或菜单栏快捷开关回写配置时（`syncAppliedConfig`），
    /// 不得冲掉用户在「智能校对」区尚未点击保存的草稿或 API Key，也不得清空既有提示。
    func testSyncAppliedConfigPreservesUnsavedLLMDraftsAndMessages() async {
        let vm = SettingsViewModel()
        vm.llmEnabled = true
        vm.llmBaseURL = "https://draft.example.com/v1"
        vm.llmAPIKey = "sk-unsaved-draft"
        vm.llmModel = "draft-model"
        vm.hotkeyMessage = "热键已更新"
        vm.hotkeyMessageKind = .success

        // 模拟即时保存（如切换识别语言）触发协调器回写已生效配置：磁盘上 llm 仍是默认空值。
        var applied = AppConfig()
        applied.asr.language = .zh
        vm.syncAppliedConfig(applied)

        XCTAssertEqual(vm.language, .zh)
        XCTAssertTrue(vm.llmEnabled, "未落盘变化的智能校对开关草稿不应被回写冲掉")
        XCTAssertEqual(vm.llmBaseURL, "https://draft.example.com/v1")
        XCTAssertEqual(vm.llmAPIKey, "sk-unsaved-draft")
        XCTAssertEqual(vm.llmModel, "draft-model")
        XCTAssertEqual(vm.hotkeyMessage, "热键已更新", "回写不应把刚显示的保存提示清空")

        // 模拟用户从菜单栏快捷切换了「启用智能校对」（外部真实改变了 llm.enabled）：只更新变更项，其余草稿仍保留。
        applied.llm.enabled = true
        vm.llmEnabled = false
        vm.syncAppliedConfig(applied)
        XCTAssertTrue(vm.llmEnabled)
        XCTAssertEqual(vm.llmBaseURL, "https://draft.example.com/v1")
        XCTAssertEqual(vm.llmAPIKey, "sk-unsaved-draft")
    }
}
