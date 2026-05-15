import AppKit
import Foundation

@MainActor
final class RecordingHUDController: NSWindowController {
    // MARK: - UI 元素

    /// 状态行：左侧声波 + 右侧状态文字
    private let dotView = PulseDotView()
    private let waveformView = WaveformView()
    private let statusLabel = NSTextField(labelWithString: "录音中...")
    /// 计时行
    private let timeLabel = NSTextField(labelWithString: "0s")
    /// 流式预览行：居左显示，超长从尾部截断
    private let previewLabel = NSTextField(labelWithString: "")
    private let previewContainer = NSView()

    private var timer: Timer?
    private var startDate: Date?
    private var hasBuiltUI = false
    private var hudOpacity: Double = 0.78
    private var accumulatedPreview = ""

    // MARK: - 初始化

    convenience init(config: UIConfig) {
        let width: CGFloat = 390
        let height: CGFloat = 108
        let rect = NSRect(x: 0, y: 0, width: width, height: height)
        let window = NSWindow(
            contentRect: rect,
            styleMask: [.borderless],
            backing: .buffered,
            defer: false
        )
        self.init(window: window)
        self.hudOpacity = config.opacity
        window.isOpaque = false
        window.hasShadow = true
        window.level = .floating
        window.backgroundColor = .clear
        window.isMovableByWindowBackground = false
        window.ignoresMouseEvents = true
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        ensureUIBuilt()
    }

    // MARK: - 公共接口

    func showHUD() {
        guard let window else { return }
        ensureUIBuilt()
        if let screen = NSScreen.main {
            let x = (screen.frame.width - window.frame.width) / 2
            let y: CGFloat = 120
            window.setFrameOrigin(NSPoint(x: x, y: y))
        }
        startDate = Date()
        timeLabel.stringValue = "0s"
        previewLabel.stringValue = displayText(for: "")
        accumulatedPreview = ""
        dotView.startPulse()
        waveformView.startAnimating()
        window.orderFrontRegardless()
        startTimer()
    }

    func hideHUD() {
        timer?.invalidate()
        timer = nil
        dotView.stopPulse()
        waveformView.stopAnimating()
        window?.orderOut(nil)
        accumulatedPreview = ""
    }

    /// 追加流式 partial 文本，从右侧增长，超长时省略左侧。
    func showPreview(_ accumulated: String) {
        accumulatedPreview = accumulated
        previewLabel.stringValue = displayText(for: accumulated)
    }

    /// 清空预览文本（final 上屏后调用）。
    func clearPreview() {
        accumulatedPreview = ""
        previewLabel.stringValue = displayText(for: "")
    }

    /// 切换到"识别中"状态（松键后等待 final）。
    func setRecognizing() {
        statusLabel.stringValue = "识别中"
        dotView.stopPulse()
        dotView.setStatic(color: NSColor(calibratedRed: 1.0, green: 0.75, blue: 0.0, alpha: 1))
        waveformView.setRecognizing()
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

        // 系统磨砂材质作为主背景，再叠加细腻的暗色和高光层。
        let backgroundView = NSVisualEffectView()
        backgroundView.material = .hudWindow
        backgroundView.blendingMode = .behindWindow
        backgroundView.state = .active
        backgroundView.wantsLayer = true
        backgroundView.layer?.cornerRadius = 22
        backgroundView.layer?.masksToBounds = true
        backgroundView.translatesAutoresizingMaskIntoConstraints = false

        let overlayView = HUDBackgroundView(opacity: hudOpacity)
        overlayView.translatesAutoresizingMaskIntoConstraints = false
        overlayView.wantsLayer = true
        overlayView.layer?.cornerRadius = 22
        overlayView.layer?.masksToBounds = true

        // 状态行：点 + 文字
        statusLabel.font = .systemFont(ofSize: 13, weight: .semibold)
        statusLabel.textColor = NSColor(white: 1.0, alpha: 0.86)
        statusLabel.alignment = .left
        statusLabel.setContentHuggingPriority(.defaultLow, for: .horizontal)

        let statusRow = NSStackView(views: [dotView, statusLabel])
        statusRow.orientation = .horizontal
        statusRow.spacing = 7
        statusRow.alignment = .centerY

        // 计时
        timeLabel.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium)
        timeLabel.textColor = NSColor(white: 1.0, alpha: 0.56)
        timeLabel.alignment = .right
        timeLabel.setContentCompressionResistancePriority(.required, for: .horizontal)
        timeLabel.setContentHuggingPriority(.required, for: .horizontal)

        let topRow = NSStackView(views: [statusRow, timeLabel])
        topRow.orientation = .horizontal
        topRow.distribution = .fill
        topRow.alignment = .centerY
        topRow.spacing = 8

        // 预览文本区域，使用深色内层承载，突出实时识别内容。
        previewContainer.wantsLayer = true
        previewContainer.layer?.backgroundColor = NSColor(white: 0.0, alpha: 0.18).cgColor
        previewContainer.layer?.cornerRadius = 13
        previewContainer.layer?.borderWidth = 1
        previewContainer.layer?.borderColor = NSColor(white: 1.0, alpha: 0.075).cgColor
        previewContainer.translatesAutoresizingMaskIntoConstraints = false

        previewLabel.font = .systemFont(ofSize: 14, weight: .regular)
        previewLabel.textColor = NSColor(white: 1.0, alpha: 0.88)
        previewLabel.alignment = .right
        previewLabel.lineBreakMode = .byTruncatingHead
        previewLabel.maximumNumberOfLines = 2
        previewLabel.cell?.truncatesLastVisibleLine = true
        previewLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        previewLabel.translatesAutoresizingMaskIntoConstraints = false

        previewContainer.addSubview(previewLabel)

        let rightStack = NSStackView(views: [topRow, previewContainer])
        rightStack.orientation = .vertical
        rightStack.alignment = .leading
        rightStack.distribution = .fill
        rightStack.spacing = 8
        rightStack.translatesAutoresizingMaskIntoConstraints = false

        let mainStack = NSStackView(views: [waveformView, rightStack])
        mainStack.orientation = .horizontal
        mainStack.alignment = .centerY
        mainStack.distribution = .fill
        mainStack.spacing = 15
        mainStack.translatesAutoresizingMaskIntoConstraints = false

        contentView.addSubview(backgroundView)
        contentView.addSubview(overlayView)
        contentView.addSubview(mainStack)
        NSLayoutConstraint.activate([
            backgroundView.leadingAnchor.constraint(equalTo: contentView.leadingAnchor),
            backgroundView.trailingAnchor.constraint(equalTo: contentView.trailingAnchor),
            backgroundView.topAnchor.constraint(equalTo: contentView.topAnchor),
            backgroundView.bottomAnchor.constraint(equalTo: contentView.bottomAnchor),

            overlayView.leadingAnchor.constraint(equalTo: contentView.leadingAnchor),
            overlayView.trailingAnchor.constraint(equalTo: contentView.trailingAnchor),
            overlayView.topAnchor.constraint(equalTo: contentView.topAnchor),
            overlayView.bottomAnchor.constraint(equalTo: contentView.bottomAnchor),

            mainStack.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 16),
            mainStack.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -16),
            mainStack.centerYAnchor.constraint(equalTo: contentView.centerYAnchor),

            waveformView.widthAnchor.constraint(equalToConstant: 48),
            waveformView.heightAnchor.constraint(equalToConstant: 66),

            rightStack.widthAnchor.constraint(equalTo: mainStack.widthAnchor, constant: -63),
            topRow.widthAnchor.constraint(equalTo: rightStack.widthAnchor),
            previewContainer.widthAnchor.constraint(equalTo: rightStack.widthAnchor),
            previewContainer.heightAnchor.constraint(equalToConstant: 46),

            previewLabel.leadingAnchor.constraint(equalTo: previewContainer.leadingAnchor, constant: 12),
            previewLabel.trailingAnchor.constraint(equalTo: previewContainer.trailingAnchor, constant: -12),
            previewLabel.centerYAnchor.constraint(equalTo: previewContainer.centerYAnchor),
        ])

        previewLabel.stringValue = displayText(for: "")
    }

    private func displayText(for text: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "正在聆听" : trimmed
    }

    // MARK: - 计时器

    private func startTimer() {
        timer?.invalidate()
        statusLabel.stringValue = "录音中"
        timer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self, let startDate = self.startDate else { return }
                let elapsed = Int(Date().timeIntervalSince(startDate))
                self.timeLabel.stringValue = "\(elapsed)s"
            }
        }
        timer?.tolerance = 0.2
        timer?.fire()
    }
}

// MARK: - HUD 背景

private final class HUDBackgroundView: NSView {
    private let opacity: Double

    init(opacity: Double) {
        self.opacity = opacity
        super.init(frame: .zero)
    }

    required init?(coder: NSCoder) { fatalError() }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        guard let context = NSGraphicsContext.current?.cgContext else { return }

        let bounds = self.bounds
        let radius: CGFloat = 22
        let path = CGPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), cornerWidth: radius, cornerHeight: radius, transform: nil)
        context.addPath(path)
        context.clip()

        let colors = [
            NSColor(calibratedRed: 0.11, green: 0.12, blue: 0.14, alpha: opacity).cgColor,
            NSColor(calibratedRed: 0.035, green: 0.04, blue: 0.055, alpha: opacity).cgColor,
        ] as CFArray
        let locations: [CGFloat] = [0.0, 1.0]
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        if let gradient = CGGradient(colorsSpace: colorSpace, colors: colors, locations: locations) {
            context.drawLinearGradient(
                gradient,
                start: CGPoint(x: bounds.midX, y: bounds.maxY),
                end: CGPoint(x: bounds.midX, y: bounds.minY),
                options: []
            )
        }

        context.resetClip()
        context.addPath(path)
        context.setStrokeColor(NSColor(white: 1.0, alpha: 0.12).cgColor)
        context.setLineWidth(1)
        context.strokePath()

        let highlightRect = NSRect(x: 1, y: bounds.height - 34, width: bounds.width - 2, height: 32)
        let highlightPath = CGPath(roundedRect: highlightRect, cornerWidth: radius - 1, cornerHeight: radius - 1, transform: nil)
        context.addPath(highlightPath)
        context.setFillColor(NSColor(white: 1.0, alpha: 0.035).cgColor)
        context.fillPath()
    }
}

// MARK: - 呼吸红点

private final class PulseDotView: NSView {
    private let circle = CALayer()

    override init(frame: NSRect) {
        super.init(frame: NSRect(x: 0, y: 0, width: 8, height: 8))
        setup()
    }

    required init?(coder: NSCoder) { fatalError() }

    override var intrinsicContentSize: NSSize { NSSize(width: 8, height: 8) }

    private func setup() {
        wantsLayer = true
        circle.frame = bounds
        circle.cornerRadius = 4
        circle.backgroundColor = NSColor(calibratedRed: 1.0, green: 0.25, blue: 0.2, alpha: 1).cgColor
        layer?.addSublayer(circle)
    }

    func startPulse() {
        circle.removeAllAnimations()
        circle.backgroundColor = NSColor(calibratedRed: 1.0, green: 0.25, blue: 0.2, alpha: 1).cgColor
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

// MARK: - 动态声波

private final class WaveformView: NSView {
    private let bars: [CALayer] = (0..<5).map { _ in CALayer() }
    private let barWidth: CGFloat = 4
    private let barSpacing: CGFloat = 5
    private let maxBarHeight: CGFloat = 44

    override init(frame: NSRect) {
        super.init(frame: frame)
        setup()
    }

    required init?(coder: NSCoder) { fatalError() }

    override var intrinsicContentSize: NSSize { NSSize(width: 48, height: 66) }

    override func layout() {
        super.layout()
        layoutBars()
    }

    private func setup() {
        wantsLayer = true
        layer?.backgroundColor = NSColor(white: 1.0, alpha: 0.06).cgColor
        layer?.cornerRadius = 16
        layer?.borderWidth = 1
        layer?.borderColor = NSColor(white: 1.0, alpha: 0.08).cgColor

        bars.forEach { bar in
            bar.cornerRadius = barWidth / 2
            bar.backgroundColor = NSColor(calibratedRed: 0.36, green: 0.74, blue: 1.0, alpha: 0.95).cgColor
            layer?.addSublayer(bar)
        }
        layoutBars()
    }

    private func layoutBars() {
        let totalWidth = CGFloat(bars.count) * barWidth + CGFloat(bars.count - 1) * barSpacing
        let startX = (bounds.width - totalWidth) / 2
        let heights: [CGFloat] = [18, 30, 40, 26, 34]
        for (index, bar) in bars.enumerated() {
            let height = heights[index]
            let x = startX + CGFloat(index) * (barWidth + barSpacing)
            let y = (bounds.height - maxBarHeight) / 2 + (maxBarHeight - height) / 2
            bar.frame = CGRect(x: x, y: y, width: barWidth, height: height)
        }
    }

    func startAnimating() {
        layer?.borderColor = NSColor(calibratedRed: 0.36, green: 0.74, blue: 1.0, alpha: 0.18).cgColor
        let scales: [CGFloat] = [0.56, 1.28, 0.72, 1.46, 0.82]
        for (index, bar) in bars.enumerated() {
            bar.removeAllAnimations()
            bar.backgroundColor = NSColor(calibratedRed: 0.36, green: 0.74, blue: 1.0, alpha: 0.95).cgColor
            let animation = CABasicAnimation(keyPath: "transform.scale.y")
            animation.fromValue = scales[index]
            animation.toValue = 0.32 + scales.reversed()[index] * 0.9
            animation.duration = 0.36 + Double(index) * 0.045
            animation.autoreverses = true
            animation.repeatCount = .infinity
            animation.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            bar.add(animation, forKey: "voiceLevel")
        }
    }

    func stopAnimating() {
        bars.forEach { $0.removeAllAnimations() }
    }

    func setRecognizing() {
        stopAnimating()
        layer?.borderColor = NSColor(calibratedRed: 1.0, green: 0.76, blue: 0.28, alpha: 0.18).cgColor
        bars.forEach {
            $0.backgroundColor = NSColor(calibratedRed: 1.0, green: 0.76, blue: 0.28, alpha: 0.92).cgColor
            $0.opacity = 0.92
        }
    }
}
