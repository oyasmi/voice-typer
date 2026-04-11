import AppKit
import Foundation

@MainActor
private final class PermissionRow {
    let kind: PermissionKind
    let titleLabel: NSTextField
    let statusLabel: NSTextField
    let authorizeButton: NSButton
    let settingsButton: NSButton

    init(kind: PermissionKind, target: AnyObject, authorizeAction: Selector, settingsAction: Selector) {
        self.kind = kind
        self.titleLabel = NSTextField(labelWithString: kind.title)
        self.statusLabel = NSTextField(labelWithString: "检查中")
        self.authorizeButton = NSButton(title: "授权", target: target, action: authorizeAction)
        self.settingsButton = NSButton(title: "系统设置", target: target, action: settingsAction)
        self.authorizeButton.identifier = NSUserInterfaceItemIdentifier(kind.title)
        self.settingsButton.identifier = NSUserInterfaceItemIdentifier(kind.title)
        self.statusLabel.textColor = .secondaryLabelColor
    }
}

@MainActor
final class SetupWindowController: NSWindowController {
    var onRequestPermission: ((PermissionKind) -> Void)?
    var onOpenSystemSettings: ((PermissionKind) -> Void)?
    var onRetryServerCheck: (() -> Void)?
    var onOpenConfigDirectory: (() -> Void)?

    private let introLabel = NSTextField(wrappingLabelWithString: "VoiceTyper 首次运行需要完成系统授权，并确保识别服务可用。权限满足后即可进入状态栏常驻模式。")
    private let serverStatusLabel = NSTextField(labelWithString: "服务状态：检查中")
    private let serverRetryButton = NSButton(title: "重试连接", target: nil, action: nil)
    private let configButton = NSButton(title: "打开配置目录", target: nil, action: nil)
    private let continueLabel = NSTextField(labelWithString: "全部就绪后可直接关闭本窗口。")
    private var hasBuiltUI = false

    private lazy var permissionRows: [PermissionRow] = PermissionKind.allCases.map {
        PermissionRow(
            kind: $0,
            target: self,
            authorizeAction: #selector(handleAuthorize),
            settingsAction: #selector(handleOpenSystemSettings)
        )
    }

    convenience init() {
        let contentRect = NSRect(x: 0, y: 0, width: 560, height: 380)
        let window = NSWindow(
            contentRect: contentRect,
            styleMask: [.titled, .closable, .miniaturizable],
            backing: .buffered,
            defer: false
        )
        self.init(window: window)
        window.title = "\(AppConstants.appName) 权限与设置"
        window.center()
        window.isReleasedWhenClosed = false
        ensureUIBuilt()
    }

    func update(snapshot: PermissionSnapshot, serviceReady: Bool, hotkeyDisplay: String) {
        ensureUIBuilt()

        for row in permissionRows {
            let status = snapshot.status(for: row.kind)
            row.statusLabel.stringValue = status.displayText
            row.authorizeButton.isEnabled = status != .authorized
            row.settingsButton.isEnabled = status != .authorized
        }

        serverStatusLabel.stringValue = "服务状态：\(serviceReady ? "已连接" : "未连接") | 当前热键：\(hotkeyDisplay)"
        continueLabel.stringValue = snapshot.allRequiredGranted && serviceReady
            ? "全部检查通过，可以关闭窗口并直接使用。"
            : "请先完成未通过项，再开始使用。"
    }

    override func showWindow(_ sender: Any?) {
        ensureUIBuilt()
        super.showWindow(sender)
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

        introLabel.maximumNumberOfLines = 0
        introLabel.font = .systemFont(ofSize: 13)

        serverRetryButton.target = self
        serverRetryButton.action = #selector(handleRetryServer)
        configButton.target = self
        configButton.action = #selector(handleOpenConfigDirectory)
        continueLabel.textColor = .secondaryLabelColor

        let rootStack = NSStackView()
        rootStack.orientation = .vertical
        rootStack.alignment = .leading
        rootStack.spacing = 18
        rootStack.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(rootStack)

        rootStack.addArrangedSubview(introLabel)

        for row in permissionRows {
            rootStack.addArrangedSubview(makeRowView(for: row))
        }

        let serverRow = NSStackView(views: [serverStatusLabel, serverRetryButton, configButton])
        serverRow.orientation = .horizontal
        serverRow.alignment = .centerY
        serverRow.spacing = 12
        rootStack.addArrangedSubview(serverRow)
        rootStack.addArrangedSubview(continueLabel)

        NSLayoutConstraint.activate([
            rootStack.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 24),
            rootStack.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -24),
            rootStack.topAnchor.constraint(equalTo: contentView.topAnchor, constant: 24),
            rootStack.bottomAnchor.constraint(lessThanOrEqualTo: contentView.bottomAnchor, constant: -24),
        ])
    }

    private func makeRowView(for row: PermissionRow) -> NSView {
        row.titleLabel.font = .boldSystemFont(ofSize: 13)

        let labels = NSStackView(views: [row.titleLabel, row.statusLabel])
        labels.orientation = .vertical
        labels.alignment = .leading
        labels.spacing = 4

        let container = NSStackView(views: [labels, row.authorizeButton, row.settingsButton])
        container.orientation = .horizontal
        container.alignment = .centerY
        container.spacing = 12

        row.authorizeButton.setContentHuggingPriority(.required, for: .horizontal)
        row.settingsButton.setContentHuggingPriority(.required, for: .horizontal)
        labels.setHuggingPriority(.defaultLow, for: .horizontal)

        return container
    }

    @objc private func handleAuthorize(_ sender: NSButton) {
        guard let identifier = sender.identifier?.rawValue,
              let row = permissionRows.first(where: { $0.kind.title == identifier }) else {
            return
        }
        onRequestPermission?(row.kind)
    }

    @objc private func handleOpenSystemSettings(_ sender: NSButton) {
        guard let identifier = sender.identifier?.rawValue,
              let row = permissionRows.first(where: { $0.kind.title == identifier }) else {
            return
        }
        onOpenSystemSettings?(row.kind)
    }

    @objc private func handleRetryServer() {
        onRetryServerCheck?()
    }

    @objc private func handleOpenConfigDirectory() {
        onOpenConfigDirectory?()
    }
}
