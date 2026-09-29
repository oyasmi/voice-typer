import Carbon.HIToolbox
import XCTest
@testable import VoiceTyper

/// R2-05a 回归：AX 控件返回的 `CFRange` 不可信，非法值必须被识别出来而不是直接
/// 喂给 `NSString.replacingCharacters` 触发 `NSRangeException`（Swift 无法捕获，直接崩溃）。
final class TextInsertionServiceTests: XCTestCase {
    func testValidRangeWithinBoundsIsAccepted() {
        let range = TextInsertionService.validInsertionRange(CFRange(location: 2, length: 3), in: 10)
        XCTAssertEqual(range, NSRange(location: 2, length: 3))
    }

    func testZeroLengthRangeAtEndIsAccepted() {
        let range = TextInsertionService.validInsertionRange(CFRange(location: 10, length: 0), in: 10)
        XCTAssertEqual(range, NSRange(location: 10, length: 0))
    }

    func testKCFNotFoundLocationIsRejected() {
        XCTAssertNil(TextInsertionService.validInsertionRange(CFRange(location: kCFNotFound, length: 0), in: 10))
    }

    func testNegativeLocationIsRejected() {
        XCTAssertNil(TextInsertionService.validInsertionRange(CFRange(location: -1, length: 2), in: 10))
    }

    func testNegativeLengthIsRejected() {
        XCTAssertNil(TextInsertionService.validInsertionRange(CFRange(location: 0, length: -1), in: 10))
    }

    func testLocationBeyondLengthIsRejected() {
        XCTAssertNil(TextInsertionService.validInsertionRange(CFRange(location: 11, length: 0), in: 10))
    }

    func testRangeOverflowingLengthIsRejected() {
        XCTAssertNil(TextInsertionService.validInsertionRange(CFRange(location: 8, length: 5), in: 10))
    }

    // MARK: - 连续粘贴兜底：pending 快照继承（避免覆盖用户最初的剪贴板）

    /// 临时恢复窗口内再次粘贴兜底，且剪贴板仍是上一段听写写入的临时文本、changeCount 未变：
    /// 必须继承上一次备份的「用户原始快照」，而不是把这段临时文本当成用户内容重新快照。
    func testInheritsPendingSnapshotWhenClipboardStillHoldsPreviousDictationText() {
        XCTAssertTrue(TextInsertionService.shouldInheritPendingSnapshot(
            currentChangeCount: 7,
            currentString: "上一段听写",
            pendingWrittenChangeCount: 7,
            pendingWrittenText: "上一段听写"
        ))
    }

    /// 用户在两次兜底之间复制了新内容（changeCount 变化）：不继承，重新快照当前剪贴板，
    /// 保证用户的新复制内容会被正确保留与恢复。
    func testDoesNotInheritWhenUserCopiedNewContent() {
        XCTAssertFalse(TextInsertionService.shouldInheritPendingSnapshot(
            currentChangeCount: 9,
            currentString: "用户刚复制的新内容",
            pendingWrittenChangeCount: 7,
            pendingWrittenText: "上一段听写"
        ))
    }

    /// changeCount 恰好相同但剪贴板字符串已被别处改写：同样不继承。
    func testDoesNotInheritWhenClipboardStringChangedWithoutChangeCountBump() {
        XCTAssertFalse(TextInsertionService.shouldInheritPendingSnapshot(
            currentChangeCount: 7,
            currentString: "别的内容",
            pendingWrittenChangeCount: 7,
            pendingWrittenText: "上一段听写"
        ))
    }

    // MARK: - A4：按键盘布局取粘贴键码

    private let qwertyV = CGKeyCode(kVK_ANSI_V)

    func testQwertyLayoutUsesANSIV() {
        let code = PasteKeyResolver.pasteKeyCode(layoutID: "com.apple.keylayout.US", layoutName: "U.S.") {
            $0 == CGKeyCode(kVK_ANSI_V) ? "v" : nil
        }
        XCTAssertEqual(code, qwertyV)
    }

    /// Dvorak 下 ANSI_V 键位产生 k，固定发它会变成 ⌘K；v 在 Period 键位上。
    func testDvorakLayoutUsesTheKeyThatProducesV() {
        let table: [CGKeyCode: String] = [
            CGKeyCode(kVK_ANSI_V): "k",
            CGKeyCode(kVK_ANSI_Period): "v",
        ]
        let code = PasteKeyResolver.pasteKeyCode(layoutID: "com.apple.keylayout.Dvorak", layoutName: "Dvorak") { table[$0] }
        XCTAssertEqual(code, CGKeyCode(kVK_ANSI_Period))
    }

    func testTranslationIsCaseInsensitive() {
        let code = PasteKeyResolver.pasteKeyCode(layoutID: nil, layoutName: nil) {
            $0 == CGKeyCode(kVK_ANSI_Period) ? "V" : nil
        }
        XCTAssertEqual(code, CGKeyCode(kVK_ANSI_Period))
    }

    /// "按住 ⌘ 时切回 QWERTY"的布局：⌘V 的键位仍是 QWERTY 的 V，不能看翻译表。
    func testQwertyCommandLayoutsAlwaysUseANSIV() {
        let table: [CGKeyCode: String] = [CGKeyCode(kVK_ANSI_Period): "v"]
        let byID = PasteKeyResolver.pasteKeyCode(
            layoutID: "com.apple.keylayout.DVORAK-QWERTYCMD", layoutName: "Dvorak - QWERTY ⌘"
        ) { table[$0] }
        XCTAssertEqual(byID, qwertyV)

        let byName = PasteKeyResolver.pasteKeyCode(layoutID: "custom.layout", layoutName: "My Layout ⌘") { table[$0] }
        XCTAssertEqual(byName, qwertyV)
    }

    func testFallsBackToANSIVWhenLayoutHasNoV() {
        let code = PasteKeyResolver.pasteKeyCode(layoutID: "com.apple.keylayout.Arabic", layoutName: "Arabic") { _ in "ب" }
        XCTAssertEqual(code, qwertyV)
    }
}
