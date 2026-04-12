import AppKit
import Foundation

@MainActor
final class AppCoordinator {
    private let configStore = ConfigStore()
    private let permissionCenter = PermissionCenter()
    private let statusBarController = StatusBarController()
    private let setupHealthCheckClient = ASRClient()

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
    private var managedHotwordsText = ""
    private var serverReady = false
    private var currentState: AppState = .booting

    func start() {
        bindStatusBarActions()

        do {
            try reloadConfigurationFromDisk()
        } catch {
            AppLog.app.error("配置加载失败: \(error.localizedDescription, privacy: .public)")
            currentState = .error("配置加载失败")
            updateStatusUI()
            return
        }

        permissions = permissionCenter.snapshot()
        if !permissions.allRequiredGranted {
            currentState = .setupRequired
        }
        updateStatusUI()

        if !permissions.allRequiredGranted {
            setupControllerIfNeeded(forceShow: true, preferredTab: .permissions)
        }

        Task {
            await reevaluateReadiness()
        }
    }

    private func bindStatusBarActions() {
        statusBarController.onOpenSetup = { [weak self] in
            self?.setupControllerIfNeeded(forceShow: true, preferredTab: nil)
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

    private func setupControllerIfNeeded(forceShow: Bool = false, preferredTab: SetupTab? = nil) {
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
            controller.onTestServerConnection = { [weak self] server in
                guard let self else { return false }
                return await self.setupHealthCheckClient.healthCheck(server: server)
            }
            controller.onSaveConfig = { [weak self] updatedConfig in
                guard let self else { return }
                try await self.applyConfig(updatedConfig)
            }
            controller.onSaveHotwords = { [weak self] text in
                guard let self else { return }
                try await self.applyHotwordsText(text)
            }
            controller.loadWindow()
            setupWindowController = controller
            refreshSetupWindowEditorContent()
        }

        guard let setupWindowController else {
            return
        }

        setupWindowController.updatePermissions(
            snapshot: permissions,
            serviceReady: serverReady,
            hotkeyDisplay: config.hotkey.displayString,
            serverStatus: currentServerStatusText()
        )

        if forceShow || !permissions.allRequiredGranted || !serverReady {
            if let preferredTab {
                setupWindowController.selectTab(preferredTab)
            }
            NSApp.activate(ignoringOtherApps: true)
            setupWindowController.presentWindow()
        }
    }

    private func hideSetupWindowIfVisible() {
        setupWindowController?.window?.orderOut(nil)
    }

    private func refreshServerStatus() async {
        guard permissions.allRequiredGranted else {
            serverReady = false
            updateStatusUI()
            setupWindowController?.updatePermissions(
                snapshot: permissions,
                serviceReady: serverReady,
                hotkeyDisplay: config.hotkey.displayString,
                serverStatus: currentServerStatusText()
            )
            return
        }

        if voiceTyperController == nil {
            voiceTyperController = VoiceTyperController(config: config, hotwords: hotwords)
            bindControllerEvents()
        }

        serverReady = await voiceTyperController?.healthCheck() ?? false
        updateStatusUI()
        setupWindowController?.updatePermissions(
            snapshot: permissions,
            serviceReady: serverReady,
            hotkeyDisplay: config.hotkey.displayString,
            serverStatus: currentServerStatusText()
        )
    }

    private func reevaluateReadiness() async {
        permissions = permissionCenter.snapshot()
        await refreshServerStatus()

        if permissions.allRequiredGranted && serverReady {
            if voiceTyperController == nil {
                voiceTyperController = VoiceTyperController(config: config, hotwords: hotwords)
                bindControllerEvents()
            }

            if let voiceTyperController, !voiceTyperController.isStarted {
                do {
                    try voiceTyperController.start()
                } catch {
                    currentState = .error("热键监听失败: \(error.localizedDescription)")
                    AppLog.hotkey.error("热键监听启动失败: \(error.localizedDescription, privacy: .public)")
                    recordingHUDController?.hideHUD()
                    updateStatusUI()
                    return
                }
            }

            switch currentState {
            case .recording, .recognizing, .inserting:
                break
            default:
                currentState = .idle
            }
            hideSetupWindowIfVisible()
        } else {
            currentState = .setupRequired
            voiceTyperController?.stop()
            recordingHUDController?.hideHUD()
            setupControllerIfNeeded(forceShow: true, preferredTab: recommendedTab())
        }

        setupWindowController?.updatePermissions(
            snapshot: permissions,
            serviceReady: serverReady,
            hotkeyDisplay: config.hotkey.displayString,
            serverStatus: currentServerStatusText()
        )
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
        statusBarController.update(
            state: currentState,
            hotkeyDisplay: config.hotkey.displayString,
            serverStatus: currentServerStatusText()
        )
    }

    private func reloadConfigurationFromDisk() throws {
        config = try configStore.loadOrCreate()
        hotwords = configStore.loadHotwords(using: config)
        managedHotwordsText = try configStore.loadManagedHotwordsText(using: config)
        recordingHUDController = RecordingHUDController(config: config.ui)
    }

    private func refreshSetupWindowEditorContent() {
        setupWindowController?.loadEditableContent(
            config: config,
            managedHotwordsText: managedHotwordsText,
            additionalHotwordFileCount: configStore.additionalHotwordFileCount(using: config)
        )
    }

    private func applyConfig(_ updatedConfig: AppConfig) async throws {
        try configStore.save(config: updatedConfig)
        try await reloadAndReevaluateAfterSettingsChange()
    }

    private func applyHotwordsText(_ text: String) async throws {
        try configStore.saveManagedHotwordsText(text, using: config)
        try await reloadAndReevaluateAfterSettingsChange()
    }

    private func reloadAndReevaluateAfterSettingsChange() async throws {
        voiceTyperController?.stop()
        voiceTyperController = nil
        serverReady = false
        try reloadConfigurationFromDisk()
        refreshSetupWindowEditorContent()
        await reevaluateReadiness()
    }

    private func recommendedTab() -> SetupTab {
        if !permissions.allRequiredGranted {
            return .permissions
        }
        if !serverReady {
            return .connection
        }
        return .permissions
    }

    private func currentServerStatusText() -> String {
        serverReady ? "已连接 \(config.server.host):\(config.server.port)" : "未连接 \(config.server.host):\(config.server.port)"
    }
}
