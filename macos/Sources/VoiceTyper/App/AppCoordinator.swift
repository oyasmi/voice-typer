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
    private var onboardingWindowController: OnboardingWindowController?
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
    /// 听写流程内的一次性错误（识别失败/插入失败/焦点变化）超时自愈到 `.idle`。
    /// HUD 早已在 2.5s 后自动隐藏该提示（`RecordingHUDController.showError`），但此前
    /// 菜单栏状态没有对应的回落，会一直停留在红色错误态直到下一次听写（R3-04）。
    private var dictationErrorRecoveryWorkItem: DispatchWorkItem?

    /// 已经"强制弹窗"过的理由。每个理由在一次未就绪期内只抢一次焦点。
    ///
    /// 此前 `.modelMissing` 分支每次 `reevaluateReadiness()` 都会 `forceShow` +
    /// `NSApp.activate(ignoringOtherApps:)`——用户刚点完「取消下载」，窗口反而抢焦点
    /// 跳到最前面（B7）。恢复就绪时清空，这样"就绪 → 又不就绪"仍然会再提醒一次。
    private var forcedPresentations: Set<ForcedPresentation> = []
    private enum ForcedPresentation: Hashable {
        case permissions
        case model
    }

    /// 上一次因"未就绪时按下热键"而把窗口摆到用户面前的时刻。用于节流：
    /// 连按热键不应该连开好几次窗口。
    private var lastBlockedAttemptPresentation: Date?
    private static let blockedAttemptPresentationInterval: TimeInterval = 5

    /// 模型下载失败后的自动重试退避序列（秒）。
    ///
    /// 只试一次就放弃太保守——下载失败绝大多数是网络抖动，而首启拿不到模型意味着
    /// 应用完全不可用，这是新用户能遇到的最糟的第一印象。断点续传数据已经落盘，
    /// 重试的成本只是续上而不是从头再来。也不能无限重试：真的没网时那会变成一个
    /// 用户看不见的死循环，所以给定次数用完后停下来，把手动重试交还给用户。
    private static let downloadRetryDelays: [TimeInterval] = [5, 20, 60]
    private var downloadRetryAttempt = 0
    private var downloadRetryTask: Task<Void, Never>?

    /// 缓存的 Fn 键冲突提示。
    ///
    /// 不在 `syncAuxiliaryWindows()` 里现算：那个函数在模型下载期间会以 5Hz 被调用，
    /// 而每次查询都要 `CFPreferencesAppSynchronize` 一次（同步 IPC）。只在真正可能变化的
    /// 时机重算：引导/设置窗口出现、用户点「重新检测」、热键配置变更。
    private var cachedFnConflictWarning: String?

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

        // 首次运行走完整引导（欢迎 → 权限 → 模型 → 试一试），而不是直接把设置窗口的
        // 权限页推到用户面前：三项权限全绿并不等于能用，真正决定"能不能用"的是
        // 「热键 → 麦克风 → 识别 → 插入」这条链，引导的最后一步会把它真的跑一遍。
        if !OnboardingRecord.isCompleted() {
            presentOnboarding()
        } else if !permissions.allRequiredGranted {
            forcedPresentations.insert(.permissions)
            setupControllerIfNeeded(forceShow: true, preferredTab: .permissions)
        }

        // 权限与模型是两条互不依赖的准备线：权限没给全时模型照样在后台下载/加载，
        // 用户授权完立刻可用。
        asrService.updateConfig(config.asr)
        Task {
            await prepareEngineForLaunch()
            await reevaluateReadiness()
        }
    }

    func openSetupWindow() {
        userOpenedSetup = true
        setupControllerIfNeeded(forceShow: true, preferredTab: nil)
    }

    /// 启动 / 尚未加载时如何准备引擎：尊重「启动时预加载模型」这项设置。
    ///
    /// 关闭时（默认）只确认模型文件在不在，不把 ~510MB 的引擎拉进内存——首次按热键
    /// 才加载，且加载与录音并行（`ASRService.makeSession`），用户通常正在说第一句话。
    private func prepareEngineForLaunch() async {
        if config.asr.preloadOnLaunch {
            await asrService.preload()
        } else {
            await asrService.prepareWithoutLoading()
        }
    }

    /// 用户点应用图标（Dock / Launchpad / 访达）时的落点：引导没走完就继续引导，
    /// 否则打开设置窗口。
    func handleReopen() {
        if !OnboardingRecord.isCompleted() {
            presentOnboarding()
        } else if isOnboardingVisible {
            onboardingWindowController?.presentWindow()
        } else {
            openSetupWindow()
        }
    }

    private func bindStatusBarActions() {
        statusBarController.onOpenSetup = { [weak self] in
            self?.openSetupWindow()
        }
        statusBarController.onTogglePause = { [weak self] in
            self?.togglePause()
        }
        statusBarController.onOpenOnboarding = { [weak self] in
            self?.presentOnboarding()
        }
        statusBarController.onCheckForUpdates = { [weak self] in
            self?.checkForUpdates()
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
                Task { await self?.refreshPermissionsWithoutStealingFocus() }
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
                    do {
                        try self.voiceTyperController?.resumeHotkeyListening()
                    } catch {
                        // 恢复失败：控制器已把自己复位成未运行，reevaluateReadiness() 会
                        // 通过 activateReadyState() 的 start() 重试路径重新拉起，仍失败则落
                        // 到 .error 让用户看到，而不是"显示就绪但热键已死"（R4-xx）。
                        AppLog.hotkey.error("恢复热键监听失败，交由就绪重评重试: \(error.localizedDescription, privacy: .public)")
                    }
                    Task { await self.reevaluateReadiness() }
                    return true
                }
            }
            controller.onPreviewHUDOpacity = { [weak self] opacity in
                self?.recordingHUDController?.previewOpacity(opacity)
            }
            controller.onOpenKeyboardSettings = {
                NSWorkspace.shared.open(SystemSettingsURL.keyboard)
            }
            controller.onClose = { [weak self] in
                self?.userOpenedSetup = false
            }
            controller.onStartModelDownload = { [weak self] in
                self?.startModelDownload()
            }
            controller.onCancelModelDownload = { [weak self] in
                self?.cancelModelDownload()
            }
            controller.onReloadModel = { [weak self] in
                guard let self, !self.currentState.isActiveDictation else { return }
                Task { await self.asrService.reload() }
            }
            controller.onTestLLMCorrection = { [weak self] llmConfig, apiKey in
                await self?.testLLMCorrection(llmConfig: llmConfig, apiKey: apiKey) ?? .failure(SimpleMessageError(message: "应用未就绪"))
            }
            controller.loadWindow()
            setupWindowController = controller
            refreshSetupWindowEditorContent()
        }

        guard let setupWindowController else { return }

        syncAuxiliaryWindows()

        // 引导窗口在前时绝不抢焦点：两个窗口互相往前抢会让用户什么都点不了。
        guard onboardingWindowController?.window?.isVisible != true else { return }

        // 是否抢焦点**只**由 forceShow 决定。此前这里还有
        // `|| !permissions.allRequiredGranted || !isModelReady`，意味着只要还没就绪，
        // 任何一次"顺手同步一下窗口内容"的调用都会把窗口抢到最前——这正是"点了取消下载，
        // 窗口反而跳出来"的成因（B7）。所有需要弹窗的调用点都已显式传 forceShow: true。
        guard forceShow else { return }

        if let preferredTab {
            setupWindowController.selectTab(preferredTab)
        }
        NSApp.activate(ignoringOtherApps: true)
        setupWindowController.presentWindow()
    }

    // MARK: - 首启引导

    func presentOnboarding() {
        if onboardingWindowController == nil {
            let controller = OnboardingWindowController()
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
            controller.onOpenKeyboardSettings = {
                NSWorkspace.shared.open(SystemSettingsURL.keyboard)
            }
            controller.onRefreshStatus = { [weak self] in
                Task { await self?.refreshPermissionsWithoutStealingFocus() }
            }
            controller.onStartModelDownload = { [weak self] in
                self?.startModelDownload()
            }
            controller.onCancelModelDownload = { [weak self] in
                self?.cancelModelDownload()
            }
            controller.onClose = { [weak self] in
                guard let self else { return }
                // 引导结束后可能仍有未完成的准备工作，这时才轮到设置窗口出场。
                Task { await self.reevaluateReadiness() }
            }
            onboardingWindowController = controller
        }
        // 引导要盖住设置窗口，避免两个窗口同时对着用户。
        setupWindowController?.window?.orderOut(nil)
        refreshFnConflictWarning()
        syncAuxiliaryWindows()
        onboardingWindowController?.presentWindow()
    }

    private var isOnboardingVisible: Bool {
        onboardingWindowController?.window?.isVisible == true
    }

    /// 把一次听写生命周期事件转给引导窗口的「试一试」步骤。窗口不可见时直接丢弃，
    /// 不做任何多余工作（电平事件每秒几十次）。
    private func forwardToOnboarding(_ event: OnboardingDictationEvent) {
        guard isOnboardingVisible else { return }
        onboardingWindowController?.handleDictationEvent(event)
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

    /// 纯函数：只依据已知信息决定"应该进入的状态"，不产生任何副作用。从
    /// `reevaluateReadiness()` 里拆出来是为了可单测（不需要构造 `AppCoordinator` 的全部
    /// 依赖），并让"决定状态"与"执行副作用"两件事在代码里分开——此前二者揉在一起，
    /// 每次任何触发点（下载进度、空闲卸载、权限变化…）调用这个函数都会无条件重跑一遍
    /// 全部副作用，这正是 R3-02 那类重入 bug 的滋生土壤（R3-12）。
    ///
    /// `previous` 只在 `.ready`/`.suspendedForIdle` 分支起作用：正在进行中的听写
    /// （`.recording`/`.recognizing`/`.inserting`）不应被模型状态变化覆盖回 `.idle`。
    static func computeTargetState(
        permissions: PermissionSnapshot,
        isPaused: Bool,
        isDownloadingModel: Bool,
        downloadProgress: Double,
        asrState: ASRService.State,
        previous: AppState
    ) -> AppState {
        if isPaused { return .paused }
        guard permissions.allRequiredGranted else { return .setupRequired }
        if isDownloadingModel { return .downloadingModel(downloadProgress) }

        switch asrState {
        case .unloaded:
            return .modelLoading
        case .loading:
            // 空闲卸载后首次按热键：makeSession() 会一边异步加载模型、一边立刻开始录音，
            // 因此模型进入 .loading 时上一轮 AppState 已稳定为活动听写态。若这里无条件
            // 返回 .modelLoading，reevaluateReadiness() 会判定"进行中的听写被未就绪覆盖"
            // 而调用 voiceTyperController.stop()，把首段录音音频丢掉（F-xx）。
            // LocalASRSession 已支持引擎未就绪时缓存 pendingAudio 并在就绪后回灌，
            // 所以听写期间的 .loading 应保留活动态，让加载与录音并行完成。
            // .unloaded（引擎从未加载）不做同样的保护：那是真正需要等待的情形。
            switch previous {
            case .recording, .recognizing, .inserting:
                return previous
            default:
                return .modelLoading
            }
        case .modelMissing:
            return .modelMissing
        case .failed(let message):
            return .error("模型加载失败: \(message)")
        case .ready, .suspendedForIdle:
            // 空闲卸载后的状态保持"就绪"：热键监听继续运行，真正的重新加载
            // 由 makeSession() 在下次按热键时按需触发，不在这里主动 preload（F-04）。
            switch previous {
            case .recording, .recognizing, .inserting:
                return previous
            default:
                return .idle
            }
        }
    }

    /// 权限页轮询、以及页面上的「重新检测」按钮共用的入口：只重新探测权限状态、刷新界面
    /// 上的勾选显示，不触发 `reevaluateReadiness()` 里"权限未齐全就 `NSApp.activate` + 置顶
    /// 设置窗口"的副作用。那条副作用是为"首次检测到缺权限，弹窗引导用户"设计的——用在
    /// 每 2 秒一次的后台轮询上，会导致用户正在系统设置里勾选权限时，本窗口每 2 秒抢一次
    /// 焦点、盖到系统设置上面，实际操作系统设置的窗口变得几乎不可用（R4-14）。
    ///
    /// 只有权限已经**全部**授权时才转交给完整的 `reevaluateReadiness()`：那种情况下它会
    /// 正常收起设置窗、把控制器切到就绪态，是用户期望看到的收尾，不属于"抢焦点"。
    private func refreshPermissionsWithoutStealingFocus() async {
        permissions = permissionCenter.snapshot()
        // 用户很可能刚从系统设置切回来，「按下🌐键」也可能是刚改的。
        refreshFnConflictWarning()
        syncAuxiliaryWindows()
        if permissions.allRequiredGranted {
            await reevaluateReadiness()
        }
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

        permissions = permissionCenter.snapshot()

        let previousState = currentState
        let target = Self.computeTargetState(
            permissions: permissions,
            isPaused: isPaused,
            isDownloadingModel: isDownloadingModel,
            downloadProgress: modelDownloadProgress,
            asrState: asrService.state,
            previous: currentState
        )
        currentState = target

        // 一段进行中的听写被"未就绪"覆盖（例如运行中权限被撤销、模型被重新加载）：
        // 必须把它正常拆掉，否则会留下一段永远收不了尾的会话。此前 `.setupRequired`
        // 分支靠无条件 `stop()` 顺带做到了这件事，现在门禁态要保留热键监听（B3），
        // 就得把"拆会话"这个意图显式写出来。
        if previousState.isActiveDictation, !target.isActiveDictation {
            voiceTyperController?.stop()
        }

        switch target {
        case .setupRequired:
            recordingHUDController?.hideHUD()
            // 只要输入监控权限具备就继续监听热键：按下时明确告知还缺什么，
            // 而不是让用户对着一个毫无反应的热键猜（B3）。
            gateHotkeyListening(reason: missingPermissionsReason())
            presentBlockingGuidance(.permissions, preferredTab: .permissions)
        case .modelMissing:
            recordingHUDController?.hideHUD()
            gateHotkeyListening(reason: "语音模型还没准备好，无法开始听写。")
            presentBlockingGuidance(.model, preferredTab: .recognition)
        case .downloadingModel(let progress):
            gateHotkeyListening(reason: "语音模型正在下载（\(Int(progress * 100))%），完成后即可开始听写。")
        case .modelLoading:
            gateHotkeyListening(reason: "识别引擎正在加载，请稍候再试。")
            if asrService.state == .unloaded {
                Task { await prepareEngineForLaunch() }
            }
        case .idle, .recording, .recognizing, .inserting:
            activateReadyState()
        default:
            break
        }

        syncAuxiliaryWindows()
        updateStatusUI()
    }

    /// 未就绪的原因文案：具体说出缺哪几项，而不是笼统的"权限不足"。
    private func missingPermissionsReason() -> String {
        let missing = PermissionKind.allCases
            .filter { permissions.status(for: $0) != .authorized }
            .map(\.title)
        guard !missing.isEmpty else { return "还有准备工作没有完成。" }
        return "还缺「\(missing.joined(separator: "」「"))」权限，暂时无法听写。"
    }

    /// 门禁态：热键照常监听，但按下时只给提示、不开始录音。
    ///
    /// 输入监控权限本身缺失时 tap 建不起来，此时退回到"停掉控制器"——那种情况下
    /// 按热键本来就不可能被我们收到，没有可提示的机会。
    private func gateHotkeyListening(reason: String) {
        guard permissions.inputMonitoring == .authorized else {
            voiceTyperController?.stop()
            return
        }
        ensureController()
        guard let controller = voiceTyperController else { return }
        controller.blockedReason = reason
        guard !controller.isStarted else { return }
        do {
            try controller.start()
        } catch {
            // 门禁态下热键起不来不算故障：用户本来就还没走完准备流程。记日志即可，
            // 不要用一个红色错误态盖住"还缺什么"这条真正有用的信息。
            AppLog.hotkey.warning("门禁态热键监听启动失败: \(error.localizedDescription, privacy: .public)")
        }
    }

    /// 强制把引导/设置窗口摆到用户面前，但同一个理由在一次未就绪期内只做一次（B7）。
    private func presentBlockingGuidance(_ reason: ForcedPresentation, preferredTab: SetupTab) {
        guard !isOnboardingVisible else { return }
        guard !forcedPresentations.contains(reason) else {
            // 已经提醒过：只确保窗口对象存在并同步内容，不再抢焦点。
            setupControllerIfNeeded()
            return
        }
        forcedPresentations.insert(reason)
        setupControllerIfNeeded(forceShow: true, preferredTab: preferredTab)
    }

    private func activateReadyState() {
        ensureController()
        // 就绪：解除门禁，并允许"下次再不就绪时"重新提醒一次。
        voiceTyperController?.blockedReason = nil
        forcedPresentations.removeAll()
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

    /// - Parameter isAutomaticRetry: 由退避重试触发时为 true。用户手动点「重试下载」
    ///   算作一次新的尝试序列，会把退避计数清零——那是明确的"我要它继续试"的信号。
    private func startModelDownload(isAutomaticRetry: Bool = false) {
        guard !isDownloadingModel else { return }
        downloadRetryTask?.cancel()
        downloadRetryTask = nil
        if !isAutomaticRetry {
            downloadRetryAttempt = 0
        }
        isDownloadingModel = true
        modelDownloadProgress = 0
        modelDownloadError = nil
        if !isPaused, permissions.allRequiredGranted {
            currentState = .downloadingModel(0)
        }
        updateStatusUI()
        syncAuxiliaryWindows()

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
                    self.syncAuxiliaryWindows()
                }
                self.isDownloadingModel = false
                self.modelDownloader = nil
                self.modelDownloadError = nil
                self.downloadRetryAttempt = 0
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
        let reason = error.localizedDescription

        if downloadRetryAttempt < Self.downloadRetryDelays.count {
            let delay = Self.downloadRetryDelays[downloadRetryAttempt]
            downloadRetryAttempt += 1
            modelDownloadError = "\(reason)将在 \(Int(delay)) 秒后自动重试"
                + "（第 \(downloadRetryAttempt)/\(Self.downloadRetryDelays.count) 次）。"
            scheduleDownloadRetry(after: delay)
        } else {
            modelDownloadError = "\(reason)已自动重试 \(Self.downloadRetryDelays.count) 次仍未成功，"
                + "请检查网络后手动重试。"
        }

        currentState = permissions.allRequiredGranted
            ? .error("模型下载失败: \(reason)")
            : .setupRequired
        if permissions.allRequiredGranted {
            forcedPresentations.insert(.model)
            setupControllerIfNeeded(forceShow: true, preferredTab: .recognition)
        }
        updateStatusUI()
        syncAuxiliaryWindows()
    }

    private func scheduleDownloadRetry(after delay: TimeInterval) {
        downloadRetryTask?.cancel()
        downloadRetryTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            guard !Task.isCancelled, let self else { return }
            // 期间用户可能已经手动下载完、或主动取消并不想再试了。
            guard !self.isDownloadingModel, self.asrService.state == .modelMissing else { return }
            AppLog.model.info("模型下载自动重试（第 \(self.downloadRetryAttempt, privacy: .public) 次）")
            self.startModelDownload(isAutomaticRetry: true)
        }
    }

    /// 用户主动取消下载：同时取消尚未触发的自动重试——他刚刚表达了"先不下"。
    private func cancelModelDownload() {
        downloadRetryTask?.cancel()
        downloadRetryTask = nil
        modelDownloader?.cancel()
    }

    /// 返回 `.success` 时携带模型的真实校对结果，`.failure` 时携带真实错误描述
    /// （401 / 超时 / 网络不通…），而不是把"网络不通"与"模型认为无需修改"混为一谈（R3-13）。
    private func testLLMCorrection(llmConfig: LLMConfig, apiKey: String) async -> Result<String, SimpleMessageError> {
        guard let chatURL = LLMEndpoint.chatCompletionsURL(from: llmConfig.baseURL) else {
            return .failure(SimpleMessageError(message: "Base URL 无法解析为合法请求地址"))
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
        do {
            let corrected = try await corrector.test(sample)
            return .success(corrected)
        } catch {
            return .failure(SimpleMessageError(message: error.localizedDescription))
        }
    }

    // MARK: - 检查更新

    /// 只在用户主动点菜单时跑一次，不做后台自动检查（见 `UpdateChecker` 的注释）。
    private func checkForUpdates() {
        Task { [weak self] in
            guard let self else { return }
            do {
                let outcome = try await UpdateChecker.checkForUpdate()
                self.presentUpdateOutcome(outcome)
            } catch {
                AppLog.app.warning("检查更新失败: \(error.localizedDescription, privacy: .public)")
                self.presentAlert(
                    title: "无法检查更新",
                    message: "\(error.localizedDescription)\n可以稍后重试，或直接到 GitHub 发布页查看。",
                    openURL: AppConstants.repositoryURL.appendingPathComponent("releases")
                )
            }
        }
    }

    private func presentUpdateOutcome(_ outcome: UpdateChecker.Outcome) {
        switch outcome {
        case .upToDate(let current):
            presentAlert(title: "已是最新版本", message: "当前版本 \(current)。", openURL: nil)
        case .updateAvailable(let release):
            presentAlert(
                title: "有新版本可用",
                message: "最新版本 \(release.version)，当前版本 \(AppConstants.version)。",
                openURL: release.pageURL,
                openButtonTitle: "打开发布页"
            )
        case .indeterminate(let pageURL):
            presentAlert(
                title: "无法比较版本号",
                message: "已获取到最新的发布信息，但无法解析版本号。请自行到发布页确认。",
                openURL: pageURL,
                openButtonTitle: "打开发布页"
            )
        }
    }

    private func presentAlert(title: String, message: String, openURL: URL?, openButtonTitle: String = "打开") {
        // `.accessory` 应用不会自动到前台，不激活的话弹窗可能出现在其他窗口后面。
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.alertStyle = .informational
        if openURL != nil {
            alert.addButton(withTitle: openButtonTitle)
            alert.addButton(withTitle: "好")
        } else {
            alert.addButton(withTitle: "好")
        }
        let response = alert.runModal()
        if let openURL, response == .alertFirstButtonReturn {
            NSWorkspace.shared.open(openURL)
        }
    }

    // MARK: - 事件绑定

    private func bindControllerEvents() {
        voiceTyperController?.onStateChange = { [weak self] state in
            guard let self else { return }
            let previous = self.currentState
            self.currentState = state
            switch state {
            case .recording:
                self.dictationErrorRecoveryWorkItem?.cancel()
                self.recordingHUDController?.showHUD()
                self.forwardToOnboarding(.recordingStarted)
            case .recognizing:
                self.recordingHUDController?.setRecognizing()
                self.forwardToOnboarding(.recognizing)
            case .inserting:
                break
            case .error(let message):
                self.recordingHUDController?.showError(message)
                self.forwardToOnboarding(.failed(message))
                self.scheduleDictationErrorRecovery()
            case .idle:
                self.dictationErrorRecoveryWorkItem?.cancel()
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
            guard let self else { return }
            self.recordingHUDController?.updateLevel(level)
            self.forwardToOnboarding(.level(level))
        }

        // 识别跑通但一个字都没有：给一个明确的、指向输入设备的提示，
        // 而不是让 HUD 静默消失（B4）。
        voiceTyperController?.onEmptyRecognition = { [weak self] in
            guard let self else { return }
            self.recordingHUDController?.showNoSpeech()
            self.forwardToOnboarding(.emptyResult)
        }

        voiceTyperController?.onBlockedAttempt = { [weak self] reason in
            self?.handleBlockedHotkeyAttempt(reason: reason)
        }

        // 本地识别已出结果、开始等 LLM 校对：把 HUD 从"识别中"切到"校对中"。
        // 菜单栏状态保持 .recognizing——这一层的粒度就到"正在处理"为止，
        // 细分到底在等本地推理还是网络是 HUD 的职责。
        voiceTyperController?.onCorrectionStarted = { [weak self] in
            self?.recordingHUDController?.setCorrecting()
        }

        voiceTyperController?.onPreviewWarning = { [weak self] message in
            self?.recordingHUDController?.flashWarning(message)
        }

        voiceTyperController?.onCancelled = { [weak self] in
            guard let self else { return }
            self.currentState = .idle
            self.recordingHUDController?.showCanceled()
            self.forwardToOnboarding(.cancelled)
            self.updateStatusUI()
        }

        voiceTyperController?.onRecognizedText = { [weak self] text in
            AppLog.app.info("识别完成 chars=\(text.count, privacy: .public)")
            self?.forwardToOnboarding(.inserted(text))
        }
    }

    /// 未就绪时按下热键。给两层反馈：HUD 立刻说明原因（用户的视线就在屏幕上），
    /// 并把对应窗口摆到面前——但做节流，连按热键不该连开好几次窗口。
    private func handleBlockedHotkeyAttempt(reason: String) {
        forwardToOnboarding(.blocked(reason))
        recordingHUDController?.showError(reason)

        guard !isOnboardingVisible else { return }
        let now = Date()
        if let last = lastBlockedAttemptPresentation,
           now.timeIntervalSince(last) < Self.blockedAttemptPresentationInterval {
            return
        }
        lastBlockedAttemptPresentation = now
        let tab: SetupTab = permissions.allRequiredGranted ? .recognition : .permissions
        setupControllerIfNeeded(forceShow: true, preferredTab: tab)
    }

    /// 与 `RecordingHUDController.showError` 的自动隐藏时长（2.5s）保持一致，让菜单栏
    /// 状态与 HUD 同步回落，而不是一直停在红色错误态直到下一次听写（R3-04）。
    private func scheduleDictationErrorRecovery() {
        dictationErrorRecoveryWorkItem?.cancel()
        let item = DispatchWorkItem { [weak self] in
            guard let self, case .error = self.currentState else { return }
            self.currentState = .idle
            self.updateStatusUI()
        }
        dictationErrorRecoveryWorkItem = item
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.5, execute: item)
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
        // 热键可能刚被改成组合键，那样就不再有 Fn 冲突可言。
        refreshFnConflictWarning()
        // HUD 是长生命周期组件：配置变更就地生效，不重建。此前每次保存非 UI 配置
        // （例如改热键）都会丢弃整个 HUD 实例，连带丢掉已构建的视图层与几何状态（B8）。
        if let recordingHUDController {
            recordingHUDController.updateConfig(config.ui)
        } else {
            recordingHUDController = RecordingHUDController(config: config.ui)
        }
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
            // 必须显式下发给 HUD：此前透明度是靠滑杆的实时预览回调"顺手"生效的，
            // HUD 位置这类没有实时预览通道的设置不会有那份运气（保存后要到下次
            // 听写才生效，且换台机器/重启后行为不一致）。
            recordingHUDController?.updateConfig(config.ui)
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

    private func syncAuxiliaryWindows() {
        let progress = isDownloadingModel ? modelDownloadProgress : nil
        setupWindowController?.updateStatus(
            permissions: permissions,
            asrState: asrService.state,
            downloadProgress: progress,
            modelDownloadError: modelDownloadError,
            hotkeyDisplay: config.hotkey.displayString,
            engineStatus: engineStatusText()
        )
        onboardingWindowController?.updateStatus(
            permissions: permissions,
            asrState: asrService.state,
            downloadProgress: progress,
            modelDownloadError: modelDownloadError,
            hotkey: config.hotkey,
            fnConflictWarning: cachedFnConflictWarning
        )
    }

    private func refreshFnConflictWarning() {
        cachedFnConflictWarning = SystemKeyboardSettings.fnConflictWarning(for: config.hotkey)
    }

    /// 菜单栏下拉里那行副标题。
    ///
    /// 必须自己感知下载进度：下载期间 `asrService.state` 仍是 `.modelMissing`，
    /// 而权限没给全时 `computeTargetState` 又会直接返回 `.setupRequired`——两者叠加的
    /// 结果是首次安装的用户在最想知道"它在下载吗、还要多久"的时刻，菜单里完全看不到
    /// 下载进度（B2）。
    private func engineStatusText() -> String {
        if isDownloadingModel {
            return "模型下载中 \(Int(modelDownloadProgress * 100))%"
        }
        if let modelDownloadError {
            return "模型下载失败：\(modelDownloadError)"
        }
        switch asrService.state {
        case .ready:
            return "引擎已就绪"
        case .suspendedForIdle:
            // 空闲卸载后与"启动时未预加载"共用这个状态，文案要对两者都成立。
            return "引擎按需加载，下次录音自动就绪"
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
