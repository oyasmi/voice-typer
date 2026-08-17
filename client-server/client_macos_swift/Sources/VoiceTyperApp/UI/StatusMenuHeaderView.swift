import AppKit

/// 菜单栏下拉菜单顶部的自定义状态视图（挂在首个 NSMenuItem.view 上）。
/// 两行布局：状态点 + 应用名·状态；副行显示热键与服务端连接情况。不可点击。
@MainActor
final class StatusMenuHeaderView: NSView {
    private let dotView = NSView()
    private let titleLabel = NSTextField(labelWithString: AppConstants.appName)
    private let subtitleLabel = NSTextField(labelWithString: "")

    private static let width: CGFloat = 268
    private static let height: CGFloat = 54

    init() {
        super.init(frame: NSRect(x: 0, y: 0, width: Self.width, height: Self.height))
        autoresizingMask = [.width]
        buildUI()
    }

    required init?(coder: NSCoder) { fatalError() }

    private func buildUI() {
        dotView.translatesAutoresizingMaskIntoConstraints = false
        dotView.wantsLayer = true
        dotView.layer?.cornerRadius = 4
        dotView.layer?.backgroundColor = NSColor.secondaryLabelColor.cgColor

        titleLabel.font = .systemFont(ofSize: 13, weight: .semibold)
        titleLabel.textColor = .labelColor
        titleLabel.lineBreakMode = .byTruncatingTail

        subtitleLabel.font = .systemFont(ofSize: 11, weight: .regular)
        subtitleLabel.textColor = .secondaryLabelColor
        subtitleLabel.lineBreakMode = .byTruncatingTail

        let textStack = NSStackView(views: [titleLabel, subtitleLabel])
        textStack.orientation = .vertical
        textStack.alignment = .leading
        textStack.spacing = 3
        textStack.translatesAutoresizingMaskIntoConstraints = false

        addSubview(dotView)
        addSubview(textStack)

        NSLayoutConstraint.activate([
            dotView.widthAnchor.constraint(equalToConstant: 8),
            dotView.heightAnchor.constraint(equalToConstant: 8),
            dotView.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 15),
            dotView.centerYAnchor.constraint(equalTo: centerYAnchor),

            textStack.leadingAnchor.constraint(equalTo: dotView.trailingAnchor, constant: 9),
            textStack.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor, constant: -14),
            textStack.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])
    }

    func update(state: AppState, hotkeyDisplay: String, serverStatus: String) {
        titleLabel.stringValue = "\(AppConstants.appName) · \(state.menuTitle)"
        subtitleLabel.stringValue = "\(hotkeyDisplay) · \(serverStatus)"
        dotView.layer?.backgroundColor = Self.dotColor(for: state).cgColor
    }

    private static func dotColor(for state: AppState) -> NSColor {
        switch state {
        case .idle:
            return .systemGreen
        case .recording:
            return .systemRed
        case .recognizing:
            return .systemOrange
        case .inserting:
            return .systemBlue
        case .connecting:
            return .systemYellow
        case .setupRequired:
            return .systemOrange
        case .paused:
            return .secondaryLabelColor
        case .error:
            return .systemRed
        case .booting:
            return .secondaryLabelColor
        }
    }
}
