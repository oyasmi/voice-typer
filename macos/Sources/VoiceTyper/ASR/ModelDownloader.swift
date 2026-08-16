import CryptoKit
import Foundation

/// 首次启动时从 ModelScope 拉取 SenseVoice-Small 权重。
///
/// 端点与 sha256 均已实测验证（见 macos/DESIGN.md §4.4）：
/// `https://www.modelscope.cn/api/v1/models/iic/SenseVoiceSmall-onnx/repo?Revision=master&FilePath=<file>`，
/// 大文件走 302 → OSS，`Range` 请求返回 206，断点续传可用。
@MainActor
final class ModelDownloader: NSObject {
    struct FileSpec {
        let name: String
        let sha256: String
        let sizeHint: Int64
    }

    enum DownloadError: LocalizedError {
        case checksumMismatch(String)
        case httpStatus(String, Int)
        case cancelled

        var errorDescription: String? {
            switch self {
            case .checksumMismatch(let name):
                return "文件 \(name) 校验失败，可能是下载损坏，请重试。"
            case .httpStatus(let name, let code):
                return "下载 \(name) 失败（HTTP \(code)）。"
            case .cancelled:
                return "下载已取消。"
            }
        }
    }

    /// 小文件先行：网络/端点问题能在花掉 230MB 流量之前就暴露。
    nonisolated static let files: [FileSpec] = [
        FileSpec(name: "config.yaml", sha256: "f71e239ba36705564b5bf2d2ffd07eece07b8e3f2bbf6d2c99d8df856339ac19", sizeHint: 1_855),
        FileSpec(name: "am.mvn", sha256: "29b3c740a2c0cfc6b308126d31d7f265fa2be74f3bb095cd2f143ea970896ae5", sizeHint: 11_203),
        FileSpec(name: "tokens.json", sha256: "a2594fc1474e78973149cba8cd1f603ebed8c39c7decb470631f66e70ce58e97", sizeHint: 352_000),
        FileSpec(name: "model_quant.onnx", sha256: "21dc965f689a78d1604717bf561e40d5a236087c85a95584567835750549e822", sizeHint: 241_216_270),
    ]

    nonisolated static let totalBytes: Int64 = files.reduce(0) { $0 + $1.sizeHint }

    private static let endpointBase = "https://www.modelscope.cn/api/v1/models/iic/SenseVoiceSmall-onnx/repo"

    private var session: URLSession!
    private var activeTask: URLSessionDownloadTask?
    private var activeContinuation: CheckedContinuation<URL, Error>?
    private var activeProgressHandler: ((Double) -> Void)?
    private var isCancelled = false

    override init() {
        super.init()
        session = URLSession(configuration: .default, delegate: self, delegateQueue: nil)
    }

    /// 下载全部 4 个文件到 `ModelLocator.downloadDestination`；已存在且校验通过的文件跳过。
    /// - Parameter onProgress: 总体进度回调（0…1），在 MainActor 上触发。
    func downloadAll(onProgress: @escaping (Double) -> Void) async throws {
        isCancelled = false
        let dir = ModelLocator.downloadDestination
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)

        var completedBytes: Int64 = 0
        for spec in Self.files {
            if isCancelled { throw DownloadError.cancelled }
            let destURL = dir.appendingPathComponent(spec.name)
            if FileManager.default.fileExists(atPath: destURL.path), Self.sha256Matches(destURL, spec.sha256) {
                completedBytes += spec.sizeHint
                onProgress(Double(completedBytes) / Double(Self.totalBytes))
                continue
            }
            let base = completedBytes
            try await downloadOne(spec, into: dir) { fileProgress in
                let overall = Double(base) + Double(spec.sizeHint) * fileProgress
                onProgress(min(1.0, overall / Double(Self.totalBytes)))
            }
            completedBytes += spec.sizeHint
            onProgress(min(1.0, Double(completedBytes) / Double(Self.totalBytes)))
        }
    }

    func cancel() {
        isCancelled = true
        activeTask?.cancel()
    }

    private func downloadOne(_ spec: FileSpec, into dir: URL, onProgress: @escaping (Double) -> Void) async throws {
        let destURL = dir.appendingPathComponent(spec.name)
        let partURL = dir.appendingPathComponent("\(spec.name).part")
        let resumeDataURL = dir.appendingPathComponent("\(spec.name).resume")

        var lastError: Error?
        for attempt in 0..<2 {
            do {
                try await performDownload(
                    url: Self.remoteURL(for: spec.name),
                    partURL: partURL,
                    resumeDataURL: resumeDataURL,
                    onProgress: onProgress
                )
                guard Self.sha256Matches(partURL, spec.sha256) else {
                    try? FileManager.default.removeItem(at: partURL)
                    try? FileManager.default.removeItem(at: resumeDataURL)
                    lastError = DownloadError.checksumMismatch(spec.name)
                    continue
                }
                try? FileManager.default.removeItem(at: destURL)
                try FileManager.default.moveItem(at: partURL, to: destURL)
                try? FileManager.default.removeItem(at: resumeDataURL)
                return
            } catch {
                lastError = error
                if attempt == 0 { AppLog.model.warning("下载 \(spec.name, privacy: .public) 第一次尝试失败，重试: \(String(describing: error), privacy: .public)") }
            }
        }
        throw lastError ?? DownloadError.checksumMismatch(spec.name)
    }

    private func performDownload(
        url: URL,
        partURL: URL,
        resumeDataURL: URL,
        onProgress: @escaping (Double) -> Void
    ) async throws {
        activeProgressHandler = onProgress
        let resumeData = try? Data(contentsOf: resumeDataURL)

        let tmpURL: URL = try await withCheckedThrowingContinuation { continuation in
            self.activeContinuation = continuation
            let task: URLSessionDownloadTask
            if let resumeData {
                task = session.downloadTask(withResumeData: resumeData)
            } else {
                task = session.downloadTask(with: url)
            }
            self.activeTask = task
            task.resume()
        }

        defer {
            activeTask = nil
            activeContinuation = nil
            activeProgressHandler = nil
        }

        // URLSessionDownloadDelegate 把文件放在系统临时目录，回调返回前必须原地移走。
        try? FileManager.default.removeItem(at: partURL)
        try FileManager.default.moveItem(at: tmpURL, to: partURL)
        try? FileManager.default.removeItem(at: resumeDataURL)
    }

    private static func remoteURL(for fileName: String) -> URL {
        var components = URLComponents(string: endpointBase)!
        components.queryItems = [
            URLQueryItem(name: "Revision", value: "master"),
            URLQueryItem(name: "FilePath", value: fileName),
        ]
        return components.url!
    }

    nonisolated static func sha256Matches(_ url: URL, _ expected: String) -> Bool {
        guard let data = try? Data(contentsOf: url, options: .mappedIfSafe) else { return false }
        let digest = SHA256.hash(data: data)
        let hex = digest.map { String(format: "%02x", $0) }.joined()
        return hex == expected
    }
}

extension ModelDownloader: URLSessionDownloadDelegate {
    nonisolated func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64,
        totalBytesExpectedToWrite: Int64
    ) {
        guard totalBytesExpectedToWrite > 0 else { return }
        let fraction = Double(totalBytesWritten) / Double(totalBytesExpectedToWrite)
        Task { @MainActor in
            self.activeProgressHandler?(fraction)
        }
    }

    nonisolated func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didFinishDownloadingTo location: URL
    ) {
        // location 指向的文件在本回调返回后会被系统删除，必须同步搬到一个我们拥有的临时位置，
        // 再回到 MainActor 完成剩余流程（校验/move）。
        let stagedURL = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
        do {
            try FileManager.default.moveItem(at: location, to: stagedURL)
        } catch {
            Task { @MainActor in
                self.activeContinuation?.resume(throwing: error)
                self.activeContinuation = nil
            }
            return
        }
        Task { @MainActor in
            self.activeContinuation?.resume(returning: stagedURL)
            self.activeContinuation = nil
        }
    }

    nonisolated func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        didCompleteWithError error: Error?
    ) {
        guard let error else { return }
        let nsError = error as NSError
        // 被取消时系统会在 userInfo 里塞 resume data；落盘供下次续传。
        if let resumeData = nsError.userInfo[NSURLSessionDownloadTaskResumeData] as? Data,
           let downloadTask = task as? URLSessionDownloadTask,
           let originalURL = downloadTask.originalRequest?.url,
           let fileName = URLComponents(url: originalURL, resolvingAgainstBaseURL: false)?
               .queryItems?.first(where: { $0.name == "FilePath" })?.value {
            let resumeDataURL = ModelLocator.downloadDestination.appendingPathComponent("\(fileName).resume")
            try? resumeData.write(to: resumeDataURL)
        }
        Task { @MainActor in
            self.activeContinuation?.resume(throwing: error)
            self.activeContinuation = nil
        }
    }
}
