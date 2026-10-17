import AppKit
import Foundation

/// 菜单栏「快捷切换」需要的当前配置快照（R8-G1）。
struct QuickSettingsState: Equatable {
    var language: ASRLanguage
    /// `audio.input_device` 的原始值：`auto` / `system` / 设备 UID。
    var inputDevice: String
    var llmEnabled: Bool
    /// 已填写 Base URL；没填时无法开启智能校对。
    var llmConfigured: Bool
    /// 听写进行中：这些项禁用（`applyConfig` 本就会拒绝）。
    var isBusy: Bool
}

@MainActor
final class StatusBarController: NSObject, NSMenuDelegate {
    var onOpenSetup: (() -> Void)?
    var onOpenOnboarding: (() -> Void)?
    var onCheckForUpdates: (() -> Void)?
    var onTogglePause: (() -> Void)?
    var onQuit: (() -> Void)?
    /// 菜单打开前取一次最近结果（R8-A2）；每次现取，不缓存文本。
    var historyEntriesProvider: (() -> [DictationHistory.Entry])?
    var onCopyHistoryEntry: ((UUID) -> Void)?
    var onClearHistory: (() -> Void)?
    /// 菜单打开时现算一次安全输入说明（R8-B2），不轮询。
    var secureInputWarningProvider: (() -> String?)?
    var quickSettingsProvider: (() -> QuickSettingsState)?
    var onSelectLanguage: ((ASRLanguage) -> Void)?
    var onSelectInputDevice: ((String) -> Void)?
    var onToggleLLM: (() -> Void)?

    private let statusItem = NSStatusBar.system.statusItem(withLength: 24)
    private let menu = NSMenu()
    /// 状态栏图标宿主。用 NSImageView 而非 button.image，以支持 SF Symbol 动效
    /// （NSStatusBarButton 本身不支持 addSymbolEffect）。
    private let iconView = NSImageView()
    /// 菜单是否处于打开态。打开时图标需反色以在高亮背景上可读。
    private var menuIsOpen = false

    private let headerView = StatusMenuHeaderView()
    private let headerItem = NSMenuItem()
    private let copyLastMenuItem = NSMenuItem(title: L("复制上一次结果"), action: #selector(handleCopyLastResult), keyEquivalent: "")
    private let recentMenuItem = NSMenuItem(title: L("最近结果"), action: nil, keyEquivalent: "")
    private let recentSubmenu = NSMenu()
    private let pauseMenuItem = NSMenuItem(title: L("暂停听写"), action: #selector(handleTogglePause), keyEquivalent: "")
    private let languageMenuItem = NSMenuItem(title: L("识别语言"), action: nil, keyEquivalent: "")
    private let languageSubmenu = NSMenu()
    private let microphoneMenuItem = NSMenuItem(title: L("麦克风"), action: nil, keyEquivalent: "")
    private let microphoneSubmenu = NSMenu()
    private let llmMenuItem = NSMenuItem(title: L("智能校对"), action: #selector(handleToggleLLM), keyEquivalent: "")
    private let setupMenuItem = NSMenuItem(title: L("权限与设置…"), action: #selector(handleOpenSetup), keyEquivalent: ",")
    private let onboardingMenuItem = NSMenuItem(title: L("使用引导…"), action: #selector(handleOpenOnboarding), keyEquivalent: "")
    private let checkUpdatesMenuItem = NSMenuItem(title: L("检查更新…"), action: #selector(handleCheckForUpdates), keyEquivalent: "")
    private let launchAtLoginMenuItem = NSMenuItem(title: L("开机自启"), action: #selector(handleToggleLaunchAtLogin), keyEquivalent: "")
    private let aboutMenuItem = NSMenuItem(title: LF("关于 %@", AppConstants.appName), action: #selector(handleAbout), keyEquivalent: "")
    private let quitMenuItem = NSMenuItem(title: L("退出"), action: #selector(handleQuit), keyEquivalent: "q")

    /// 已应用到状态栏图标的外观。用于避免重复设置图标而打断符号动效。
    ///
    /// 比较的是"外观"而不是 `AppState`：`.downloadingModel(Double)` 带着进度做载荷，
    /// 每 200ms 就是一个"不相等"的新状态，用 AppState 全等判断会让每次进度更新都走一遍
    /// `removeAllSymbolEffects` + `addSymbolEffect`，把本该连续的脉冲动效每 200ms 打断
    /// 重建一次——恰好是这段代码原本想避免的事（B1）。
    private var appliedAppearance: StatusAppearance?

    /// 状态栏图标的三要素。符号 / 着色 / 动效都不变时，就不需要重设图标。
    private struct StatusAppearance: Equatable {
        let symbolName: String
        let tint: StatusTint
        let effect: IconAnimation
    }

    private enum StatusTint {
        /// 随菜单栏深浅自适应的中性色。
        case neutral
        case attention
        case warning
        case progress
        case muted

        var color: NSColor {
            switch self {
            case .neutral: return .labelColor
            case .attention: return .systemRed
            case .warning: return .systemOrange
            case .progress: return .systemYellow
            case .muted: return .secondaryLabelColor
            }
        }
    }

    /// 刻意不叫 `SymbolEffect`：那是 Symbols 框架里协议的名字，在本类作用域内同名会让
    /// `addSymbolEffect(.pulse, …)` 这类调用的名称查找变得含糊，读代码的人也容易看错。
    private enum IconAnimation {
        case none
        case pulse
        case variableColor
    }

    override init() {
        super.init()

        headerItem.view = headerView

        [pauseMenuItem, setupMenuItem, onboardingMenuItem, checkUpdatesMenuItem,
         launchAtLoginMenuItem, aboutMenuItem, quitMenuItem, copyLastMenuItem, llmMenuItem].forEach { $0.target = self }

        languageMenuItem.submenu = languageSubmenu
        microphoneMenuItem.submenu = microphoneSubmenu
        languageMenuItem.image = NSImage(systemSymbolName: "globe", accessibilityDescription: nil)
        microphoneMenuItem.image = NSImage(systemSymbolName: "mic", accessibilityDescription: nil)
        llmMenuItem.image = NSImage(systemSymbolName: "sparkles", accessibilityDescription: nil)

        copyLastMenuItem.image = NSImage(systemSymbolName: "doc.on.clipboard", accessibilityDescription: nil)
        recentMenuItem.image = NSImage(systemSymbolName: "clock.arrow.circlepath", accessibilityDescription: nil)
        recentMenuItem.submenu = recentSubmenu
        copyLastMenuItem.isEnabled = false
        recentMenuItem.isEnabled = false

        setupMenuItem.image = NSImage(systemSymbolName: "gearshape", accessibilityDescription: nil)
        onboardingMenuItem.image = NSImage(systemSymbolName: "sparkles", accessibilityDescription: nil)
        checkUpdatesMenuItem.image = NSImage(systemSymbolName: "arrow.triangle.2.circlepath", accessibilityDescription: nil)
        aboutMenuItem.image = NSImage(systemSymbolName: "info.circle", accessibilityDescription: nil)
        quitMenuItem.image = NSImage(systemSymbolName: "power", accessibilityDescription: nil)
        updatePauseItemImage(isPaused: false)

        // 刻意不提供「打开配置目录」：config.yaml 是内部存储而非配置入口。所有配置项都在
        // 设置窗口里有界面，手改 YAML 只会绕过 UI 校验、还要重启才生效（B9，见 ConfigStore）。
        menu.items = [
            headerItem,
            .separator(),
            copyLastMenuItem,
            recentMenuItem,
            .separator(),
            pauseMenuItem,
            languageMenuItem,
            microphoneMenuItem,
            llmMenuItem,
            launchAtLoginMenuItem,
            .separator(),
            setupMenuItem,
            onboardingMenuItem,
            .separator(),
            checkUpdatesMenuItem,
            aboutMenuItem,
            .separator(),
            quitMenuItem,
        ]

        menu.delegate = self
        statusItem.menu = menu

        if let button = statusItem.button {
            iconView.imageScaling = .scaleProportionallyDown
            iconView.translatesAutoresizingMaskIntoConstraints = false
            button.image = nil
            button.addSubview(iconView)
            NSLayoutConstraint.activate([
                iconView.centerXAnchor.constraint(equalTo: button.centerXAnchor),
                iconView.centerYAnchor.constraint(equalTo: button.centerYAnchor),
                iconView.widthAnchor.constraint(equalToConstant: 18),
                iconView.heightAnchor.constraint(equalToConstant: 18),
            ])
        }

        let initialAppearance = Self.appearance(for: .booting)
        apply(initialAppearance)
        appliedAppearance = initialAppearance
    }

    func update(state: AppState, hotkeyDisplay: String, engineStatus: String) {
        let appearance = Self.appearance(for: state)
        if appliedAppearance != appearance {
            apply(appearance)
            appliedAppearance = appearance
        }
        // 悬停即可看到当前状态，不必先点开菜单（B6）。
        // 无障碍描述也在这里更新而不是跟着图标走：`.downloadingModel(进度)` 的描述每次都在变，
        // 若并进 StatusAppearance 会让外观比较重新变得每次都不相等，B1 就白修了。
        let description = "\(AppConstants.appName) · \(state.menuTitle)"
        statusItem.button?.toolTip = description
        statusItem.button?.setAccessibilityLabel(description)
        headerView.update(state: state, hotkeyDisplay: hotkeyDisplay, engineStatus: engineStatus)
        updatePauseItem(for: state)
    }

    // MARK: - 状态栏图标外观

    /// 把 `AppState` 映射为状态栏图标的外观三要素。
    /// - 中性态（就绪/输入/启动）用 labelColor，随菜单栏深浅自适应。
    /// - 语义态（录音/错误/暂停等）用语义色。
    /// - 加载/下载/识别中叠加符号动效；若系统未渲染动效，仍有颜色与图标形状作为状态信号。
    private static func appearance(for state: AppState) -> StatusAppearance {
        let tint: StatusTint
        let effect: IconAnimation
        switch state {
        case .recording, .error:
            tint = .attention
            effect = .none
        case .setupRequired, .modelMissing:
            tint = .warning
            effect = .none
        case .modelLoading, .downloadingModel:
            tint = .progress
            effect = .pulse
        case .paused:
            tint = .muted
            effect = .none
        case .recognizing:
            tint = .neutral
            effect = .variableColor
        case .booting, .idle, .inserting:
            tint = .neutral
            effect = .none
        }
        return StatusAppearance(symbolName: state.statusSymbolName, tint: tint, effect: effect)
    }

    private func apply(_ appearance: StatusAppearance) {
        let image = NSImage(systemSymbolName: appearance.symbolName, accessibilityDescription: nil)
        image?.isTemplate = true
        iconView.image = image
        iconView.contentTintColor = menuIsOpen ? .selectedMenuItemTextColor : appearance.tint.color

        iconView.removeAllSymbolEffects()
        switch appearance.effect {
        case .pulse:
            iconView.addSymbolEffect(.pulse, options: .repeating)
        case .variableColor:
            iconView.addSymbolEffect(.variableColor.iterative, options: .repeating)
        case .none:
            break
        }
    }

    // MARK: - 暂停项

    private func updatePauseItem(for state: AppState) {
        let isPaused = state == .paused
        pauseMenuItem.title = isPaused ? L("恢复听写") : L("暂停听写")
        updatePauseItemImage(isPaused: isPaused)
        // 录音/识别/插入进行中，以及启动/待授权/模型加载阶段不允许切换。
        switch state {
        case .idle, .paused, .error:
            pauseMenuItem.isEnabled = true
        case .booting, .setupRequired, .modelMissing, .downloadingModel, .modelLoading,
             .recording, .recognizing, .inserting:
            pauseMenuItem.isEnabled = false
        }
    }

    private func updatePauseItemImage(isPaused: Bool) {
        let symbol = isPaused ? "play.circle" : "pause.circle"
        pauseMenuItem.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
    }

    // MARK: - 开机自启

    private func refreshLaunchAtLoginItem() {
        let enabled = LaunchAtLogin.isEnabled
        launchAtLoginMenuItem.state = enabled ? .on : .off
        launchAtLoginMenuItem.image = NSImage(
            systemSymbolName: enabled ? "checkmark.circle" : "circle",
            accessibilityDescription: nil
        )
    }

    // MARK: - NSMenuDelegate

    /// 每次打开前按内存里的最近结果重建子菜单。条目标题只放截断文本，不设 toolTip。
    func menuNeedsUpdate(_ menu: NSMenu) {
        guard menu === self.menu else { return }
        rebuildQuickSettings()
        let entries = historyEntriesProvider?() ?? []
        copyLastMenuItem.isEnabled = !entries.isEmpty
        recentMenuItem.isEnabled = !entries.isEmpty
        recentSubmenu.removeAllItems()
        guard !entries.isEmpty else { return }
        for entry in entries {
            let item = NSMenuItem(
                title: DictationHistory.menuTitle(for: entry),
                action: #selector(handleHistoryEntry(_:)),
                keyEquivalent: ""
            )
            item.target = self
            item.representedObject = entry.id
            recentSubmenu.addItem(item)
        }
        recentSubmenu.addItem(.separator())
        let clear = NSMenuItem(title: L("清空最近结果"), action: #selector(handleClearHistory), keyEquivalent: "")
        clear.target = self
        recentSubmenu.addItem(clear)
    }

    func menuWillOpen(_ menu: NSMenu) {
        headerView.setWarning(secureInputWarningProvider?())
        refreshLaunchAtLoginItem()
        menuIsOpen = true
        iconView.contentTintColor = .selectedMenuItemTextColor
    }

    func menuDidClose(_ menu: NSMenu) {
        menuIsOpen = false
        iconView.contentTintColor = (appliedAppearance?.tint ?? .neutral).color
    }

    // MARK: - Actions

    // MARK: - 快捷切换（R8-G1）

    private func rebuildQuickSettings() {
        guard let state = quickSettingsProvider?() else { return }

        languageSubmenu.removeAllItems()
        for language in ASRLanguage.allCases {
            let item = NSMenuItem(title: language.displayName, action: #selector(handleSelectLanguage(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = language.rawValue
            item.state = language == state.language ? .on : .off
            item.isEnabled = !state.isBusy
            languageSubmenu.addItem(item)
        }

        microphoneSubmenu.removeAllItems()
        let choices: [(title: String, value: String)] = [
            (L("自动（戴蓝牙耳机时使用内置麦克风）"), "auto"),
            (L("跟随系统默认输入"), "system"),
        ]
        for choice in choices {
            let item = NSMenuItem(title: choice.title, action: #selector(handleSelectInputDevice(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = choice.value
            item.state = choice.value == state.inputDevice ? .on : .off
            item.isEnabled = !state.isBusy
            microphoneSubmenu.addItem(item)
        }
        if !choices.contains(where: { $0.value == state.inputDevice }) {
            // 配置里指定了具体设备：只展示，不在这里改（设备列表属于后续批次）。
            let item = NSMenuItem(title: L("指定设备"), action: nil, keyEquivalent: "")
            item.state = .on
            item.isEnabled = false
            microphoneSubmenu.addItem(item)
        }

        llmMenuItem.state = state.llmEnabled ? .on : .off
        llmMenuItem.title = state.llmConfigured ? L("智能校对") : L("智能校对（在设置中配置）")
        llmMenuItem.isEnabled = state.llmConfigured && !state.isBusy
        languageMenuItem.isEnabled = !state.isBusy
        microphoneMenuItem.isEnabled = !state.isBusy
    }

    @objc private func handleSelectLanguage(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let language = ASRLanguage(rawValue: raw) else { return }
        onSelectLanguage?(language)
    }

    @objc private func handleSelectInputDevice(_ sender: NSMenuItem) {
        guard let value = sender.representedObject as? String else { return }
        onSelectInputDevice?(value)
    }

    @objc private func handleToggleLLM() {
        onToggleLLM?()
    }

    @objc private func handleCopyLastResult() {
        guard let first = historyEntriesProvider?().first else { return }
        onCopyHistoryEntry?(first.id)
    }

    @objc private func handleHistoryEntry(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? UUID else { return }
        onCopyHistoryEntry?(id)
    }

    @objc private func handleClearHistory() {
        onClearHistory?()
    }

    @objc private func handleTogglePause() {
        onTogglePause?()
    }

    @objc private func handleOpenSetup() {
        onOpenSetup?()
    }

    @objc private func handleOpenOnboarding() {
        onOpenOnboarding?()
    }

    @objc private func handleCheckForUpdates() {
        onCheckForUpdates?()
    }

    @objc private func handleToggleLaunchAtLogin() {
        do {
            try LaunchAtLogin.setEnabled(!LaunchAtLogin.isEnabled)
        } catch {
            AppLog.app.error("切换开机自启失败: \(error.localizedDescription, privacy: .public)")
            let alert = NSAlert()
            alert.messageText = L("无法更改开机自启设置")
            alert.informativeText = error.localizedDescription
            alert.alertStyle = .warning
            alert.runModal()
        }
        refreshLaunchAtLoginItem()
    }

    @objc private func handleAbout() {
        NSApp.activate(ignoringOtherApps: true)

        var options: [NSApplication.AboutPanelOptionKey: Any] = [.credits: aboutCredits]
        if let applicationIcon = NSApp.applicationIconImage {
            options[.applicationIcon] = applicationIcon
        }
        NSApp.orderFrontStandardAboutPanel(options: options)
    }

    /// 标准关于面板的补充信息。应用名与版本由 Info.plist 提供，交给 AppKit 按系统样式排版。
    private var aboutCredits: NSAttributedString {
        let paragraphStyle = NSMutableParagraphStyle()
        paragraphStyle.alignment = .center
        paragraphStyle.lineSpacing = 3

        let credits = NSMutableAttributedString(
            string: L("本地优先的离线语音输入工具") + "\n",
            attributes: [
                .font: NSFont.systemFont(ofSize: NSFont.smallSystemFontSize, weight: .medium),
                .foregroundColor: NSColor.labelColor,
            ]
        )
        credits.append(NSAttributedString(
            string: L("音频仅在设备端处理") + "\n\n",
            attributes: [
                .font: NSFont.systemFont(ofSize: NSFont.smallSystemFontSize),
                .foregroundColor: NSColor.secondaryLabelColor,
            ]
        ))
        credits.append(NSAttributedString(
            string: L("GitHub 项目主页 ↗"),
            attributes: [
                .font: NSFont.systemFont(ofSize: NSFont.smallSystemFontSize),
                .foregroundColor: NSColor.linkColor,
                .link: AppConstants.repositoryURL,
            ]
        ))
        credits.addAttribute(
            .paragraphStyle,
            value: paragraphStyle,
            range: NSRange(location: 0, length: credits.length)
        )
        return credits
    }

    @objc private func handleQuit() {
        onQuit?()
    }
}
