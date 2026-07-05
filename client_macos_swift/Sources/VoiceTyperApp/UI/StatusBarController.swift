import AppKit
import Foundation

@MainActor
final class StatusBarController: NSObject, NSMenuDelegate {
    var onOpenSetup: (() -> Void)?
    var onOpenConfigDirectory: (() -> Void)?
    var onReconnectServer: (() -> Void)?
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
    private let reconnectMenuItem = NSMenuItem(title: "重新连接服务", action: #selector(handleReconnect), keyEquivalent: "")
    private let configMenuItem = NSMenuItem(title: "打开配置目录", action: #selector(handleOpenConfigDirectory), keyEquivalent: "")
    private let launchAtLoginMenuItem = NSMenuItem(title: "开机自启", action: #selector(handleToggleLaunchAtLogin), keyEquivalent: "")
    private let aboutMenuItem = NSMenuItem(title: "关于 \(AppConstants.appName)", action: #selector(handleAbout), keyEquivalent: "")
    private let quitMenuItem = NSMenuItem(title: "退出", action: #selector(handleQuit), keyEquivalent: "q")

    /// 已应用到状态栏图标的状态。用于避免同状态重复设置图标而打断符号动效。
    private var appliedState: AppState?

    override init() {
        super.init()

        headerItem.view = headerView

        [pauseMenuItem, setupMenuItem, reconnectMenuItem, configMenuItem,
         launchAtLoginMenuItem, aboutMenuItem, quitMenuItem].forEach { $0.target = self }

        setupMenuItem.image = NSImage(systemSymbolName: "gearshape", accessibilityDescription: nil)
        reconnectMenuItem.image = NSImage(systemSymbolName: "arrow.clockwise", accessibilityDescription: nil)
        configMenuItem.image = NSImage(systemSymbolName: "folder", accessibilityDescription: nil)
        aboutMenuItem.image = NSImage(systemSymbolName: "info.circle", accessibilityDescription: nil)
        quitMenuItem.image = NSImage(systemSymbolName: "power", accessibilityDescription: nil)
        updatePauseItemImage(isPaused: false)

        menu.items = [
            headerItem,
            .separator(),
            pauseMenuItem,
            .separator(),
            setupMenuItem,
            reconnectMenuItem,
            configMenuItem,
            .separator(),
            launchAtLoginMenuItem,
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

        applyStatusAppearance(.booting)
        appliedState = .booting
    }

    func update(state: AppState, hotkeyDisplay: String, serverStatus: String) {
        if appliedState != state {
            applyStatusAppearance(state)
            appliedState = state
        }
        headerView.update(state: state, hotkeyDisplay: hotkeyDisplay, serverStatus: serverStatus)
        updatePauseItem(for: state)
    }

    // MARK: - 状态栏图标外观

    /// 更新状态栏图标的符号、着色与动效。
    /// - 中性态（就绪/输入/启动）用 labelColor，随菜单栏深浅自适应。
    /// - 语义态（录音/错误/暂停等）用语义色。
    /// - 连接中/识别中叠加符号动效；若系统未渲染动效，仍有颜色与图标形状作为状态信号。
    private func applyStatusAppearance(_ state: AppState) {
        let image = NSImage(systemSymbolName: state.statusSymbolName, accessibilityDescription: state.menuTitle)
        image?.isTemplate = true
        iconView.image = image
        iconView.contentTintColor = menuIsOpen ? .selectedMenuItemTextColor : Self.tintColor(for: state)

        iconView.removeAllSymbolEffects()
        switch state {
        case .connecting:
            iconView.addSymbolEffect(.pulse, options: .repeating)
        case .recognizing:
            iconView.addSymbolEffect(.variableColor.iterative, options: .repeating)
        default:
            break
        }
    }

    private static func tintColor(for state: AppState) -> NSColor {
        switch state {
        case .recording, .error:
            return .systemRed
        case .setupRequired:
            return .systemOrange
        case .connecting:
            return .systemYellow
        case .paused:
            return .secondaryLabelColor
        case .booting, .idle, .recognizing, .inserting:
            return .labelColor
        }
    }

    // MARK: - 暂停项

    private func updatePauseItem(for state: AppState) {
        let isPaused = state == .paused
        pauseMenuItem.title = isPaused ? "恢复听写" : "暂停听写"
        updatePauseItemImage(isPaused: isPaused)
        // 录音/识别/插入进行中，以及启动/待授权阶段不允许切换。
        switch state {
        case .idle, .connecting, .paused, .error:
            pauseMenuItem.isEnabled = true
        case .booting, .setupRequired, .recording, .recognizing, .inserting:
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
        // 开机自启状态可能被系统设置在外部改动，每次打开时刷新。
        refreshLaunchAtLoginItem()
        // 菜单打开时状态项高亮，图标反色以保持可读。
        menuIsOpen = true
        iconView.contentTintColor = .selectedMenuItemTextColor
    }

    func menuDidClose(_ menu: NSMenu) {
        menuIsOpen = false
        iconView.contentTintColor = Self.tintColor(for: appliedState ?? .idle)
    }

    // MARK: - Actions

    @objc private func handleTogglePause() {
        onTogglePause?()
    }

    @objc private func handleOpenSetup() {
        onOpenSetup?()
    }

    @objc private func handleReconnect() {
        onReconnectServer?()
    }

    @objc private func handleOpenConfigDirectory() {
        onOpenConfigDirectory?()
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
        NSApp.orderFrontStandardAboutPanel(nil)
    }

    @objc private func handleQuit() {
        onQuit?()
    }
}
