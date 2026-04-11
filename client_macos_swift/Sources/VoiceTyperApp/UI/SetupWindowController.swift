import AppKit
import Foundation

@MainActor
private final class PermissionRow {
    let kind: PermissionKind
    let cardView = NSView()
    let titleLabel: NSTextField
    let indicatorLabel: NSTextField
    let statusLabel: NSTextField
    let authorizeButton: NSButton
    let settingsButton: NSButton

    init(kind: PermissionKind, target: AnyObject, authorizeAction: Selector, settingsAction: Selector) {
        self.kind = kind
        self.titleLabel = NSTextField(labelWithString: kind.title)
        self.indicatorLabel = NSTextField(labelWithString: "●")
        self.statusLabel = NSTextField(labelWithString: "检查中")
        self.authorizeButton = NSButton(title: "授权", target: target, action: authorizeAction)
        self.settingsButton = NSButton(title: "系统设置", target: target, action: settingsAction)
        self.authorizeButton.identifier = NSUserInterfaceItemIdentifier(kind.title)
        self.settingsButton.identifier = NSUserInterfaceItemIdentifier(kind.title)
        self.statusLabel.textColor = .secondaryLabelColor
        self.indicatorLabel.font = .systemFont(ofSize: 13, weight: .bold)
        self.indicatorLabel.textColor = .systemOrange
    }
}

@MainActor
final class SetupWindowController: NSWindowController {
    var onRequestPermission: ((PermissionKind) -> Void)?
    var onOpenSystemSettings: ((PermissionKind) -> Void)?
    var onRetryServerCheck: (() -> Void)?
    var onOpenConfigDirectory: (() -> Void)?

    private let introTitleLabel = NSTextField(labelWithString: "权限与设置")
    private let introLabel = NSTextField(wrappingLabelWithString: "VoiceTyper 需要麦克风、辅助功能和输入监控权限来监听热键、录制语音并自动输入文字。缺失项会影响正常使用。")
    private let serverTitleLabel = NSTextField(labelWithString: "服务连接")
    private let serverIndicatorLabel = NSTextField(labelWithString: "●")
    private let serverStatusLabel = NSTextField(labelWithString: "检查中")
    private let hotkeyBadgeLabel = NSTextField(labelWithString: "热键")
    private let serverRetryButton = NSButton(title: "重试连接", target: nil, action: nil)
    private let configButton = NSButton(title: "打开配置目录", target: nil, action: nil)
    private let summaryCardView = NSView()
    private let summaryIconLabel = NSTextField(labelWithString: "⚠️")
    private let summaryTitleLabel = NSTextField(labelWithString: "仍需完成检查项")
    private let continueLabel = NSTextField(wrappingLabelWithString: "请先完成未通过项，再开始使用。")
    private let versionLabel = NSTextField(labelWithString: "版本 \(AppConstants.version)")
    private let closeButton = NSButton(title: "关闭", target: nil, action: nil)
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
        let contentRect = NSRect(x: 0, y: 0, width: 580, height: 460)
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
            applyStatusStyle(for: row, status: status)
        }

        serverIndicatorLabel.textColor = serviceReady ? .systemGreen : .systemRed
        serverStatusLabel.stringValue = serviceReady
            ? "识别服务已连接，可以直接开始语音输入。"
            : "识别服务未连接，请确认服务端是否已启动。"
        hotkeyBadgeLabel.stringValue = "热键  \(hotkeyDisplay)"

        if snapshot.allRequiredGranted && serviceReady {
            summaryIconLabel.stringValue = "✅"
            summaryTitleLabel.stringValue = "全部检查通过"
            continueLabel.stringValue = "所有权限和服务连接都已就绪，现在可以直接关闭本窗口并开始使用 VoiceTyper。"
            applyCardStyle(summaryCardView, backgroundColor: NSColor.systemGreen.withAlphaComponent(0.12), borderColor: NSColor.systemGreen.withAlphaComponent(0.24))
        } else {
            summaryIconLabel.stringValue = "⚠️"
            summaryTitleLabel.stringValue = "仍需完成检查项"
            continueLabel.stringValue = "请先完成未授权项或修复服务连接问题。处理完成后，本窗口会自动更新状态。"
            applyCardStyle(summaryCardView, backgroundColor: NSColor.systemOrange.withAlphaComponent(0.12), borderColor: NSColor.systemOrange.withAlphaComponent(0.24))
        }
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

        contentView.wantsLayer = true
        contentView.layer?.backgroundColor = NSColor.windowBackgroundColor.cgColor

        introTitleLabel.font = .systemFont(ofSize: 24, weight: .semibold)
        introLabel.maximumNumberOfLines = 0
        introLabel.font = .systemFont(ofSize: 13)
        introLabel.textColor = .secondaryLabelColor
        serverTitleLabel.font = .systemFont(ofSize: 15, weight: .semibold)
        serverIndicatorLabel.font = .systemFont(ofSize: 13, weight: .bold)
        serverStatusLabel.font = .systemFont(ofSize: 13)
        serverStatusLabel.textColor = .secondaryLabelColor
        hotkeyBadgeLabel.font = .monospacedSystemFont(ofSize: 13, weight: .semibold)
        hotkeyBadgeLabel.alignment = .center
        hotkeyBadgeLabel.textColor = .controlAccentColor
        summaryIconLabel.font = .systemFont(ofSize: 24)
        summaryTitleLabel.font = .systemFont(ofSize: 16, weight: .semibold)
        continueLabel.maximumNumberOfLines = 0
        continueLabel.font = .systemFont(ofSize: 13)
        continueLabel.textColor = .secondaryLabelColor
        versionLabel.font = .systemFont(ofSize: 11)
        versionLabel.textColor = .tertiaryLabelColor

        serverRetryButton.target = self
        serverRetryButton.action = #selector(handleRetryServer)
        serverRetryButton.bezelStyle = .rounded
        configButton.target = self
        configButton.action = #selector(handleOpenConfigDirectory)
        configButton.bezelStyle = .rounded
        closeButton.target = self
        closeButton.action = #selector(handleClose)
        closeButton.bezelStyle = .rounded
        closeButton.controlSize = .large
        closeButton.font = .systemFont(ofSize: 14, weight: .semibold)
        closeButton.contentTintColor = .controlAccentColor

        let rootStack = NSStackView()
        rootStack.orientation = .vertical
        rootStack.alignment = .leading
        rootStack.spacing = 16
        rootStack.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(rootStack)

        let headerStack = NSStackView(views: [introTitleLabel, introLabel])
        headerStack.orientation = .vertical
        headerStack.alignment = .leading
        headerStack.spacing = 8
        rootStack.addArrangedSubview(headerStack)
        headerStack.widthAnchor.constraint(equalTo: rootStack.widthAnchor).isActive = true

        for row in permissionRows {
            let card = makePermissionCard(for: row)
            rootStack.addArrangedSubview(card)
            card.widthAnchor.constraint(equalTo: rootStack.widthAnchor).isActive = true
        }

        let serverCard = makeServerCard()
        rootStack.addArrangedSubview(serverCard)
        serverCard.widthAnchor.constraint(equalTo: rootStack.widthAnchor).isActive = true

        let summaryCard = makeSummaryCard()
        rootStack.addArrangedSubview(summaryCard)
        summaryCard.widthAnchor.constraint(equalTo: rootStack.widthAnchor).isActive = true

        let footerRow = makeFooterRow()
        rootStack.addArrangedSubview(footerRow)
        footerRow.widthAnchor.constraint(equalTo: rootStack.widthAnchor).isActive = true

        NSLayoutConstraint.activate([
            rootStack.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 24),
            rootStack.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -24),
            rootStack.topAnchor.constraint(equalTo: contentView.topAnchor, constant: 24),
            rootStack.bottomAnchor.constraint(lessThanOrEqualTo: contentView.bottomAnchor, constant: -24),
        ])
    }

    private func makePermissionCard(for row: PermissionRow) -> NSView {
        row.titleLabel.font = .boldSystemFont(ofSize: 13)
        row.statusLabel.font = .systemFont(ofSize: 13)
        row.authorizeButton.bezelStyle = .rounded
        row.settingsButton.bezelStyle = .rounded

        let statusRow = NSStackView(views: [row.indicatorLabel, row.statusLabel])
        statusRow.orientation = .horizontal
        statusRow.alignment = .centerY
        statusRow.spacing = 6

        let labels = NSStackView(views: [row.titleLabel, statusRow])
        labels.orientation = .vertical
        labels.alignment = .leading
        labels.spacing = 4

        let buttonRow = NSStackView(views: [row.authorizeButton, row.settingsButton])
        buttonRow.orientation = .horizontal
        buttonRow.alignment = .centerY
        buttonRow.spacing = 8

        let spacer = NSView()
        spacer.translatesAutoresizingMaskIntoConstraints = false
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        spacer.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        let container = NSStackView(views: [labels, spacer, buttonRow])
        container.orientation = .horizontal
        container.alignment = .centerY
        container.spacing = 12
        container.translatesAutoresizingMaskIntoConstraints = false

        row.authorizeButton.setContentHuggingPriority(.required, for: .horizontal)
        row.settingsButton.setContentHuggingPriority(.required, for: .horizontal)
        labels.setHuggingPriority(.defaultLow, for: .horizontal)

        row.cardView.translatesAutoresizingMaskIntoConstraints = false
        applyCardStyle(row.cardView, backgroundColor: NSColor.controlBackgroundColor, borderColor: NSColor.separatorColor.withAlphaComponent(0.35))
        row.cardView.addSubview(container)

        NSLayoutConstraint.activate([
            container.leadingAnchor.constraint(equalTo: row.cardView.leadingAnchor, constant: 16),
            container.trailingAnchor.constraint(equalTo: row.cardView.trailingAnchor, constant: -16),
            container.topAnchor.constraint(equalTo: row.cardView.topAnchor, constant: 14),
            container.bottomAnchor.constraint(equalTo: row.cardView.bottomAnchor, constant: -14),
        ])

        return row.cardView
    }

    private func makeServerCard() -> NSView {
        let titleRow = NSStackView(views: [serverTitleLabel, NSView(), hotkeyBadgeLabel])
        titleRow.orientation = .horizontal
        titleRow.alignment = .centerY
        titleRow.spacing = 12

        hotkeyBadgeLabel.setContentHuggingPriority(.required, for: .horizontal)
        hotkeyBadgeLabel.setContentCompressionResistancePriority(.required, for: .horizontal)
        applyBadgeStyle(hotkeyBadgeLabel)

        let statusRow = NSStackView(views: [serverIndicatorLabel, serverStatusLabel])
        statusRow.orientation = .horizontal
        statusRow.alignment = .centerY
        statusRow.spacing = 6

        let buttonRow = NSStackView(views: [serverRetryButton, configButton])
        buttonRow.orientation = .horizontal
        buttonRow.alignment = .centerY
        buttonRow.spacing = 8

        let buttonSpacer = NSView()
        buttonSpacer.translatesAutoresizingMaskIntoConstraints = false
        buttonSpacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        buttonSpacer.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        let actionRow = NSStackView(views: [buttonSpacer, buttonRow])
        actionRow.orientation = .horizontal
        actionRow.alignment = .centerY
        actionRow.spacing = 0

        let bodyStack = NSStackView(views: [titleRow, statusRow, actionRow])
        bodyStack.orientation = .vertical
        bodyStack.alignment = .leading
        bodyStack.spacing = 10
        bodyStack.translatesAutoresizingMaskIntoConstraints = false

        let cardView = NSView()
        cardView.translatesAutoresizingMaskIntoConstraints = false
        applyCardStyle(
            cardView,
            backgroundColor: NSColor.controlAccentColor.withAlphaComponent(0.08),
            borderColor: NSColor.controlAccentColor.withAlphaComponent(0.22)
        )
        cardView.addSubview(bodyStack)

        NSLayoutConstraint.activate([
            bodyStack.leadingAnchor.constraint(equalTo: cardView.leadingAnchor, constant: 16),
            bodyStack.trailingAnchor.constraint(equalTo: cardView.trailingAnchor, constant: -16),
            bodyStack.topAnchor.constraint(equalTo: cardView.topAnchor, constant: 14),
            bodyStack.bottomAnchor.constraint(equalTo: cardView.bottomAnchor, constant: -14),
        ])

        let container = NSView()
        container.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(cardView)

        NSLayoutConstraint.activate([
            cardView.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            cardView.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            cardView.topAnchor.constraint(equalTo: container.topAnchor),
            cardView.bottomAnchor.constraint(equalTo: container.bottomAnchor),
        ])

        return container
    }

    private func makeSummaryCard() -> NSView {
        let labelStack = NSStackView(views: [summaryTitleLabel, continueLabel])
        labelStack.orientation = .vertical
        labelStack.alignment = .leading
        labelStack.spacing = 6

        let contentStack = NSStackView(views: [summaryIconLabel, labelStack])
        contentStack.orientation = .horizontal
        contentStack.alignment = .centerY
        contentStack.spacing = 12
        contentStack.translatesAutoresizingMaskIntoConstraints = false

        summaryCardView.translatesAutoresizingMaskIntoConstraints = false
        summaryCardView.addSubview(contentStack)

        NSLayoutConstraint.activate([
            contentStack.leadingAnchor.constraint(equalTo: summaryCardView.leadingAnchor, constant: 16),
            contentStack.trailingAnchor.constraint(equalTo: summaryCardView.trailingAnchor, constant: -16),
            contentStack.topAnchor.constraint(equalTo: summaryCardView.topAnchor, constant: 14),
            contentStack.bottomAnchor.constraint(equalTo: summaryCardView.bottomAnchor, constant: -14),
        ])

        return summaryCardView
    }

    private func makeFooterRow() -> NSView {
        closeButton.widthAnchor.constraint(greaterThanOrEqualToConstant: 128).isActive = true
        closeButton.setContentHuggingPriority(.required, for: .horizontal)
        versionLabel.setContentHuggingPriority(.required, for: .horizontal)

        let spacer = NSView()
        spacer.translatesAutoresizingMaskIntoConstraints = false
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        spacer.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        let row = NSStackView(views: [versionLabel, spacer, closeButton])
        row.orientation = .horizontal
        row.alignment = .centerY
        row.spacing = 12
        return row
    }

    private func applyStatusStyle(for row: PermissionRow, status: PermissionStatus) {
        switch status {
        case .authorized:
            row.indicatorLabel.textColor = .systemGreen
            row.statusLabel.textColor = .systemGreen
        case .denied:
            row.indicatorLabel.textColor = .systemRed
            row.statusLabel.textColor = .systemRed
        case .notDetermined:
            row.indicatorLabel.textColor = .systemOrange
            row.statusLabel.textColor = .systemOrange
        }
    }

    private func applyCardStyle(_ view: NSView, backgroundColor: NSColor, borderColor: NSColor) {
        view.wantsLayer = true
        view.layer?.backgroundColor = backgroundColor.cgColor
        view.layer?.cornerRadius = 14
        view.layer?.borderWidth = 1
        view.layer?.borderColor = borderColor.cgColor
    }

    private func applyBadgeStyle(_ label: NSTextField) {
        label.wantsLayer = true
        label.drawsBackground = true
        label.backgroundColor = NSColor.controlAccentColor.withAlphaComponent(0.14)
        label.textColor = .controlAccentColor
        label.isBezeled = false
        label.layer?.cornerRadius = 10
        label.layer?.masksToBounds = true
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

    @objc private func handleClose() {
        window?.orderOut(nil)
    }
}
