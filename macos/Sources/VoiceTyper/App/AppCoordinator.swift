import AppKit
import Foundation

/// 每次应用进程对缺失模型至少自动尝试一次，同一进程内失败后不无限循环重试。
/// 下次启动会再自动尝试，并复用 ModelDownloader 已保存的断点数据。
struct AutomaticModelDownloadPolicy {
    private(set) var hasAttempted = false

    mutating func shouldStart(for state: ASRService.State, isDownloading: Bool) -> Bool {
        guard !hasAttempted, !isDownloading, state == .modelMissing else { return false }
        hasAttempted = true
        return true
    }
}

@MainActor
final class AppCoordinator {
    private let configStore = ConfigStore()
    private let permissionCenter = PermissionCenter()
    private let statusBarController = StatusBarController()
    private let asrService = ASRService()

    private var setupWindowController: SetupWindowController?
    private var recordingHUDController: RecordingHUDController?
    private var voiceTyperController: VoiceTyperController?

    private var config = AppConfig()
    private var permissions = PermissionSnapshot(
        microphone: .notDetermined,
        accessibility: .denied,
        inputMonitoring: .denied
    )
    private var currentState: AppState = .booting
    /// 用户是否已从菜单主动暂停听写。暂停时不监听热键。
    private var isPaused = false
    /// 用户是否主动打开了设置窗口。为 true 时保存设置不会自动关闭窗口。
    private var userOpenedSetup = false

    private var modelDownloader: ModelDownloader?
    private var isDownloadingModel = false
    private var modelDownloadProgress = 0.0
    private var modelDownloadError: String?
    private var automaticModelDownloadPolicy = AutomaticModelDownloadPolicy()

    func start() {
        bindStatusBarActions()
        asrService.onStateChange = { [weak self] _ in
            Task { @MainActor in await self?.reevaluateReadiness() }
        }

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

        // 权限与模型是两条互不依赖的准备线：权限没给全时模型照样在后台下载/加载，
        // 用户授权完立刻可用。
        asrService.updateConfig(config.asr)
        Task {
            await asrService.preload()
            await reevaluateReadiness()
        }
    }

    func openSetupWindow() {
        userOpenedSetup = true
        setupControllerIfNeeded(forceShow: true, preferredTab: nil)
    }

    private func bindStatusBarActions() {
        statusBarController.onOpenSetup = { [weak self] in
            self?.openSetupWindow()
        }
        statusBarController.onTogglePause = { [weak self] in
            self?.togglePause()
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
            controller.onRetryReadinessCheck = { [weak self] in
                Task { await self?.reevaluateReadiness() }
            }
            controller.onSaveConfig = { [weak self] updatedConfig in
                guard let self else { return }
                try await self.applyConfig(updatedConfig)
            }
            controller.onSuspendHotkey = { [weak self] suspend -> Bool in
                guard let self else { return false }
                if suspend {
                    // 录制热键期间停掉全局监听，避免按 Fn 当场触发录音——但绝不能销毁
                    // 一段正在进行的听写（R2-04）：那样会在用户毫无察觉的情况下丢内容。
                    guard !self.currentState.isActiveDictation else { return false }
                    self.voiceTyperController?.suspendHotkeyListening()
                    return true
                } else {
                    try? self.voiceTyperController?.resumeHotkeyListening()
                    Task { await self.reevaluateReadiness() }
                    return true
                }
            }
            controller.onPreviewHUDOpacity = { [weak self] opacity in
                self?.recordingHUDController?.previewOpacity(opacity)
            }
            controller.onClose = { [weak self] in
                self?.userOpenedSetup = false
            }
            controller.onStartModelDownload = { [weak self] in
                self?.startModelDownload()
            }
            controller.onCancelModelDownload = { [weak self] in
                self?.modelDownloader?.cancel()
            }
            controller.onReloadModel = { [weak self] in
                guard let self, !self.currentState.isActiveDictation else { return }
                Task { await self.asrService.reload() }
            }
            controller.onTestLLMCorrection = { [weak self] llmConfig, apiKey in
                await self?.testLLMCorrection(llmConfig: llmConfig, apiKey: apiKey) ?? false
            }
            controller.loadWindow()
            setupWindowController = controller
            refreshSetupWindowEditorContent()
        }

        guard let setupWindowController else { return }

        syncSetupWindow()

        if forceShow || !permissions.allRequiredGranted || !isModelReady {
            if let preferredTab {
                setupWindowController.selectTab(preferredTab)
            }
            NSApp.activate(ignoringOtherApps: true)
            setupWindowController.presentWindow()
        }
    }

    private func hideSetupWindowIfVisible() {
        guard !userOpenedSetup else { return }
        setupWindowController?.window?.orderOut(nil)
    }

    private func togglePause() {
        if isPaused {
            isPaused = false
            Task { await reevaluateReadiness() }
        } else {
            isPaused = true
            voiceTyperController?.stop()
            recordingHUDController?.hideHUD()
            currentState = .paused
            updateStatusUI()
        }
    }

    private var isModelReady: Bool {
        asrService.state == .ready || asrService.state == .suspendedForIdle
    }

    /// 重新评估权限与模型就绪状态，并驱动状态机。
    ///
    /// - 权限缺失：必须用户介入，强制弹出设置窗口（权限页）。
    /// - 模型缺失：不等待权限，每次应用启动自动尝试下载一次。
    /// - 权限齐全但模型加载中/失败：进入对应状态。
    /// - 权限齐全且模型就绪：启动热键监听并进入 `.idle`。
    private func reevaluateReadiness() async {
        // 模型准备与 TCC 授权完全独立。首次定位到模型缺失后立即下载，
        // 用户只需处理必须亲自确认的系统权限；用户暂停听写也不阻断后台准备。
        if automaticModelDownloadPolicy.shouldStart(
            for: asrService.state,
            isDownloading: isDownloadingModel
        ) {
            startModelDownload()
        }

        guard !isPaused else {
            currentState = .paused
            updateStatusUI()
            return
        }

        permissions = permissionCenter.snapshot()

        guard permissions.allRequiredGranted else {
            currentState = .setupRequired
            voiceTyperController?.stop()
            recordingHUDController?.hideHUD()
            setupControllerIfNeeded(forceShow: true, preferredTab: .permissions)
            syncSetupWindow()
            updateStatusUI()
            return
        }

        if isDownloadingModel {
            currentState = .downloadingModel(modelDownloadProgress)
            syncSetupWindow()
            updateStatusUI()
            return
        }

        switch asrService.state {
        case .unloaded:
            Task { await asrService.preload() }
            currentState = .modelLoading
        case .loading:
            currentState = .modelLoading
        case .modelMissing:
            currentState = .modelMissing
            setupControllerIfNeeded(forceShow: true, preferredTab: .recognition)
        case .failed(let message):
            currentState = .error("模型加载失败: \(message)")
        case .ready, .suspendedForIdle:
            // 空闲卸载后的状态保持"就绪"：热键监听继续运行，真正的重新加载
            // 由 makeSession() 在下次按热键时按需触发，不在这里主动 preload（F-04）。
            activateReadyState()
        }

        syncSetupWindow()
        updateStatusUI()
    }

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

    private func ensureController() {
        if voiceTyperController == nil {
            voiceTyperController = VoiceTyperController(config: config, asrService: asrService)
            bindControllerEvents()
        }
    }

    // MARK: - 模型下载

    private func startModelDownload() {
        guard !isDownloadingModel else { return }
        isDownloadingModel = true
        modelDownloadProgress = 0
        modelDownloadError = nil
        if !isPaused, permissions.allRequiredGranted {
            currentState = .downloadingModel(0)
        }
        updateStatusUI()
        syncSetupWindow()

        let downloader = ModelDownloader()
        modelDownloader = downloader

        Task {
            do {
                try await downloader.downloadAll { [weak self] progress in
                    guard let self, self.isDownloadingModel else { return }
                    self.modelDownloadProgress = progress
                    if !self.isPaused, self.permissions.allRequiredGranted {
                        self.currentState = .downloadingModel(progress)
                    }
                    self.updateStatusUI()
                    self.syncSetupWindow()
                }
                self.isDownloadingModel = false
                self.modelDownloader = nil
                self.modelDownloadError = nil
                await self.asrService.reload()
                await self.reevaluateReadiness()
            } catch let error as ModelDownloader.DownloadError {
                self.isDownloadingModel = false
                self.modelDownloader = nil
                if case .cancelled = error {
                    // 用户主动取消：回到 .modelMissing 原地重试，而不是显示为红色失败态；
                    // resume 数据已由 ModelDownloader 落盘保留（F-11）。
                    await self.reevaluateReadiness()
                } else {
                    self.handleModelDownloadFailure(error)
                }
            } catch {
                self.isDownloadingModel = false
                self.modelDownloader = nil
                self.handleModelDownloadFailure(error)
            }
        }
    }

    private func handleModelDownloadFailure(_ error: Error) {
        AppLog.model.error("模型下载失败: \(String(describing: error), privacy: .public)")
        modelDownloadError = error.localizedDescription
        currentState = permissions.allRequiredGranted
            ? .error("模型下载失败: \(error.localizedDescription)")
            : .setupRequired
        if permissions.allRequiredGranted {
            setupControllerIfNeeded(forceShow: true, preferredTab: .recognition)
        }
        updateStatusUI()
        syncSetupWindow()
    }

    private func testLLMCorrection(llmConfig: LLMConfig, apiKey: String) async -> Bool {
        guard let chatURL = LLMEndpoint.chatCompletionsURL(from: llmConfig.baseURL) else {
            return false
        }
        let corrector = LLMCorrector(config: LLMCorrector.Config(
            chatCompletionsURL: chatURL,
            apiKey: apiKey,
            model: llmConfig.model,
            temperature: llmConfig.temperature,
            maxTokens: llmConfig.maxTokens,
            timeout: llmConfig.timeout
        ))
        let sample = "呃，这个功能和并之后应该可以用了吧"
        let result = await corrector.correct(sample)
        return result != sample && !result.isEmpty
    }

    // MARK: - 事件绑定

    private func bindControllerEvents() {
        voiceTyperController?.onStateChange = { [weak self] state in
            guard let self else { return }
            let previous = self.currentState
            self.currentState = state
            switch state {
            case .recording:
                self.recordingHUDController?.showHUD()
            case .recognizing:
                self.recordingHUDController?.setRecognizing()
            case .inserting:
                break
            case .error(let message):
                self.recordingHUDController?.showError(message)
            case .idle:
                if previous == .inserting {
                    self.recordingHUDController?.showSuccess()
                } else {
                    self.recordingHUDController?.hideHUD()
                }
            default:
                self.recordingHUDController?.hideHUD()
            }
            self.updateStatusUI()
        }

        voiceTyperController?.onPreviewUpdate = { [weak self] accumulated in
            self?.recordingHUDController?.showPreview(accumulated)
        }

        voiceTyperController?.onAudioLevel = { [weak self] level in
            self?.recordingHUDController?.updateLevel(level)
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
            AppLog.app.info("识别完成 chars=\(text.count, privacy: .public)")
        }
    }

    private func updateStatusUI() {
        statusBarController.update(
            state: currentState,
            hotkeyDisplay: config.hotkey.displayString,
            engineStatus: engineStatusText()
        )
    }

    private func reloadConfigurationFromDisk() throws {
        config = try configStore.loadOrCreate()
        recordingHUDController = RecordingHUDController(config: config.ui)
    }

    private func refreshSetupWindowEditorContent() {
        setupWindowController?.loadEditableContent(config: config)
    }

    enum ConfigSaveError: LocalizedError {
        case dictationInProgress

        var errorDescription: String? {
            "正在录音/识别/输入，请等待当前听写完成后再保存此项设置"
        }
    }

    private func applyConfig(_ updatedConfig: AppConfig) async throws {
        let previousConfig = config
        let onlyUIChanged = previousConfig.asr == updatedConfig.asr
            && previousConfig.llm == updatedConfig.llm
            && previousConfig.hotkey == updatedConfig.hotkey
        // 会破坏性重建控制器/热键监听的保存（非 UI-only）在听写进行中会丢已录制内容，
        // 拒绝而不是静默销毁；HUD 透明度等 UI-only 改动不受影响，可随时保存。
        if !onlyUIChanged, currentState.isActiveDictation {
            throw ConfigSaveError.dictationInProgress
        }

        try configStore.save(config: updatedConfig)
        if onlyUIChanged {
            // 只有 ui 段变化：不销毁/重建控制器，只更新内存配置与 HUD/设置窗口显示。
            config = updatedConfig.validated()
            refreshSetupWindowEditorContent()
        } else {
            try await reloadAndReevaluateAfterSettingsChange()
        }
    }

    private func reloadAndReevaluateAfterSettingsChange() async throws {
        voiceTyperController?.stop()
        voiceTyperController = nil
        try reloadConfigurationFromDisk()
        asrService.updateConfig(config.asr)
        refreshSetupWindowEditorContent()
        await reevaluateReadiness()
    }

    private func syncSetupWindow() {
        setupWindowController?.updateStatus(
            permissions: permissions,
            asrState: asrService.state,
            downloadProgress: isDownloadingModel ? modelDownloadProgress : nil,
            modelDownloadError: modelDownloadError,
            hotkeyDisplay: config.hotkey.displayString,
            engineStatus: engineStatusText()
        )
    }

    private func engineStatusText() -> String {
        switch asrService.state {
        case .ready:
            return "引擎已就绪"
        case .suspendedForIdle:
            return "引擎已空闲卸载，下次录音自动加载"
        case .loading:
            return "模型加载中…"
        case .modelMissing:
            return "需要下载语音模型"
        case .unloaded:
            return "引擎未加载"
        case .failed(let message):
            return "模型加载失败: \(message)"
        }
    }
}
