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

    func testDegenerateWidthDoesNotHang() {
        let fitted = RecordingHUDController.tailFitting("一些文字", width: 0, maximumLines: 2, font: font)
        XCTAssertEqual(fitted.text, "一些文字", "宽度非法时原样返回，不做任何截断")
    }

    // MARK: - 预览排版与实际绘制一致

    /// 用与浮窗相同配置的标签量出 `text` 在预览区里实际占的高度。
    /// 不限行数：这样多折出来的一行能被量到，而不是被 `maximumNumberOfLines` 悄悄裁掉。
    private func renderedHeight(_ text: String, hudWidth: CGFloat) -> CGFloat {
        let label = NSTextField(labelWithString: "")
        RecordingHUDController.configurePreviewLabel(label)
        label.maximumNumberOfLines = 0
        label.stringValue = text
        let clipWidth = hudWidth - RecordingHUDController.contentHorizontalInset * 2
        return label.cell!.cellSize(forBounds: NSRect(x: 0, y: 0, width: clipWidth, height: 10_000)).height
    }

    /// 核心不变量：算出几行，标签就画几行。算少了最后一行会被预览区裁掉（看不到刚说的话），
    /// 算多了预览下方留空——后者正是此前「浮窗下面空着两行」的根源之一。
    func testFittedLineCountMatchesRenderedLines() {
        let base = "这是一段 mixed 中英文 VoiceTyper 预览，用来测量换行是否一致。Hello, world! 1234 "
        let source = String(repeating: base, count: 6)
        let lineHeight = RecordingHUDController.previewLineHeight
        for hudWidth in [RecordingHUDController.defaultHudWidth, RecordingHUDController.maximumHudWidth] {
            for length in stride(from: 1, through: source.count, by: 3) {
                let text = String(source.prefix(length))
                let fitted = RecordingHUDController.fitPreview(text, hudWidth: hudWidth)
                let rendered = renderedHeight(fitted.text, hudWidth: hudWidth)
                XCTAssertEqual(
                    rendered, lineHeight * CGFloat(fitted.lineCount), accuracy: 0.5,
                    "宽 \(hudWidth)、\(length) 字：算作 \(fitted.lineCount) 行，实际画了 \(rendered / lineHeight) 行"
                )
                XCTAssertLessThanOrEqual(fitted.lineCount, RecordingHUDController.previewMaximumLines)
            }
        }
    }

    /// 浮窗随文字加宽时，给出的宽度必须真的容得下一行——差 4pt 行片段内边距就会折到看不见的第二行。
    func testPreferredWidthKeepsTextOnOneLine() {
        for text in ["今天下午三点开会", "我们把这个需求排进下周的迭代里，周三之前给出设计稿 and review"] {
            let width = RecordingHUDController.preferredHudWidth(for: text)
            XCTAssertEqual(RecordingHUDController.fitPreview(text, hudWidth: width).lineCount, 1)
            XCTAssertEqual(renderedHeight(text, hudWidth: width), RecordingHUDController.previewLineHeight, accuracy: 0.5)
        }
    }

    /// 窗口高度只比预览多出固定的上下边距：每多一行正好长一行高，预览下方的留白不足一行。
    func testExpandedHeightLeavesNoEmptyLineBelowPreview() {
        let lineHeight = RecordingHUDController.previewLineHeight
        for lines in 1...RecordingHUDController.previewMaximumLines {
            let height = RecordingHUDController.expandedHeight(lineCount: lines)
            let below = height - RecordingHUDController.previewTopOffset
                - RecordingHUDController.previewHeight(lineCount: lines)
            XCTAssertEqual(RecordingHUDController.previewHeight(lineCount: lines), lineHeight * CGFloat(lines))
            XCTAssertLessThan(below, lineHeight)
            if lines > 1 {
                XCTAssertEqual(height - RecordingHUDController.expandedHeight(lineCount: lines - 1), lineHeight)
            }
        }
    }

    // MARK: - 录音状态行

    // MARK: - R8-C3：上限倒计时

    // MARK: - R8-D3：预览尾部淡显

}
