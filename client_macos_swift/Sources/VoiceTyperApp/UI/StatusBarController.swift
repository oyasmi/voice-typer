import AppKit
import Foundation

@MainActor
final class StatusBarController: NSObject {
    var onOpenSetup: (() -> Void)?
    var onOpenConfigDirectory: (() -> Void)?
    var onReconnectServer: (() -> Void)?
    var onQuit: (() -> Void)?

    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let menu = NSMenu()

    private let statusMenuItem = NSMenuItem(title: "启动中", action: nil, keyEquivalent: "")
    private let hotkeyMenuItem = NSMenuItem(title: "热键: -", action: nil, keyEquivalent: "")
    private let serverMenuItem = NSMenuItem(title: "服务: 检查中", action: nil, keyEquivalent: "")
    private let setupMenuItem = NSMenuItem(title: "权限与设置...", action: #selector(handleOpenSetup), keyEquivalent: "")
    private let reconnectMenuItem = NSMenuItem(title: "重新连接服务", action: #selector(handleReconnect), keyEquivalent: "")
    private let configMenuItem = NSMenuItem(title: "打开配置目录", action: #selector(handleOpenConfigDirectory), keyEquivalent: "")
    private let quitMenuItem = NSMenuItem(title: "退出", action: #selector(handleQuit), keyEquivalent: "q")

    override init() {
        super.init()

        statusMenuItem.isEnabled = false
        hotkeyMenuItem.isEnabled = false
        serverMenuItem.isEnabled = false

        [setupMenuItem, reconnectMenuItem, configMenuItem, quitMenuItem].forEach {
            $0.target = self
        }

        menu.items = [
            statusMenuItem,
            hotkeyMenuItem,
            serverMenuItem,
            .separator(),
            setupMenuItem,
            reconnectMenuItem,
            configMenuItem,
            .separator(),
            quitMenuItem,
        ]

        statusItem.menu = menu
        statusItem.button?.title = "⏳"
    }

    func update(state: AppState, hotkeyDisplay: String, serverStatus: String) {
        statusItem.button?.title = state.statusIcon
        statusMenuItem.title = state.menuTitle
        hotkeyMenuItem.title = "热键: \(hotkeyDisplay)"
        serverMenuItem.title = "服务: \(serverStatus)"
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

    @objc private func handleQuit() {
        onQuit?()
    }
}
