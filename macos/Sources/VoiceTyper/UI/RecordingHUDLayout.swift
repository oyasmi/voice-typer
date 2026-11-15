import AppKit
import Foundation

/// `RecordingHUDController` 的纯排版度量、宽度档位、文本分行与状态格式化逻辑（S-6）。
/// 与窗口状态机和动画拆分后，几何与文本计算保持无副作用纯函数，供 `HUDLayoutTests` 直接钉死。
extension RecordingHUDController {
    // MARK: - 几何与字体常量

    /// 宽度策略：浮窗是顺带瞥一眼的辅助信息，尺寸变化要少、跳得干脆、只增不减。
    /// - 起步宽度 400（约 25 个汉字）；状态行（设备名 + 操作提示）实测更宽时放宽，至多 460。
    static let compactWidthRange: ClosedRange<CGFloat> = 400...460
    /// - 预览一行放不下时直接跳到最宽 600（约 40 个汉字一行，中文舒适行长的上限），之后换行。
    static let maximumHudWidth: CGFloat = 600
    static let maximumScreenWidthRatio: CGFloat = 0.5
    static let screenMargin: CGFloat = 16
    /// 纵向排版：顶部内边距 + 状态行 + 状态行与预览的间距 + 预览 N 行 + 底部内边距。
    static let topInset: CGFloat = 10
    static let topRowHeight: CGFloat = 28
    static let previewTopSpacing: CGFloat = 6
    /// 比 `topInset` 略大：状态行文字在 28pt 行内垂直居中，视觉上的上边距约 16pt，底部与之相称。
    static let bottomInset: CGFloat = 14
    static let compactHeight: CGFloat = topInset + topRowHeight + topInset
    static let cornerRadius: CGFloat = 24
    /// 预览最多显示的行数（超过三行时按整行滚动，见 `lastLines`）。
    static let previewMaximumLines = 3
    /// 行数减少后要持续这么久才收矮：识别修正常让预览短暂少一行，立刻收矮会一跳一跳。变高则立即生效。
    static let lineShrinkDelay: TimeInterval = 0.6
    static let previewFont = NSFont.systemFont(ofSize: 14, weight: .medium)
    static let statusFont = NSFont.systemFont(ofSize: 13, weight: .semibold)
    static let timeFont = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium)
    /// `NSTextField` 绘制文字时左右各留 2pt 行片段内边距，实际换行宽度比标签窄 4pt。
    static let labelTextPadding: CGFloat = 4
    /// 内容区左右内缩，与 buildUI 里 topRow / previewClipView 的约束常量保持一致。
    static let contentHorizontalInset: CGFloat = 16
    /// 状态行各元素的尺寸，buildUI 与 `compactWidth` 共用。
    static let indicatorSize: CGFloat = 16
    static let waveformWidth: CGFloat = 44
    static let topRowSpacing: CGFloat = 10
    /// 计时位预留的宽度样本：录音上限 120 秒，最长显示为「2:00」。
    static let timeWidthSample = "0:00"
    /// 底部落点距屏幕可见区下沿的距离（避开 Dock）。
    static let screenBottomOffset: CGFloat = 80
    /// 跟随光标时浮窗与光标之间留出的间距。
    static let cursorGap: CGFloat = 24
    /// 跟随插入点时，浮窗与插入点之间留出的间距。
    static let caretGap: CGFloat = 8
    static let leadingEllipsis = "…"
    /// 状态行里输入设备名的显示上限，超出截断。
    static let inputDeviceNameLimit = 14
    /// 状态行带操作提示（「再按 X 结束」「Esc 取消」）时设备名收紧上限（R8-C4）。
    static let inputDeviceNameLimitWithHint = 8
    /// 状态行常规色（次级灰白）。flashWarning 期间临时升为警示色。
    static let statusTextColor: NSColor = .secondaryLabelColor
    static let timeTextColor: NSColor = .tertiaryLabelColor
    /// 录音剩余不足这么多秒时，计时位置改为橙色倒计时（R8-C3）。
    static let countdownWindowSeconds = 15
    /// 预览尾部变化的淡显时长（R8-D3）。
    static let previewHighlightDuration: TimeInterval = 0.3

    /// 预览字体的单行高度。
    static let previewLineHeight = lineHeight(of: previewFont)
    /// 预览区上沿距窗口顶边的距离。
    static let previewTopOffset: CGFloat = topInset + topRowHeight + previewTopSpacing

    /// 展开态窗口高度：预览区正好容纳 `lineCount` 行，下方只留 `bottomInset`。
    static func expandedHeight(lineCount: Int) -> CGFloat {
        previewTopOffset + previewHeight(lineCount: lineCount) + bottomInset
    }

    static func previewHeight(lineCount: Int) -> CGFloat {
        previewLineHeight * CGFloat(max(1, min(previewMaximumLines, lineCount)))
    }

    // MARK: - 预览分行与前缀比较

    /// 按浮窗宽度排预览：扣掉左右内缩与标签自身的行片段内边距，得到标签真正的换行宽度。
    static func fitPreview(_ display: String, hudWidth: CGFloat) -> (text: String, lineCount: Int) {
        let wrapWidth = max(40, hudWidth - contentHorizontalInset * 2) - labelTextPadding
        return lastLines(display, width: wrapWidth, maximumLines: previewMaximumLines, font: previewFont)
    }

    /// 两段文本的公共前缀长度（按 Character 计）。
    static func commonPrefixLength(_ a: String, _ b: String) -> Int {
        var count = 0
        var left = a.makeIterator()
        var right = b.makeIterator()
        while let x = left.next(), let y = right.next(), x == y { count += 1 }
        return count
    }

    /// 把「与上一次预览分叉的位置」换算成排版后文本里的下标（R8-D3）。
    static func highlightStart(divergence: Int, displayCount: Int, fittedCount: Int) -> Int {
        let dropped = max(0, displayCount - fittedCount)
        return min(max(divergence - dropped, 0), fittedCount)
    }

    /// 把 `text` 从头按词换行排进 `width`，超过 `maximumLines` 行时只返回最后 `maximumLines` 行
    /// （原文的一个后缀，从某一行的行首开始），以及实际占用的行数。
    static func lastLines(
        _ text: String,
        width: CGFloat,
        maximumLines: Int,
        font: NSFont
    ) -> (text: String, lineCount: Int) {
        guard width > 0, maximumLines >= 1, !text.isEmpty else { return (text, 1) }
        let lineStarts = lineStartOffsets(text, width: width, font: font)
        guard lineStarts.count > maximumLines else { return (text, max(1, lineStarts.count)) }
        let start = lineStarts[lineStarts.count - maximumLines]
        return ((text as NSString).substring(from: start), maximumLines)
    }

    /// 每一行行首在 `text` 里的 UTF-16 偏移。换行规则与预览标签一致（按词换行、无行片段内边距的宽度）。
    static func lineStartOffsets(_ text: String, width: CGFloat, font: NSFont) -> [Int] {
        let storage = NSTextStorage(string: text, attributes: [.font: font])
        let container = NSTextContainer(size: CGSize(width: width, height: .greatestFiniteMagnitude))
        container.lineFragmentPadding = 0
        let layoutManager = NSLayoutManager()
        layoutManager.addTextContainer(container)
        storage.addLayoutManager(layoutManager)
        var starts: [Int] = []
        layoutManager.enumerateLineFragments(forGlyphRange: layoutManager.glyphRange(for: container)) {
            _, _, _, glyphRange, _ in
            starts.append(layoutManager.characterIndexForGlyph(at: glyphRange.location))
        }
        return starts
    }

    /// 单行文字的排版高度（14pt 系统字体为 17pt），与标签实际绘制的行距一致。
    static func lineHeight(of font: NSFont) -> CGFloat {
        let height = ("字" as NSString).boundingRect(
            with: CGSize(width: CGFloat.greatestFiniteMagnitude, height: .greatestFiniteMagnitude),
            options: [.usesLineFragmentOrigin, .usesFontLeading],
            attributes: [.font: font]
        ).height
        return max(ceil(height), 1)
    }

    // MARK: - 宽度档位与文案格式化

    /// 紧凑态宽度：状态行（指示点、波形、状态文字、计时）实测宽度，夹在 `compactWidthRange` 内。
    static func compactWidth(status: String) -> CGFloat {
        func labelWidth(_ text: String, _ font: NSFont) -> CGFloat {
            ceil((text as NSString).size(withAttributes: [.font: font]).width) + labelTextPadding
        }
        let content = indicatorSize + waveformWidth + topRowSpacing * 4
            + labelWidth(status, statusFont) + labelWidth(timeWidthSample, timeFont)
        let total = content + contentHorizontalInset * 2
        return min(max(total, compactWidthRange.lowerBound), compactWidthRange.upperBound)
    }

    /// 让 `text` 恰好排成一行所需的浮窗宽度（未做上限夹逼）。
    static func singleLineHudWidth(for text: String) -> CGFloat {
        let textWidth = ceil((text as NSString).size(withAttributes: [.font: previewFont]).width)
        return textWidth + labelTextPadding + contentHorizontalInset * 2
    }

    /// 浮窗该用的宽度档位：预览单行放得进紧凑宽度就用紧凑宽度，否则直接用最宽档（之后换行）。
    static func targetWidth(status: String, preview: String) -> CGFloat {
        let compact = compactWidth(status: status)
        let trimmed = preview.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, singleLineHudWidth(for: trimmed) > compact else { return compact }
        return maximumHudWidth
    }

    /// 录音状态行文案（含输入设备名与可选操作提示）。
    static func recordingStatus(inputDeviceName: String?, hint: String? = nil) -> String {
        let rawHint = hint?.trimmingCharacters(in: .whitespacesAndNewlines)
        let effectiveHint = (rawHint?.isEmpty == false) ? rawHint : nil
        guard let name = inputDeviceName?.trimmingCharacters(in: .whitespacesAndNewlines),
              !name.isEmpty else {
            return effectiveHint.map { "\(L("录音中")) · \($0)" } ?? L("录音中")
        }
        let limit = effectiveHint == nil ? Self.inputDeviceNameLimit : Self.inputDeviceNameLimitWithHint
        let shortened = name.count > limit
            ? String(name.prefix(limit)) + Self.leadingEllipsis
            : name
        let base = LF("录音中 · %@", shortened)
        return effectiveHint.map { "\(base) · \($0)" } ?? base
    }

    /// 预览标签的排版配置。单独抽出来，让测试能按同一配置验证「算出的行数 = 实际绘制的行数」。
    static func configurePreviewLabel(_ label: NSTextField) {
        label.font = previewFont
        label.textColor = .labelColor
        label.alignment = .left
        label.lineBreakMode = .byWordWrapping
        label.maximumNumberOfLines = previewMaximumLines
        label.cell?.usesSingleLineMode = false
        label.cell?.wraps = true
        label.cell?.isScrollable = false
        label.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
    }

    /// 距单段录音上限还剩多少秒；剩余多于 15 秒或已到上限返回 nil（R8-C3）。
    static func countdownSeconds(
        elapsed: TimeInterval, maximumSeconds: Int = LocalASRSession.maxSessionSeconds
    ) -> Int? {
        let remaining = Int(ceil(Double(maximumSeconds) - elapsed))
        return (1...countdownWindowSeconds).contains(remaining) ? remaining : nil
    }

    static func timeString(_ elapsed: Int) -> String {
        if elapsed < 1 { return "" }
        if elapsed < 60 { return "\(elapsed)s" }
        return String(format: "%d:%02d", elapsed / 60, elapsed % 60)
    }
}
