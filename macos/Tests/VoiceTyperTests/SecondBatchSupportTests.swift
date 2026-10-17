import XCTest
@testable import VoiceTyper

/// 第二批的纯逻辑：下载进度详情、录音提示、重新授权引导、卡键物理状态、AX 回读判定。
final class SecondBatchSupportTests: XCTestCase {
    // MARK: - R8-C5 DownloadStats

    func testSpeedIsNilWithoutOneSecondOfDataOrProgress() {
        var stats = DownloadStats(totalBytes: 100_000_000)
        XCTAssertNil(stats.bytesPerSecond)
        stats.record(progress: 0.1, at: 100)
        stats.record(progress: 0.2, at: 100.5)
        XCTAssertNil(stats.bytesPerSecond, "跨度不足 1s")
        var stalled = DownloadStats(totalBytes: 100_000_000)
        stalled.record(progress: 0.3, at: 0)
        stalled.record(progress: 0.3, at: 3)
        XCTAssertNil(stalled.bytesPerSecond, "没有进展")
    }

    func testSpeedUsesOnlyTheLastEightSeconds() {
        var stats = DownloadStats(totalBytes: 100_000_000)
        stats.record(progress: 0.0, at: 0)
        stats.record(progress: 0.5, at: 1) // 很快的开头，之后会滑出窗口
        stats.record(progress: 0.5, at: 10)
        stats.record(progress: 0.6, at: 12) // 窗口 [4,12]：从 0.5@10 到 0.6@12
        let speed = try? XCTUnwrap(stats.bytesPerSecond)
        XCTAssertEqual(speed ?? 0, 5_000_000, accuracy: 1)
    }

    // MARK: - R8-C4 RecordingHint

    @MainActor
    // MARK: - R8-H1 ReauthorizationNotice

    func testReauthorizationShownOnlyWhenVersionChangedPermissionsLostAndAdHoc() {
        func show(
            last: String? = "3.5.1", current: String = "3.5.2", was: Bool = true, now: Bool = false, adHoc: Bool = true
        ) -> Bool {
            ReauthorizationNotice.shouldShow(
                lastVersion: last, currentVersion: current,
                wereAllGranted: was, nowAllGranted: now, mayLosePermissionsOnUpdate: adHoc
            )
        }
        XCTAssertTrue(show())
        XCTAssertFalse(show(last: nil), "首次安装")
        XCTAssertFalse(show(last: "3.5.2"), "版本没变")
        XCTAssertFalse(show(was: false), "上次本来就没给齐")
        XCTAssertFalse(show(now: true), "这次权限仍然齐全")
        XCTAssertFalse(show(adHoc: false), "Developer ID 签名不显示")
    }

    // MARK: - R8-B3 HotkeyPhysicalState

    func testFnPhysicalState() {
        let fn = HotkeyConfig(modifiers: [], key: "fn", mode: .hold)
        XCTAssertTrue(HotkeyPhysicalState.isHeld(fn, flags: 0x800000))
        XCTAssertFalse(HotkeyPhysicalState.isHeld(fn, flags: 0))
    }

    func testModifierOnlyPhysicalStateUsesDeviceBit() {
        let rightCommand = HotkeyConfig(modifiers: [], key: "right_command", mode: .hold)
        XCTAssertTrue(HotkeyPhysicalState.isHeld(rightCommand, flags: 0x100000 | 0x10))
        XCTAssertFalse(HotkeyPhysicalState.isHeld(rightCommand, flags: 0x100000 | 0x08), "左 ⌘ 不算")
        XCTAssertFalse(HotkeyPhysicalState.isHeld(rightCommand, flags: 0))
    }

    func testComboPhysicalStateChecksModifiersNotMainKey() {
        let combo = HotkeyConfig(modifiers: ["option", "control"], key: "d", mode: .hold)
        XCTAssertTrue(HotkeyPhysicalState.isHeld(combo, flags: 0x80000 | 0x40000))
        XCTAssertFalse(HotkeyPhysicalState.isHeld(combo, flags: 0x80000), "缺 ⌃")
        XCTAssertFalse(HotkeyPhysicalState.isHeld(combo, flags: 0))
    }

    // MARK: - R8-A3a 回读判定

}
