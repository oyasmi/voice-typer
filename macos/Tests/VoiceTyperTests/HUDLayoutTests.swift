import AppKit
import XCTest
@testable import VoiceTyper

/// HUD 的排版与尺寸：预览只显示最后几行、按整行滚动，宽度按档位只增不减，高度按行数推导。
///
/// 值得测的理由：`lastLines` 决定了长听写时用户看到的是自己**刚说完**的话还是
/// 一段话的开头。方向反了不会崩溃、不会报错，只会让预览悄悄变得没用——
/// 这类"静默退化"正是最需要钉子的地方。
@MainActor
final class HUDLayoutTests: XCTestCase {
    private let font = NSFont.systemFont(ofSize: 14, weight: .medium)

    // MARK: - 预览只显示最后几行

    func testShortTextIsKeptWholeAndOccupiesOneLine() {
        let text = "今天下午三点开会"
        let fitted = RecordingHUDController.lastLines(text, width: 600, maximumLines: 3, font: font)
        XCTAssertEqual(fitted.text, text)
        XCTAssertEqual(fitted.lineCount, 1)
    }

    /// 核心不变量：超长文本必须保留**结尾**（最新说出的内容），显示的是原文的后缀，正好占满行数上限。
    func testOverlongTextKeepsTailAsWholeLines() {
        let text = String(repeating: "语音输入测试文本", count: 60) + "最后这几个字必须留下"
        let fitted = RecordingHUDController.lastLines(text, width: 300, maximumLines: 3, font: font)

        XCTAssertTrue(fitted.text.hasSuffix("最后这几个字必须留下"), "必须保留结尾，而不是开头")
        XCTAssertTrue(text.hasSuffix(fitted.text), "显示的必须是原文的后缀——不能凭空改写内容")
        XCTAssertLessThan(fitted.text.count, text.count)
        XCTAssertEqual(fitted.lineCount, 3)
        XCTAssertEqual(
            RecordingHUDController.lineStartOffsets(fitted.text, width: 300, font: font).count, 3,
            "显示的部分从某一行的行首开始，自己排版也正好三行"
        )
    }

    /// 按整行滚动：文字逐字增长时，显示窗口的起点只会跳到原先某一行的行首，不会逐字往前爬；
    /// 起点不变期间，已经显示的行一字不动。
    func testOverflowScrollsByWholeLines() {
        let source = String(repeating: "我们下周和 backend 一起 review 一下接口的设计，", count: 12)
        let width: CGFloat = 300
        var previousStart = 0
        var previousText = ""
        var scrolled = false
        for length in 1...source.count {
            let text = String(source.prefix(length))
            let fitted = RecordingHUDController.lastLines(text, width: width, maximumLines: 3, font: font)
            let start = (text as NSString).length - (fitted.text as NSString).length
            XCTAssertGreaterThanOrEqual(start, previousStart, "\(length) 字：显示窗口不能往回退")
            if start == previousStart {
                // 起点没动：上一次显示的首行（已经完整的那一行）原样保留。
                let previousStarts = RecordingHUDController.lineStartOffsets(previousText, width: width, font: font)
                if previousStarts.count > 1 {
                    let firstLine = (previousText as NSString).substring(to: previousStarts[1])
                    XCTAssertTrue(fitted.text.hasPrefix(firstLine), "\(length) 字：已显示的首行不应变化")
                }
            } else {
                scrolled = true
                let previousLineStarts = RecordingHUDController.lineStartOffsets(
                    String(source.prefix(length - 1)), width: width, font: font
                )
                XCTAssertTrue(previousLineStarts.contains(start), "\(length) 字：新起点必须是原先某一行的行首")
            }
            previousStart = start
            previousText = fitted.text
        }
        XCTAssertTrue(scrolled, "样本应长到触发滚动")
    }

    func testDegenerateWidthDoesNotHang() {
        let fitted = RecordingHUDController.lastLines("一些文字", width: 0, maximumLines: 3, font: font)
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
    /// 算多了预览下方留空。排版用的是 `NSLayoutManager`，绘制用的是标签，两者的断行必须一致。
    func testFittedLineCountMatchesRenderedLines() {
        let base = "这是一段 mixed 中英文 VoiceTyper 预览，用来测量换行是否一致。Hello, world! 1234 "
        let source = String(repeating: base, count: 6)
        let lineHeight = RecordingHUDController.previewLineHeight
        for hudWidth in [RecordingHUDController.compactWidthRange.lowerBound, RecordingHUDController.maximumHudWidth] {
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

    /// 单行宽度必须真的容得下一行——差 4pt 行片段内边距就会折到看不见的第二行。
    func testSingleLineWidthKeepsTextOnOneLine() {
        for text in ["今天下午三点开会", "我们把这个需求排进下周的迭代里，周三之前给出设计稿 and review"] {
            let width = RecordingHUDController.singleLineHudWidth(for: text)
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

    // MARK: - 宽度档位

    func testWidthStepsFollowContent() {
        let range = RecordingHUDController.compactWidthRange
        let status = RecordingHUDController.recordingStatus(inputDeviceName: "MacBook Pro 麦克风")
        let compact = RecordingHUDController.compactWidth(status: status)
        XCTAssertEqual(compact, range.lowerBound, "常见状态行用起步宽度")

        let longStatus = RecordingHUDController.recordingStatus(
            inputDeviceName: "Jabra Evolve2 65 立体声耳机", hint: "再按 Right Command 结束"
        )
        XCTAssertTrue(range.contains(RecordingHUDController.compactWidth(status: longStatus)), "状态行再长也不超出紧凑范围")

        XCTAssertEqual(RecordingHUDController.targetWidth(status: status, preview: ""), compact)
        XCTAssertEqual(RecordingHUDController.targetWidth(status: status, preview: "好的"), compact)
        XCTAssertEqual(
            RecordingHUDController.targetWidth(status: status, preview: String(repeating: "一句较长的话", count: 6)),
            RecordingHUDController.maximumHudWidth,
            "起步宽度放不下一行就直接跳到最宽，不经过中间档"
        )
    }

    /// 真实浮窗：一次听写内宽度只增不减；行数减少时延迟收矮。
    func testWidthOnlyGrowsAndLineShrinkIsDeferred() throws {
        let hud = RecordingHUDController(config: UIConfig())
        defer { hud.hideHUD() }
        hud.showHUD(inputDeviceName: "MacBook Pro 麦克风")
        let window = try XCTUnwrap(hud.window)
        spin(0.4)
        let compactWidth = window.frame.width

        hud.showPreview(String(repeating: "我们把这个需求排进下周的迭代里，", count: 6))
        spin(0.5)
        let wideWidth = window.frame.width
        let tallHeight = window.frame.height
        XCTAssertGreaterThan(wideWidth, compactWidth)

        hud.showPreview("好的")
        spin(0.3)
        XCTAssertEqual(window.frame.width, wideWidth, "预览变短时宽度不回缩")
        XCTAssertEqual(window.frame.height, tallHeight, "行数刚减少时先不收矮")

        spin(0.8)
        XCTAssertLessThan(window.frame.height, tallHeight, "持续变少后收矮")
        XCTAssertEqual(window.frame.width, wideWidth)
    }

    private func spin(_ seconds: TimeInterval) {
        RunLoop.main.run(until: Date().addingTimeInterval(seconds))
    }
}
