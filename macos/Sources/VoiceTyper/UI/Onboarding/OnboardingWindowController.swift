import AppKit
import Foundation
import SwiftUI

/// 首启引导窗口。
///
/// 与设置窗口分开而不是塞成第四个 tab：引导是一次性的线性流程（有"上一步/下一步"），
/// 设置窗口是随时可回来的并列面板，两种交互模型混在一起两边都不好用。引导完成后这个
/// 窗口就不再自动出现，用户仍可从菜单栏「使用引导…」重新打开。
@MainActor
final class OnboardingWindowController: NSWindowController, NSWindowDelegate {
    var onRequestPermission: ((PermissionKind) -> Void)?
    var onOpenSystemSettings: ((PermissionKind) -> Void)?
    var onOpenKeyboardSettings: (() -> Void)?
    /// 请求协调器重新探测权限 / Fn 键设置等外部状态，并回推一次 `updateStatus`。
    var onRefreshStatus: (() -> Void)?
    var onStartModelDownload: (() -> Void)?
    var onCancelModelDownload: (() -> Void)?
    /// 窗口关闭（无论是走完流程还是直接关掉）时通知协调器。
    var onClose: (() -> Void)?

    private let viewModel = OnboardingViewModel()
    private var hasBuiltUI = false
    /// 可见期间的轮询：用户会在本窗口与系统设置之间来回切换，需要主动重新探测
    /// TCC 状态与键盘设置。与设置窗口同一套理由（TCC 查询是本地 IPC，2s 成本可忽略），
    /// 但这里**不会**抢焦点——只刷新显示。
    private var statusPollTimer: Timer?

    private static let contentSize = NSSize(width: 660, height: 560)

    convenience init() {
        let window = NSWindow(
            contentRect: NSRect(origin: .zero, size: OnboardingWindowController.contentSize),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false
        )
        self.init(window: window)
        window.title = "\(AppConstants.appName) 使用引导"
        window.isReleasedWhenClosed = false
        window.delegate = self
        ensureUIBuilt()
    }

    // MARK: - 对外接口

    func updateStatus(
        permissions: PermissionSnapshot,
        asrState: ASRService.State,
        downloadProgress: Double?,
        modelDownloadError: String?,
        hotkey: HotkeyConfig,
        fnConflictWarning: String?
    ) {
        ensureUIBuilt()
        viewModel.permissions = permissions
        viewModel.asrState = asrState
        viewModel.downloadProgress = downloadProgress
        viewModel.modelDownloadError = modelDownloadError
        viewModel.hotkeyDisplay = hotkey.displayString
        viewModel.hotkeyMode = hotkey.mode
        viewModel.fnConflictWarning = fnConflictWarning
    }

    func handleDictationEvent(_ event: OnboardingDictationEvent) {
        ensureUIBuilt()
        viewModel.handle(event)
    }

    func presentWindow() {
        ensureUIBuilt()
        guard let window else { return }
        window.center()
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
        startStatusPoll()
        onRefreshStatus?()
    }

    override func showWindow(_ sender: Any?) {
        presentWindow()
    }

    // MARK: - NSWindowDelegate

    func windowWillClose(_ notification: Notification) {
        stopStatusPoll()
        // 关掉窗口就算"引导已经给过了"：用户可能是看完就走，也可能是选择跳过，
        // 无论哪种都不该在下次启动时再弹一次。需要回看时菜单栏有「使用引导…」。
        OnboardingRecord.markCompleted()
        onClose?()
    }

    // MARK: - 构建

    private func ensureUIBuilt() {
        guard !hasBuiltUI else { return }
        hasBuiltUI = true

        viewModel.onRequestPermission = { [weak self] kind in self?.onRequestPermission?(kind) }
        viewModel.onOpenSystemSettings = { [weak self] kind in self?.onOpenSystemSettings?(kind) }
        viewModel.onOpenKeyboardSettings = { [weak self] in self?.onOpenKeyboardSettings?() }
        viewModel.onRefreshStatus = { [weak self] in self?.onRefreshStatus?() }
        viewModel.onStartModelDownload = { [weak self] in self?.onStartModelDownload?() }
        viewModel.onCancelModelDownload = { [weak self] in self?.onCancelModelDownload?() }
        viewModel.onFinish = { [weak self] in
            // 走完流程：关窗即可，完成标记由 windowWillClose 统一落。
            self?.window?.performClose(nil)
        }

        let hosting = NSHostingController(rootView: OnboardingView(vm: viewModel))
        hosting.preferredContentSize = Self.contentSize
        window?.contentViewController = hosting
    }

    // MARK: - 轮询

    private func startStatusPoll() {
        guard statusPollTimer == nil else { return }
        let timer = Timer.scheduledTimer(withTimeInterval: 2.0, repeats: true) { [weak self] _ in
            self?.onRefreshStatus?()
        }
        timer.tolerance = 0.5
        statusPollTimer = timer
    }

    private func stopStatusPoll() {
        statusPollTimer?.invalidate()
        statusPollTimer = nil
    }
}
