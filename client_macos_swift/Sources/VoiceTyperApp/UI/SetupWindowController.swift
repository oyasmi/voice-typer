import AppKit
import Foundation

enum SetupTab: Int {
    case permissions
    case connection
    case advanced

    var title: String {
        switch self {
        case .permissions:
            return "权限"
        case .connection:
            return "连接与热键"
        case .advanced:
            return "用户热词"
        }
    }
}

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
    }
}

private enum SetupWindowValidationError: LocalizedError {
    case emptyHost
    case invalidPort
    case emptyHotkeyKey

    var errorDescription: String? {
        switch self {
        case .emptyHost:
            return "服务地址不能为空。"
        case .invalidPort:
            return "端口必须是 1 到 65535 之间的数字。"
        case .emptyHotkeyKey:
            return "组合键模式下必须填写主键。"
        }
    }
}

@MainActor
final class SetupWindowController: NSWindowController, NSTabViewDelegate, NSTextViewDelegate, NSTextFieldDelegate, NSWindowDelegate {
    var onRequestPermission: ((PermissionKind) -> Void)?
    var onOpenSystemSettings: ((PermissionKind) -> Void)?
    var onRetryServerCheck: (() -> Void)?
    var onTestServerConnection: ((ServerConfig) async -> Bool)?
    var onSaveConfig: ((AppConfig) async throws -> Void)?
    var onSaveHotwords: ((String) async throws -> Void)?

    private let tabView = NSTabView()
    private let versionLabel = NSTextField(labelWithString: "版本 \(AppConstants.version)")
    private let closeButton = NSButton(title: "关闭", target: nil, action: nil)

    private let serverTitleLabel = NSTextField(labelWithString: "服务连接")
    private let serverIndicatorLabel = NSTextField(labelWithString: "●")
    private let serverStatusLabel = NSTextField(labelWithString: "检查中")
    private let permissionHotkeyBadgeLabel = NSTextField(labelWithString: "热键  Fn🌐")
    private let serverRetryButton = NSButton(title: "重试连接", target: nil, action: nil)
    private let summaryCardView = NSView()
    private let summaryTitleLabel = NSTextField(labelWithString: "仍需完成检查项")
    private let continueLabel = NSTextField(wrappingLabelWithString: "请先完成未通过项，再开始使用。")

    private let hostField = NSTextField(string: "")
    private let portField = NSTextField(string: "")
    private let apiKeyField = NSSecureTextField(string: "")
    private let streamingButton = NSButton(checkboxWithTitle: "流式识别（推荐，低延迟）", target: nil, action: nil)
    private let llmRecorrectButton = NSButton(checkboxWithTitle: "启用 LLM 纠错", target: nil, action: nil)
    private let hotkeyModeControl = NSSegmentedControl(labels: ["Fn", "组合键"], trackingMode: .selectOne, target: nil, action: nil)
    private let modifierControlButton = NSButton(checkboxWithTitle: "Control", target: nil, action: nil)
    private let modifierOptionButton = NSButton(checkboxWithTitle: "Option", target: nil, action: nil)
    private let modifierCommandButton = NSButton(checkboxWithTitle: "Command", target: nil, action: nil)
    private let modifierShiftButton = NSButton(checkboxWithTitle: "Shift", target: nil, action: nil)
    private let hotkeyKeyField = NSTextField(string: "")
    private let hotkeyPreviewLabel = NSTextField(labelWithString: "当前热键：Fn🌐")
    private let connectionMessageLabel = NSTextField(wrappingLabelWithString: "")
    private let testConnectionButton = NSButton(title: "测试连接", target: nil, action: nil)
    private let saveConfigButton = NSButton(title: "保存并应用", target: nil, action: nil)

    private let hotwordsTextView = NSTextView()
    private let hotwordsInfoLabel = NSTextField(wrappingLabelWithString: "")
    private let hotwordsCountLabel = NSTextField(labelWithString: "词条数：0")
    private let hotwordsMessageLabel = NSTextField(wrappingLabelWithString: "")
    private let reloadHotwordsButton = NSButton(title: "重新加载", target: nil, action: nil)
    private let saveHotwordsButton = NSButton(title: "保存并应用", target: nil, action: nil)

    private var hasBuiltUI = false
    private var tabItems: [SetupTab: NSTabViewItem] = [:]
    private var selectedTab: SetupTab = .permissions
    private var loadedConfig = AppConfig()
    private var loadedManagedHotwordsText = ""
    private var additionalHotwordFileCount = 0
    private var preferredWindowFrame: NSRect?

    private lazy var permissionRows: [PermissionRow] = PermissionKind.allCases.map {
        PermissionRow(
            kind: $0,
            target: self,
            authorizeAction: #selector(handleAuthorize),
            settingsAction: #selector(handleOpenSystemSettings)
        )
    }

    convenience init() {
        let contentRect = NSRect(x: 0, y: 0, width: 760, height: 640)
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
        window.delegate = self
        preferredWindowFrame = window.frame
        ensureUIBuilt()
    }

    func loadEditableContent(config: AppConfig, managedHotwordsText: String, additionalHotwordFileCount: Int) {
        ensureUIBuilt()

        loadedConfig = config
        loadedManagedHotwordsText = managedHotwordsText
        self.additionalHotwordFileCount = additionalHotwordFileCount

        hostField.stringValue = config.server.host
        portField.stringValue = String(config.server.port)
        apiKeyField.stringValue = config.server.apiKey
        streamingButton.state = config.server.streaming ? .on : .off
        llmRecorrectButton.state = config.server.llmRecorrect ? .on : .off

        if config.hotkey.key.lowercased() == "fn" {
            hotkeyModeControl.selectedSegment = 0
            hotkeyKeyField.stringValue = ""
        } else {
            hotkeyModeControl.selectedSegment = 1
            hotkeyKeyField.stringValue = config.hotkey.key
        }

        let modifiers = Set(config.hotkey.modifiers.map { $0.lowercased() })
        modifierControlButton.state = modifiers.contains("ctrl") || modifiers.contains("control") ? .on : .off
        modifierOptionButton.state = modifiers.contains("alt") || modifiers.contains("option") ? .on : .off
        modifierCommandButton.state = modifiers.contains("cmd") || modifiers.contains("command") ? .on : .off
        modifierShiftButton.state = modifiers.contains("shift") ? .on : .off

        hotwordsTextView.string = managedHotwordsText
        hotwordsMessageLabel.stringValue = ""
        connectionMessageLabel.stringValue = ""

        updateHotkeyEditorState()
        updateHotkeyPreview()
        updateHotwordsMeta()
    }

    func updatePermissions(snapshot: PermissionSnapshot, serviceReady: Bool, hotkeyDisplay: String, serverStatus: String) {
        ensureUIBuilt()

        for row in permissionRows {
            let status = snapshot.status(for: row.kind)
            row.statusLabel.stringValue = status.displayText
            row.authorizeButton.isEnabled = status != .authorized
            row.settingsButton.isEnabled = status != .authorized
            applyStatusStyle(for: row, status: status)
        }

        serverIndicatorLabel.textColor = serviceReady ? .systemGreen : .systemRed
        serverStatusLabel.stringValue = serverStatus
        permissionHotkeyBadgeLabel.stringValue = "热键  \(hotkeyDisplay)"

        if snapshot.allRequiredGranted && serviceReady {
            summaryTitleLabel.stringValue = "全部检查通过"
            continueLabel.stringValue = "权限和服务连接都已就绪。现在可以直接关闭本窗口并开始使用 VoiceTyper。"
            applyCardStyle(
                summaryCardView,
                backgroundColor: NSColor.systemGreen.withAlphaComponent(0.12),
                borderColor: NSColor.systemGreen.withAlphaComponent(0.24)
            )
        } else {
            summaryTitleLabel.stringValue = "仍需完成检查项"
            continueLabel.stringValue = "请先完成未授权项或修复服务连接问题。处理完成后，本窗口会自动更新状态。"
            applyCardStyle(
                summaryCardView,
                backgroundColor: NSColor.systemOrange.withAlphaComponent(0.12),
                borderColor: NSColor.systemOrange.withAlphaComponent(0.24)
            )
        }
    }

    func selectTab(_ tab: SetupTab) {
        ensureUIBuilt()
        guard let item = tabItems[tab] else {
            return
        }
        selectedTab = tab
        tabView.selectTabViewItem(item)
    }

    override func showWindow(_ sender: Any?) {
        presentWindow()
    }

    func presentWindow() {
        ensureUIBuilt()
        guard let window else {
            return
        }
        if let preferredWindowFrame {
            window.setFrame(preferredWindowFrame, display: false)
        }
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
    }

    func windowDidMove(_ notification: Notification) {
        guard let window else {
            return
        }
        preferredWindowFrame = window.frame
    }

    func tabView(_ tabView: NSTabView, didSelect tabViewItem: NSTabViewItem?) {
        guard let identifier = tabViewItem?.identifier as? Int,
              let tab = SetupTab(rawValue: identifier) else {
            return
        }
        selectedTab = tab
    }

    func textDidChange(_ notification: Notification) {
        if notification.object as AnyObject? === hotwordsTextView {
            updateHotwordsMeta()
        }
    }

    func controlTextDidChange(_ obj: Notification) {
        guard let field = obj.object as? NSTextField, field === hotkeyKeyField else {
            return
        }
        updateHotkeyPreview()
    }

    private func ensureUIBuilt() {
        guard !hasBuiltUI else {
            return
        }
        hasBuiltUI = true
        buildUI()
    }

    private func buildUI() {
        guard let contentView = window?.contentView else {
            return
        }

        contentView.wantsLayer = true
        contentView.layer?.backgroundColor = NSColor.windowBackgroundColor.cgColor

        tabView.translatesAutoresizingMaskIntoConstraints = false
        tabView.tabViewType = .topTabsBezelBorder
        tabView.delegate = self

        closeButton.target = self
        closeButton.action = #selector(handleClose)
        closeButton.bezelStyle = .rounded
        closeButton.controlSize = .large
        closeButton.font = .systemFont(ofSize: 14, weight: .semibold)

        versionLabel.font = .systemFont(ofSize: 11)
        versionLabel.textColor = .tertiaryLabelColor

        let footerRow = makeFooterRow()
        footerRow.translatesAutoresizingMaskIntoConstraints = false

        contentView.addSubview(tabView)
        contentView.addSubview(footerRow)

        NSLayoutConstraint.activate([
            tabView.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 18),
            tabView.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -18),
            tabView.topAnchor.constraint(equalTo: contentView.topAnchor, constant: 16),
            tabView.bottomAnchor.constraint(equalTo: footerRow.topAnchor, constant: -16),

            footerRow.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 18),
            footerRow.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -18),
            footerRow.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -16),
            footerRow.heightAnchor.constraint(greaterThanOrEqualToConstant: 44),
        ])

        buildPermissionsTab()
        buildConnectionTab()
        buildAdvancedTab()
        selectTab(selectedTab)
    }

    private func buildPermissionsTab() {
        let stack = makePageStack()

        for row in permissionRows {
            stack.addArrangedSubview(makePermissionCard(for: row))
        }

        stack.addArrangedSubview(makePermissionServerCard())
        stack.addArrangedSubview(makeSummaryCard())

        installTabContent(stack, for: .permissions)
    }

    private func buildConnectionTab() {
        configureConnectionControls()

        let headerLabel = NSTextField(labelWithString: "服务连接")
        headerLabel.font = .systemFont(ofSize: 16, weight: .semibold)
        headerLabel.alignment = .left

        let serverGrid = NSGridView(views: [
            [makeFormLabel("服务地址"), hostField],
            [makeFormLabel("端口"), portField],
            [makeFormLabel("API Key"), apiKeyField],
        ])
        serverGrid.rowSpacing = 12
        serverGrid.columnSpacing = 12
        serverGrid.column(at: 0).xPlacement = .trailing
        serverGrid.column(at: 1).xPlacement = .fill
        hostField.widthAnchor.constraint(greaterThanOrEqualToConstant: 460).isActive = true
        portField.widthAnchor.constraint(greaterThanOrEqualToConstant: 140).isActive = true

        let streamingRow = NSStackView(views: [streamingButton, makeFlexibleSpacer()])
        streamingRow.orientation = .horizontal
        streamingRow.alignment = .centerY
        streamingRow.spacing = 8

        let streamingHint = NSTextField(wrappingLabelWithString: "流式模式通过 WebSocket 实时回传识别结果，延迟更低；非流式模式支持热词，兼容旧服务端。")
        streamingHint.maximumNumberOfLines = 0
        streamingHint.font = .systemFont(ofSize: 12)
        streamingHint.textColor = .secondaryLabelColor

        let llmRow = NSStackView(views: [llmRecorrectButton, makeFlexibleSpacer()])
        llmRow.orientation = .horizontal
        llmRow.alignment = .centerY
        llmRow.spacing = 8

        let serverButtonRow = alignedTrailingRow(with: [testConnectionButton, saveConfigButton])

        let hotkeyHeader = NSTextField(labelWithString: "热键")
        hotkeyHeader.font = .systemFont(ofSize: 16, weight: .semibold)
        hotkeyHeader.alignment = .left

        let modifiersRow = NSStackView(views: [
            modifierControlButton,
            modifierOptionButton,
            modifierCommandButton,
            modifierShiftButton,
            makeFlexibleSpacer(),
        ])
        modifiersRow.orientation = .horizontal
        modifiersRow.alignment = .centerY
        modifiersRow.spacing = 12

        let hotkeyGrid = NSGridView(views: [
            [makeFormLabel("热键模式"), makeFillContainer(for: hotkeyModeControl)],
            [makeFormLabel("修饰键"), makeFillContainer(for: modifiersRow)],
            [makeFormLabel("主键"), makeFillContainer(for: hotkeyKeyField)],
            [makeFormLabel("预览"), makeFillContainer(for: hotkeyPreviewLabel)],
        ])
        hotkeyGrid.rowSpacing = 12
        hotkeyGrid.columnSpacing = 12
        hotkeyGrid.column(at: 0).xPlacement = .trailing
        hotkeyGrid.column(at: 1).xPlacement = .fill

        let hotkeyHint = NSTextField(wrappingLabelWithString: "推荐默认使用 Fn；如果切换为组合键，请填写一个主键，并按需勾选修饰键。")
        hotkeyHint.maximumNumberOfLines = 0
        hotkeyHint.font = .systemFont(ofSize: 12)
        hotkeyHint.textColor = .secondaryLabelColor

        let separator = NSBox()
        separator.boxType = .separator

        let stack = makePageStack()
        stack.addArrangedSubview(headerLabel)
        stack.addArrangedSubview(serverGrid)
        stack.addArrangedSubview(streamingRow)
        stack.addArrangedSubview(streamingHint)
        stack.addArrangedSubview(llmRow)
        stack.addArrangedSubview(serverButtonRow)
        stack.addArrangedSubview(connectionMessageLabel)
        stack.addArrangedSubview(separator)
        stack.addArrangedSubview(hotkeyHeader)
        stack.addArrangedSubview(hotkeyGrid)
        stack.addArrangedSubview(hotkeyHint)

        headerLabel.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        serverGrid.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        streamingRow.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        streamingHint.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        llmRow.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        serverButtonRow.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        connectionMessageLabel.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        separator.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        hotkeyHeader.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        hotkeyGrid.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        hotkeyHint.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true

        installTabContent(stack, for: .connection)
    }

    private func buildAdvancedTab() {
        hotwordsTextView.isRichText = false
        hotwordsTextView.isAutomaticQuoteSubstitutionEnabled = false
        hotwordsTextView.isAutomaticTextReplacementEnabled = false
        hotwordsTextView.isContinuousSpellCheckingEnabled = false
        hotwordsTextView.font = .monospacedSystemFont(ofSize: 13, weight: .regular)
        hotwordsTextView.delegate = self
        hotwordsTextView.textContainerInset = NSSize(width: 12, height: 12)
        hotwordsTextView.textContainer?.widthTracksTextView = true
        hotwordsTextView.isHorizontallyResizable = false
        hotwordsTextView.isVerticallyResizable = true
        hotwordsTextView.autoresizingMask = [.width]

        let scrollView = NSScrollView()
        scrollView.hasVerticalScroller = true
        scrollView.autohidesScrollers = true
        scrollView.borderType = .bezelBorder
        scrollView.documentView = hotwordsTextView
        scrollView.heightAnchor.constraint(equalToConstant: 360).isActive = true

        hotwordsInfoLabel.maximumNumberOfLines = 0
        hotwordsInfoLabel.font = .systemFont(ofSize: 12)
        hotwordsInfoLabel.textColor = .secondaryLabelColor

        hotwordsCountLabel.font = .systemFont(ofSize: 12, weight: .medium)
        hotwordsCountLabel.textColor = .secondaryLabelColor

        hotwordsMessageLabel.maximumNumberOfLines = 0
        hotwordsMessageLabel.font = .systemFont(ofSize: 12)
        hotwordsMessageLabel.textColor = .secondaryLabelColor

        reloadHotwordsButton.target = self
        reloadHotwordsButton.action = #selector(handleReloadHotwords)
        reloadHotwordsButton.bezelStyle = .rounded
        applySecondaryButtonStyle(reloadHotwordsButton)

        saveHotwordsButton.target = self
        saveHotwordsButton.action = #selector(handleSaveHotwords)
        saveHotwordsButton.bezelStyle = .rounded
        applyPrimaryButtonStyle(saveHotwordsButton)

        let metaRow = NSStackView(views: [hotwordsCountLabel, makeFlexibleSpacer()])
        metaRow.orientation = .horizontal
        metaRow.alignment = .centerY
        metaRow.spacing = 8

        let actionRow = alignedTrailingRow(with: [reloadHotwordsButton, saveHotwordsButton])

        let stack = makePageStack()
        let headerLabel = NSTextField(labelWithString: "编辑主热词文件")
        headerLabel.font = .systemFont(ofSize: 16, weight: .semibold)
        headerLabel.alignment = .left
        stack.addArrangedSubview(headerLabel)
        stack.addArrangedSubview(hotwordsInfoLabel)
        stack.addArrangedSubview(scrollView)
        stack.addArrangedSubview(metaRow)
        stack.addArrangedSubview(actionRow)
        stack.addArrangedSubview(hotwordsMessageLabel)

        headerLabel.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        hotwordsInfoLabel.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        scrollView.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        metaRow.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        actionRow.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        hotwordsMessageLabel.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true

        installTabContent(stack, for: .advanced)
    }

    private func installTabContent(_ content: NSView, for tab: SetupTab) {
        content.translatesAutoresizingMaskIntoConstraints = false

        let container = NSView()
        container.addSubview(content)

        NSLayoutConstraint.activate([
            content.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 18),
            content.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -18),
            content.topAnchor.constraint(equalTo: container.topAnchor, constant: 18),
            content.bottomAnchor.constraint(lessThanOrEqualTo: container.bottomAnchor, constant: -18),
        ])

        let item = NSTabViewItem(identifier: tab.rawValue)
        item.label = tab.title
        item.view = container
        tabItems[tab] = item
        tabView.addTabViewItem(item)
    }

    private func makePageStack() -> NSStackView {
        let stack = NSStackView()
        stack.orientation = .vertical
        stack.alignment = .width
        stack.spacing = 14
        stack.edgeInsets = NSEdgeInsetsZero
        return stack
    }

    private func verticalSectionStack(_ views: [NSView]) -> NSStackView {
        let stack = NSStackView(views: views)
        stack.orientation = .vertical
        stack.alignment = .width
        stack.spacing = 12
        return stack
    }

    private func makePermissionCard(for row: PermissionRow) -> NSView {
        row.titleLabel.font = .systemFont(ofSize: 16, weight: .semibold)
        row.titleLabel.textColor = .labelColor

        row.indicatorLabel.font = .systemFont(ofSize: 14, weight: .bold)
        row.statusLabel.font = .systemFont(ofSize: 14, weight: .medium)

        row.authorizeButton.bezelStyle = .rounded
        row.settingsButton.bezelStyle = .rounded
        applyPrimaryButtonStyle(row.authorizeButton)
        applySecondaryButtonStyle(row.settingsButton)

        let statusRow = NSStackView(views: [row.indicatorLabel, row.statusLabel])
        statusRow.orientation = .horizontal
        statusRow.alignment = .centerY
        statusRow.spacing = 8

        let infoStack = NSStackView(views: [row.titleLabel, statusRow])
        infoStack.orientation = .vertical
        infoStack.alignment = .leading
        infoStack.spacing = 10
        infoStack.setContentCompressionResistancePriority(.required, for: .horizontal)
        infoStack.setContentCompressionResistancePriority(.required, for: .vertical)

        let buttonRow = NSStackView(views: [row.authorizeButton, row.settingsButton])
        buttonRow.orientation = .horizontal
        buttonRow.alignment = .centerY
        buttonRow.spacing = 8

        let contentRow = NSStackView(views: [infoStack, makeFlexibleSpacer(), buttonRow])
        contentRow.orientation = .horizontal
        contentRow.alignment = .centerY
        contentRow.spacing = 14
        contentRow.translatesAutoresizingMaskIntoConstraints = false

        row.cardView.translatesAutoresizingMaskIntoConstraints = false
        applyCardStyle(
            row.cardView,
            backgroundColor: NSColor.controlBackgroundColor,
            borderColor: NSColor.separatorColor.withAlphaComponent(0.35)
        )
        row.cardView.addSubview(contentRow)

        NSLayoutConstraint.activate([
            contentRow.leadingAnchor.constraint(equalTo: row.cardView.leadingAnchor, constant: 18),
            contentRow.trailingAnchor.constraint(equalTo: row.cardView.trailingAnchor, constant: -18),
            contentRow.topAnchor.constraint(equalTo: row.cardView.topAnchor, constant: 16),
            contentRow.bottomAnchor.constraint(equalTo: row.cardView.bottomAnchor, constant: -16),
            row.cardView.heightAnchor.constraint(greaterThanOrEqualToConstant: 92),
        ])

        return row.cardView
    }

    private func makePermissionServerCard() -> NSView {
        serverTitleLabel.font = .systemFont(ofSize: 16, weight: .semibold)
        serverIndicatorLabel.font = .systemFont(ofSize: 14, weight: .bold)
        serverStatusLabel.font = .systemFont(ofSize: 14, weight: .medium)
        serverStatusLabel.textColor = .secondaryLabelColor

        permissionHotkeyBadgeLabel.font = .monospacedSystemFont(ofSize: 13, weight: .semibold)
        permissionHotkeyBadgeLabel.alignment = .center
        applyBadgeStyle(permissionHotkeyBadgeLabel)

        serverRetryButton.target = self
        serverRetryButton.action = #selector(handleRetryServer)
        serverRetryButton.bezelStyle = .rounded
        applySecondaryButtonStyle(serverRetryButton)

        let topRow = NSStackView(views: [serverTitleLabel, permissionHotkeyBadgeLabel, makeFlexibleSpacer(), serverRetryButton])
        topRow.orientation = .horizontal
        topRow.alignment = .centerY
        topRow.spacing = 12

        let statusRow = NSStackView(views: [serverIndicatorLabel, serverStatusLabel])
        statusRow.orientation = .horizontal
        statusRow.alignment = .centerY
        statusRow.spacing = 8
        let contentStack = NSStackView(views: [topRow, statusRow])
        contentStack.orientation = .vertical
        contentStack.alignment = .width
        contentStack.spacing = 12
        contentStack.translatesAutoresizingMaskIntoConstraints = false

        let card = NSView()
        card.translatesAutoresizingMaskIntoConstraints = false
        applyCardStyle(
            card,
            backgroundColor: NSColor.controlAccentColor.withAlphaComponent(0.08),
            borderColor: NSColor.controlAccentColor.withAlphaComponent(0.22)
        )
        card.addSubview(contentStack)

        NSLayoutConstraint.activate([
            contentStack.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 18),
            contentStack.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -18),
            contentStack.topAnchor.constraint(equalTo: card.topAnchor, constant: 18),
            contentStack.bottomAnchor.constraint(equalTo: card.bottomAnchor, constant: -18),
            topRow.widthAnchor.constraint(equalTo: contentStack.widthAnchor),
            statusRow.widthAnchor.constraint(equalTo: contentStack.widthAnchor),
        ])

        return card
    }

    private func makeSummaryCard() -> NSView {
        summaryTitleLabel.font = .systemFont(ofSize: 15, weight: .semibold)
        continueLabel.maximumNumberOfLines = 0
        continueLabel.font = .systemFont(ofSize: 13)
        continueLabel.textColor = .secondaryLabelColor

        let accentView = NSView()
        accentView.translatesAutoresizingMaskIntoConstraints = false
        accentView.wantsLayer = true
        accentView.layer?.backgroundColor = NSColor.systemOrange.cgColor
        accentView.layer?.cornerRadius = 2
        accentView.widthAnchor.constraint(equalToConstant: 4).isActive = true

        let textStack = NSStackView(views: [summaryTitleLabel, continueLabel])
        textStack.orientation = .vertical
        textStack.alignment = .leading
        textStack.spacing = 6

        let contentRow = NSStackView(views: [accentView, textStack])
        contentRow.orientation = .horizontal
        contentRow.alignment = .centerY
        contentRow.spacing = 12
        contentRow.translatesAutoresizingMaskIntoConstraints = false

        summaryCardView.translatesAutoresizingMaskIntoConstraints = false
        summaryCardView.addSubview(contentRow)

        NSLayoutConstraint.activate([
            contentRow.leadingAnchor.constraint(equalTo: summaryCardView.leadingAnchor, constant: 18),
            contentRow.trailingAnchor.constraint(equalTo: summaryCardView.trailingAnchor, constant: -18),
            contentRow.topAnchor.constraint(equalTo: summaryCardView.topAnchor, constant: 16),
            contentRow.bottomAnchor.constraint(equalTo: summaryCardView.bottomAnchor, constant: -16),
            summaryCardView.heightAnchor.constraint(greaterThanOrEqualToConstant: 86),
        ])

        return summaryCardView
    }

    private func makeSectionCard(
        title: String,
        body: NSView,
        backgroundColor: NSColor = .controlBackgroundColor,
        borderColor: NSColor = NSColor.separatorColor.withAlphaComponent(0.35)
    ) -> NSView {
        let titleLabel = NSTextField(labelWithString: title)
        titleLabel.font = .systemFont(ofSize: 15, weight: .semibold)

        let contentStack = NSStackView(views: [titleLabel, body])
        contentStack.orientation = .vertical
        contentStack.alignment = .width
        contentStack.spacing = 12
        contentStack.translatesAutoresizingMaskIntoConstraints = false

        let card = NSView()
        card.translatesAutoresizingMaskIntoConstraints = false
        applyCardStyle(card, backgroundColor: backgroundColor, borderColor: borderColor)
        card.addSubview(contentStack)

        NSLayoutConstraint.activate([
            contentStack.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 18),
            contentStack.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -18),
            contentStack.topAnchor.constraint(equalTo: card.topAnchor, constant: 18),
            contentStack.bottomAnchor.constraint(equalTo: card.bottomAnchor, constant: -18),
        ])

        return card
    }

    private func makeFooterRow() -> NSView {
        closeButton.widthAnchor.constraint(greaterThanOrEqualToConstant: 128).isActive = true
        closeButton.setContentHuggingPriority(.required, for: .horizontal)
        versionLabel.setContentHuggingPriority(.required, for: .horizontal)

        let row = NSStackView(views: [versionLabel, makeFlexibleSpacer(), closeButton])
        row.orientation = .horizontal
        row.alignment = .centerY
        row.spacing = 12
        return row
    }

    private func makeFormLabel(_ title: String) -> NSTextField {
        let label = NSTextField(labelWithString: title)
        label.font = .systemFont(ofSize: 14, weight: .medium)
        label.alignment = .right
        label.widthAnchor.constraint(equalToConstant: 90).isActive = true
        return label
    }

    private func makeFlexibleSpacer() -> NSView {
        let spacer = NSView()
        spacer.translatesAutoresizingMaskIntoConstraints = false
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        spacer.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        return spacer
    }

    private func makeFillContainer(for content: NSView) -> NSView {
        content.translatesAutoresizingMaskIntoConstraints = false

        let container = NSView()
        container.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(content)

        NSLayoutConstraint.activate([
            content.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            content.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            content.topAnchor.constraint(equalTo: container.topAnchor),
            content.bottomAnchor.constraint(equalTo: container.bottomAnchor),
        ])

        return container
    }

    private func alignedTrailingRow(with views: [NSView]) -> NSStackView {
        let buttons = NSStackView(views: views)
        buttons.orientation = .horizontal
        buttons.alignment = .centerY
        buttons.spacing = 8

        let row = NSStackView(views: [makeFlexibleSpacer(), buttons])
        row.orientation = .horizontal
        row.alignment = .centerY
        row.spacing = 0
        return row
    }

    private func configureConnectionControls() {
        hostField.placeholderString = "127.0.0.1"
        portField.placeholderString = "6008"
        apiKeyField.placeholderString = "可选"
        hotkeyKeyField.placeholderString = "例如 space / a / f6"
        hotkeyKeyField.delegate = self

        hotkeyModeControl.selectedSegment = 0
        hotkeyModeControl.target = self
        hotkeyModeControl.action = #selector(handleHotkeyModeChanged)

        [modifierControlButton, modifierOptionButton, modifierCommandButton, modifierShiftButton].forEach {
            $0.target = self
            $0.action = #selector(handleModifierChanged)
        }

        hotkeyPreviewLabel.font = .monospacedSystemFont(ofSize: 14, weight: .semibold)
        hotkeyPreviewLabel.textColor = .controlAccentColor

        connectionMessageLabel.maximumNumberOfLines = 0
        connectionMessageLabel.font = .systemFont(ofSize: 12)
        connectionMessageLabel.textColor = .secondaryLabelColor

        testConnectionButton.target = self
        testConnectionButton.action = #selector(handleTestConnection)
        testConnectionButton.bezelStyle = .rounded
        applySecondaryButtonStyle(testConnectionButton)

        saveConfigButton.target = self
        saveConfigButton.action = #selector(handleSaveConfig)
        saveConfigButton.bezelStyle = .rounded

        applyPrimaryButtonStyle(saveConfigButton)
    }

    private func updateHotkeyEditorState() {
        let isFnMode = hotkeyModeControl.selectedSegment == 0
        hotkeyKeyField.isEnabled = !isFnMode
        modifierControlButton.isEnabled = !isFnMode
        modifierOptionButton.isEnabled = !isFnMode
        modifierCommandButton.isEnabled = !isFnMode
        modifierShiftButton.isEnabled = !isFnMode
    }

    private func updateHotkeyPreview() {
        if hotkeyModeControl.selectedSegment == 0 {
            hotkeyPreviewLabel.stringValue = "当前热键：Fn🌐"
            return
        }

        let parts = currentModifierStrings().map { $0.uppercased() } + [hotkeyKeyField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()]
        let display = parts.filter { !$0.isEmpty }.joined(separator: "+")
        hotkeyPreviewLabel.stringValue = display.isEmpty ? "当前热键：未完成配置" : "当前热键：\(display)"
    }

    private func updateHotwordsMeta() {
        let count = hotwordsTextView.string
            .components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty && !$0.hasPrefix("#") }
            .count
        hotwordsCountLabel.stringValue = "词条数：\(count)"

        if additionalHotwordFileCount > 0 {
            hotwordsInfoLabel.stringValue = "这里编辑的是主热词文件。当前还有 \(additionalHotwordFileCount) 个附加词库会继续参与加载，但不在此处编辑。"
        } else {
            hotwordsInfoLabel.stringValue = "这里编辑的是主热词文件。保存后会立即写回本地词库并重新加载。"
        }
    }

    private func currentModifierStrings() -> [String] {
        var modifiers: [String] = []
        if modifierControlButton.state == .on {
            modifiers.append("ctrl")
        }
        if modifierOptionButton.state == .on {
            modifiers.append("option")
        }
        if modifierCommandButton.state == .on {
            modifiers.append("command")
        }
        if modifierShiftButton.state == .on {
            modifiers.append("shift")
        }
        return modifiers
    }

    private func draftServerConfig() throws -> ServerConfig {
        let host = hostField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !host.isEmpty else {
            throw SetupWindowValidationError.emptyHost
        }

        guard let port = Int(portField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)),
              (1...65535).contains(port) else {
            throw SetupWindowValidationError.invalidPort
        }

        return ServerConfig(
            host: host,
            port: port,
            timeout: loadedConfig.server.timeout,
            apiKey: apiKeyField.stringValue,
            llmRecorrect: llmRecorrectButton.state == .on,
            streaming: streamingButton.state == .on
        )
    }

    private func draftHotkeyConfig() throws -> HotkeyConfig {
        if hotkeyModeControl.selectedSegment == 0 {
            return HotkeyConfig(modifiers: [], key: "fn")
        }

        let key = hotkeyKeyField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !key.isEmpty else {
            throw SetupWindowValidationError.emptyHotkeyKey
        }

        return HotkeyConfig(modifiers: currentModifierStrings(), key: key)
    }

    private func draftConfig() throws -> AppConfig {
        var draft = loadedConfig
        draft.server = try draftServerConfig()
        draft.hotkey = try draftHotkeyConfig()
        return draft
    }

    private func setConnectionMessage(_ text: String, color: NSColor) {
        connectionMessageLabel.stringValue = text
        connectionMessageLabel.textColor = color
    }

    private func setHotwordsMessage(_ text: String, color: NSColor) {
        hotwordsMessageLabel.stringValue = text
        hotwordsMessageLabel.textColor = color
    }

    private func setConnectionActionsEnabled(_ enabled: Bool) {
        testConnectionButton.isEnabled = enabled
        saveConfigButton.isEnabled = enabled
    }

    private func setHotwordsActionsEnabled(_ enabled: Bool) {
        reloadHotwordsButton.isEnabled = enabled
        saveHotwordsButton.isEnabled = enabled
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
        label.drawsBackground = true
        label.backgroundColor = NSColor.controlAccentColor.withAlphaComponent(0.14)
        label.textColor = .controlAccentColor
        label.isBezeled = false
        label.wantsLayer = true
        label.layer?.cornerRadius = 10
        label.layer?.masksToBounds = true
    }

    private func applyPrimaryButtonStyle(_ button: NSButton) {
        button.bezelColor = .controlAccentColor
        button.contentTintColor = .white
    }

    private func applySecondaryButtonStyle(_ button: NSButton) {
        _ = button
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

    @objc private func handleHotkeyModeChanged() {
        updateHotkeyEditorState()
        updateHotkeyPreview()
    }

    @objc private func handleModifierChanged() {
        updateHotkeyPreview()
    }

    @objc private func handleTestConnection() {
        let action = onTestServerConnection

        do {
            let server = try draftServerConfig()
            setConnectionActionsEnabled(false)
            setConnectionMessage("正在测试连接...", color: .secondaryLabelColor)

            Task { @MainActor [weak self] in
                guard let self else { return }
                let isReady = await action?(server) ?? false
                self.setConnectionActionsEnabled(true)
                self.setConnectionMessage(
                    isReady ? "连接成功，识别服务已就绪。" : "连接失败，请检查服务地址、端口和服务端状态。",
                    color: isReady ? .systemGreen : .systemRed
                )
            }
        } catch {
            setConnectionMessage(error.localizedDescription, color: .systemRed)
        }
    }

    @objc private func handleSaveConfig() {
        do {
            let config = try draftConfig()
            guard let onSaveConfig else {
                return
            }

            setConnectionActionsEnabled(false)
            setConnectionMessage("正在保存并应用设置...", color: .secondaryLabelColor)

            Task { @MainActor [weak self] in
                guard let self else { return }
                do {
                    try await onSaveConfig(config)
                    self.setConnectionMessage("设置已保存并生效。", color: .systemGreen)
                } catch {
                    self.setConnectionMessage("保存失败：\(error.localizedDescription)", color: .systemRed)
                }
                self.setConnectionActionsEnabled(true)
            }
        } catch {
            setConnectionMessage(error.localizedDescription, color: .systemRed)
        }
    }

    @objc private func handleReloadHotwords() {
        hotwordsTextView.string = loadedManagedHotwordsText
        setHotwordsMessage("已重新加载当前磁盘中的热词内容。", color: .secondaryLabelColor)
        updateHotwordsMeta()
    }

    @objc private func handleSaveHotwords() {
        guard let onSaveHotwords else {
            return
        }

        let text = hotwordsTextView.string
        setHotwordsActionsEnabled(false)
        setHotwordsMessage("正在保存并应用热词...", color: .secondaryLabelColor)

        Task { @MainActor [weak self] in
            guard let self else { return }
            do {
                try await onSaveHotwords(text)
                self.setHotwordsMessage("热词已保存并重新加载。", color: .systemGreen)
            } catch {
                self.setHotwordsMessage("保存失败：\(error.localizedDescription)", color: .systemRed)
            }
            self.setHotwordsActionsEnabled(true)
        }
    }

    @objc private func handleClose() {
        window?.orderOut(nil)
    }
}
