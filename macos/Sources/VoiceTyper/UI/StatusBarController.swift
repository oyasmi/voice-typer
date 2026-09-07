import AppKit
import Foundation

@MainActor
final class StatusBarController: NSObject, NSMenuDelegate {
    var onOpenSetup: (() -> Void)?
    var onOpenOnboarding: (() -> Void)?
    var onCheckForUpdates: (() -> Void)?
    var onTogglePause: (() -> Void)?
    var onQuit: (() -> Void)?

    private let statusItem = NSStatusBar.system.statusItem(withLength: 24)
    private let menu = NSMenu()
    /// 状态栏图标宿主。用 NSImageView 而非 button.image，以支持 SF Symbol 动效
    /// （NSStatusBarButton 本身不支持 addSymbolEffect）。
    private let iconView = NSImageView()
    /// 菜单是否处于打开态。打开时图标需反色以在高亮背景上可读。
    private var menuIsOpen = false

    private let headerView = StatusMenuHeaderView()
    private let headerItem = NSMenuItem()
    private let pauseMenuItem = NSMenuItem(title: "暂停听写", action: #selector(handleTogglePause), keyEquivalent: "")
    private let setupMenuItem = NSMenuItem(title: "权限与设置…", action: #selector(handleOpenSetup), keyEquivalent: ",")
    private let onboardingMenuItem = NSMenuItem(title: "使用引导…", action: #selector(handleOpenOnboarding), keyEquivalent: "")
    private let checkUpdatesMenuItem = NSMenuItem(title: "检查更新…", action: #selector(handleCheckForUpdates), keyEquivalent: "")
    private let launchAtLoginMenuItem = NSMenuItem(title: "开机自启", action: #selector(handleToggleLaunchAtLogin), keyEquivalent: "")
    private let aboutMenuItem = NSMenuItem(title: "关于 \(AppConstants.appName)", action: #selector(handleAbout), keyEquivalent: "")
    private let quitMenuItem = NSMenuItem(title: "退出", action: #selector(handleQuit), keyEquivalent: "q")

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
         launchAtLoginMenuItem, aboutMenuItem, quitMenuItem].forEach { $0.target = self }

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
            pauseMenuItem,
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
        pauseMenuItem.title = isPaused ? "恢复听写" : "暂停听写"
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

    func menuWillOpen(_ menu: NSMenu) {
        refreshLaunchAtLoginItem()
        menuIsOpen = true
        iconView.contentTintColor = .selectedMenuItemTextColor
    }

    func menuDidClose(_ menu: NSMenu) {
        menuIsOpen = false
        iconView.contentTintColor = (appliedAppearance?.tint ?? .neutral).color
    }

    // MARK: - Actions

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
            alert.messageText = "无法更改开机自启设置"
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
            string: "本地优先的离线语音输入工具\n",
            attributes: [
                .font: NSFont.systemFont(ofSize: NSFont.smallSystemFontSize, weight: .medium),
                .foregroundColor: NSColor.labelColor,
            ]
        )
        credits.append(NSAttributedString(
            string: "音频仅在设备端处理\n\n",
            attributes: [
                .font: NSFont.systemFont(ofSize: NSFont.smallSystemFontSize),
                .foregroundColor: NSColor.secondaryLabelColor,
            ]
        ))
        credits.append(NSAttributedString(
            string: "GitHub 项目主页 ↗",
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
