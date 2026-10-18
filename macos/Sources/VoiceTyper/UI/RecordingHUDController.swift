import AppKit
import Foundation
import QuartzCore

@MainActor
final class RecordingHUDController: NSWindowController {
    // MARK: - 几何常量

    /// 宽度策略：浮窗是顺带瞥一眼的辅助信息，尺寸变化要少、跳得干脆、只增不减。
    /// - 起步宽度 400（约 25 个汉字）；状态行（设备名 + 操作提示）实测更宽时放宽，至多 460。
    static let compactWidthRange: ClosedRange<CGFloat> = 400...460
    /// - 预览一行放不下时直接跳到最宽 600（约 40 个汉字一行，中文舒适行长的上限），之后换行。
    ///   只有两档：中间档比起步只宽几个字，多跳一次换不来什么。
    /// - 一次显示期间只增不减（`widthFloor`）：预览被修正变短时不回缩，整个过程最多跳一次。
    ///   浮窗从中心向两边变宽而文字左对齐，每跳一次文字整体左移一下，逐字跟随时会一直在挪。
    static let maximumHudWidth: CGFloat = 600
    private static let maximumScreenWidthRatio: CGFloat = 0.5
    private static let screenMargin: CGFloat = 16
    /// 纵向排版：顶部内边距 + 状态行 + 状态行与预览的间距 + 预览 N 行 + 底部内边距。
    /// 高度一律由这几段与预览字体的行高推出，不再手写总高——此前手写的 100 / 122 在预览下方
    /// 留出了约两行的空白。
    private static let topInset: CGFloat = 10
    private static let topRowHeight: CGFloat = 28
    private static let previewTopSpacing: CGFloat = 6
    /// 比 `topInset` 略大：状态行文字在 28pt 行内垂直居中，视觉上的上边距约 16pt，底部与之相称。
    private static let bottomInset: CGFloat = 14
    private static let compactHeight: CGFloat = topInset + topRowHeight + topInset
    private static let cornerRadius: CGFloat = 24
    /// 预览最多显示的行数。
    ///
    /// 一行 + 前导省略号意味着长句只剩尾巴，而"边说边校对识别结果"正是 HUD 存在的理由。
    /// 宽度收窄到 `maximumHudWidth` 后用三行补回容量（约 120 个汉字，连续说 30 秒左右），
    /// 不会更多地遮挡用户正在工作的窗口。超过三行时按整行滚动：第四行出现的同时第一行整行消失，
    /// 其余各行的断行位置不变（见 `lastLines`）。
    static let previewMaximumLines = 3
    /// 行数减少后要持续这么久才收矮：识别修正常让预览短暂少一行，立刻收矮会一跳一跳。变高则立即生效。
    static let lineShrinkDelay: TimeInterval = 0.6
    static let previewFont = NSFont.systemFont(ofSize: 14, weight: .medium)
    static let statusFont = NSFont.systemFont(ofSize: 13, weight: .semibold)
    static let timeFont = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium)
    /// `NSTextField` 绘制文字时左右各留 2pt 行片段内边距，实际换行宽度比标签窄 4pt。
    /// 测量（`fitPreview`、`singleLineHudWidth`、`compactWidth`）必须算上它，否则算作一行的文字会折到看不见的下一行。
    static let labelTextPadding: CGFloat = 4
    /// 内容区左右内缩，与 buildUI 里 topRow / previewClipView 的约束常量保持一致。
    static let contentHorizontalInset: CGFloat = 16
    /// 状态行各元素的尺寸，buildUI 与 `compactWidth` 共用。
    private static let indicatorSize: CGFloat = 16
    private static let waveformWidth: CGFloat = 44
    private static let topRowSpacing: CGFloat = 10
    /// 计时位预留的宽度样本：录音上限 120 秒，最长显示为「2:00」。
    private static let timeWidthSample = "0:00"
    /// 底部落点距屏幕可见区下沿的距离（避开 Dock）。
    private static let screenBottomOffset: CGFloat = 80
    /// 跟随光标时浮窗与光标之间留出的间距。
    private static let cursorGap: CGFloat = 24
    private static let leadingEllipsis = "…"
    /// 状态行里输入设备名的显示上限，超出截断——设备名可以很长
    /// （"Jabra Evolve2 65 立体声耳机"），不能让它把计时挤出可视区。
    private static let inputDeviceNameLimit = 14
    /// 状态行带操作提示（「再按 X 结束」「Esc 取消」）时设备名收紧到这么多字符，免得把计时挤出可视区（R8-C4）。
    private static let inputDeviceNameLimitWithHint = 8

    // MARK: - UI 元素

    private let dotView = PulseDotView()
    private let glyphView = NSImageView()
    private let waveformView = WaveformView()
    private let statusLabel = NSTextField(labelWithString: L("录音中"))
    private let timeLabel = NSTextField(labelWithString: "")
    private let previewLabel = NSTextField(labelWithString: "")
    private let previewClipView = NSView()
    private let effectView = NSVisualEffectView()
    private let dimView = NSView()

    /// HUD 当前阶段。取代原先用 `startDate == nil` 间接推断状态的写法。
    private enum Phase {
        case hidden
        case recording
        case recognizing
        case transient  // 成功 / 错误 / 已取消等一次性提示
    }

    private var timer: Timer?
    private var startDate: Date?
    private var phase: Phase = .hidden
    private var hasBuiltUI = false
    private var hudOpacity: Double = 0.85

    /// 状态行常规色（次级灰白，见 buildUI 的层级注释）。flashWarning 期间临时升为警示色。
    private static let statusTextColor: NSColor = .secondaryLabelColor

    /// 展开态（显示 preview 行）与否。
    private var isExpanded = false
    /// 展开态当前应有的窗口高度（随预览行数变化）。
    private var currentExpandedHeight: CGFloat {
        Self.expandedHeight(lineCount: previewLineCount)
    }

    /// 预览字体的单行高度。
    static let previewLineHeight = lineHeight(of: previewFont)

    /// 展开态窗口高度：预览区正好容纳 `lineCount` 行，下方只留 `bottomInset`。
    static func expandedHeight(lineCount: Int) -> CGFloat {
        previewTopOffset + previewHeight(lineCount: lineCount) + bottomInset
    }

    /// 预览区上沿距窗口顶边的距离。
    static let previewTopOffset: CGFloat = topInset + topRowHeight + previewTopSpacing

    static func previewHeight(lineCount: Int) -> CGFloat {
        previewLineHeight * CGFloat(max(1, min(previewMaximumLines, lineCount)))
    }

    /// 当前 HUD 宽度。随预览内容按档位增长到上限，避免文本继续把窗口推到屏幕外。
    private var currentHudWidth: CGFloat = compactWidthRange.lowerBound
    /// 本次显示期间已经到过的宽度：宽度只增不减。浮窗隐藏或开始新一次听写时归零。
    private var widthFloor: CGFloat = 0
    /// 行数减少后的延迟收矮（见 `lineShrinkDelay`）。
    private var lineShrinkWorkItem: DispatchWorkItem?
    private var pendingShrinkLineCount = 1
    /// 当前窗口底边左下角锚点（展开/收起时保持底边不动，向上生长）。
    private var anchorOrigin: CGPoint = .zero
    /// 收起防抖：preview 短暂清空时延迟收起，避免抖动。
    private var collapseWorkItem: DispatchWorkItem?
    /// 一次性提示的自动隐藏任务，phase 变化时作废。
    private var transientHideItem: DispatchWorkItem?
    /// `flashWarning` 的 statusLabel 恢复任务，phase 变化时作废。
    private var warningRestoreWorkItem: DispatchWorkItem?
    private var lastLevelUpdate: CFTimeInterval = 0
    /// 正处于「校对中」：警告闪现结束后据此恢复流光。
    private var isCorrecting = false
    /// 只对前景（topRow / previewClipView）做「呼气」缩放；磨砂层是它们的兄弟视图，
    /// 对 `NSVisualEffectView` 做 transform 会破坏磨砂渲染，所以绝不动它。
    private let topRow = NSStackView()

    /// 浮窗落点。`.hidden` 时只保留错误类提示，见 `HUDPosition.hidden` 的说明。
    private var hudPosition: HUDPosition = .bottomCenter
    /// 本次听写（或一次性提示）期间固定的落屏与插入点（R8-E2 / R8-E3）：只在浮窗出现时计算一次，
    /// 听写过程中不随焦点变化而跳动，也避免每次预览刷新都做一次跨进程 AX 查询。
    private var anchorScreen: NSScreen?
    private var caretAnchor: CGRect?
    private var anchorsResolved = false
    /// 跟随插入点时，浮窗与插入点之间留出的间距。
    private static let caretGap: CGFloat = 8
    /// 预览尾部变化的淡显时长（R8-D3）。
    static let previewHighlightDuration: TimeInterval = 0.3
    private var previewHighlightWorkItem: DispatchWorkItem?
    /// 上一次送达的预览原文，用于求公共前缀（R8-D3）。
    private var previousPreviewText = ""
    /// 预览当前占几行（1…`previewMaximumLines`），决定窗口高度与预览区高度。
    private var previewLineCount = 1
    private var previewHeightConstraint: NSLayoutConstraint?
    /// 录音期状态行的基准文案（含输入设备名）。警告闪现结束后要恢复成它。
    private var recordingStatusText = L("录音中")
    /// 识别 / 校对阶段状态行的基准文案。
    private var recognizingStatusText = L("识别中")

    // MARK: - 初始化

    convenience init(config: UIConfig) {
        let rect = NSRect(
            x: 0, y: 0,
            width: RecordingHUDController.compactWidthRange.lowerBound, height: RecordingHUDController.compactHeight
        )
        let panel = NSPanel(
            contentRect: rect,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        self.init(window: panel)
        self.hudOpacity = config.opacity

        panel.isFloatingPanel = true
        panel.becomesKeyOnlyIfNeeded = true
        panel.hidesOnDeactivate = false
        panel.isOpaque = false
        panel.hasShadow = true
        panel.level = .floating
        panel.backgroundColor = .clear
        panel.isMovableByWindowBackground = false
        panel.ignoresMouseEvents = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.appearance = NSAppearance(named: .vibrantDark)
        ensureUIBuilt()
    }

    // MARK: - 公共接口

    /// `.hidden` 下要跳过的"过程类"展示（录音、预览、识别、成功、取消）。
    /// 错误与"没有识别到内容"不受影响——那些信息在别处拿不到，一并静音就成了静默失败。
    private var suppressesProgressHUD: Bool { hudPosition == .hidden }

    /// - Parameter inputDeviceName: 本次录音实际使用的输入设备名（由调用方传入，
    ///   可能与系统默认输入不同——见 `AudioInputPolicy`）。
    /// - Parameter hint: 状态行末尾的操作提示（R8-C4），见 `RecordingHint`。
    func showHUD(inputDeviceName: String?, hint: String? = nil) {
        guard !suppressesProgressHUD else { return }
        ensureUIBuilt()
        cancelTransientHide()
        cancelCollapse()
        cancelWarningRestore()
        endCorrectingShimmer()
        cancelPreviewHighlight()
        previousPreviewText = ""
        // 新的一次听写：重新确定落在哪块屏、哪个位置（焦点窗口可能已经换了）。
        refreshAnchors()

        phase = .recording
        startDate = Date()
        isExpanded = false

        recordingStatusText = Self.recordingStatus(inputDeviceName: inputDeviceName, hint: hint)
        setStatus(recordingStatusText)
        // 新的一次听写从起步宽度重新开始，不继承上一次显示到过的宽度。
        widthFloor = 0
        currentHudWidth = Self.compactWidth(status: recordingStatusText)
        resetTimeLabel()
        setPreviewLineCount(1, animated: false)
        previewLabel.stringValue = displayText(for: "")
        previewLabel.alphaValue = 0

        showGlyph(false)
        dotView.isHidden = false
        dotView.startPulse()
        waveformView.isHidden = false
        waveformView.startListening()

        present(height: Self.compactHeight)
        startTimer()
    }

    func hideHUD() {
        timer?.invalidate()
        timer = nil
        cancelPreviewHighlight()
        previousPreviewText = ""
        clearAnchors()
        cancelCollapse()
        cancelTransientHide()
        cancelWarningRestore()
        cancelLineShrink()
        endCorrectingShimmer()
        phase = .hidden
        widthFloor = 0
        dotView.stopPulse()
        waveformView.stop()
        dismiss()
    }

    /// 显示流式 partial 文本。本地识别引擎给出的是**全量**预览，
    /// 这里整体替换而非追加。有内容时展开 HUD，短暂清空时防抖收起。
    func showPreview(_ text: String) {
        guard !suppressesProgressHUD else { return }
        let hasText = !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        if hasText {
            cancelCollapse()
            // 先定宽再排文：能容纳多少字取决于最终宽度，顺序反了会按旧宽度截出错误的片段。
            updateHudWidth(for: text, animated: true)
            let fitted = layoutPreviewText(displayText(for: text))
            applyPreviewHighlight(fitted: fitted, display: displayText(for: text), text: text)
            previousPreviewText = text
            setExpanded(true)
        } else {
            // 宽高都不动，只等防抖收起：短暂清空后很快又有字时，浮窗不必先缩一下再长回来。
            cancelPreviewHighlight()
            previousPreviewText = ""
            previewLabel.stringValue = displayText(for: "")
            scheduleCollapse()
        }
    }

    /// 切换到"识别中"状态（松键后等待 final）。
    func setRecognizing() {
        guard !suppressesProgressHUD else { return }
        cancelTransientHide()
        cancelWarningRestore()
        endCorrectingShimmer()
        // 松键后录音结束，冻结计时（保留最后时长）。上限倒计时的橙色「还剩 N 秒」不应带进识别阶段。
        timer?.invalidate()
        timer = nil
        if let startDate {
            timeLabel.stringValue = Self.timeString(Int(Date().timeIntervalSince(startDate)))
            timeLabel.textColor = Self.timeTextColor
        }
        phase = .recognizing
        recognizingStatusText = L("识别中")
        setStatus(recognizingStatusText)
        showGlyph(false)
        dotView.isHidden = false
        dotView.stopPulse()
        dotView.setStatic(color: .systemOrange)
        waveformView.isHidden = false
        waveformView.settleThenPulse()
        // 录音结束的"呼气"：前景内容轻微收缩再回弹，给"收声"一个触感。
        for view in [topRow, previewClipView] {
            if let exhale = HUDMotion.exhaleAnimation(bounds: view.bounds) {
                view.layer?.add(exhale, forKey: "hudExhale")
            }
        }
    }

    /// 本地识别已出结果，正在等 LLM 校对。
    ///
    /// 与"识别中"分开的理由：本地推理是几百毫秒且完全可控，而校对要走网络、最长可以等到
    /// `llm.timeout`。两者都显示"识别中"的话，用户既不知道该不该继续等，也无从判断慢在哪。
    ///
    /// - Parameter hotkeyDisplay: 当前热键的显示名；提供时状态行提示「再按 %@ 跳过」（R8-C2）。
    func setCorrecting(hotkeyDisplay: String? = nil) {
        guard !suppressesProgressHUD else { return }
        guard phase == .recognizing else { return }
        cancelWarningRestore()
        if let hotkeyDisplay, !hotkeyDisplay.isEmpty {
            recognizingStatusText = LF("校对中… · 再按 %@ 跳过", hotkeyDisplay)
        } else {
            recognizingStatusText = L("校对中…")
        }
        setStatus(recognizingStatusText)
        widenForStatus()
        dotView.setStatic(color: .systemBlue)
        isCorrecting = true
        startShimmer()
    }

    /// 引擎还在加载而用户在等（R8-A1）：只改状态行文案，警告闪现结束后恢复为它。
    func setEngineWait(_ stage: EngineWaitStage) {
        guard !suppressesProgressHUD else { return }
        switch stage {
        case .recording:
            guard phase == .recording else { return }
            recordingStatusText = L("录音中 · 模型加载中…")
            if warningRestoreWorkItem == nil { setStatus(recordingStatusText) }
        case .finalizing:
            guard phase == .recognizing, !isCorrecting else { return }
            recognizingStatusText = L("识别中 · 正在加载模型…")
            if warningRestoreWorkItem == nil { setStatus(recognizingStatusText) }
        }
    }

    /// 实时音量电平（0…1 量级），驱动波形。仅录音阶段生效，内部节流。
    func updateLevel(_ level: Float) {
        guard phase == .recording else { return }
        let now = CACurrentMediaTime()
        guard now - lastLevelUpdate >= 0.03 else { return }
        lastLevelUpdate = now
        waveformView.updateLevel(level)
    }

    /// final 文本插入成功后的一次性反馈（绿色对钩），约 0.7s 后淡出。
    ///
    /// 带 `note`（校对回落 / 被跳过，R8-C1/C2）时改为「已输入 · 未校对」并多停留一会儿，下一行写原因。
    /// 浮窗设为「不显示」时，只有配置类问题（`showsWhenHUDHidden`）才仍然提示。
    func showSuccess(note: VoiceTyperController.InsertionNote? = nil) {
        if let note {
            guard !suppressesProgressHUD || note.showsWhenHUDHidden else { return }
            showTransient(
                status: L("已输入 · 未校对"),
                message: note.message,
                glyph: "checkmark.circle.fill",
                color: .systemOrange,
                expanded: true,
                autoHideAfter: 2.5
            )
            return
        }
        guard !suppressesProgressHUD else { return }
        showTransient(
            status: L("已输入"),
            message: "",
            glyph: "checkmark.circle.fill",
            color: .systemGreen,
            expanded: false,
            autoHideAfter: 0.7
        )
    }

    /// 一次性错误浮层，约 2.5s 后自动隐藏。
    func showError(_ message: String) {
        showTransient(
            status: L("错误"),
            message: message.isEmpty ? L("服务异常") : message,
            glyph: "exclamationmark.circle.fill",
            color: .systemRed,
            expanded: true,
            autoHideAfter: 2.5
        )
    }

    /// 模型就绪等「用户需要知道、但不属于听写过程」的提示（R8-C5）。不受「不显示」影响。
    func showNotice(_ status: String, autoHideAfter: TimeInterval = 3.0) {
        guard phase != .recording, phase != .recognizing else { return }
        showTransient(
            status: status,
            message: "",
            glyph: "checkmark.circle.fill",
            color: .systemGreen,
            expanded: false,
            autoHideAfter: autoHideAfter
        )
    }

    /// 焦点在密码输入框，已拒绝开始听写（R8-B2）。解释类信息，即使浮窗设为「不显示」也提示。
    func showSecureFieldRefusal() {
        showTransient(
            status: L("无法听写"),
            message: L("当前是密码输入框，VoiceTyper 不会在这里听写"),
            glyph: "lock.fill",
            color: .systemOrange,
            expanded: true,
            autoHideAfter: 2.0
        )
    }

    /// 用户从「最近结果」菜单复制后的确认（R8-A2）。用户主动操作，不受「不显示」影响；
    /// 但听写进行中不打断过程类浮窗。
    func showCopiedToClipboard() {
        guard phase != .recording, phase != .recognizing else { return }
        showTransient(
            status: L("已复制到剪贴板"),
            message: "",
            glyph: "doc.on.clipboard.fill",
            color: .systemGreen,
            expanded: false,
            autoHideAfter: 0.9
        )
    }

    /// 识别跑通但结果为空的一次性提示。与"错误"分开：这不是故障，而是没采到语音，
    /// 用户需要的是"去检查麦克风/输入设备"这条具体线索，而不是一个红色叉（B4）。
    func showNoSpeech() {
        showTransient(
            status: L("没有识别到内容"),
            message: L("没听到说话内容，请确认麦克风未静音、输入设备选择正确。"),
            glyph: "waveform.slash",
            color: .systemOrange,
            expanded: true,
            autoHideAfter: 2.5
        )
    }

    /// "已取消"提示浮层，约 1.0s 后自动隐藏（用户按 Esc 取消录音）。
    func showCanceled() {
        guard !suppressesProgressHUD else { return }
        showTransient(
            status: L("已取消"),
            message: "",
            glyph: "xmark.circle.fill",
            color: NSColor(white: 0.7, alpha: 1),
            expanded: false,
            autoHideAfter: 1.0
        )
    }

    /// 录音过程中的非致命提示（如 partial 暂时不可用）。仅闪烁 statusLabel。
    /// 录音中或识别中的非致命提示（如 partial 暂时不可用、上一段听写尚未完成）。
    /// 两个阶段都要能看到，否则识别中按热键会得到完全无反馈的"死键"观感（R4-06）。
    /// 警告是状态行上唯一必须被注意的内容：闪现期临时升为警示色，恢复时回落。
    func flashWarning(_ message: String) {
        guard phase == .recording || phase == .recognizing else { return }
        cancelWarningRestore()
        // 警示色文字不应被流光遮罩；isCorrecting 保持不变，恢复时按它重新启动。
        stopShimmer()
        statusLabel.stringValue = message.isEmpty ? L("识别提示") : message
        statusLabel.textColor = .systemOrange
        let item = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.restoreStatusLabel()
        }
        warningRestoreWorkItem = item
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2, execute: item)
    }

    /// 写状态行文案并回落常规色。警告闪现之外的所有状态切换都应走这里，
    /// 保证被取消的警告不会把警示色带进下一个状态。
    private func setStatus(_ text: String) {
        statusLabel.stringValue = text
        statusLabel.textColor = Self.statusTextColor
    }

    /// 警告闪现结束后按当前阶段恢复状态行文案与颜色。
    private func restoreStatusLabel() {
        switch phase {
        case .recording:
            setStatus(recordingStatusText)
        case .recognizing:
            setStatus(recognizingStatusText)
            if isCorrecting { startShimmer() }
        case .hidden, .transient:
            statusLabel.textColor = Self.statusTextColor
        }
    }

    // MARK: - 「校对中」流光

    private func startShimmer() {
        stopShimmer()
        guard let mask = HUDMotion.shimmerMask(for: statusLabel.bounds) else { return }
        statusLabel.layer?.mask = mask
    }

    /// 必须把 mask 清空，否则文字会一直保持半透明。
    private func stopShimmer() {
        statusLabel.layer?.mask = nil
    }

    /// 离开「校对中」的所有路径都走这里。
    private func endCorrectingShimmer() {
        isCorrecting = false
        stopShimmer()
    }

    /// 状态行尺寸随文案与布局变化时，同步 mask 的 frame（mask 小于文字会把文字裁掉）。
    private func syncShimmerFrame() {
        guard let mask = statusLabel.layer?.mask else { return }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        mask.frame = statusLabel.bounds
        CATransaction.commit()
    }

    /// 应用已保存的 UI 配置。
    ///
    /// 存在的意义：此前配置保存走的是"整个 `RecordingHUDController` 重建一份"，于是每次
    /// 改热键（与 HUD 毫无关系）都会丢弃当前实例、连带丢掉已构建的视图层与几何状态（B8）。
    /// HUD 是长生命周期的单例式组件，配置变更就地生效即可。
    func updateConfig(_ config: UIConfig) {
        ensureUIBuilt()
        hudOpacity = config.opacity
        if hudPosition != config.hudPosition { anchorsResolved = false }
        hudPosition = config.hudPosition
        applyDimOpacity()
        // 切到"不显示"时，正在显示的过程类浮窗要立刻收掉，而不是等这次听写结束。
        if suppressesProgressHUD, phase == .recording || phase == .recognizing {
            hideHUD()
        }
    }

    /// 临时以给定不透明度预览 HUD 背景（设置页透明度滑杆用）。
    func previewOpacity(_ opacity: Double) {
        ensureUIBuilt()
        hudOpacity = opacity
        applyDimOpacity()

        // 位置设为"不显示"时不该为了预览透明度反而把浮窗弹出来（设置页也会同步禁用滑杆）。
        guard !suppressesProgressHUD else { return }

        // 真实会话进行中：只实时改透明度，不打断。
        switch phase {
        case .recording, .recognizing:
            return
        case .hidden, .transient:
            break
        }

        phase = .transient
        setStatus(L("背景预览"))
        resetTimeLabel()
        previewLabel.alphaValue = 0
        showGlyph(false)
        dotView.isHidden = false
        dotView.stopPulse()
        dotView.setStatic(color: .systemGreen)
        waveformView.isHidden = true

        if window?.isVisible != true {
            isExpanded = false
            widthFloor = 0
            currentHudWidth = Self.compactWidth(status: statusLabel.stringValue)
            present(height: Self.compactHeight)
        } else {
            updateHudWidth(for: "", animated: true)
        }

        // 每次拖动都重排自动隐藏，避免拖动过程中 HUD 提前消失。
        cancelTransientHide()
        let item = DispatchWorkItem { [weak self] in
            guard let self, self.phase == .transient else { return }
            self.hideHUD()
        }
        transientHideItem = item
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0, execute: item)
    }

    // MARK: - 预览排版

    /// 切换预览区占几行，并把窗口高度跟着调整。变高立即生效；录音 / 识别中展开态下变矮要等
    /// `lineShrinkDelay`，期间行数又回升则作废。
    private func setPreviewLineCount(_ lines: Int, animated: Bool) {
        let clamped = max(1, min(Self.previewMaximumLines, lines))
        let defersShrink = clamped < previewLineCount && isExpanded
            && (phase == .recording || phase == .recognizing)
        guard defersShrink else {
            cancelLineShrink()
            applyPreviewLineCount(clamped, animated: animated)
            return
        }
        pendingShrinkLineCount = clamped
        // 从第一次变少开始计时，后续更新只改目标行数，不重新计时。
        guard lineShrinkWorkItem == nil else { return }
        let item = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.lineShrinkWorkItem = nil
            self.applyPreviewLineCount(self.pendingShrinkLineCount, animated: true)
        }
        lineShrinkWorkItem = item
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.lineShrinkDelay, execute: item)
    }

    private func applyPreviewLineCount(_ lines: Int, animated: Bool) {
        guard lines != previewLineCount else { return }
        previewLineCount = lines
        previewHeightConstraint?.constant = Self.previewHeight(lineCount: lines)
        applyExpandedHeight(animated: animated)
    }

    private func cancelLineShrink() {
        lineShrinkWorkItem?.cancel()
        lineShrinkWorkItem = nil
    }

    /// 展开态下把窗口高度对齐到 `currentExpandedHeight`（底边不动，向上生长）。
    private func applyExpandedHeight(animated: Bool) {
        guard isExpanded, let window, window.isVisible else { return }
        let height = currentExpandedHeight
        let metrics = layoutMetrics(for: currentHudWidth, height: height)
        anchorOrigin = metrics.origin
        currentHudWidth = metrics.width
        let target = NSRect(x: anchorOrigin.x, y: anchorOrigin.y, width: currentHudWidth, height: height)
        guard animated else {
            window.setFrame(target, display: true)
            window.invalidateShadow()
            return
        }
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.16
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            window.animator().setFrame(target, display: true)
        } completionHandler: { [weak self] in
            MainActor.assumeIsolated { self?.window?.invalidateShadow() }
        }
    }

    /// 把一段已经准备好的展示文本排进当前宽度：超出上限行数时只显示最后几行
    /// （保留最新说出的内容），并据此决定预览区占几行。
    @discardableResult
    private func layoutPreviewText(_ display: String) -> String {
        let fitted = Self.fitPreview(display, hudWidth: currentHudWidth)
        previewLabel.stringValue = fitted.text
        setPreviewLineCount(fitted.lineCount, animated: true)
        return fitted.text
    }

    /// 按浮窗宽度排预览：扣掉左右内缩与标签自身的行片段内边距，得到标签真正的换行宽度。
    static func fitPreview(_ display: String, hudWidth: CGFloat) -> (text: String, lineCount: Int) {
        let wrapWidth = max(40, hudWidth - contentHorizontalInset * 2) - labelTextPadding
        return lastLines(display, width: wrapWidth, maximumLines: previewMaximumLines, font: previewFont)
    }

    // MARK: - 预览尾部淡显（R8-D3）

    /// 两段文本的公共前缀长度（按 Character 计）。
    static func commonPrefixLength(_ a: String, _ b: String) -> Int {
        var count = 0
        var left = a.makeIterator()
        var right = b.makeIterator()
        while let x = left.next(), let y = right.next(), x == y { count += 1 }
        return count
    }

    /// 把「与上一次预览分叉的位置」换算成排版后文本里的下标。
    ///
    /// 排版（`lastLines`）超过行数上限时只保留最后几行，显示的是原文的后缀：被滚出去的字符数是
    /// `displayCount - fittedCount`，分叉点要相应平移；分叉点落在滚出去的部分里则整段都是新内容。
    static func highlightStart(divergence: Int, displayCount: Int, fittedCount: Int) -> Int {
        let dropped = max(0, displayCount - fittedCount)
        return min(max(divergence - dropped, 0), fittedCount)
    }

    /// 预览与上一次的公共前缀之后的部分以次级色显示约 300ms 再变为常规色。
    /// 「减少动态效果」下不做；第一次出字（没有可比较的上一次）与没有变化时也不做。
    private func applyPreviewHighlight(fitted: String, display: String, text: String) {
        cancelPreviewHighlight()
        guard !HUDMotion.reduceMotion, !previousPreviewText.isEmpty, text != previousPreviewText else { return }
        let divergence = Self.commonPrefixLength(previousPreviewText, text)
        let start = Self.highlightStart(divergence: divergence, displayCount: display.count, fittedCount: fitted.count)
        guard start < fitted.count else { return }

        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = previewLabel.alignment
        paragraph.lineBreakMode = .byWordWrapping
        let font = previewLabel.font ?? Self.previewFont
        let attributed = NSMutableAttributedString(string: fitted, attributes: [
            .font: font, .foregroundColor: NSColor.labelColor, .paragraphStyle: paragraph,
        ])
        let characters = Array(fitted)
        let prefixLength = String(characters.prefix(start)).utf16.count
        attributed.addAttribute(
            .foregroundColor, value: NSColor.secondaryLabelColor,
            range: NSRange(location: prefixLength, length: attributed.length - prefixLength)
        )
        previewLabel.attributedStringValue = attributed

        let item = DispatchWorkItem { [weak self] in
            guard let self else { return }
            // 重新设置纯文本：回到标签自身的字体与常规色。
            self.previewLabel.stringValue = fitted
            self.previewHighlightWorkItem = nil
        }
        previewHighlightWorkItem = item
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.previewHighlightDuration, execute: item)
    }

    private func cancelPreviewHighlight() {
        previewHighlightWorkItem?.cancel()
        previewHighlightWorkItem = nil
    }

    /// 把 `text` 从头按词换行排进 `width`，超过 `maximumLines` 行时只返回最后 `maximumLines` 行
    /// （原文的一个后缀，从某一行的行首开始），以及实际占用的行数。
    ///
    /// 为什么从头排、按整行滚动，而不是逐字丢掉开头再加 `…`：逐字丢开头时，每来一个字所有行的断行
    /// 位置都会变，整段文字像蛇一样往前爬，眼睛无处落脚。从头排版时已经显示过的行断行不变，
    /// 第四行出现的同时第一行整行消失，其余各行只是整体上移一行。
    ///
    /// 不交给 `NSTextField` 的 `.byTruncatingHead`：AppKit 只保证对单个行片段做首部截断，多行时
    /// 没有明确契约，万一退化成截尾部，用户看到的会是长听写的开头而不是刚说完的话。
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

    /// 录音状态行文案。带上当前输入设备名，让"录到的是哪个麦克风"这件事不必靠猜——
    /// 这是静音提示触发后用户最需要的下一条线索。
    static func recordingStatus(inputDeviceName: String?, hint: String? = nil) -> String {
        let hint = hint?.trimmingCharacters(in: .whitespacesAndNewlines).nilIfEmpty
        guard let name = inputDeviceName?.trimmingCharacters(in: .whitespacesAndNewlines),
              !name.isEmpty else {
            return hint.map { "\(L("录音中")) · \($0)" } ?? L("录音中")
        }
        let limit = hint == nil ? Self.inputDeviceNameLimit : Self.inputDeviceNameLimitWithHint
        let shortened = name.count > limit
            ? String(name.prefix(limit)) + Self.leadingEllipsis
            : name
        let base = LF("录音中 · %@", shortened)
        return hint.map { "\(base) · \($0)" } ?? base
    }

    // MARK: - 一次性提示

    private func showTransient(
        status: String,
        message: String,
        glyph: String,
        color: NSColor,
        expanded: Bool,
        autoHideAfter: TimeInterval
    ) {
        ensureUIBuilt()
        timer?.invalidate()
        timer = nil
        startDate = nil
        cancelCollapse()
        cancelWarningRestore()
        cancelTransientHide()
        cancelPreviewHighlight()
        endCorrectingShimmer()
        phase = .transient
        // 紧接在听写后面的提示（「已输入」等）沿用当前宽度，不先缩一下；单独出现时从紧凑宽度起步。
        if window?.isVisible != true { widthFloor = 0 }

        setStatus(status)
        resetTimeLabel()
        previewLabel.alphaValue = expanded ? 1 : 0

        dotView.isHidden = true
        dotView.stopPulse()
        waveformView.isHidden = true
        waveformView.stop()
        setGlyph(symbol: glyph, color: color)
        showGlyph(true)
        // 结果出现的轻微弹性：图标弹入，状态文字从下方 2pt 处上移到位。
        // 图标固定 16×16（见 buildUI 的 indicator 约束），首次显示时视图尚未布局，不能读 bounds。
        glyphView.layer?.add(
            HUDMotion.popInAnimation(bounds: CGRect(x: 0, y: 0, width: 16, height: 16)), forKey: "hudPopIn"
        )
        statusLabel.layer?.add(HUDMotion.textRiseAnimation(), forKey: "hudTextRise")
        if expanded {
            updateHudWidth(for: message, animated: window?.isVisible == true)
            // 错误文案可能不短（"目标窗口已变化，结果已复制到剪贴板"），走与预览同一套
            // 排版，需要时占两行，而不是被截成半句看不懂的话。
            layoutPreviewText(message)
        } else {
            previewLabel.stringValue = message
            setPreviewLineCount(1, animated: false)
            updateHudWidth(for: "", animated: window?.isVisible == true)
        }

        if window?.isVisible == true {
            setExpanded(expanded)
        } else {
            isExpanded = expanded
            present(height: expanded ? currentExpandedHeight : Self.compactHeight)
        }

        let item = DispatchWorkItem { [weak self] in
            guard let self, self.phase == .transient else { return }
            self.hideHUD()
        }
        transientHideItem = item
        DispatchQueue.main.asyncAfter(deadline: .now() + autoHideAfter, execute: item)
    }

    // MARK: - 窗口显示 / 定位 / 动画

    /// 浮窗出现时确定一次落屏与插入点（R8-E2 / R8-E3）：
    /// - 「跟随输入位置」：取得到可信的插入点就落在插入点所在屏；取不到回落鼠标所在屏与鼠标位置。
    /// - 其他位置：落在前台应用焦点窗口所在屏；取不到回落鼠标所在屏。
    /// 一次性计算并缓存到本次听写结束。
    private func refreshAnchors() {
        anchorScreen = nil
        caretAnchor = nil
        anchorsResolved = true
        let screens = NSScreen.screens
        let frames = screens.map(\.frame)
        if hudPosition == .nearCursor {
            if let caret = FocusGeometry.caretRect(),
               let index = FocusGeometry.screenIndex(for: caret, screenFrames: frames) {
                caretAnchor = caret
                anchorScreen = screens[index]
            }
            return
        }
        if let window = FocusGeometry.focusedWindowFrame(),
           let index = FocusGeometry.screenIndex(for: window, screenFrames: frames) {
            anchorScreen = screens[index]
        }
    }

    private func clearAnchors() {
        anchorScreen = nil
        caretAnchor = nil
        anchorsResolved = false
    }

    /// 目标屏幕：缓存的焦点窗口 / 插入点所在屏，回退鼠标所在屏，再回退主屏。
    private func targetScreen() -> NSScreen? {
        if let anchorScreen, NSScreen.screens.contains(anchorScreen) { return anchorScreen }
        let mouse = NSEvent.mouseLocation
        return NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) }
            ?? NSScreen.main
            ?? NSScreen.screens.first
    }

    /// 计算指定尺寸下的窗口宽度与底边左下角锚点，确保 HUD 不越出可见屏幕。
    /// - Parameter height: 本次要摆放的窗口高度。跟随光标时需要它才能把浮窗放到光标下方。
    private func layoutMetrics(for requestedWidth: CGFloat, height: CGFloat) -> (origin: CGPoint, width: CGFloat) {
        guard let screen = targetScreen() else {
            return (CGPoint(x: 0, y: 0), min(requestedWidth, Self.maximumHudWidth))
        }
        let visible = screen.visibleFrame
        let availableWidth = max(180, visible.width - Self.screenMargin * 2)
        let maximumWidth = min(Self.maximumHudWidth, visible.width * Self.maximumScreenWidthRatio, availableWidth)
        // 曾经这里把默认宽度写成了上限，HUD 宽度永远等于上限，按内容选宽度的结果被完全忽略（R3-11）。
        // 档位由 `targetWidth(status:preview:)` 决定，这里只做屏幕夹逼（窄屏受屏幕宽度一半约束）。
        let width = min(requestedWidth, maximumWidth)

        let desiredOrigin: CGPoint
        switch hudPosition {
        case .bottomCenter, .hidden:
            // `.hidden` 只保留错误类提示，那些仍按默认位置摆放。
            desiredOrigin = CGPoint(x: visible.midX - width / 2, y: visible.minY + Self.screenBottomOffset)
        case .bottomRight:
            desiredOrigin = CGPoint(
                x: visible.maxX - Self.screenMargin - width,
                y: visible.minY + Self.screenBottomOffset
            )
        case .nearCursor:
            if let caretAnchor {
                // 放在插入点**下方** 8pt（上方会挡住正在输入的行）；下方放不下翻到上方。
                desiredOrigin = FocusGeometry.originNearCaret(
                    caret: caretAnchor, size: CGSize(width: width, height: height),
                    visible: visible, gap: Self.caretGap, margin: Self.screenMargin
                )
            } else {
                // 取不到输入位置：跟随鼠标，放在鼠标下方。贴近屏幕下沿时下面的夹逼会把它顶回可见区。
                let mouse = NSEvent.mouseLocation
                desiredOrigin = CGPoint(x: mouse.x - width / 2, y: mouse.y - Self.cursorGap - height)
            }
        }

        let minX = visible.minX + Self.screenMargin
        let maxX = max(minX, visible.maxX - Self.screenMargin - width)
        let minY = visible.minY + Self.screenMargin
        let maxY = max(minY, visible.maxY - Self.screenMargin - height)
        return (
            CGPoint(
                x: min(max(desiredOrigin.x, minX), maxX),
                y: min(max(desiredOrigin.y, minY), maxY)
            ),
            width
        )
    }

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
    /// 未做「只增不减」与屏幕夹逼，那两步在 `updateHudWidth` / `layoutMetrics`。
    static func targetWidth(status: String, preview: String) -> CGFloat {
        let compact = compactWidth(status: status)
        let trimmed = preview.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, singleLineHudWidth(for: trimmed) > compact else { return compact }
        return maximumHudWidth
    }

    /// 状态行文案变长（如「校对中… · 再按 X 跳过」）时按需加宽，同样只增不减。
    private func widenForStatus() {
        guard window?.isVisible == true else { return }
        updateHudWidth(for: previousPreviewText, animated: true)
    }

    private func updateHudWidth(for text: String, animated: Bool) {
        let height = isExpanded ? currentExpandedHeight : Self.compactHeight
        let requested = max(Self.targetWidth(status: statusLabel.stringValue, preview: text), widthFloor)
        let metrics = layoutMetrics(for: requested, height: height)
        currentHudWidth = metrics.width
        widthFloor = metrics.width
        anchorOrigin = metrics.origin

        guard let window, window.isVisible else { return }
        let target = NSRect(x: anchorOrigin.x, y: anchorOrigin.y, width: currentHudWidth, height: height)
        // 档位没变时每次预览刷新都会走到这里，不必重放动画。
        guard window.frame != target else { return }
        guard animated else {
            window.setFrame(target, display: true)
            window.invalidateShadow()
            return
        }

        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.16
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            window.animator().setFrame(target, display: true)
        } completionHandler: { [weak self] in
            MainActor.assumeIsolated { self?.window?.invalidateShadow() }
        }
    }

    /// 带入场动画呈现窗口（alpha 0→1 + 上移 10pt）。
    private func present(height: CGFloat) {
        guard let window else { return }
        if !anchorsResolved { refreshAnchors() }
        let metrics = layoutMetrics(for: currentHudWidth, height: height)
        anchorOrigin = metrics.origin
        currentHudWidth = metrics.width
        widthFloor = max(widthFloor, currentHudWidth)
        let endFrame = NSRect(x: anchorOrigin.x, y: anchorOrigin.y, width: currentHudWidth, height: height)

        // 已可见（连读：上一次成功/错误提示或其淡出动画还没结束）时直接就位，
        // 不重放入场动画——否则每次连读 HUD 都会闪黑一下再跳 10pt。
        if window.isVisible {
            window.setFrame(endFrame, display: true)
            if window.alphaValue < 1 {
                NSAnimationContext.runAnimationGroup { ctx in
                    ctx.duration = 0.12
                    window.animator().alphaValue = 1
                } completionHandler: { [weak self] in
                    MainActor.assumeIsolated { self?.window?.invalidateShadow() }
                }
            } else {
                window.invalidateShadow()
            }
            return
        }

        let startFrame = NSRect(x: anchorOrigin.x, y: anchorOrigin.y - 10, width: currentHudWidth, height: height)

        window.setFrame(startFrame, display: false)
        window.alphaValue = 0
        window.orderFrontRegardless()

        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.18
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            window.animator().setFrame(endFrame, display: true)
            window.animator().alphaValue = 1
        } completionHandler: { [weak self] in
            MainActor.assumeIsolated { self?.window?.invalidateShadow() }
        }
    }

    private func dismiss() {
        guard let window, window.isVisible else {
            window?.orderOut(nil)
            return
        }
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.22
            ctx.timingFunction = CAMediaTimingFunction(name: .easeIn)
            window.animator().alphaValue = 0
        } completionHandler: { [weak self] in
            MainActor.assumeIsolated {
                guard let self, let window = self.window else { return }
                // 淡出期间若重新显示（phase 非 hidden）则不真正下线。
                if self.phase == .hidden {
                    window.orderOut(nil)
                    window.alphaValue = 1
                }
            }
        }
    }

    /// 在展开 / 紧凑之间切换，保持底边不动向上生长。
    private func setExpanded(_ expanded: Bool, animated: Bool = true) {
        guard isExpanded != expanded else {
            previewLabel.alphaValue = expanded ? 1 : 0
            return
        }
        isExpanded = expanded
        guard let window, window.isVisible else {
            previewLabel.alphaValue = expanded ? 1 : 0
            return
        }
        let height = expanded ? currentExpandedHeight : Self.compactHeight
        let metrics = layoutMetrics(for: currentHudWidth, height: height)
        anchorOrigin = metrics.origin
        currentHudWidth = metrics.width
        let target = NSRect(x: anchorOrigin.x, y: anchorOrigin.y, width: currentHudWidth, height: height)

        guard animated else {
            window.setFrame(target, display: true)
            previewLabel.alphaValue = expanded ? 1 : 0
            window.invalidateShadow()
            return
        }

        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.2
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            window.animator().setFrame(target, display: true)
            previewLabel.animator().alphaValue = expanded ? 1 : 0
        } completionHandler: { [weak self] in
            MainActor.assumeIsolated { self?.window?.invalidateShadow() }
        }
    }

    private func scheduleCollapse() {
        cancelCollapse()
        let item = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.setExpanded(false)
            // 收起后才把预览区回到一行，下次展开从一行长起。
            self.setPreviewLineCount(1, animated: false)
        }
        collapseWorkItem = item
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6, execute: item)
    }

    private func cancelCollapse() {
        collapseWorkItem?.cancel()
        collapseWorkItem = nil
    }

    private func cancelTransientHide() {
        transientHideItem?.cancel()
        transientHideItem = nil
    }

    private func cancelWarningRestore() {
        warningRestoreWorkItem?.cancel()
        warningRestoreWorkItem = nil
    }

    // MARK: - 前导指示（点 / 符号）

    private func showGlyph(_ show: Bool) {
        glyphView.isHidden = !show
    }

    private func setGlyph(symbol: String, color: NSColor) {
        let image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
        glyphView.image = image
        glyphView.contentTintColor = color
    }

    // MARK: - UI 构建

    private func ensureUIBuilt() {
        guard !hasBuiltUI else { return }
        buildUI()
        hasBuiltUI = true
    }

    private func buildUI() {
        guard let contentView = window?.contentView else { return }
        contentView.wantsLayer = true
        contentView.layer?.backgroundColor = NSColor.clear.cgColor

        // 单层磨砂材质 + 暗色叠层 + 细描边，替代原手绘渐变。
        effectView.material = .hudWindow
        effectView.blendingMode = .behindWindow
        effectView.state = .active
        effectView.wantsLayer = true
        effectView.layer?.cornerRadius = Self.cornerRadius
        effectView.layer?.cornerCurve = .continuous
        effectView.layer?.masksToBounds = true
        effectView.layer?.borderWidth = 1
        effectView.layer?.borderColor = NSColor(white: 1.0, alpha: 0.12).cgColor
        effectView.translatesAutoresizingMaskIntoConstraints = false

        dimView.wantsLayer = true
        dimView.layer?.backgroundColor = NSColor.black.cgColor
        dimView.translatesAutoresizingMaskIntoConstraints = false
        effectView.addSubview(dimView)

        // 前导指示：呼吸点与符号叠放，二选一显示。
        let indicator = NSView()
        indicator.translatesAutoresizingMaskIntoConstraints = false
        dotView.translatesAutoresizingMaskIntoConstraints = false
        glyphView.translatesAutoresizingMaskIntoConstraints = false
        glyphView.imageScaling = .scaleProportionallyDown
        glyphView.wantsLayer = true
        glyphView.isHidden = true
        indicator.addSubview(dotView)
        indicator.addSubview(glyphView)

        // 层级约定:预览文字是 HUD 的正文(用户实时校对识别结果),用主色;
        // 状态标签只是装饰性提示(呼吸点/波形已在传达录音状态),降为次级色。
        statusLabel.font = Self.statusFont
        statusLabel.textColor = Self.statusTextColor
        statusLabel.alignment = .left
        // 状态行现在会带上输入设备名（"录音中 · MacBook Pro 麦克风"），长度不再可控。
        // 必须允许它被压缩并截尾——否则窄屏 / 长设备名时它会把右侧的计时挤出可视区。
        // 压缩阻力低于 timeLabel 的 `.required`，保证被牺牲的总是状态行而不是计时。
        statusLabel.wantsLayer = true
        statusLabel.postsFrameChangedNotifications = true
        // 观察者随 statusLabel（由本控制器持有、同生命周期）一起消亡，无需单独移除。
        NotificationCenter.default.addObserver(
            forName: NSView.frameDidChangeNotification, object: statusLabel, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.syncShimmerFrame() }
        }
        statusLabel.lineBreakMode = .byTruncatingTail
        statusLabel.cell?.usesSingleLineMode = true
        statusLabel.cell?.wraps = false
        statusLabel.setContentHuggingPriority(.defaultLow, for: .horizontal)
        statusLabel.setContentCompressionResistancePriority(.defaultHigh, for: .horizontal)

        timeLabel.font = Self.timeFont
        timeLabel.textColor = .tertiaryLabelColor
        timeLabel.alignment = .right
        timeLabel.setContentCompressionResistancePriority(.required, for: .horizontal)
        timeLabel.setContentHuggingPriority(.required, for: .horizontal)

        let spacer = NSView()
        spacer.translatesAutoresizingMaskIntoConstraints = false
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        spacer.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        [indicator, waveformView, statusLabel, spacer, timeLabel].forEach { topRow.addArrangedSubview($0) }
        topRow.wantsLayer = true
        topRow.orientation = .horizontal
        topRow.alignment = .centerY
        topRow.distribution = .fill
        topRow.spacing = Self.topRowSpacing
        topRow.translatesAutoresizingMaskIntoConstraints = false

        previewClipView.wantsLayer = true
        previewClipView.layer?.masksToBounds = true
        previewClipView.translatesAutoresizingMaskIntoConstraints = false

        Self.configurePreviewLabel(previewLabel)
        previewLabel.alphaValue = 0
        previewLabel.translatesAutoresizingMaskIntoConstraints = false
        previewClipView.addSubview(previewLabel)

        contentView.addSubview(effectView)
        contentView.addSubview(topRow)
        contentView.addSubview(previewClipView)

        NSLayoutConstraint.activate([
            effectView.leadingAnchor.constraint(equalTo: contentView.leadingAnchor),
            effectView.trailingAnchor.constraint(equalTo: contentView.trailingAnchor),
            effectView.topAnchor.constraint(equalTo: contentView.topAnchor),
            effectView.bottomAnchor.constraint(equalTo: contentView.bottomAnchor),

            dimView.leadingAnchor.constraint(equalTo: effectView.leadingAnchor),
            dimView.trailingAnchor.constraint(equalTo: effectView.trailingAnchor),
            dimView.topAnchor.constraint(equalTo: effectView.topAnchor),
            dimView.bottomAnchor.constraint(equalTo: effectView.bottomAnchor),

            indicator.widthAnchor.constraint(equalToConstant: Self.indicatorSize),
            indicator.heightAnchor.constraint(equalToConstant: Self.indicatorSize),
            dotView.centerXAnchor.constraint(equalTo: indicator.centerXAnchor),
            dotView.centerYAnchor.constraint(equalTo: indicator.centerYAnchor),
            glyphView.leadingAnchor.constraint(equalTo: indicator.leadingAnchor),
            glyphView.trailingAnchor.constraint(equalTo: indicator.trailingAnchor),
            glyphView.topAnchor.constraint(equalTo: indicator.topAnchor),
            glyphView.bottomAnchor.constraint(equalTo: indicator.bottomAnchor),

            waveformView.widthAnchor.constraint(equalToConstant: Self.waveformWidth),
            waveformView.heightAnchor.constraint(equalToConstant: 22),

            topRow.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: Self.contentHorizontalInset),
            topRow.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -Self.contentHorizontalInset),
            topRow.topAnchor.constraint(equalTo: contentView.topAnchor, constant: Self.topInset),
            topRow.heightAnchor.constraint(equalToConstant: Self.topRowHeight),

            previewClipView.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: Self.contentHorizontalInset),
            previewClipView.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -Self.contentHorizontalInset),
            previewClipView.topAnchor.constraint(equalTo: topRow.bottomAnchor, constant: Self.previewTopSpacing),

            // 标签与预览区四边对齐，换行宽度就是预览区宽度。此前左边是 `<=` 且横向强制贴合内容：
            // 换行标签的固有宽度是「整段排成一行」的宽度，于是标签被撑成一条向左伸出的长行，
            // 两行预览从未生效，窗口却按两行长高，下方空出一大片。
            previewLabel.leadingAnchor.constraint(equalTo: previewClipView.leadingAnchor),
            previewLabel.trailingAnchor.constraint(equalTo: previewClipView.trailingAnchor),
            previewLabel.topAnchor.constraint(equalTo: previewClipView.topAnchor),
            previewLabel.heightAnchor.constraint(equalTo: previewClipView.heightAnchor),
        ])

        let previewHeight = previewClipView.heightAnchor.constraint(equalToConstant: Self.previewHeight(lineCount: 1))
        previewHeight.isActive = true
        previewHeightConstraint = previewHeight

        applyDimOpacity()
        previewLabel.stringValue = displayText(for: "")
    }

    /// 预览标签的排版配置。单独抽出来，让测试能按同一配置验证「算出的行数 = 实际绘制的行数」。
    static func configurePreviewLabel(_ label: NSTextField) {
        label.font = previewFont
        label.textColor = .labelColor
        // 左对齐：多行时右对齐会让首行左边参差不齐；浮窗宽度随文字伸缩，单行时两者看起来几乎一样。
        label.alignment = .left
        // 首部截断（与 Windows 的 TruncateToFitFromStart 对齐）由 `fitPreview` 在
        // 送进来之前算好，这里只负责按词换行、最多 `previewMaximumLines` 行。
        // 不用 `.byTruncatingHead`：AppKit 对多行时的首部截断没有明确契约，
        // 万一退化成截尾部，用户看到的会是长听写的开头而不是刚说完的话。
        label.lineBreakMode = .byWordWrapping
        label.maximumNumberOfLines = previewMaximumLines
        label.cell?.usesSingleLineMode = false
        label.cell?.wraps = true
        label.cell?.isScrollable = false
        // 宽度完全由约束决定；固有宽度（整段排一行）不能参与竞争。
        label.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
    }

    private func applyDimOpacity() {
        // hudOpacity 直接作为磨砂背景层的整体不透明度：越低，面板越透、桌面越明显。
        // 文字与状态行是 contentView 的兄弟视图，不在 effectView 内，不随之变淡，保证可读性。
        // 之前只在 0.2~0.4 之间调一层黑色叠层的 alpha，磨砂材质本身始终不透明，
        // 所以滑杆全程看不出任何透明变化（用户反馈的 bug）。下限 40%。
        let clamped = min(max(hudOpacity, 0.4), 1.0)
        effectView.alphaValue = CGFloat(clamped)
        dimView.layer?.opacity = Float(0.35)
    }

    private func displayText(for text: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? L("正在聆听…") : trimmed
    }

    // MARK: - 计时器

    private static let timeTextColor: NSColor = .tertiaryLabelColor
    /// 录音剩余不足这么多秒时，计时位置改为橙色倒计时（R8-C3）。
    private static let countdownWindowSeconds = 15

    private func resetTimeLabel() {
        timeLabel.stringValue = ""
        timeLabel.textColor = Self.timeTextColor
    }

    /// 距单段录音上限还剩多少秒；剩余多于 15 秒或已到上限返回 nil（R8-C3）。
    /// HUD 的起点与真实录音起点最多相差 150ms（单独修饰键延迟显示），可接受。
    static func countdownSeconds(
        elapsed: TimeInterval, maximumSeconds: Int = LocalASRSession.maxSessionSeconds
    ) -> Int? {
        let remaining = Int(ceil(Double(maximumSeconds) - elapsed))
        return (1...countdownWindowSeconds).contains(remaining) ? remaining : nil
    }

    private func startTimer() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self, let startDate = self.startDate else { return }
                let elapsed = Date().timeIntervalSince(startDate)
                if let remaining = Self.countdownSeconds(elapsed: elapsed) {
                    self.timeLabel.stringValue = LF("还剩 %d 秒", remaining)
                    self.timeLabel.textColor = .systemOrange
                } else {
                    self.timeLabel.stringValue = Self.timeString(Int(elapsed))
                    self.timeLabel.textColor = Self.timeTextColor
                }
            }
        }
        timer?.tolerance = 0.1
        timer?.fire()
    }

    private static func timeString(_ elapsed: Int) -> String {
        if elapsed < 1 { return "" }
        if elapsed < 60 { return "\(elapsed)s" }
        return String(format: "%d:%02d", elapsed / 60, elapsed % 60)
    }
}

// MARK: - 呼吸点

private final class PulseDotView: NSView {
    private let circle = CALayer()

    override init(frame: NSRect) {
        super.init(frame: NSRect(x: 0, y: 0, width: 8, height: 8))
        setup()
    }

    required init?(coder: NSCoder) { fatalError() }

    override var intrinsicContentSize: NSSize { NSSize(width: 8, height: 8) }

    override func layout() {
        super.layout()
        circle.frame = bounds
        circle.cornerRadius = min(bounds.width, bounds.height) / 2
    }

    private func setup() {
        wantsLayer = true
        circle.frame = NSRect(x: 0, y: 0, width: 8, height: 8)
        circle.cornerRadius = 4
        circle.backgroundColor = NSColor.systemRed.cgColor
        layer?.addSublayer(circle)
    }

    func startPulse() {
        circle.removeAllAnimations()
        circle.backgroundColor = NSColor.systemRed.cgColor
        let anim = CABasicAnimation(keyPath: "opacity")
        anim.fromValue = 0.5
        anim.toValue = 1.0
        anim.duration = 0.6
        anim.autoreverses = true
        anim.repeatCount = .infinity
        anim.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
        circle.add(anim, forKey: "pulse")
    }

    func stopPulse() {
        circle.removeAllAnimations()
        circle.opacity = 1.0
    }

    /// 换成静态颜色，并带一次颜色过渡（红 → 橙 → 蓝）。
    func setStatic(color: NSColor) {
        let from = circle.presentation()?.backgroundColor ?? circle.backgroundColor
        circle.removeAllAnimations()
        circle.backgroundColor = color.cgColor
        circle.opacity = 1.0
        let transition = CABasicAnimation(keyPath: "backgroundColor")
        transition.fromValue = from
        transition.toValue = color.cgColor
        transition.duration = HUDMotion.colorTransitionDuration
        circle.add(transition, forKey: "colorTransition")
    }
}

// MARK: - 电平驱动声波

private final class WaveformView: NSView {
    private let bars: [CALayer] = (0..<5).map { _ in CALayer() }
    private let barWidth: CGFloat = 4
    private let barSpacing: CGFloat = 4
    private let minBarHeight: CGFloat = 3
    private let maxBarHeight: CGFloat = 18
    /// 各条基础权重，制造中间高两侧低的自然形态。
    private let weights: [CGFloat] = [0.55, 0.85, 1.0, 0.7, 0.9]
    /// 快攻慢放后的显示电平（0…1）。
    private var displayLevel: CGFloat = 0
    private var recognizing = false

    override init(frame: NSRect) {
        super.init(frame: frame)
        setup()
    }

    required init?(coder: NSCoder) { fatalError() }

    override var intrinsicContentSize: NSSize { NSSize(width: 44, height: 22) }

    override func layout() {
        super.layout()
        layoutBars()
    }

    private func setup() {
        wantsLayer = true
        bars.forEach { bar in
            bar.cornerRadius = barWidth / 2
            bar.backgroundColor = NSColor(white: 1.0, alpha: 0.9).cgColor
            layer?.addSublayer(bar)
        }
        layoutBars()
    }

    private func layoutBars() {
        let totalWidth = CGFloat(bars.count) * barWidth + CGFloat(bars.count - 1) * barSpacing
        let startX = (bounds.width - totalWidth) / 2
        for (index, bar) in bars.enumerated() {
            let height = heightForBar(index)
            let x = startX + CGFloat(index) * (barWidth + barSpacing)
            let y = (bounds.height - height) / 2
            bar.frame = CGRect(x: x, y: y, width: barWidth, height: height)
        }
    }

    private func heightForBar(_ index: Int) -> CGFloat {
        let base = minBarHeight + (maxBarHeight - minBarHeight) * displayLevel * weights[index]
        return min(max(base, minBarHeight), maxBarHeight)
    }

    /// 进入录音（电平驱动）态。
    func startListening() {
        recognizing = false
        displayLevel = 0
        bars.forEach {
            $0.removeAllAnimations()
            $0.backgroundColor = NSColor(white: 1.0, alpha: 0.9).cgColor
        }
        layoutBars()
    }

    /// 输入线性 RMS 电平，做分贝归一化 + 快攻慢放平滑后更新条高。
    func updateLevel(_ level: Float) {
        guard !recognizing else { return }
        let db = 20 * log10(max(Double(level), 1e-7))
        let normalized = CGFloat(min(max((db + 50) / 40, 0), 1))
        displayLevel = max(normalized, displayLevel * 0.82)

        CATransaction.begin()
        CATransaction.setAnimationDuration(0.08)
        layoutBars()
        CATransaction.commit()
    }

    /// 识别中：先在 0.12s 内收拢到最低高度并由白变橙（"收声"），随后开始柔和的
    /// 确定型顺序脉动。无电平输入，脉动的 `beginTime` 顺延收拢时长。
    func settleThenPulse() {
        recognizing = true
        displayLevel = 0
        let settle = HUDMotion.reduceMotion
            ? min(HUDMotion.barsSettleDuration, HUDMotion.reducedMotionDuration)
            : HUDMotion.barsSettleDuration

        // 条高与颜色的变化交给 CALayer 的隐式动画，放在同一个事务里统一时长。
        CATransaction.begin()
        CATransaction.setAnimationDuration(settle)
        layoutBars()
        bars.forEach { $0.backgroundColor = NSColor.systemOrange.cgColor }
        CATransaction.commit()

        for (index, bar) in bars.enumerated() {
            bar.removeAnimation(forKey: "recognizing")
            let anim = CABasicAnimation(keyPath: "transform.scale.y")
            anim.fromValue = 0.4
            anim.toValue = 1.0
            anim.duration = 0.6
            anim.beginTime = CACurrentMediaTime() + settle + Double(index) * 0.09
            anim.autoreverses = true
            anim.repeatCount = .infinity
            anim.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            bar.add(anim, forKey: "recognizing")
        }
    }

    func stop() {
        bars.forEach { $0.removeAllAnimations() }
    }
}

private extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }
}
