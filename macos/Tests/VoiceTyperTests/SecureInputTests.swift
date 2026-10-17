import XCTest
@testable import VoiceTyper

final class SecureInputTests: XCTestCase {
    /// 安全输入开启时组合键热键收不到事件；Fn / 单独修饰键走 flagsChanged，不受影响。
    func testWarnsOnlyWhenSecureInputBlocksAComboHotkey() {
        XCTAssertEqual(
            SecureInput.warning(isEnabled: true, ownerName: "终端", hotkeyIsCombo: true),
            "「终端」开启了安全输入，组合键热键暂时收不到"
        )
        XCTAssertEqual(
            SecureInput.warning(isEnabled: true, ownerName: nil, hotkeyIsCombo: true),
            "「某个应用」开启了安全输入，组合键热键暂时收不到"
        )
        XCTAssertNil(SecureInput.warning(isEnabled: true, ownerName: "终端", hotkeyIsCombo: false))
        XCTAssertNil(SecureInput.warning(isEnabled: false, ownerName: "终端", hotkeyIsCombo: true))
    }
}
