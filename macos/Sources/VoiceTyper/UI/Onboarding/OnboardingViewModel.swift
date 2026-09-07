import Foundation
import SwiftUI

/// 首启引导是否已完成的持久化记录。
///
/// 刻意放在 `UserDefaults` 而不是 `config.yaml`：这不是用户配置，而是应用自己的一次性
/// 状态，不该出现在配置文件里让用户去看、去改（也与「弱化手改配置文件」的取向一致）。
enum OnboardingRecord {
    private static let key = "onboarding.completedFlowVersion"

    /// 引导流程本身的版本。将来若引导步骤发生实质性变化（新增了必须让老用户也看到的
    /// 内容），把这个数字加一即可让所有人再走一次，而不需要另造一个开关。
    static let currentFlowVersion = 1

    static func isCompleted(defaults: UserDefaults = .standard) -> Bool {
        defaults.integer(forKey: key) >= currentFlowVersion
    }

    static func markCompleted(defaults: UserDefaults = .standard) {
        defaults.set(currentFlowVersion, forKey: key)
    }
}

enum OnboardingStep: Int, CaseIterable {
    case welcome
    case permissions
    case model
    case trial

    var title: String {
        switch self {
        case .welcome: return "欢迎"
        case .permissions: return "系统权限"
        case .model: return "语音模型"
        case .trial: return "试一试"
        }
    }
}

/// 「试一试」这一步的实时结果。
///
/// 这一步是整个引导的重点：三项权限授权完成 + 模型下载完成**并不等于能用**。真实链路是
/// 「热键被捕获 → 麦克风出声 → 识别出文本 → 文本插进目标应用」，其中任何一环坏掉，
/// 用户看到的现象都是同一个——"按了没反应"。这一步用一次真实听写把整条链跑通，
/// 并在失败时指出到底断在哪一环。
enum OnboardingTrialState: Equatable {
    case idle
    case recording
    case recognizing
    case succeeded(String)
    /// 链路跑通但没采到语音。与 `.failed` 分开：这不是故障，指向的是输入设备。
    case noSpeech
    case cancelled
    case failed(String)
    /// 尚未就绪就按了热键（缺权限 / 模型没好）。
    case blocked(String)
}

/// 引导窗口把听写生命周期需要的信息压缩成这几个事件，由 `AppCoordinator` 推送进来。
/// 引导层不直接持有控制器，避免出现第二条驱动听写的路径。
enum OnboardingDictationEvent {
    case recordingStarted
    case level(Float)
    case recognizing
    case inserted(String)
    case emptyResult
    case cancelled
    case failed(String)
    case blocked(String)
}

@MainActor
@Observable
final class OnboardingViewModel {
    // MARK: 流程

    var step: OnboardingStep = .welcome

    // MARK: 由协调器推送的状态

    var permissions = PermissionSnapshot(microphone: .notDetermined, accessibility: .denied, inputMonitoring: .denied)
    var asrState: ASRService.State = .unloaded
    var downloadProgress: Double?
    var modelDownloadError: String?
    var hotkeyDisplay = "Fn🌐"
    var hotkeyMode: HotkeyMode = .hold
    /// Fn 与系统「按下🌐键」行为冲突时的提示文案；无冲突为 nil。
    var fnConflictWarning: String?

    // MARK: 试一试

    var trial: OnboardingTrialState = .idle
    /// 试用输入框的内容。真实听写会通过 AX / 粘贴写进这里——这正是我们要验证的能力，
    /// 所以不能用"把识别结果直接赋值给它"来伪造成功。
    var trialText = ""
    /// 本次试用录音里出现过的最大电平。用于在识别结果为空时区分"没出声"与"识别不出"。
    private var trialPeakLevel: Float = 0

    /// 低于此电平视为基本没有采到声音（线性 RMS，约 -48dBFS）。
    /// 只用来细化提示措辞，判错的代价仅仅是提示不够精准，不影响任何流程。
    private static let silenceLevelThreshold: Float = 0.004

    // MARK: 注入的回调

    var onRequestPermission: ((PermissionKind) -> Void)?
    var onOpenSystemSettings: ((PermissionKind) -> Void)?
    var onOpenKeyboardSettings: (() -> Void)?
    var onRefreshStatus: (() -> Void)?
    var onStartModelDownload: (() -> Void)?
    var onCancelModelDownload: (() -> Void)?
    var onFinish: (() -> Void)?

    // MARK: - 派生状态

    var grantedPermissionCount: Int {
        PermissionKind.allCases.filter { permissions.status(for: $0) == .authorized }.count
    }

    var isModelReady: Bool {
        asrState == .ready || asrState == .suspendedForIdle
    }

    /// 「试一试」是否具备执行条件。不具备时这一步只显示还缺什么，不假装可以试。
    var canRunTrial: Bool {
        permissions.allRequiredGranted && isModelReady
    }

    var trialBlockingHint: String? {
        guard !canRunTrial else { return nil }
        if !permissions.allRequiredGranted {
            return "还有系统权限没有授予，请回到上一步完成授权。"
        }
        return "语音模型还没准备好，请等待或回到上一步重试下载。"
    }

    /// 底部主按钮文案。
    var primaryActionTitle: String {
        switch step {
        case .welcome, .permissions, .model:
            return "下一步"
        case .trial:
            if case .succeeded = trial { return "完成" }
            return "先跳过"
        }
    }

    var canGoBack: Bool { step != .welcome }

    // MARK: - 流程控制

    func goBack() {
        guard let previous = OnboardingStep(rawValue: step.rawValue - 1) else { return }
        step = previous
        refreshOnStepChange()
    }

    func goForward() {
        guard let next = OnboardingStep(rawValue: step.rawValue + 1) else {
            finish()
            return
        }
        step = next
        refreshOnStepChange()
    }

    func finish() {
        OnboardingRecord.markCompleted()
        onFinish?()
    }

    /// 每次切换步骤都重新探测一次：用户很可能刚从系统设置切回来。
    private func refreshOnStepChange() {
        resetTrial()
        onRefreshStatus?()
    }

    func resetTrial() {
        trial = .idle
        trialPeakLevel = 0
    }

    // MARK: - 听写事件

    func handle(_ event: OnboardingDictationEvent) {
        // 只有停在「试一试」这一步时才把听写事件解释成试用结果；用户在别的步骤顺手按了
        // 热键（完全合法）不应该污染试用状态。
        guard step == .trial else { return }

        switch event {
        case .recordingStarted:
            trialPeakLevel = 0
            trial = .recording
        case .level(let level):
            guard case .recording = trial else { return }
            trialPeakLevel = max(trialPeakLevel, level)
        case .recognizing:
            trial = .recognizing
        case .inserted(let text):
            trial = .succeeded(text)
        case .emptyResult:
            trial = trialPeakLevel < Self.silenceLevelThreshold ? .noSpeech : .failed(
                "采到了声音，但没有识别出文字。请靠近麦克风、放慢一点再试一次。"
            )
        case .cancelled:
            trial = .cancelled
        case .failed(let message):
            trial = .failed(message)
        case .blocked(let reason):
            trial = .blocked(reason)
        }
    }

    // MARK: - 试用结果文案

    /// 试用结果的标题 / 说明 / 语义色，集中在这里，避免视图里散落 switch。
    var trialSummary: (symbol: String, tint: Color, title: String, detail: String)? {
        switch trial {
        case .idle:
            return nil
        case .recording:
            return ("mic.fill", .red, "正在录音…", "说一句话，然后\(hotkeyMode == .hold ? "松开热键" : "再按一次热键")。")
        case .recognizing:
            return ("waveform", .orange, "识别中…", "本地引擎正在处理这段音频。")
        case .succeeded(let text):
            return ("checkmark.seal.fill", .green, "全部跑通了", "已插入：\(text)")
        case .noSpeech:
            return (
                "waveform.slash", .orange, "没有采到声音",
                "热键与识别链路是通的，但这段录音几乎是静音。请检查麦克风是否被静音、"
                    + "「系统设置 → 声音 → 输入」里选中的设备是否正确，然后再试一次。"
            )
        case .cancelled:
            return ("xmark.circle.fill", .secondary, "已取消", "按 Esc 可以随时取消，识别结果不会被插入。再试一次吧。")
        case .failed(let message):
            return ("exclamationmark.triangle.fill", .red, "没有成功", message)
        case .blocked(let reason):
            return ("exclamationmark.triangle.fill", .orange, "还不能开始", reason)
        }
    }
}
