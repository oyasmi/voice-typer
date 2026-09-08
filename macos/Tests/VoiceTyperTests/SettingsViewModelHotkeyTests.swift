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
    /// 若直接落盘，用户每换一次热键就会被悄悄拽回「按住说话」。
    func testRecordingNewHotkeyPreservesTriggerMode() async {
        let (vm, recorder) = makeViewModel(
            initialHotkey: HotkeyConfig(modifiers: [], key: "fn", mode: .toggle)
        )

        vm.applyRecordedHotkey(HotkeyConfig(modifiers: ["ctrl"], key: "f2"))
        await settle()

        XCTAssertEqual(recorder.callCount, 1)
        XCTAssertEqual(recorder.saved?.hotkey.key, "f2")
        XCTAssertEqual(recorder.saved?.hotkey.modifiers, ["ctrl"])
        XCTAssertEqual(recorder.saved?.hotkey.mode, .toggle, "换热键不应把触发方式重置为默认值")
    }

    func testResetToFnPreservesTriggerMode() async {
        let (vm, recorder) = makeViewModel(
            initialHotkey: HotkeyConfig(modifiers: ["ctrl"], key: "f2", mode: .toggle)
        )

        vm.resetHotkeyToFn()
        await settle()

        XCTAssertEqual(recorder.saved?.hotkey.key, "fn")
        XCTAssertEqual(recorder.saved?.hotkey.mode, .toggle)
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
}
