import AppKit
import Foundation

@MainActor
final class AppCoordinator {
    private let configStore = ConfigStore()
    private let permissionCenter = PermissionCenter()
    private let statusBarController = StatusBarController()

    private var setupWindowController: SetupWindowController?
    private var recordingHUDController: RecordingHUDController?
    private var voiceTyperController: VoiceTyperController?

    private var config = AppConfig()
    private var permissions = PermissionSnapshot(
        microphone: .notDetermined,
        accessibility: .denied,
        inputMonitoring: .denied
    )
    private var hotwords: [String] = []
    private var isEnabled = false
    private var serverReady = false
    private var currentState: AppState = .booting

    func start() {
        bindStatusBarActions()

        do {
            config = try configStore.loadOrCreate()
            hotwords = configStore.loadHotwords(using: config)
            recordingHUDController = RecordingHUDController(config: config.ui)
        } catch {
            AppLog.app.error("配置加载失败: \(error.localizedDescription, privacy: .public)")
            currentState = .error("配置加载失败")
            updateStatusUI()
            return
        }

        permissions = permissionCenter.snapshot()
        updateStatusUI()

        Task {
            await reevaluateReadiness()
        }
    }

    private func bindStatusBarActions() {
        statusBarController.onToggleEnabled = { [weak self] in
            self?.toggleEnabled()
        }
        statusBarController.onOpenSetup = { [weak self] in
            self?.setupControllerIfNeeded(forceShow: true)
        }
        statusBarController.onReconnectServer = { [weak self] in
            Task { await self?.refreshServerStatus() }
        }
        statusBarController.onOpenConfigDirectory = { [weak self] in
            self?.configStore.openConfigDirectory()
        }
        statusBarController.onQuit = {
            NSApp.terminate(nil)
        }
    }

    private func setupControllerIfNeeded(forceShow: Bool = false) {
        if setupWindowController == nil {
            let controller = SetupWindowController()
            controller.onRequestPermission = { [weak self] kind in
                Task {
                    guard let self else { return }
                    _ = await self.permissionCenter.request(kind)
                    self.permissions = self.permissionCenter.snapshot()
                    await self.reevaluateReadiness()
                }
            }
            controller.onOpenSystemSettings = { [weak self] kind in
                self?.permissionCenter.openSystemSettings(for: kind)
            }
            controller.onRetryServerCheck = { [weak self] in
                Task { await self?.refreshServerStatus() }
            }
            controller.onOpenConfigDirectory = { [weak self] in
                self?.configStore.openConfigDirectory()
            }
            controller.loadWindow()
            setupWindowController = controller
        }

        guard let setupWindowController else {
            return
        }

        setupWindowController.update(snapshot: permissions, serviceReady: serverReady, hotkeyDisplay: config.hotkey.displayString)

        if forceShow || !permissions.allRequiredGranted || !serverReady {
            setupWindowController.showWindow(nil)
            NSApp.activate(ignoringOtherApps: true)
        }
    }

    private func hideSetupWindowIfVisible() {
        setupWindowController?.window?.orderOut(nil)
    }

    private func toggleEnabled() {
        if isEnabled {
            voiceTyperController?.stop()
            isEnabled = false
            currentState = .paused
            recordingHUDController?.hideHUD()
            updateStatusUI()
            return
        }

        Task {
            await reevaluateReadiness()
        }
    }

    private func refreshServerStatus() async {
        guard permissions.allRequiredGranted else {
            serverReady = false
            updateStatusUI()
            setupWindowController?.update(snapshot: permissions, serviceReady: serverReady, hotkeyDisplay: config.hotkey.displayString)
            return
        }

        if voiceTyperController == nil {
            voiceTyperController = VoiceTyperController(config: config, hotwords: hotwords)
            bindControllerEvents()
        }

        serverReady = await voiceTyperController?.healthCheck() ?? false
        updateStatusUI()
        setupWindowController?.update(snapshot: permissions, serviceReady: serverReady, hotkeyDisplay: config.hotkey.displayString)
    }

    private func reevaluateReadiness() async {
        permissions = permissionCenter.snapshot()
        await refreshServerStatus()

        if permissions.allRequiredGranted && serverReady {
            if voiceTyperController == nil {
                voiceTyperController = VoiceTyperController(config: config, hotwords: hotwords)
                bindControllerEvents()
            }

            if !isEnabled {
                do {
                    try voiceTyperController?.start()
                    isEnabled = true
                    currentState = .idle
                } catch {
                    currentState = .error("热键监听启动失败")
                    AppLog.hotkey.error("热键监听启动失败: \(error.localizedDescription, privacy: .public)")
                }
            }
            hideSetupWindowIfVisible()
        } else {
            currentState = .setupRequired
            isEnabled = false
            voiceTyperController?.stop()
            setupControllerIfNeeded(forceShow: true)
        }

        setupWindowController?.update(snapshot: permissions, serviceReady: serverReady, hotkeyDisplay: config.hotkey.displayString)
        updateStatusUI()
    }

    private func bindControllerEvents() {
        voiceTyperController?.onStateChange = { [weak self] state in
            guard let self else { return }
            self.currentState = state
            switch state {
            case .recording:
                self.recordingHUDController?.showHUD()
            default:
                self.recordingHUDController?.hideHUD()
            }
            self.updateStatusUI()
        }

        voiceTyperController?.onRecognizedText = { text in
            AppLog.app.info("识别结果: \(text, privacy: .public)")
        }
    }

    private func updateStatusUI() {
        let serverStatus = serverReady ? "已连接 \(config.server.host):\(config.server.port)" : "未连接 \(config.server.host):\(config.server.port)"
        statusBarController.update(
            state: currentState,
            hotkeyDisplay: config.hotkey.displayString,
            serverStatus: serverStatus,
            isEnabled: isEnabled
        )
    }
}
