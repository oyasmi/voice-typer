import Carbon.HIToolbox
import XCTest
@testable import VoiceTyper

/// `HotkeyEventFilter`：从 `HotkeyService` 迁出的「事件 → 动作」状态机（R8-B1）。
/// 先钉住迁移前的行为（动作序列），再钉住吞键规则。
final class HotkeyEventFilterTests: XCTestCase {
    private let fnFlag: UInt64 = 0x800000
    private let command: UInt64 = 0x100000
    private let control: UInt64 = 0x40000
    private let option: UInt64 = 0x80000
    private let shift: UInt64 = 0x20000

    private let escape = Int64(kVK_Escape)
    private let space = Int64(kVK_Space)
    private let keyD = Int64(kVK_ANSI_D)
    private let keyA = Int64(kVK_ANSI_A)

    private func filter(
        key: String, modifiers: [String] = []
    ) -> HotkeyEventFilter {
        HotkeyEventFilter(hotkey: HotkeyConfig(modifiers: modifiers, key: key, mode: .hold))
    }

    private func down(
        _ filter: inout HotkeyEventFilter, _ keyCode: Int64, flags: UInt64 = 0, isRepeat: Bool = false,
        acceptsCancelWhenInactive: Bool = false
    ) -> HotkeyEventFilter.Decision {
        filter.handle(
            .keyDown(keyCode: keyCode, flags: flags, isRepeat: isRepeat),
            acceptsCancelWhenInactive: acceptsCancelWhenInactive
        )
    }

    private func up(
        _ filter: inout HotkeyEventFilter, _ keyCode: Int64, acceptsCancelWhenInactive: Bool = false
    ) -> HotkeyEventFilter.Decision {
        filter.handle(.keyUp(keyCode: keyCode), acceptsCancelWhenInactive: acceptsCancelWhenInactive)
    }

    // MARK: - 迁移前的行为（动作序列）

    func testFnPressAndReleaseFollowFlagsChanged() {
        var f = filter(key: "fn")
        XCTAssertEqual(f.handle(.flagsChanged(keyCode: 63, flags: fnFlag), acceptsCancelWhenInactive: false).actions, [.press])
        XCTAssertEqual(f.handle(.flagsChanged(keyCode: 63, flags: fnFlag), acceptsCancelWhenInactive: false).actions, [],
                       "重复的按下状态不再触发")
        XCTAssertEqual(f.handle(.flagsChanged(keyCode: 63, flags: 0), acceptsCancelWhenInactive: false).actions, [.release])
        XCTAssertEqual(f.handle(.flagsChanged(keyCode: 63, flags: 0), acceptsCancelWhenInactive: false).actions, [])
    }

    func testComboPressRepeatAndReleaseActions() {
        var f = filter(key: "space", modifiers: ["option"])
        XCTAssertEqual(down(&f, space, flags: option).actions, [.press])
        XCTAssertEqual(down(&f, space, flags: option, isRepeat: true).actions, [], "自动重复不再触发按下")
        XCTAssertEqual(up(&f, space).actions, [.release])
    }

    func testComboRequiresExactModifiers() {
        var f = filter(key: "space", modifiers: ["option"])
        XCTAssertEqual(down(&f, space, flags: 0).actions, [])
        XCTAssertEqual(down(&f, space, flags: option | shift).actions, [], "多出修饰键不匹配")
        XCTAssertEqual(down(&f, keyA, flags: option).actions, [], "键码不匹配")
        XCTAssertEqual(down(&f, space, flags: option).actions, [.press])
    }

    func testComboReleaseOnlyAfterPress() {
        var f = filter(key: "space", modifiers: ["control"])
        XCTAssertEqual(up(&f, space).actions, [])
    }

    func testEscapeCancelsWhileActiveOrWhenInactiveWindowIsOpen() {
        var f = filter(key: "space", modifiers: ["option"])
        XCTAssertEqual(down(&f, escape).actions, [], "未激活且取消窗口关闭时 Esc 无动作")
        XCTAssertEqual(down(&f, escape, acceptsCancelWhenInactive: true).actions, [.cancel])

        XCTAssertEqual(down(&f, space, flags: option).actions, [.press])
        XCTAssertEqual(down(&f, escape).actions, [.cancel], "按住期间 Esc 取消")
    }

    func testFnEscapeCancelsWhileHeld() {
        var f = filter(key: "fn")
        _ = f.handle(.flagsChanged(keyCode: 63, flags: fnFlag), acceptsCancelWhenInactive: false)
        XCTAssertEqual(down(&f, escape).actions, [.cancel])
    }

    func testModifierOnlyHotkeyBehaviourMatchesRecognizer() {
        var f = filter(key: "right_command")
        let rightCommandDown = f.handle(.flagsChanged(keyCode: 54, flags: command | 0x10), acceptsCancelWhenInactive: false)
        XCTAssertEqual(rightCommandDown.actions, [.press])
        XCTAssertEqual(down(&f, keyA, flags: command | 0x10).actions, [.gestureCancelled], "按住期间按别的键 = 组合快捷键")
        XCTAssertEqual(f.handle(.flagsChanged(keyCode: 54, flags: 0), acceptsCancelWhenInactive: false).actions, [],
                       "已作废的手势抬起不触发 release")

        let again = f.handle(.flagsChanged(keyCode: 54, flags: command | 0x10), acceptsCancelWhenInactive: false)
        XCTAssertEqual(again.actions, [.press])
        XCTAssertEqual(f.handle(.flagsChanged(keyCode: 54, flags: 0), acceptsCancelWhenInactive: false).actions, [.release])
    }

    func testModifierOnlyMouseDownInvalidatesGesture() {
        var f = filter(key: "right_option")
        _ = f.handle(.flagsChanged(keyCode: 61, flags: option | 0x40), acceptsCancelWhenInactive: false)
        XCTAssertEqual(f.handle(.mouseDown, acceptsCancelWhenInactive: false).actions, [.gestureCancelled])
    }

    func testModifierOnlyEscapeCancelsAndInvalidatesTheTap() {
        var f = filter(key: "right_command")
        _ = f.handle(.flagsChanged(keyCode: 54, flags: command | 0x10), acceptsCancelWhenInactive: false)
        XCTAssertEqual(down(&f, escape, flags: command | 0x10).actions, [.cancel])
        XCTAssertEqual(f.handle(.flagsChanged(keyCode: 54, flags: 0), acceptsCancelWhenInactive: false).actions, [],
                       "Esc 取消后松开修饰键不应再多发一个 release")
    }

    // MARK: - 吞键规则（R8-B1）

    func testComboPressRepeatAndReleaseAreConsumedAndPaired() {
        var f = filter(key: "space", modifiers: ["option"])
        let press = down(&f, space, flags: option)
        XCTAssertEqual(press, HotkeyEventFilter.Decision(actions: [.press], consume: true))

        let autoRepeat = down(&f, space, flags: option, isRepeat: true)
        XCTAssertEqual(autoRepeat, HotkeyEventFilter.Decision(actions: [], consume: true), "自动重复同样要吞")

        let release = up(&f, space)
        XCTAssertEqual(release, HotkeyEventFilter.Decision(actions: [.release], consume: true))

        // 配对结清：之后单独按空格是普通按键，必须放行。
        XCTAssertEqual(down(&f, space, flags: 0), HotkeyEventFilter.Decision())
        XCTAssertEqual(up(&f, space), HotkeyEventFilter.Decision(), "放行了 keyDown 就必须放行 keyUp")
    }

    func testReleasingModifierBeforeMainKeyStillPairs() {
        var f = filter(key: "space", modifiers: ["option"])
        XCTAssertTrue(down(&f, space, flags: option).consume)
        // 先松 ⌥（flagsChanged 永不吞），主键仍按住并自动重复：flags 已不含 ⌥，仍须吞。
        let flagsChange = f.handle(.flagsChanged(keyCode: 58, flags: 0), acceptsCancelWhenInactive: false)
        XCTAssertFalse(flagsChange.consume)
        XCTAssertEqual(down(&f, space, flags: 0, isRepeat: true).consume, true)
        let release = up(&f, space)
        XCTAssertEqual(release, HotkeyEventFilter.Decision(actions: [.release], consume: true))
    }

    func testNonMatchingModifiersPassThroughKeyDownAndKeyUp() {
        var f = filter(key: "space", modifiers: ["option"])
        XCTAssertEqual(down(&f, space, flags: 0), HotkeyEventFilter.Decision())
        XCTAssertEqual(down(&f, space, flags: option | shift), HotkeyEventFilter.Decision())
        XCTAssertEqual(up(&f, space), HotkeyEventFilter.Decision(), "没吞 keyDown 就不能吞 keyUp")
    }

    func testOtherKeysAndMouseAreNeverConsumed() {
        var f = filter(key: "space", modifiers: ["option"])
        XCTAssertEqual(down(&f, space, flags: option).consume, true)
        XCTAssertEqual(down(&f, keyA, flags: option), HotkeyEventFilter.Decision())
        XCTAssertEqual(up(&f, keyA), HotkeyEventFilter.Decision())
        XCTAssertEqual(f.handle(.mouseDown, acceptsCancelWhenInactive: false), HotkeyEventFilter.Decision())
    }

    func testEscapeIsConsumedOnlyWhenItCancels() {
        var f = filter(key: "space", modifiers: ["option"])
        // 不在听写：Esc 原样交给前台应用（退出全屏、关闭对话框…），keyUp 同样放行。
        XCTAssertEqual(down(&f, escape), HotkeyEventFilter.Decision())
        XCTAssertEqual(up(&f, escape), HotkeyEventFilter.Decision())

        // 识别阶段（取消窗口打开）：吞 keyDown / 自动重复 / keyUp。
        XCTAssertEqual(
            down(&f, escape, acceptsCancelWhenInactive: true),
            HotkeyEventFilter.Decision(actions: [.cancel], consume: true)
        )
        XCTAssertEqual(
            down(&f, escape, isRepeat: true),
            HotkeyEventFilter.Decision(actions: [], consume: true),
            "同一次物理按下的重复继续吞，即便取消窗口已被控制器关闭"
        )
        XCTAssertEqual(up(&f, escape), HotkeyEventFilter.Decision(actions: [], consume: true))
        // 配对结清，下一次 Esc 又是普通按键。
        XCTAssertEqual(down(&f, escape), HotkeyEventFilter.Decision())
        XCTAssertEqual(up(&f, escape), HotkeyEventFilter.Decision())
    }

    func testEscapeCancelDuringComboHoldIsConsumed() {
        var f = filter(key: "space", modifiers: ["option"])
        _ = down(&f, space, flags: option)
        XCTAssertEqual(down(&f, escape), HotkeyEventFilter.Decision(actions: [.cancel], consume: true))
        XCTAssertEqual(up(&f, escape).consume, true)
        // 主键仍按着：松开时照常产生 release 并吞掉。
        XCTAssertEqual(up(&f, space), HotkeyEventFilter.Decision(actions: [.release], consume: true))
    }

    func testFlagsChangedIsNeverConsumed() {
        var fn = filter(key: "fn")
        XCTAssertEqual(
            fn.handle(.flagsChanged(keyCode: 63, flags: fnFlag), acceptsCancelWhenInactive: false),
            HotkeyEventFilter.Decision(actions: [.press], consume: false)
        )
        XCTAssertEqual(
            fn.handle(.flagsChanged(keyCode: 63, flags: 0), acceptsCancelWhenInactive: false),
            HotkeyEventFilter.Decision(actions: [.release], consume: false)
        )

        var modifier = filter(key: "right_command")
        XCTAssertEqual(
            modifier.handle(.flagsChanged(keyCode: 54, flags: command | 0x10), acceptsCancelWhenInactive: false).consume,
            false
        )
        XCTAssertEqual(
            modifier.handle(.flagsChanged(keyCode: 54, flags: 0), acceptsCancelWhenInactive: false).consume,
            false
        )
    }

    func testModifierOnlyHotkeyNeverConsumesOrdinaryKeys() {
        var f = filter(key: "right_command")
        _ = f.handle(.flagsChanged(keyCode: 54, flags: command | 0x10), acceptsCancelWhenInactive: false)
        // ⌘C：手势作废，但 C 是用户的普通快捷键，必须放行。
        XCTAssertEqual(
            down(&f, Int64(kVK_ANSI_C), flags: command | 0x10),
            HotkeyEventFilter.Decision(actions: [.gestureCancelled], consume: false)
        )
        XCTAssertEqual(up(&f, Int64(kVK_ANSI_C)), HotkeyEventFilter.Decision())
    }

    func testFnHotkeyDoesNotConsumeKeys() {
        var f = filter(key: "fn")
        _ = f.handle(.flagsChanged(keyCode: 63, flags: fnFlag), acceptsCancelWhenInactive: false)
        XCTAssertEqual(down(&f, keyA, flags: fnFlag), HotkeyEventFilter.Decision())
        XCTAssertEqual(up(&f, keyA), HotkeyEventFilter.Decision())
    }

    func testEscapeWithModifierOnlyHotkeyIsConsumedAndStillPairs() {
        var f = filter(key: "right_command")
        _ = f.handle(.flagsChanged(keyCode: 54, flags: command | 0x10), acceptsCancelWhenInactive: false)
        XCTAssertEqual(down(&f, escape, flags: command | 0x10), HotkeyEventFilter.Decision(actions: [.cancel], consume: true))
        XCTAssertEqual(up(&f, escape).consume, true)
        XCTAssertEqual(
            f.handle(.flagsChanged(keyCode: 54, flags: 0), acceptsCancelWhenInactive: false),
            HotkeyEventFilter.Decision(),
            "flagsChanged 不吞；取消后松开修饰键不再产生 release"
        )
    }

    /// keyUp 丢失（tap 被系统短暂禁用等）后，下一次非重复的 keyDown 必须按新的按下处理，
    /// 不能被陈旧的「已吞」记录永久当成重复。
    func testStaleConsumedKeyIsRecoveredByNextFreshKeyDown() {
        var f = filter(key: "space", modifiers: ["option"])
        XCTAssertTrue(down(&f, escape, acceptsCancelWhenInactive: true).consume) // 取消，keyUp 随后丢失
        // 取消窗口早已关闭；用户之后再按 Esc（非重复）应当正常放行给前台应用。
        XCTAssertEqual(down(&f, escape), HotkeyEventFilter.Decision())
        XCTAssertEqual(up(&f, escape), HotkeyEventFilter.Decision())
    }

    // MARK: - 卡键复位（R8-B3）

    func testResetClearsActiveStateSoNextPressIsFresh() {
        var f = filter(key: "space", modifiers: ["option"])
        XCTAssertEqual(down(&f, space, flags: option).actions, [.press])
        f.reset()
        XCTAssertEqual(up(&f, space).actions, [], "复位后没有激活态，不再产生 release")
        XCTAssertEqual(down(&f, space, flags: option).actions, [.press], "下一次按下是全新的一次")

        var fn = filter(key: "fn")
        _ = fn.handle(.flagsChanged(keyCode: 63, flags: fnFlag), acceptsCancelWhenInactive: false)
        fn.reset()
        XCTAssertEqual(fn.handle(.flagsChanged(keyCode: 63, flags: fnFlag), acceptsCancelWhenInactive: false).actions, [.press])
    }

    func testResetReleasesModifierRecognizer() {
        var f = filter(key: "right_command")
        _ = f.handle(.flagsChanged(keyCode: 54, flags: command | 0x10), acceptsCancelWhenInactive: false)
        f.reset()
        XCTAssertEqual(
            f.handle(.flagsChanged(keyCode: 54, flags: command | 0x10), acceptsCancelWhenInactive: false).actions,
            [.press], "复位后识别器回到空闲，新的按下重新开始"
        )
    }
}
