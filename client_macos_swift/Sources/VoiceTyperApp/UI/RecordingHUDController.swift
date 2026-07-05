import AppKit
import Foundation
import QuartzCore

@MainActor
final class RecordingHUDController: NSWindowController {
    // MARK: - 几何常量

    private static let hudWidth: CGFloat = 340
    private static let compactHeight: CGFloat = 48
    private static let expandedHeight: CGFloat = 100
    private static let cornerRadius: CGFloat = 24

    // MARK: - UI 元素

    private let dotView = PulseDotView()
    private let glyphView = NSImageView()
    private let waveformView = WaveformView()
    private let statusLabel = NSTextField(labelWithString: "录音中")
    private let timeLabel = NSTextField(labelWithString: "")
    private let previewLabel = NSTextField(labelWithString: "")
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
    private var accumulatedPreview = ""

    /// 展开态（显示 preview 行）与否。
    private var isExpanded = false
    /// 当前窗口底边左下角锚点（展开/收起时保持底边不动，向上生长）。
    private var anchorOrigin: CGPoint = .zero
    /// 收起防抖：preview 短暂清空时延迟收起，避免抖动。
    private var collapseWorkItem: DispatchWorkItem?
    /// 一次性提示的自动隐藏任务，phase 变化时作废。
    private var transientHideItem: DispatchWorkItem?
    private var lastLevelUpdate: CFTimeInterval = 0

    // MARK: - 初始化

    convenience init(config: UIConfig) {
        let rect = NSRect(x: 0, y: 0, width: RecordingHUDController.hudWidth, height: RecordingHUDController.compactHeight)
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

    func showHUD() {
        ensureUIBuilt()
        cancelTransientHide()
        cancelCollapse()

        phase = .recording
        startDate = Date()
        isExpanded = false
        accumulatedPreview = ""

        statusLabel.stringValue = "录音中"
        timeLabel.stringValue = ""
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
        cancelCollapse()
        cancelTransientHide()
        phase = .hidden
        dotView.stopPulse()
        waveformView.stop()
        accumulatedPreview = ""
        dismiss()
    }

    /// 追加流式 partial 文本。有内容时展开 HUD，短暂清空时防抖收起。
    func showPreview(_ accumulated: String) {
        accumulatedPreview = accumulated
        previewLabel.stringValue = displayText(for: accumulated)

        let hasText = !accumulated.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        if hasText {
            cancelCollapse()
            setExpanded(true)
        } else {
            scheduleCollapse()
        }
    }

    /// 清空预览文本（final 上屏后调用），立即收起。
    func clearPreview() {
        accumulatedPreview = ""
        previewLabel.stringValue = displayText(for: "")
        cancelCollapse()
        setExpanded(false)
    }

    /// 切换到"识别中"状态（松键后等待 final）。
    func setRecognizing() {
        cancelTransientHide()
        // 松键后录音结束，冻结计时（保留最后时长）。
        timer?.invalidate()
        timer = nil
        phase = .recognizing
        statusLabel.stringValue = "识别中"
        showGlyph(false)
        dotView.isHidden = false
        dotView.stopPulse()
        dotView.setStatic(color: .systemOrange)
        waveformView.isHidden = false
        waveformView.setRecognizing()
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
    func showSuccess() {
        showTransient(
            status: "已输入",
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
            status: "错误",
            message: message.isEmpty ? "服务异常" : message,
            glyph: "exclamationmark.circle.fill",
            color: .systemRed,
            expanded: true,
            autoHideAfter: 2.5
        )
    }

    /// "已取消"提示浮层，约 1.0s 后自动隐藏（用户按 Esc 取消录音）。
    func showCanceled() {
        showTransient(
            status: "已取消",
            message: "",
            glyph: "xmark.circle.fill",
            color: NSColor(white: 0.7, alpha: 1),
            expanded: false,
            autoHideAfter: 1.0
        )
    }

    /// 录音过程中的非致命提示（如 partial 暂时不可用）。仅闪烁 statusLabel。
    func flashWarning(_ message: String) {
        guard phase == .recording else { return }
        statusLabel.stringValue = message.isEmpty ? "识别提示" : message
        let item = DispatchWorkItem { [weak self] in
            guard let self, self.phase == .recording else { return }
            self.statusLabel.stringValue = "录音中"
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2, execute: item)
    }

    /// 临时以给定不透明度预览 HUD 背景（设置页透明度滑杆用）。
    func previewOpacity(_ opacity: Double) {
        ensureUIBuilt()
        hudOpacity = opacity
        applyDimOpacity()

        // 真实会话进行中：只实时改透明度，不打断。
        switch phase {
        case .recording, .recognizing:
            return
        case .hidden, .transient:
            break
        }

        phase = .transient
        statusLabel.stringValue = "背景预览"
        timeLabel.stringValue = ""
        previewLabel.alphaValue = 0
        showGlyph(false)
        dotView.isHidden = false
        dotView.stopPulse()
        dotView.setStatic(color: .systemGreen)
        waveformView.isHidden = true

        if window?.isVisible != true {
            isExpanded = false
            present(height: Self.compactHeight)
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
        cancelTransientHide()
        phase = .transient

        statusLabel.stringValue = status
        timeLabel.stringValue = ""
        previewLabel.stringValue = message
        previewLabel.alphaValue = expanded ? 1 : 0

        dotView.isHidden = true
        dotView.stopPulse()
        waveformView.isHidden = true
        waveformView.stop()
        setGlyph(symbol: glyph, color: color)
        showGlyph(true)

        if window?.isVisible == true {
            setExpanded(expanded)
        } else {
            isExpanded = expanded
            present(height: expanded ? Self.expandedHeight : Self.compactHeight)
        }

        let item = DispatchWorkItem { [weak self] in
            guard let self, self.phase == .transient else { return }
            self.hideHUD()
        }
        transientHideItem = item
        DispatchQueue.main.asyncAfter(deadline: .now() + autoHideAfter, execute: item)
    }

    // MARK: - 窗口显示 / 定位 / 动画

    /// 计算目标屏幕（鼠标所在屏，回退主屏），返回给定高度下的底边左下角锚点。
    private func computeAnchorOrigin() -> CGPoint {
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) }
            ?? NSScreen.main
            ?? NSScreen.screens.first
        guard let screen else { return .zero }
        let visible = screen.visibleFrame
        let x = visible.midX - Self.hudWidth / 2
        let y = visible.minY + 80
        return CGPoint(x: x, y: y)
    }

    /// 带入场动画呈现窗口（alpha 0→1 + 上移 10pt）。
    private func present(height: CGFloat) {
        guard let window else { return }
        anchorOrigin = computeAnchorOrigin()
        let startFrame = NSRect(x: anchorOrigin.x, y: anchorOrigin.y - 10, width: Self.hudWidth, height: height)
        let endFrame = NSRect(x: anchorOrigin.x, y: anchorOrigin.y, width: Self.hudWidth, height: height)

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
        let height = expanded ? Self.expandedHeight : Self.compactHeight
        let target = NSRect(x: anchorOrigin.x, y: anchorOrigin.y, width: Self.hudWidth, height: height)

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
        glyphView.isHidden = true
        indicator.addSubview(dotView)
        indicator.addSubview(glyphView)

        statusLabel.font = .systemFont(ofSize: 13, weight: .semibold)
        statusLabel.textColor = .labelColor
        statusLabel.alignment = .left
        statusLabel.setContentHuggingPriority(.defaultLow, for: .horizontal)

        timeLabel.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium)
        timeLabel.textColor = .tertiaryLabelColor
        timeLabel.alignment = .right
        timeLabel.setContentCompressionResistancePriority(.required, for: .horizontal)
        timeLabel.setContentHuggingPriority(.required, for: .horizontal)

        let spacer = NSView()
        spacer.translatesAutoresizingMaskIntoConstraints = false
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        spacer.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        let topRow = NSStackView(views: [indicator, waveformView, statusLabel, spacer, timeLabel])
        topRow.orientation = .horizontal
        topRow.alignment = .centerY
        topRow.distribution = .fill
        topRow.spacing = 10
        topRow.translatesAutoresizingMaskIntoConstraints = false

        previewLabel.font = .systemFont(ofSize: 14, weight: .regular)
        previewLabel.textColor = .labelColor
        previewLabel.alignment = .left
        previewLabel.lineBreakMode = .byTruncatingHead
        previewLabel.maximumNumberOfLines = 2
        previewLabel.cell?.truncatesLastVisibleLine = true
        previewLabel.alphaValue = 0
        previewLabel.translatesAutoresizingMaskIntoConstraints = false

        contentView.addSubview(effectView)
        contentView.addSubview(topRow)
        contentView.addSubview(previewLabel)

        NSLayoutConstraint.activate([
            effectView.leadingAnchor.constraint(equalTo: contentView.leadingAnchor),
            effectView.trailingAnchor.constraint(equalTo: contentView.trailingAnchor),
            effectView.topAnchor.constraint(equalTo: contentView.topAnchor),
            effectView.bottomAnchor.constraint(equalTo: contentView.bottomAnchor),

            dimView.leadingAnchor.constraint(equalTo: effectView.leadingAnchor),
            dimView.trailingAnchor.constraint(equalTo: effectView.trailingAnchor),
            dimView.topAnchor.constraint(equalTo: effectView.topAnchor),
            dimView.bottomAnchor.constraint(equalTo: effectView.bottomAnchor),

            indicator.widthAnchor.constraint(equalToConstant: 16),
            indicator.heightAnchor.constraint(equalToConstant: 16),
            dotView.centerXAnchor.constraint(equalTo: indicator.centerXAnchor),
            dotView.centerYAnchor.constraint(equalTo: indicator.centerYAnchor),
            glyphView.leadingAnchor.constraint(equalTo: indicator.leadingAnchor),
            glyphView.trailingAnchor.constraint(equalTo: indicator.trailingAnchor),
            glyphView.topAnchor.constraint(equalTo: indicator.topAnchor),
            glyphView.bottomAnchor.constraint(equalTo: indicator.bottomAnchor),

            waveformView.widthAnchor.constraint(equalToConstant: 44),
            waveformView.heightAnchor.constraint(equalToConstant: 22),

            topRow.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 16),
            topRow.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -16),
            topRow.topAnchor.constraint(equalTo: contentView.topAnchor, constant: 10),
            topRow.heightAnchor.constraint(equalToConstant: 28),

            previewLabel.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 16),
            previewLabel.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -16),
            previewLabel.topAnchor.constraint(equalTo: topRow.bottomAnchor, constant: 8),
        ])

        applyDimOpacity()
        previewLabel.stringValue = displayText(for: "")
    }

    private func applyDimOpacity() {
        // opacity 越大背景越沉、文字对比越强；下限保底可读性。
        let clamped = min(max(hudOpacity, 0.5), 1.0)
        dimView.layer?.opacity = Float(clamped * 0.4)
    }

    private func displayText(for text: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "正在聆听…" : trimmed
    }

    // MARK: - 计时器

    private func startTimer() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self, let startDate = self.startDate else { return }
                self.timeLabel.stringValue = Self.timeString(Int(Date().timeIntervalSince(startDate)))
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

    func setStatic(color: NSColor) {
        circle.removeAllAnimations()
        circle.backgroundColor = color.cgColor
        circle.opacity = 1.0
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

    /// 识别中：无电平输入，改用柔和的确定型顺序脉动（橙色）。
    func setRecognizing() {
        recognizing = true
        displayLevel = 0
        let color = NSColor.systemOrange.cgColor
        for (index, bar) in bars.enumerated() {
            bar.removeAllAnimations()
            bar.backgroundColor = color
            let anim = CABasicAnimation(keyPath: "transform.scale.y")
            anim.fromValue = 0.4
            anim.toValue = 1.0
            anim.duration = 0.6
            anim.beginTime = CACurrentMediaTime() + Double(index) * 0.09
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
