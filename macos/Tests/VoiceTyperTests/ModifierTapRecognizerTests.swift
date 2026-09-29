import XCTest
@testable import VoiceTyper

/// 单独修饰键"干净单击"的手势状态机。flags 用真实的 CGEventFlags 数值：
/// 通用位 ⌘ 0x100000 / ⌥ 0x80000 / ⌃ 0x40000 / ⇧ 0x20000 / CapsLock 0x10000，
/// 设备位 右⌘ 0x10 / 左⌘ 0x8 / 右⌥ 0x40 / 左⌥ 0x20 / 右⇧ 0x4 / 左⇧ 0x2。
final class ModifierTapRecognizerTests: XCTestCase {
    private let cmd: UInt64 = 0x100000
    private let opt: UInt64 = 0x80000
    private let shift: UInt64 = 0x20000
    private let capsLock: UInt64 = 0x10000

    private let rightCmdDown: UInt64 = 0x100000 | 0x10
    private func rightCmdEvent(_ flags: UInt64) -> ModifierTapRecognizer.Input { .flagsChanged(keyCode: 54, flags: flags) }

    private func recognizer(_ target: ModifierHotkey = .rightCommand) -> ModifierTapRecognizer {
        ModifierTapRecognizer(target: target)
    }

    func testCleanTapBeginsThenEnds() {
        var r = recognizer()
        XCTAssertEqual(r.handle(rightCmdEvent(rightCmdDown)), .began)
        XCTAssertTrue(r.isHolding)
        XCTAssertEqual(r.handle(rightCmdEvent(0)), .ended)
        XCTAssertFalse(r.isHolding)
    }

    func testKeyDownWhileHoldingCancelsAndReleaseIsSilent() {
        var r = recognizer()
        XCTAssertEqual(r.handle(rightCmdEvent(rightCmdDown)), .began)
        XCTAssertEqual(r.handle(.keyDown), .cancelled)
        XCTAssertNil(r.handle(rightCmdEvent(0)), "已作废的手势抬起时不应再输出 .ended")
        XCTAssertFalse(r.isHolding)
        // 下一次单击仍然正常。
        XCTAssertEqual(r.handle(rightCmdEvent(rightCmdDown)), .began)
    }

    func testMouseDownWhileHoldingCancels() {
        var r = recognizer()
        _ = r.handle(rightCmdEvent(rightCmdDown))
        XCTAssertEqual(r.handle(.mouseDown), .cancelled)
        XCTAssertNil(r.handle(rightCmdEvent(0)))
    }

    func testAnotherModifierWhileHoldingCancels() {
        var r = recognizer()
        _ = r.handle(rightCmdEvent(rightCmdDown))
        XCTAssertEqual(r.handle(.flagsChanged(keyCode: 56, flags: rightCmdDown | shift | 0x2)), .cancelled)
        // 先松开 Shift、再松开右 ⌘：都不应有输出。
        XCTAssertNil(r.handle(.flagsChanged(keyCode: 56, flags: rightCmdDown)))
        XCTAssertNil(r.handle(rightCmdEvent(0)))
        XCTAssertFalse(r.isHolding)
    }

    func testDoesNotBeginWhenAnotherModifierAlreadyHeld() {
        var r = recognizer()
        XCTAssertNil(r.handle(.flagsChanged(keyCode: 56, flags: shift | 0x2)))
        XCTAssertNil(r.handle(rightCmdEvent(rightCmdDown | shift | 0x2)))
        XCTAssertFalse(r.isHolding)
    }

    /// 左右不串：左 ⌘ 与右 ⌘ 的通用位相同，只能靠 keyCode 与设备位区分。
    func testLeftCommandDoesNotTriggerRightCommandHotkey() {
        var r = recognizer()
        XCTAssertNil(r.handle(.flagsChanged(keyCode: 55, flags: cmd | 0x8)))
        XCTAssertNil(r.handle(.flagsChanged(keyCode: 55, flags: 0)))
        // 左 ⌘ 已按住时再按右 ⌘ 也不算干净单击。
        XCTAssertNil(r.handle(.flagsChanged(keyCode: 55, flags: cmd | 0x8)))
        XCTAssertNil(r.handle(rightCmdEvent(cmd | 0x8 | 0x10)))
    }

    func testCapsLockDoesNotInterfere() {
        var r = recognizer()
        XCTAssertEqual(r.handle(rightCmdEvent(rightCmdDown | capsLock)), .began)
        XCTAssertEqual(r.handle(rightCmdEvent(capsLock)), .ended)
    }

    func testCancelledIsEmittedAtMostOncePerGesture() {
        var r = recognizer()
        _ = r.handle(rightCmdEvent(rightCmdDown))
        XCTAssertEqual(r.handle(.keyDown), .cancelled)
        XCTAssertNil(r.handle(.keyDown))
        XCTAssertNil(r.handle(.mouseDown))
        XCTAssertNil(r.handle(.flagsChanged(keyCode: 56, flags: rightCmdDown | shift | 0x2)))
    }

    func testIdleIgnoresKeyAndMouseEvents() {
        var r = recognizer()
        XCTAssertNil(r.handle(.keyDown))
        XCTAssertNil(r.handle(.mouseDown))
    }

    func testOptionKeysAreDistinguishedBySide() {
        var right = recognizer(.rightOption)
        XCTAssertNil(right.handle(.flagsChanged(keyCode: 58, flags: opt | 0x20)), "左 ⌥ 不应触发右 ⌥")
        _ = right.handle(.flagsChanged(keyCode: 58, flags: 0))
        XCTAssertEqual(right.handle(.flagsChanged(keyCode: 61, flags: opt | 0x40)), .began)

        var left = recognizer(.leftOption)
        XCTAssertEqual(left.handle(.flagsChanged(keyCode: 58, flags: opt | 0x20)), .began)

        // 同一修饰键的另一侧已被按住：也算"其他修饰键"。
        var bothSides = recognizer(.rightOption)
        XCTAssertNil(bothSides.handle(.flagsChanged(keyCode: 61, flags: opt | 0x20 | 0x40)))
    }

    func testEveryModifierHotkeyHasUniqueKeyCodeAndDeviceMask() {
        XCTAssertEqual(Set(ModifierHotkey.allCases.map(\.keyCode)).count, ModifierHotkey.allCases.count)
        XCTAssertEqual(Set(ModifierHotkey.allCases.map(\.deviceMask)).count, ModifierHotkey.allCases.count)
        for hotkey in ModifierHotkey.allCases {
            XCTAssertEqual(ModifierHotkey.from(keyCode: hotkey.keyCode), hotkey)
        }
    }
}
