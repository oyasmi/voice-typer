import AppKit
import Foundation
import SwiftUI

enum SetupTab: Int, CaseIterable {
    case permissions
    case connection
    case hotkey
    case hotwords
    case general

    var title: String {
        switch self {
        case .permissions: return "权限"
        case .connection: return "连接"
        case .hotkey: return "热键"
        case .hotwords: return "热词"
        case .general: return "通用"
        }
    }

    var symbol: String {
        switch self {
        case .permissions: return "checkmark.shield"
        case .connection: return "network"
        case .hotkey: return "keyboard"
        case .hotwords: return "text.book.closed"
        case .general: return "gearshape"
        }
    }
}

@MainActor
final class SetupWindowController: NSWindowController, NSWindowDelegate {
    var onRequestPermission: ((PermissionKind) -> Void)?
    var onOpenSystemSettings: ((PermissionKind) -> Void)?
    var onRetryServerCheck: (() -> Void)?
    var onTestServerConnection: ((ServerConfig) async -> Bool)?
    var onSaveConfig: ((AppConfig) async throws -> Void)?
    var onSaveHotwords: ((String) async throws -> Void)?
    /// 热键录制期间挂起 / 恢复全局热键监听。
    var onSuspendHotkey: ((Bool) -> Void)?
    /// 实时预览 HUD 背景不透明度。
    var onPreviewHUDOpacity: ((Double) -> Void)?
    /// 窗口关闭时通知（用于复位"用户主动打开"标记）。
    var onClose: (() -> Void)?

    private let viewModel = SettingsViewModel()
    private let tabController = NSTabViewController()
    private var hasBuiltUI = false
    private var preferredOrigin: NSPoint?

    private static let contentWidth: CGFloat = 720
    private static let contentHeight: CGFloat = 480
    /// 内容左右边距。grouped Form 在 NSHostingController 中自动边距会塌陷为 0、且会忽略
    /// SwiftUI 层的 padding，故在 AppKit 层给 hosting 视图加物理内缩，确保内容不贴边。
    private static let contentHInset: CGFloat = 20

    convenience init() {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: Self.contentWidth, height: Self.contentHeight),
            styleMask: [.titled, .closable, .miniaturizable],
            backing: .buffered,
            defer: false
        )
        self.init(window: window)
        window.title = "\(AppConstants.appName) 设置"
        window.isReleasedWhenClosed = false
        window.delegate = self
        ensureUIBuilt()
    }

    // MARK: - 对外接口（保持 AppCoordinator 调用不变）

    func loadEditableContent(config: AppConfig, managedHotwordsText: String, additionalHotwordFileCount: Int) {
        ensureUIBuilt()
        viewModel.load(
            config: config,
            managedHotwordsText: managedHotwordsText,
            additionalHotwordFileCount: additionalHotwordFileCount,
            launchAtLogin: LaunchAtLogin.isEnabled
        )
    }

    func updatePermissions(snapshot: PermissionSnapshot, serviceReady: Bool, hotkeyDisplay: String, serverStatus: String) {
        ensureUIBuilt()
        viewModel.permissions = snapshot
        viewModel.serviceReady = serviceReady
        viewModel.hotkeyDisplay = hotkeyDisplay
        viewModel.serverStatus = serverStatus
    }

    func selectTab(_ tab: SetupTab) {
        ensureUIBuilt()
        tabController.selectedTabViewItemIndex = tab.rawValue
    }

    override func showWindow(_ sender: Any?) {
        presentWindow()
    }

    func presentWindow() {
        ensureUIBuilt()
        guard let window else { return }
        if let preferredOrigin {
            window.setFrameOrigin(preferredOrigin)
        } else {
            window.center()
        }
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
    }

    func windowDidMove(_ notification: Notification) {
        preferredOrigin = window?.frame.origin
    }

    func windowWillClose(_ notification: Notification) {
        onClose?()
    }

    // MARK: - 构建

    private func ensureUIBuilt() {
        guard !hasBuiltUI else { return }
        hasBuiltUI = true
        wireViewModelCallbacks()
        buildTabs()
        window?.contentViewController = tabController
    }

    private func wireViewModelCallbacks() {
        viewModel.onRequestPermission = { [weak self] kind in self?.onRequestPermission?(kind) }
        viewModel.onOpenSystemSettings = { [weak self] kind in self?.onOpenSystemSettings?(kind) }
        viewModel.onRetryServerCheck = { [weak self] in self?.onRetryServerCheck?() }
        viewModel.onTestServerConnection = { [weak self] server in
            await self?.onTestServerConnection?(server) ?? false
        }
        viewModel.onSaveConfig = { [weak self] config in
            try await self?.onSaveConfig?(config)
        }
        viewModel.onSaveHotwords = { [weak self] text in
            try await self?.onSaveHotwords?(text)
        }
        viewModel.onSuspendHotkey = { [weak self] suspend in self?.onSuspendHotkey?(suspend) }
        viewModel.onPreviewHUDOpacity = { [weak self] opacity in self?.onPreviewHUDOpacity?(opacity) }
        viewModel.onToggleLaunchAtLogin = { [weak viewModel] enabled in
            do {
                try LaunchAtLogin.setEnabled(enabled)
            } catch {
                AppLog.app.error("切换开机自启失败: \(error.localizedDescription, privacy: .public)")
                let alert = NSAlert()
                alert.messageText = "无法更改开机自启设置"
                alert.informativeText = error.localizedDescription
                alert.alertStyle = .warning
                alert.runModal()
            }
            // 以系统实际状态回填，避免注册失败时开关与真实状态不一致。
            viewModel?.launchAtLogin = LaunchAtLogin.isEnabled
        }
    }

    private func buildTabs() {
        tabController.tabStyle = .toolbar
        for tab in SetupTab.allCases {
            tabController.addTabViewItem(makeTabItem(tab))
        }
    }

    private func makeTabItem(_ tab: SetupTab) -> NSTabViewItem {
        let hosting = NSHostingController(rootView: page(for: tab))

        // 用容器视图承载 hosting，并在左右加物理内缩，保证内容不贴窗口边缘。
        let container = NSViewController()
        container.view = NSView(frame: NSRect(x: 0, y: 0, width: Self.contentWidth, height: Self.contentHeight))
        container.preferredContentSize = NSSize(width: Self.contentWidth, height: Self.contentHeight)
        container.addChild(hosting)
        hosting.view.translatesAutoresizingMaskIntoConstraints = false
        container.view.addSubview(hosting.view)
        NSLayoutConstraint.activate([
            hosting.view.leadingAnchor.constraint(equalTo: container.view.leadingAnchor, constant: Self.contentHInset),
            hosting.view.trailingAnchor.constraint(equalTo: container.view.trailingAnchor, constant: -Self.contentHInset),
            hosting.view.topAnchor.constraint(equalTo: container.view.topAnchor),
            hosting.view.bottomAnchor.constraint(equalTo: container.view.bottomAnchor),
        ])

        let item = NSTabViewItem(viewController: container)
        item.label = tab.title
        item.image = NSImage(systemSymbolName: tab.symbol, accessibilityDescription: tab.title)
        return item
    }

    private func page(for tab: SetupTab) -> AnyView {
        let sized: (any View) -> AnyView = { view in
            AnyView(AnyView(view).frame(maxWidth: .infinity, maxHeight: .infinity))
        }
        switch tab {
        case .permissions:
            return sized(PermissionsSettingsView(vm: viewModel))
        case .connection:
            return sized(ConnectionSettingsView(vm: viewModel))
        case .hotkey:
            return sized(HotkeySettingsView(vm: viewModel))
        case .hotwords:
            return sized(HotwordsSettingsView(vm: viewModel))
        case .general:
            return sized(GeneralSettingsView(vm: viewModel))
        }
    }
}
