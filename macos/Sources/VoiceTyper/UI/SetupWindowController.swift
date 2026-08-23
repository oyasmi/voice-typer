import AppKit
import Foundation
import SwiftUI

enum SetupTab: Int, CaseIterable {
    case permissions
    case recognition
    case general

    var title: String {
        switch self {
        case .permissions: return "权限"
        case .recognition: return "识别"
        case .general: return "通用"
        }
    }

    var symbol: String {
        switch self {
        case .permissions: return "checkmark.shield"
        case .recognition: return "waveform"
        case .general: return "gearshape"
        }
    }
}

@MainActor
final class SetupWindowController: NSWindowController, NSWindowDelegate {
    var onRequestPermission: ((PermissionKind) -> Void)?
    var onOpenSystemSettings: ((PermissionKind) -> Void)?
    var onRetryReadinessCheck: (() -> Void)?
    var onSaveConfig: ((AppConfig) async throws -> Void)?
    /// 热键录制期间挂起 / 恢复全局热键监听。返回值：请求是否被接受（R2-04）。
    var onSuspendHotkey: ((Bool) -> Bool)?
    /// 实时预览 HUD 背景不透明度。
    var onPreviewHUDOpacity: ((Double) -> Void)?
    /// 窗口关闭时通知（用于复位"用户主动打开"标记）。
    var onClose: (() -> Void)?
    var onStartModelDownload: (() -> Void)?
    var onCancelModelDownload: (() -> Void)?
    var onReloadModel: (() -> Void)?
    var onTestLLMCorrection: ((LLMConfig, String) async -> Result<String, SimpleMessageError>)?

    private let viewModel = SettingsViewModel()
    private let tabController = NSTabViewController()
    private var hasBuiltUI = false
    private var preferredOrigin: NSPoint?
    /// 权限页在"未授权"时的轮询：用户去系统设置里勾选后切回本窗口，之前没有任何
    /// 机制会主动重新探测 TCC 状态——`onRetryReadinessCheck` 存在但从未被调用过，
    /// 界面文案却写着"处理完成后本窗口会自动更新状态"，实际会一直卡在未授权直到
    /// 用户再点一次「授权」按钮（R4-01）。TCC 查询是本地 IPC，2s 轮询成本可忽略；
    /// 一旦权限齐全或窗口不可见就停表，不会无限期空转。
    private var permissionPollTimer: Timer?

    private static let contentWidth: CGFloat = 720
    /// 以展开智能校对后的“识别”页为准，完整容纳最长页面且不留下过多空白。
    private static let contentHeight: CGFloat = 540
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

    func loadEditableContent(config: AppConfig) {
        ensureUIBuilt()
        viewModel.load(config: config, launchAtLogin: LaunchAtLogin.isEnabled)
    }

    func updateStatus(
        permissions: PermissionSnapshot,
        asrState: ASRService.State,
        downloadProgress: Double?,
        modelDownloadError: String?,
        hotkeyDisplay: String,
        engineStatus: String
    ) {
        ensureUIBuilt()
        viewModel.permissions = permissions
        viewModel.asrState = asrState
        viewModel.downloadProgress = downloadProgress
        viewModel.modelDownloadError = modelDownloadError
        viewModel.hotkeyDisplay = hotkeyDisplay
        viewModel.engineStatus = engineStatus
        // 只要不在下载中，busy 状态就必须完全由当前 asrState 决定；
        // 不能被 asrState == .modelMissing 卡住导致下载失败/取消后按钮永久 disabled（F-11）。
        if downloadProgress == nil {
            viewModel.modelActionBusy = (asrState == .loading)
        }

        if permissions.allRequiredGranted {
            stopPermissionPoll()
        } else if window?.isVisible == true {
            startPermissionPollIfNeeded()
        }
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

        if !viewModel.permissions.allRequiredGranted {
            startPermissionPollIfNeeded()
        }
    }

    func windowDidMove(_ notification: Notification) {
        preferredOrigin = window?.frame.origin
    }

    func windowWillClose(_ notification: Notification) {
        stopPermissionPoll()
        onClose?()
    }

    // MARK: - 权限轮询

    private func startPermissionPollIfNeeded() {
        guard permissionPollTimer == nil else { return }
        let timer = Timer.scheduledTimer(withTimeInterval: 2.0, repeats: true) { [weak self] _ in
            self?.onRetryReadinessCheck?()
        }
        timer.tolerance = 0.5
        permissionPollTimer = timer
    }

    private func stopPermissionPoll() {
        permissionPollTimer?.invalidate()
        permissionPollTimer = nil
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
        viewModel.onRetryReadinessCheck = { [weak self] in self?.onRetryReadinessCheck?() }
        viewModel.onSaveConfig = { [weak self] config in
            try await self?.onSaveConfig?(config)
        }
        viewModel.onSuspendHotkey = { [weak self] suspend in self?.onSuspendHotkey?(suspend) ?? false }
        viewModel.onPreviewHUDOpacity = { [weak self] opacity in self?.onPreviewHUDOpacity?(opacity) }
        viewModel.onStartModelDownload = { [weak self] in self?.onStartModelDownload?() }
        viewModel.onCancelModelDownload = { [weak self] in self?.onCancelModelDownload?() }
        viewModel.onReloadModel = { [weak self] in self?.onReloadModel?() }
        viewModel.onTestLLMCorrection = { [weak self] llmConfig, apiKey in
            await self?.onTestLLMCorrection?(llmConfig, apiKey) ?? .failure(SimpleMessageError(message: "内部错误：测试通道不可用"))
        }
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
        case .recognition:
            return sized(RecognitionSettingsView(vm: viewModel))
        case .general:
            return sized(GeneralSettingsView(vm: viewModel))
        }
    }
}
