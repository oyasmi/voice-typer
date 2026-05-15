import AppKit
import Foundation

@MainActor
final class RecordingHUDController: NSWindowController {
    // MARK: - UI 元素

    /// 状态行：顶部左侧红点 + 右侧状态文字
    private let dotView = PulseDotView()
    private let statusLabel = NSTextField(labelWithString: "录音中...")
    /// 计时行
    private let timeLabel = NSTextField(labelWithString: "0s")
    /// 流式预览行：右对齐，超长从左截断
    private let previewLabel = NSTextField(labelWithString: "")

    private var timer: Timer?
    private var startDate: Date?
    private var hasBuiltUI = false
    private var hudOpacity: Double = 0.78
    private var accumulatedPreview = ""

    // MARK: - 初始化

    convenience init(config: UIConfig) {
        let width: CGFloat = 320
        let height: CGFloat = 90
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
        previewLabel.stringValue = ""
        accumulatedPreview = ""
        dotView.startPulse()
        window.orderFrontRegardless()
        startTimer()
    }

    func hideHUD() {
        timer?.invalidate()
        timer = nil
        dotView.stopPulse()
        window?.orderOut(nil)
        accumulatedPreview = ""
    }

    /// 追加流式 partial 文本，在预览行右对齐显示。
    func showPreview(_ accumulated: String) {
        accumulatedPreview = accumulated
        previewLabel.stringValue = accumulated
    }

    /// 清空预览文本（final 上屏后调用）。
    func clearPreview() {
        accumulatedPreview = ""
        previewLabel.stringValue = ""
    }

    /// 切换到"识别中"状态（松键后等待 final）。
    func setRecognizing() {
        statusLabel.stringValue = "识别中..."
        dotView.stopPulse()
        dotView.setStatic(color: NSColor(calibratedRed: 1.0, green: 0.75, blue: 0.0, alpha: 1))
    }

    // MARK: - UI 构建

    private func ensureUIBuilt() {
        guard !hasBuiltUI else { return }
        buildUI()
        hasBuiltUI = true
    }

    private func buildUI() {
        guard let contentView = window?.contentView else { return }

        // 背景层
        contentView.wantsLayer = true
        let bg = contentView.layer!
        bg.backgroundColor = NSColor(calibratedWhite: 0.10, alpha: hudOpacity).cgColor
        bg.cornerRadius = 14
        bg.masksToBounds = true
        // 高光描边
        bg.borderWidth = 1
        bg.borderColor = NSColor(white: 1.0, alpha: 0.08).cgColor

        // 状态行：点 + 文字
        statusLabel.font = .systemFont(ofSize: 14, weight: .medium)
        statusLabel.textColor = .white
        statusLabel.alignment = .left
        statusLabel.setContentHuggingPriority(.defaultLow, for: .horizontal)

        let statusRow = NSStackView(views: [dotView, statusLabel])
        statusRow.orientation = .horizontal
        statusRow.spacing = 6
        statusRow.alignment = .centerY

        // 计时
        timeLabel.font = .monospacedDigitSystemFont(ofSize: 12, weight: .regular)
        timeLabel.textColor = NSColor(white: 1.0, alpha: 0.5)
        timeLabel.alignment = .right
        timeLabel.setContentCompressionResistancePriority(.required, for: .horizontal)
        timeLabel.setContentHuggingPriority(.required, for: .horizontal)

        let topRow = NSStackView(views: [statusRow, timeLabel])
        topRow.orientation = .horizontal
        topRow.distribution = .fill
        topRow.alignment = .centerY
        topRow.spacing = 8

        // 预览行（右对齐，宽度受限截断）
        previewLabel.font = .systemFont(ofSize: 12, weight: .regular)
        previewLabel.textColor = NSColor(white: 1.0, alpha: 0.55)
        previewLabel.alignment = .right
        previewLabel.lineBreakMode = .byTruncatingHead
        previewLabel.maximumNumberOfLines = 1
        previewLabel.cell?.truncatesLastVisibleLine = true
        previewLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        // 主 stack
        let mainStack = NSStackView(views: [topRow, previewLabel])
        mainStack.orientation = .vertical
        mainStack.alignment = .leading
        mainStack.distribution = .fill
        mainStack.spacing = 6
        mainStack.translatesAutoresizingMaskIntoConstraints = false

        contentView.addSubview(mainStack)
        NSLayoutConstraint.activate([
            mainStack.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 16),
            mainStack.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -16),
            mainStack.centerYAnchor.constraint(equalTo: contentView.centerYAnchor),
            topRow.widthAnchor.constraint(equalTo: mainStack.widthAnchor),
            previewLabel.widthAnchor.constraint(equalTo: mainStack.widthAnchor),
        ])
    }

    // MARK: - 计时器

    private func startTimer() {
        timer?.invalidate()
        statusLabel.stringValue = "录音中..."
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
