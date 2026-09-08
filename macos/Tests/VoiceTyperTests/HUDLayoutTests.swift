import AppKit
import XCTest
@testable import VoiceTyper

/// HUD 里两段纯计算逻辑：预览文本的首部截断，以及录音状态行的设备名拼接。
///
/// 值得测的理由：`tailFitting` 决定了长听写时用户看到的是自己**刚说完**的话还是
/// 一段话的开头。方向反了不会崩溃、不会报错，只会让预览悄悄变得没用——
/// 这类"静默退化"正是最需要钉子的地方。
@MainActor
final class HUDLayoutTests: XCTestCase {
    private let font = NSFont.systemFont(ofSize: 14, weight: .medium)

    // MARK: - 预览文本截断

    func testShortTextIsKeptWholeAndOccupiesOneLine() {
        let text = "今天下午三点开会"
        let fitted = RecordingHUDController.tailFitting(
            text, width: 600, maximumLines: 2, font: font
        )
        XCTAssertEqual(fitted.text, text)
        XCTAssertEqual(fitted.lineCount, 1)
    }

    func testEmptyTextIsPassedThrough() {
        let fitted = RecordingHUDController.tailFitting("", width: 600, maximumLines: 2, font: font)
        XCTAssertEqual(fitted.text, "")
        XCTAssertEqual(fitted.lineCount, 1)
    }

    /// 核心不变量：超长文本必须保留**结尾**（最新说出的内容），并以省略号开头。
    func testOverlongTextKeepsTailAndPrefixesEllipsis() {
        let text = String(repeating: "语音输入测试文本", count: 60) + "最后这几个字必须留下"
        let fitted = RecordingHUDController.tailFitting(
            text, width: 300, maximumLines: 2, font: font
        )

        XCTAssertTrue(fitted.text.hasPrefix("…"), "截断后的文本应以省略号开头，表明前面被截掉了")
        XCTAssertTrue(fitted.text.hasSuffix("最后这几个字必须留下"), "必须保留结尾，而不是开头")
        XCTAssertLessThan(fitted.text.count, text.count)
        XCTAssertLessThanOrEqual(fitted.lineCount, 2)

        // 去掉省略号后，剩下的必须是原文的一个后缀——不能凭空改写内容。
        let withoutEllipsis = String(fitted.text.dropFirst())
        XCTAssertTrue(text.hasSuffix(withoutEllipsis))
    }

    func testNarrowerWidthKeepsLessText() {
        let text = String(repeating: "测试", count: 200)
        let wide = RecordingHUDController.tailFitting(text, width: 600, maximumLines: 2, font: font)
        let narrow = RecordingHUDController.tailFitting(text, width: 200, maximumLines: 2, font: font)
        XCTAssertLessThan(narrow.text.count, wide.text.count)
    }

    func testMoreLinesKeepMoreText() {
        let text = String(repeating: "测试", count: 200)
        let oneLine = RecordingHUDController.tailFitting(text, width: 300, maximumLines: 1, font: font)
        let twoLines = RecordingHUDController.tailFitting(text, width: 300, maximumLines: 2, font: font)
        XCTAssertGreaterThan(twoLines.text.count, oneLine.text.count)
        XCTAssertEqual(oneLine.lineCount, 1)
    }

    func testDegenerateWidthDoesNotHang() {
        let fitted = RecordingHUDController.tailFitting("一些文字", width: 0, maximumLines: 2, font: font)
        XCTAssertEqual(fitted.text, "一些文字", "宽度非法时原样返回，不做任何截断")
    }

    // MARK: - 录音状态行

    func testRecordingStatusWithoutDeviceNameStaysPlain() {
        XCTAssertEqual(RecordingHUDController.recordingStatus(inputDeviceName: nil), "录音中")
        XCTAssertEqual(RecordingHUDController.recordingStatus(inputDeviceName: "   "), "录音中")
    }

    func testRecordingStatusIncludesDeviceName() {
        XCTAssertEqual(
            RecordingHUDController.recordingStatus(inputDeviceName: "MacBook 麦克风"),
            "录音中 · MacBook 麦克风"
        )
    }

    /// 设备名可以很长（"Jabra Evolve2 65 立体声耳机"），不能让它把右侧计时挤出可视区。
    func testLongDeviceNameIsTruncated() {
        let status = RecordingHUDController.recordingStatus(
            inputDeviceName: "Jabra Evolve2 65 Stereo Headset Microphone"
        )
        XCTAssertTrue(status.hasPrefix("录音中 · "))
        XCTAssertTrue(status.hasSuffix("…"))
        XCTAssertLessThan(status.count, 25)
    }
}
