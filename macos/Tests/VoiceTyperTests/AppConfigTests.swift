import XCTest
import Yams
@testable import VoiceTyper

/// R2-12 回归：`clamp` 的 `value < lower || value > upper` 对 NaN 两边都是 false，
/// 会让 NaN 原样穿透而不是被夹逼，必须为浮点字段单独处理非有限数值。
final class AppConfigTests: XCTestCase {
    func testValidatedResetsNaNTemperatureToLowerBound() {
        var config = AppConfig()
        config.llm.temperature = .nan
        let validated = config.validated()
        XCTAssertEqual(validated.llm.temperature, 0)
    }

    func testValidatedResetsInfiniteOpacityToLowerBound() {
        var config = AppConfig()
        config.ui.opacity = .infinity
        let validated = config.validated()
        XCTAssertEqual(validated.ui.opacity, 0.1)
    }

    func testValidatedResetsNegativeInfiniteTimeoutToLowerBound() {
        var config = AppConfig()
        config.llm.timeout = -.infinity
        let validated = config.validated()
        XCTAssertEqual(validated.llm.timeout, 1)
    }

    func testValidatedClampsFiniteOutOfRangeValuesAsBefore() {
        var config = AppConfig()
        config.ui.opacity = 5.0
        config.asr.threads = 9_999
        let validated = config.validated()
        XCTAssertEqual(validated.ui.opacity, 1.0)
        XCTAssertEqual(validated.asr.threads, 32)
    }

    func testValidatedKeepsInRangeValuesUnchanged() {
        var config = AppConfig()
        config.llm.temperature = 0.7
        let validated = config.validated()
        XCTAssertEqual(validated.llm.temperature, 0.7)
    }

    /// R4-05：设置窗口的 UI 校验（`SettingsViewModel.applyHotkey`）不是改配置的唯一入口，
    /// 手改 `config.yaml` 可以绕过它写出一个不带修饰键的普通字母键，导致在任何 App 里
    /// 正常打字都会触发录音。`validated()` 必须是这条规则的最后一道防线。
    func testValidatedFallsBackToFnWhenNonFnKeyHasNoModifier() {
        var config = AppConfig()
        config.hotkey = HotkeyConfig(modifiers: [], key: "d")
        let validated = config.validated()
        XCTAssertEqual(validated.hotkey.key, "fn")
        XCTAssertTrue(validated.hotkey.modifiers.isEmpty)
    }

    func testValidatedFallsBackToFnForUnsupportedKeyName() {
        var config = AppConfig()
        config.hotkey = HotkeyConfig(modifiers: ["cmd"], key: "not-a-real-key")
        let validated = config.validated()
        XCTAssertEqual(validated.hotkey.key, "fn")
    }

    func testValidatedKeepsNonFnKeyWithModifier() {
        var config = AppConfig()
        config.hotkey = HotkeyConfig(modifiers: ["cmd", "shift"], key: "d")
        let validated = config.validated()
        XCTAssertEqual(validated.hotkey.key, "d")
        XCTAssertEqual(validated.hotkey.modifiers, ["cmd", "shift"])
    }

    func testValidatedKeepsBareFnWithoutModifier() {
        var config = AppConfig()
        config.hotkey = HotkeyConfig(modifiers: [], key: "fn")
        let validated = config.validated()
        XCTAssertEqual(validated.hotkey.key, "fn")
    }

    func testConfigDecodesNaNAndInfFromYAMLWithoutCrashing() throws {
        let yaml = """
        llm:
          temperature: .nan
        ui:
          opacity: .inf
        """
        let decoded = try YAMLDecoder().decode(AppConfig.self, from: yaml)
        XCTAssertTrue(decoded.llm.temperature.isNaN)
        XCTAssertTrue(decoded.ui.opacity.isInfinite)

        let validated = decoded.validated()
        XCTAssertEqual(validated.llm.temperature, 0)
        XCTAssertEqual(validated.ui.opacity, 0.1)
    }
}
