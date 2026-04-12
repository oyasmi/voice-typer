import AppKit
import Foundation

@MainActor
final class RecordingHUDController: NSWindowController {
    private let titleLabel = NSTextField(labelWithString: "🎤 录音中...")
    private let timeLabel = NSTextField(labelWithString: "0s")
    private var timer: Timer?
    private var startDate: Date?
    private var hasBuiltUI = false
    private var hudOpacity: Double = 0.85

    convenience init(config: UIConfig) {
        let rect = NSRect(x: 0, y: 0, width: config.width, height: config.height)
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

    func showHUD() {
        guard let window else {
            return
        }

        ensureUIBuilt()

        if let screen = NSScreen.main {
            let x = (screen.frame.width - window.frame.width) / 2
            let y: CGFloat = 120
            window.setFrameOrigin(NSPoint(x: x, y: y))
        }

        startDate = Date()
        timeLabel.stringValue = "0s"
        window.orderFrontRegardless()
        startTimer()
    }

    func hideHUD() {
        timer?.invalidate()
        timer = nil
        window?.orderOut(nil)
    }

    private func ensureUIBuilt() {
        guard !hasBuiltUI else {
            return
        }
        buildUI()
        hasBuiltUI = true
    }

    private func buildUI() {
        guard let contentView = window?.contentView else {
            return
        }

        titleLabel.font = .systemFont(ofSize: 16, weight: .medium)
        titleLabel.textColor = .white
        titleLabel.alignment = .center
        timeLabel.font = .systemFont(ofSize: 12)
        timeLabel.textColor = .lightGray
        timeLabel.alignment = .center

        let stack = NSStackView(views: [titleLabel, timeLabel])
        stack.orientation = .vertical
        stack.distribution = .fillEqually
        stack.alignment = .centerX
        stack.spacing = 8
        stack.translatesAutoresizingMaskIntoConstraints = false

        contentView.wantsLayer = true
        contentView.layer?.backgroundColor = NSColor(calibratedWhite: 0.12, alpha: hudOpacity).cgColor
        contentView.layer?.cornerRadius = 12
        contentView.layer?.masksToBounds = true
        contentView.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: contentView.centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: contentView.centerYAnchor),
            stack.leadingAnchor.constraint(greaterThanOrEqualTo: contentView.leadingAnchor, constant: 20),
            stack.trailingAnchor.constraint(lessThanOrEqualTo: contentView.trailingAnchor, constant: -20),
        ])
    }

    private func startTimer() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self, let startDate = self.startDate else {
                    return
                }
                let elapsed = Int(Date().timeIntervalSince(startDate))
                self.timeLabel.stringValue = "\(elapsed)s"
            }
        }
        timer?.tolerance = 0.2
        timer?.fire()
    }
}
