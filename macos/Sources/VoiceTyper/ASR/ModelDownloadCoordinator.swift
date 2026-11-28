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

/// 模型下载器的最小抽象，供协调器编排测试注入可控桩（S-6）。
@MainActor
protocol ModelDownloading: AnyObject {
    func downloadAll(onProgress: @escaping (Double) -> Void) async throws
    func cancel()
}

extension ModelDownloader: ModelDownloading {}

/// 模型下载编排器（S-6，对齐 Windows `ModelDownloadOrchestrator`）：
/// 将首次自动拉取策略、进度/速率统计、失败指数退避重试与手动取消从 `AppCoordinator` 中解耦。
@MainActor
final class ModelDownloadCoordinator {
    /// 模型下载失败后的自动重试退避序列（秒）：
    /// 网络抖动占首启失败的大多数，配合断点续传以有限次数自动续拉；用尽后交还用户手动重试。
    static let defaultRetryDelays: [TimeInterval] = [5, 20, 60]

    private(set) var isDownloading = false
    private(set) var progress = 0.0
    private(set) var errorMessage: String?
    /// 下载完成后等待模型加载就绪时向用户弹一次「可以用了」提示（R8-C5）。
    private(set) var pendingReadyNotice = false

    var onProgressChanged: ((Double) -> Void)?
    var onSucceeded: (() async -> Void)?
    var onCancelled: (() -> Void)?
    var onFailed: ((_ rawReason: String, _ retriesExhausted: Bool) -> Void)?
    /// 自动退避到期时再次确认模型是否仍缺失（用户可能已手动侧载或状态已改变）。
    var isModelStillMissing: (() -> Bool)?

    private var automaticPolicy = AutomaticModelDownloadPolicy()
    private var downloadStats = DownloadStats()
    private var activeDownloader: (any ModelDownloading)?
    private(set) var retryAttempt = 0
    private var retryTask: Task<Void, Never>?

    private let retryDelays: [TimeInterval]
    private let downloaderFactory: () -> any ModelDownloading
    private let sleep: (TimeInterval) async -> Void
    private let uptime: () -> TimeInterval

    init(
        retryDelays: [TimeInterval] = ModelDownloadCoordinator.defaultRetryDelays,
        downloaderFactory: @escaping () -> any ModelDownloading = { ModelDownloader() },
        sleep: @escaping (TimeInterval) async -> Void = { delay in
            try? await Task.sleep(nanoseconds: UInt64(max(0, delay) * 1_000_000_000))
        },
        uptime: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }
    ) {
        self.retryDelays = retryDelays
        self.downloaderFactory = downloaderFactory
        self.sleep = sleep
        self.uptime = uptime
    }

    /// 下载进度详情（已下载 / 总量 · 速度 · 剩余）；不在下载时为 nil（R8-C5）。
    var detailText: String? {
        guard isDownloading else { return nil }
        return downloadStats.text(progress: progress)
    }

    func consumePendingReadyNotice() {
        pendingReadyNotice = false
    }

    /// 若本进程尚未自动尝试过且当前确系模型缺失，启动首次自动下载。
    func startAutomaticIfNeeded(for asrState: ASRService.State) {
        if automaticPolicy.shouldStart(for: asrState, isDownloading: isDownloading) {
            start()
        }
    }

    /// - Parameter isAutomaticRetry: 由退避重试触发时为 true；用户手动点「立即下载 / 重试下载」
    ///   传 false，会将退避计数清零重新开始一轮尝试。
    func start(isAutomaticRetry: Bool = false) {
        guard !isDownloading else { return }
        retryTask?.cancel()
        retryTask = nil
        if !isAutomaticRetry {
            retryAttempt = 0
        }
        isDownloading = true
        progress = 0
        errorMessage = nil
        downloadStats.reset()
        onProgressChanged?(0)

        let downloader = downloaderFactory()
        activeDownloader = downloader

        Task { [weak self] in
            guard let self else { return }
            do {
                try await downloader.downloadAll { [weak self] fraction in
                    guard let self, self.isDownloading else { return }
                    self.progress = fraction
                    self.downloadStats.record(progress: fraction, at: self.uptime())
                    self.onProgressChanged?(fraction)
                }
                self.isDownloading = false
                self.activeDownloader = nil
                self.errorMessage = nil
                self.retryAttempt = 0
                self.pendingReadyNotice = true
                await self.onSucceeded?()
            } catch let error as ModelDownloader.DownloadError {
                self.isDownloading = false
                self.activeDownloader = nil
                if case .cancelled = error {
                    self.onCancelled?()
                } else {
                    self.handleFailure(error)
                }
            } catch {
                self.isDownloading = false
                self.activeDownloader = nil
                self.handleFailure(error)
            }
        }
    }

    /// 用户主动取消下载：同时取消尚未触发的自动退避重试。
    func cancel() {
        let hadPendingRetry = retryTask != nil
        retryTask?.cancel()
        retryTask = nil
        if let downloader = activeDownloader {
            downloader.cancel()
        } else if hadPendingRetry {
            errorMessage = nil
            onCancelled?()
        }
    }

    private func handleFailure(_ error: Error) {
        AppLog.model.error("模型下载失败: \(String(describing: error), privacy: .public)")
        let reason = error.localizedDescription

        let retriesExhausted: Bool
        if retryAttempt < retryDelays.count {
            let delay = retryDelays[retryAttempt]
            retryAttempt += 1
            errorMessage = LF(
                "%@将在 %d 秒后自动重试（第 %d/%d 次）。",
                reason, Int(delay), retryAttempt, retryDelays.count
            )
            retriesExhausted = false
            scheduleRetry(after: delay)
        } else {
            errorMessage = LF(
                "%@已自动重试 %d 次仍未成功，请检查网络后手动重试。",
                reason, retryDelays.count
            )
            retriesExhausted = true
        }
        onFailed?(reason, retriesExhausted)
    }

    private func scheduleRetry(after delay: TimeInterval) {
        retryTask?.cancel()
        retryTask = Task { [weak self] in
            guard let self else { return }
            await self.sleep(delay)
            guard !Task.isCancelled else { return }
            guard !self.isDownloading, self.isModelStillMissing?() ?? true else { return }
            AppLog.model.info("模型下载自动重试（第 \(self.retryAttempt, privacy: .public) 次）")
            self.start(isAutomaticRetry: true)
        }
    }
}
