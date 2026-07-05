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
    private var managedHotwordsText = ""
    private var serverReady = false
    private var currentState: AppState = .booting
    /// 服务端未就绪时的后台退避轮询任务。nil 表示当前未在轮询。
    private var serverPollTask: Task<Void, Never>?

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

    func openSetupWindow() {
        setupControllerIfNeeded(forceShow: true, preferredTab: nil)
    }

    private func bindStatusBarActions() {
        statusBarController.onOpenSetup = { [weak self] in
            self?.setupControllerIfNeeded(forceShow: true, preferredTab: nil)
        }
        statusBarController.onReconnectServer = { [weak self] in
            Task { await self?.reevaluateReadiness() }
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
                Task { await self?.reevaluateReadiness() }
            }
            controller.onTestServerConnection = { server in
                await ServerHealthProbe.check(server: server).ready
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

        syncSetupWindow()

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

    /// 重新评估权限与服务端就绪状态，并驱动状态机。
    ///
    /// - 权限缺失：必须用户介入，强制弹出设置窗口（权限页）。
    /// - 权限齐全但服务端未就绪：进入 `.connecting`，后台退避轮询自动重连，
    ///   不打扰用户（覆盖"开机自启客户端时服务端仍在加载模型"的常见场景）。
    /// - 权限齐全且服务端就绪：启动热键监听并进入 `.idle`。
    private func reevaluateReadiness() async {
        permissions = permissionCenter.snapshot()

        guard permissions.allRequiredGranted else {
            cancelServerPolling()
            serverReady = false
            currentState = .setupRequired
            voiceTyperController?.stop()
            recordingHUDController?.hideHUD()
            setupControllerIfNeeded(forceShow: true, preferredTab: .permissions)
            syncSetupWindow()
            updateStatusUI()
            return
        }

        serverReady = await ServerHealthProbe.check(server: config.server).ready
        if serverReady {
            cancelServerPolling()
            activateReadyState()
        } else {
            beginConnecting()
        }

        syncSetupWindow()
        updateStatusUI()
    }

    /// 权限与服务端均就绪：确保控制器已启动并进入 idle。
    private func activateReadyState() {
        ensureController()
        if let controller = voiceTyperController, !controller.isStarted {
            do {
                try controller.start()
            } catch {
                currentState = .error("热键监听失败: \(error.localizedDescription)")
                AppLog.hotkey.error("热键监听启动失败: \(error.localizedDescription, privacy: .public)")
                recordingHUDController?.hideHUD()
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
    }

    /// 服务端未就绪：进入"连接中"并开始后台退避轮询。不监听热键、不弹窗。
    private func beginConnecting() {
        voiceTyperController?.stop()
        recordingHUDController?.hideHUD()
        currentState = .connecting
        startServerPolling()
    }

    /// 后台退避轮询服务端 `/health`，就绪后自动切入 idle。
    private func startServerPolling() {
        guard serverPollTask == nil else { return }
        serverPollTask = Task { @MainActor [weak self] in
            let backoffSeconds: [UInt64] = [2, 3, 5, 8, 10]
            var attempt = 0
            while !Task.isCancelled {
                let delay = backoffSeconds[min(attempt, backoffSeconds.count - 1)]
                attempt += 1
                try? await Task.sleep(nanoseconds: delay * 1_000_000_000)
                guard !Task.isCancelled, let self else { return }

                // 轮询期间权限若被撤销，交回 reevaluateReadiness 统一处理。
                guard self.permissions.allRequiredGranted else {
                    self.serverPollTask = nil
                    await self.reevaluateReadiness()
                    return
                }

                let ready = await ServerHealthProbe.check(server: self.config.server).ready
                guard !Task.isCancelled else { return }
                if ready {
                    self.serverPollTask = nil
                    self.serverReady = true
                    self.activateReadyState()
                    self.syncSetupWindow()
                    self.updateStatusUI()
                    return
                }
            }
        }
    }

    private func cancelServerPolling() {
        serverPollTask?.cancel()
        serverPollTask = nil
    }

    private func ensureController() {
        if voiceTyperController == nil {
            voiceTyperController = VoiceTyperController(config: config, hotwords: hotwords)
            bindControllerEvents()
        }
    }

    private func syncSetupWindow() {
        setupWindowController?.updatePermissions(
            snapshot: permissions,
            serviceReady: serverReady,
            hotkeyDisplay: config.hotkey.displayString,
            serverStatus: currentServerStatusText()
        )
    }

    private func bindControllerEvents() {
        voiceTyperController?.onStateChange = { [weak self] state in
            guard let self else { return }
            self.currentState = state
            switch state {
            case .recording:
                self.recordingHUDController?.showHUD()
            case .recognizing:
                // 保持 HUD 可见，切换为"识别中"样式
                self.recordingHUDController?.setRecognizing()
            case .error(let message):
                // HUD 用错误样式短暂提示后自隐，避免用户对"录音消失"无感
                self.recordingHUDController?.showError(message)
            default:
                self.recordingHUDController?.hideHUD()
            }
            self.updateStatusUI()
        }

        voiceTyperController?.onPreviewUpdate = { [weak self] accumulated in
            self?.recordingHUDController?.showPreview(accumulated)
        }

        voiceTyperController?.onPreviewWarning = { [weak self] message in
            self?.recordingHUDController?.flashWarning(message)
        }

        voiceTyperController?.onCancelled = { [weak self] in
            guard let self else { return }
            self.currentState = .idle
            self.recordingHUDController?.showCanceled()
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
        cancelServerPolling()
        voiceTyperController?.stop()
        voiceTyperController = nil
        serverReady = false
        try reloadConfigurationFromDisk()
        refreshSetupWindowEditorContent()
        await reevaluateReadiness()
    }

    private func currentServerStatusText() -> String {
        let endpoint = "\(config.server.host):\(config.server.port)"
        if serverReady {
            return "已连接 \(endpoint)"
        }
        if case .connecting = currentState {
            return "连接中 \(endpoint)"
        }
        return "未连接 \(endpoint)"
    }
}
