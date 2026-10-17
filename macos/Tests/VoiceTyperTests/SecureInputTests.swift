import XCTest
@testable import VoiceTyper

final class SecureInputTests: XCTestCase {
    func testWarningMentionsOwnerAppForComboHotkey() {
        XCTAssertEqual(
            SecureInput.warning(isEnabled: true, ownerName: "终端", hotkeyIsCombo: true),
            "「终端」开启了安全输入，组合键热键暂时收不到"
        )
    }

    func testWarningFallsBackToGenericAppName() {
        XCTAssertEqual(
            SecureInput.warning(isEnabled: true, ownerName: nil, hotkeyIsCombo: true),
            "「某个应用」开启了安全输入，组合键热键暂时收不到"
        )
    }

    func testNoWarningForFnOrModifierHotkeyOrWhenDisabled() {
        XCTAssertNil(SecureInput.warning(isEnabled: true, ownerName: "终端", hotkeyIsCombo: false))
        XCTAssertNil(SecureInput.warning(isEnabled: false, ownerName: "终端", hotkeyIsCombo: true))
    }
}
