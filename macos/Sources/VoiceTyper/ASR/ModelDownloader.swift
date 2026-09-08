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

    /// 启用分段并行下载的体积门槛。
    ///
    /// 只有 `model_quant.onnx`（241MB）够得上。另外三个文件加起来不到 400KB，
    /// 为它们多开连接，握手开销比省下的传输时间还多。
    static let segmentedDownloadMinimumBytes: Int64 = 32 * 1024 * 1024
    /// 分段数。4 是"能吃满常见带宽"与"不至于被 CDN 当成异常流量"之间的折中；
    /// 继续加分段对单机下载的边际收益很快趋近于零。
    static let segmentCount = 4
    /// 拼接分段文件时的搬运块大小。固定 1MB，让内存占用与文件大小无关。
    nonisolated private static let fileCopyChunkSize = 1 << 20

    private var session: URLSession!
    /// 分段并行下载专用的会话：**不设 delegate**，与上面那个 delegate 驱动的
    /// 单连接会话彻底隔离。分段路径用 per-task delegate 拿进度，两套机制混在同一个
    /// 会话上只会让 `activeContinuation` / resume data 那些既有状态莫名其妙地被触碰。
    private var segmentSession: URLSession!
    /// 分段下载一旦失败过就整体停用，本次运行余下部分老老实实走单连接。
    /// 分段是优化，单连接是那条经过实测与多轮修复的可靠路径——出问题时应该回落到它，
    /// 而不是反复用同一个可能不被服务端支持的方式撞墙。
    private var segmentedDownloadDisabled = false
    /// 正在进行的分段下载任务，供 `cancel()` 中断。
    private var activeSegmentedTask: Task<Void, Error>?

    private var activeTask: URLSessionDownloadTask?
    private var activeContinuation: CheckedContinuation<URL, Error>?
    private var activeProgressHandler: ((Double) -> Void)?
    private var isCancelled = false
    /// 节流状态：241MB 的文件下载会产生几千次 `didWriteData` 回调，若每次都不加过滤地
    /// 跳到 MainActor 触发 `onProgress`，`AppCoordinator` 侧会跟着做几千次全量 UI 刷新
    /// （`updateStatusUI` + `syncAuxiliaryWindows`）（R3-09）。`nonisolated(unsafe)`：需要在
    /// `didWriteData`（nonisolated 的 delegate 回调）里同步判断是否要跳过本次回调，避免
    /// 为每一次回调都创建一个 `Task { @MainActor }`。只在 `URLSession` 的私有串行 delegate
    /// 队列（写）与 `performDownload` 在任务 resume 之前（重置）访问，二者有明确的先后关系，
    /// 不构成并发读写——与本类里 `activeTask`/`activeContinuation` 的既有约定一致。
    private nonisolated(unsafe) var lastReportedProgress: Double = -1
    private nonisolated(unsafe) var lastProgressReportTime: CFAbsoluteTime = 0

    private let fileSpecs: [FileSpec]
    /// `nonisolated`：需要在 `urlSession(_:task:didCompleteWithError:)`（nonisolated 的
    /// delegate 回调）里落盘 resume data 时使用注入的目录，而不是硬编码
    /// `ModelLocator.downloadDestination`——否则测试注入的临时目录会被绕过，
    /// `.resume` 碎片文件写进用户真实的模型目录（R2-11）。
    private nonisolated let downloadDestination: URL
    private var totalBytes: Int64 { fileSpecs.reduce(0) { $0 + $1.sizeHint } }

    /// - Parameters:
    ///   - sessionConfiguration: 仅供测试注入打桩的 URLProtocol，默认使用真实网络。
    ///   - fileSpecs: 仅供测试注入较小的假文件清单，默认使用真实的 4 个模型文件。
    ///   - downloadDestination: 仅供测试注入临时目录，默认使用 `ModelLocator.downloadDestination`。
    init(
        sessionConfiguration: URLSessionConfiguration = .default,
        fileSpecs: [FileSpec] = ModelDownloader.files,
        downloadDestination: URL = ModelLocator.downloadDestination
    ) {
        self.fileSpecs = fileSpecs
        self.downloadDestination = downloadDestination
        super.init()
        session = URLSession(configuration: sessionConfiguration, delegate: self, delegateQueue: nil)
        segmentSession = URLSession(configuration: sessionConfiguration)
    }

    /// 下载全部文件到 `downloadDestination`；已存在且校验通过的文件跳过。
    /// - Parameter onProgress: 总体进度回调（0…1），在 MainActor 上触发。
    func downloadAll(onProgress: @escaping (Double) -> Void) async throws {
        defer {
            session.finishTasksAndInvalidate()
            segmentSession.finishTasksAndInvalidate()
        }
        isCancelled = false
        let dir = downloadDestination
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)

        var completedBytes: Int64 = 0
        for spec in fileSpecs {
            if isCancelled { throw DownloadError.cancelled }
            let destURL = dir.appendingPathComponent(spec.name)
            if FileManager.default.fileExists(atPath: destURL.path), await Self.sha256Matches(destURL, spec.sha256) {
                completedBytes += spec.sizeHint
                onProgress(Double(completedBytes) / Double(totalBytes))
                continue
            }
            let base = completedBytes
            try await downloadOne(spec, into: dir) { fileProgress in
                let overall = Double(base) + Double(spec.sizeHint) * fileProgress
                onProgress(min(1.0, overall / Double(self.totalBytes)))
            }
            completedBytes += spec.sizeHint
            onProgress(min(1.0, Double(completedBytes) / Double(totalBytes)))
        }
    }

    /// 用 `cancel(byProducingResumeData:)` 而不是普通 `cancel()`：后者产生的
    /// `NSURLErrorCancelled` 不带 resume data，`didCompleteWithError` 里那段落盘逻辑
    /// 只对"系统触发的取消"（网络中断等）生效，用户主动点取消时永远走不到——
    /// 表现为"取消再重来"等价于从零开始，241MB 的模型文件每次取消都要重下（R4-02）。
    func cancel() {
        isCancelled = true
        activeSegmentedTask?.cancel()
        guard let task = activeTask else { return }
        let resumeDataURL: URL? = Self.fileName(for: task).map {
            downloadDestination.appendingPathComponent("\($0).resume")
        }
        task.cancel(byProducingResumeData: { data in
            guard let resumeDataURL else { return }
            if let data {
                // 原子写入，理由同 `didCompleteWithError` 里的同类写入（见其注释）。
                try? data.write(to: resumeDataURL, options: .atomic)
            } else {
                // 系统未能产出可用的续传数据：清掉可能残留的旧 .resume，避免下次
                // 用一份与本次进度无关的陈旧数据去续传（R4-03 的另一面）。
                try? FileManager.default.removeItem(at: resumeDataURL)
            }
        })
    }

    private func downloadOne(_ spec: FileSpec, into dir: URL, onProgress: @escaping (Double) -> Void) async throws {
        let destURL = dir.appendingPathComponent(spec.name)
        let partURL = dir.appendingPathComponent("\(spec.name).part")
        let resumeDataURL = dir.appendingPathComponent("\(spec.name).resume")

        // 大文件先试分段并行。失败不抛出，而是停用分段并落回下面那条经过实测的单连接
        // 路径——分段只是优化，绝不能让一个优化把首次安装唯一的必经之路带崩。
        if !segmentedDownloadDisabled, spec.sizeHint >= Self.segmentedDownloadMinimumBytes {
            if try await attemptSegmentedDownload(spec, into: dir, partURL: partURL, destURL: destURL, onProgress: onProgress) {
                try? FileManager.default.removeItem(at: resumeDataURL)
                return
            }
        }

        var lastError: Error?
        for attempt in 0..<2 {
            if isCancelled { throw DownloadError.cancelled }
            do {
                try await performDownload(
                    url: Self.remoteURL(for: spec.name),
                    partURL: partURL,
                    resumeDataURL: resumeDataURL,
                    // 第二次本地重试强制丢弃 resume 数据、从零开始：陈旧或与本次失败
                    // 相关的 .resume 内容会让"重试"精确重放同一个失败，跨越一次
                    // app 重启也不会自愈（R4-03）。
                    useResumeData: attempt == 0,
                    onProgress: onProgress
                )
                guard await Self.sha256Matches(partURL, spec.sha256) else {
                    try? FileManager.default.removeItem(at: partURL)
                    try? FileManager.default.removeItem(at: resumeDataURL)
                    lastError = DownloadError.checksumMismatch(spec.name)
                    continue
                }
                try Self.install(partURL, to: destURL)
                try? FileManager.default.removeItem(at: resumeDataURL)
                return
            } catch {
                if isCancelled { throw DownloadError.cancelled }
                lastError = error
                if attempt == 0 { AppLog.model.warning("下载 \(spec.name, privacy: .public) 第一次尝试失败，重试: \(String(describing: error), privacy: .public)") }
            }
        }
        throw lastError ?? DownloadError.checksumMismatch(spec.name)
    }

    /// `replaceItemAt` 要求目标已存在；全新安装的首个文件并不存在，因此首次部署必须走
    /// 同目录 move（同样是原子的）。旧文件校验失败时才原子替换。
    private static func install(_ partURL: URL, to destURL: URL) throws {
        if FileManager.default.fileExists(atPath: destURL.path) {
            _ = try FileManager.default.replaceItemAt(destURL, withItemAt: partURL)
        } else {
            try FileManager.default.moveItem(at: partURL, to: destURL)
        }
    }

    private func performDownload(
        url: URL,
        partURL: URL,
        resumeDataURL: URL,
        useResumeData: Bool,
        onProgress: @escaping (Double) -> Void
    ) async throws {
        activeProgressHandler = onProgress
        lastReportedProgress = -1
        lastProgressReportTime = 0
        let resumeData: Data?
        if useResumeData {
            resumeData = try? Data(contentsOf: resumeDataURL)
        } else {
            resumeData = nil
            try? FileManager.default.removeItem(at: resumeDataURL)
        }

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

    // MARK: - 分段并行下载

    /// 试一次分段并行下载。
    /// - Returns: true = 已下载、校验并安装完毕；false = 这条路走不通，调用方应回落单连接。
    /// - Throws: 仅在用户取消时抛出 `.cancelled`；其余失败一律吞掉并返回 false。
    private func attemptSegmentedDownload(
        _ spec: FileSpec,
        into dir: URL,
        partURL: URL,
        destURL: URL,
        onProgress: @escaping (Double) -> Void
    ) async throws -> Bool {
        if isCancelled { throw DownloadError.cancelled }

        guard let total = await probeTotalBytesIfRangeSupported(Self.remoteURL(for: spec.name)) else {
            AppLog.model.info("服务端未确认支持 Range 请求，\(spec.name, privacy: .public) 走单连接下载")
            segmentedDownloadDisabled = true
            return false
        }

        do {
            let task = Task { [weak self] in
                guard let self else { return }
                try await self.runSegments(spec, totalBytes: total, into: dir, partURL: partURL, onProgress: onProgress)
            }
            activeSegmentedTask = task
            defer { activeSegmentedTask = nil }
            try await task.value

            if isCancelled { throw DownloadError.cancelled }
            guard await Self.sha256Matches(partURL, spec.sha256) else {
                AppLog.model.warning("分段下载结果校验失败，回落单连接: \(spec.name, privacy: .public)")
                try? FileManager.default.removeItem(at: partURL)
                Self.removeSegmentFiles(for: spec, in: dir)
                segmentedDownloadDisabled = true
                return false
            }
            try Self.install(partURL, to: destURL)
            return true
        } catch is CancellationError {
            throw DownloadError.cancelled
        } catch {
            if isCancelled { throw DownloadError.cancelled }
            AppLog.model.warning("分段下载失败，回落单连接: \(String(describing: error), privacy: .public)")
            // 分段文件保留：单连接路径用不到它们，但下次启动若再次尝试分段可以续上。
            // 只有校验失败（内容确实是坏的）才删。
            segmentedDownloadDisabled = true
            return false
        }
    }

    /// 用一个 1 字节的 Range 请求同时确认两件事：服务端支持范围请求，以及文件总长度。
    /// 比 HEAD 更可靠——部分 CDN 对 HEAD 的支持并不完整，而 206 + `Content-Range`
    /// 是我们真正依赖的能力，直接测它最实在。
    private func probeTotalBytesIfRangeSupported(_ url: URL) async -> Int64? {
        var request = URLRequest(url: url)
        request.setValue("bytes=0-0", forHTTPHeaderField: "Range")
        guard let (_, response) = try? await segmentSession.data(for: request),
              let http = response as? HTTPURLResponse,
              http.statusCode == 206,
              let contentRange = http.value(forHTTPHeaderField: "Content-Range"),
              let total = Self.totalBytes(fromContentRange: contentRange),
              total > 0
        else {
            return nil
        }
        return total
    }

    /// 解析 `Content-Range: bytes 0-0/241216270` 末尾的总长度。
    /// 总长未知（`*`）时返回 nil——那种情况下没法切分段。
    static func totalBytes(fromContentRange value: String) -> Int64? {
        guard let slashIndex = value.lastIndex(of: "/") else { return nil }
        let tail = value[value.index(after: slashIndex)...].trimmingCharacters(in: .whitespaces)
        guard tail != "*" else { return nil }
        return Int64(tail)
    }

    /// 把 `[0, totalBytes)` 均分成 `segmentCount` 段的闭区间列表。
    /// 余数摊给最后一段，保证覆盖完整且互不重叠。
    static func segmentRanges(totalBytes: Int64, segmentCount: Int) -> [ClosedRange<Int64>] {
        guard totalBytes > 0, segmentCount > 0 else { return [] }
        let count = Int64(segmentCount)
        guard totalBytes > count else { return [0...(totalBytes - 1)] }
        let chunk = totalBytes / count
        return (0..<segmentCount).map { index in
            let start = Int64(index) * chunk
            let end = index == segmentCount - 1 ? totalBytes - 1 : start + chunk - 1
            return start...end
        }
    }

    /// 文件字节数；不存在或读不到属性时返回 0。
    /// 单独抽出来是因为 `attributesOfItem` 返回 `[FileAttributeKey: Any]`，
    /// 内联写成 `try?` + 下标 + `as?` 三连很容易在可选层数上出错。
    static func fileSize(at url: URL) -> Int64 {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attributes[.size] as? NSNumber else {
            return 0
        }
        return size.int64Value
    }

    private static func segmentURL(for spec: FileSpec, index: Int, in dir: URL) -> URL {
        dir.appendingPathComponent("\(spec.name).seg\(index)")
    }

    private static func removeSegmentFiles(for spec: FileSpec, in dir: URL) {
        for index in 0..<segmentCount {
            try? FileManager.default.removeItem(at: segmentURL(for: spec, index: index, in: dir))
        }
    }

    /// 并行下载所有分段，然后按序拼成 `.part`。
    ///
    /// 续传靠的是各分段文件已有的字节数，而不是 `URLSession` 的不透明 resume data——
    /// 后者的畸形数据会直接让 CFNetwork 把进程 abort（见 `didCompleteWithError` 的注释）。
    /// 自己按 Range 续传是完全透明的：磁盘上有多少就从多少继续要。
    private func runSegments(
        _ spec: FileSpec,
        totalBytes: Int64,
        into dir: URL,
        partURL: URL,
        onProgress: @escaping (Double) -> Void
    ) async throws {
        let ranges = Self.segmentRanges(totalBytes: totalBytes, segmentCount: Self.segmentCount)
        guard !ranges.isEmpty else { throw DownloadError.checksumMismatch(spec.name) }

        var alreadyOnDisk: [Int64] = []
        for index in ranges.indices {
            let url = Self.segmentURL(for: spec, index: index, in: dir)
            let size = Self.fileSize(at: url)
            let expected = ranges[index].upperBound - ranges[index].lowerBound + 1
            // 磁盘上的分段比它应有的长度还大：说明是上一次用了不同切分留下的垃圾，丢掉重来。
            if size > expected {
                try? FileManager.default.removeItem(at: url)
                alreadyOnDisk.append(0)
            } else {
                alreadyOnDisk.append(size)
            }
        }

        // 复用既有的进度字段：上报闭包只捕获 self（MainActor 类，Sendable），
        // 不把调用方传进来的非 Sendable 闭包捎带跨越隔离边界。
        activeProgressHandler = onProgress
        defer { activeProgressHandler = nil }
        let progress = SegmentProgress(
            resumedBytes: alreadyOnDisk,
            totalBytes: totalBytes,
            report: { [weak self] fraction in
                Task { @MainActor in self?.activeProgressHandler?(fraction) }
            }
        )
        progress.reportNow()

        let session = segmentSession!
        let remoteURL = Self.remoteURL(for: spec.name)

        try await withThrowingTaskGroup(of: Void.self) { group in
            for index in ranges.indices {
                let range = ranges[index]
                let resumed = alreadyOnDisk[index]
                let expected = range.upperBound - range.lowerBound + 1
                guard resumed < expected else { continue } // 这段上次已经下完了

                let segmentURL = Self.segmentURL(for: spec, index: index, in: dir)
                group.addTask {
                    var request = URLRequest(url: remoteURL)
                    request.setValue("bytes=\(range.lowerBound + resumed)-\(range.upperBound)",
                                     forHTTPHeaderField: "Range")
                    let delegate = SegmentProgressDelegate { received in
                        progress.update(segment: index, received: received)
                    }
                    let (temporaryURL, response) = try await session.download(for: request, delegate: delegate)
                    guard let http = response as? HTTPURLResponse, http.statusCode == 206 else {
                        try? FileManager.default.removeItem(at: temporaryURL)
                        let code = (response as? HTTPURLResponse)?.statusCode ?? -1
                        throw DownloadError.httpStatus(spec.name, code)
                    }
                    try Self.appendFile(at: temporaryURL, to: segmentURL)
                }
            }
            try await group.waitForAll()
        }

        try Self.assembleSegments(spec, ranges: ranges, in: dir, into: partURL, expectedTotal: totalBytes)
        onProgress(1.0)
    }

    /// 把临时文件的内容追加到分段文件末尾。目标不存在时直接 move（零拷贝）。
    nonisolated private static func appendFile(at source: URL, to destination: URL) throws {
        let fileManager = FileManager.default
        guard fileManager.fileExists(atPath: destination.path) else {
            try fileManager.moveItem(at: source, to: destination)
            return
        }
        let reader = try FileHandle(forReadingFrom: source)
        defer { try? reader.close() }
        let writer = try FileHandle(forWritingTo: destination)
        defer { try? writer.close() }
        try writer.seekToEnd()
        // 分块搬运：内存占用与文件大小无关，不会因为一个 60MB 的分段就吃掉 60MB。
        while let chunk = try reader.read(upToCount: fileCopyChunkSize), !chunk.isEmpty {
            try writer.write(contentsOf: chunk)
        }
        try? fileManager.removeItem(at: source)
    }

    private static func assembleSegments(
        _ spec: FileSpec,
        ranges: [ClosedRange<Int64>],
        in dir: URL,
        into partURL: URL,
        expectedTotal: Int64
    ) throws {
        let fileManager = FileManager.default
        try? fileManager.removeItem(at: partURL)
        guard fileManager.createFile(atPath: partURL.path, contents: nil) else {
            throw DownloadError.checksumMismatch(spec.name)
        }
        let writer = try FileHandle(forWritingTo: partURL)
        defer { try? writer.close() }

        for index in ranges.indices {
            let segmentURL = segmentURL(for: spec, index: index, in: dir)
            let expected = ranges[index].upperBound - ranges[index].lowerBound + 1
            guard fileSize(at: segmentURL) == expected else {
                // 拼接前就把"长度对不上"拦下来：否则要等算完 241MB 的 sha256 才发现。
                throw DownloadError.checksumMismatch(spec.name)
            }
            let reader = try FileHandle(forReadingFrom: segmentURL)
            defer { try? reader.close() }
            while let chunk = try reader.read(upToCount: fileCopyChunkSize), !chunk.isEmpty {
                try writer.write(contentsOf: chunk)
            }
        }
        try writer.close()

        guard fileSize(at: partURL) == expectedTotal else {
            throw DownloadError.checksumMismatch(spec.name)
        }
        removeSegmentFiles(for: spec, in: dir)
    }

    private static func remoteURL(for fileName: String) -> URL {
        var components = URLComponents(string: endpointBase)!
        components.queryItems = [
            URLQueryItem(name: "Revision", value: "master"),
            URLQueryItem(name: "FilePath", value: fileName),
        ]
        return components.url!
    }

    /// 在 detached task 里跑：241 MB 文件的 SHA256（虽有硬件加速、约 0.1~0.2s）
    /// 不应占用调用方所在的 actor（F-12）。
    nonisolated static func sha256Matches(_ url: URL, _ expected: String) async -> Bool {
        await Task.detached(priority: .utility) {
            guard let data = try? Data(contentsOf: url, options: .mappedIfSafe) else { return false }
            let digest = SHA256.hash(data: data)
            let hex = digest.map { String(format: "%02x", $0) }.joined()
            return hex == expected
        }.value
    }

    nonisolated private static func fileName(for task: URLSessionTask) -> String? {
        guard let url = task.originalRequest?.url else { return nil }
        return URLComponents(url: url, resolvingAgainstBaseURL: false)?
            .queryItems?.first(where: { $0.name == "FilePath" })?.value
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

        // 节流：变化 ≥0.5% 或距上次 ≥200ms 才转发一次；首尾两次（fraction 归零后的首次
        // 回调、以及下载完成的 fraction==1）不节流，保证 UI 立即看到开始与结束（R3-09）。
        let now = CFAbsoluteTimeGetCurrent()
        let isEdge = fraction >= 1.0 || lastReportedProgress < 0
        guard isEdge
            || fraction - lastReportedProgress >= 0.005
            || now - lastProgressReportTime >= 0.2
        else {
            return
        }
        lastReportedProgress = fraction
        lastProgressReportTime = now

        Task { @MainActor in
            self.activeProgressHandler?(fraction)
        }
    }

    nonisolated func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didFinishDownloadingTo location: URL
    ) {
        // 非 2xx（如 ModelScope 返回 404/HTML）不应把响应体当模型文件落盘（F-12）。
        if let http = downloadTask.response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            let name = Self.fileName(for: downloadTask) ?? "unknown"
            Task { @MainActor in
                self.activeContinuation?.resume(throwing: DownloadError.httpStatus(name, http.statusCode))
                self.activeContinuation = nil
            }
            return
        }

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
            let resumeDataURL = downloadDestination.appendingPathComponent("\(fileName).resume")
            // 原子写入：resume data 是系统私有的不透明格式，`downloadTask(withResumeData:)`
            // 遇到损坏/截断的数据不是抛可捕获的 Swift 错误，而是在 CFNetwork 内部直接
            // 抛出 ObjC 异常导致进程 abort（已实测确认）。App 被杀 / 崩溃 / 断电这类场景下，
            // 非原子写入可能留下半截文件，下次启动读到它就会让下载功能整个带崩 App——
            // 原子写不能防住所有畸形数据，但能消除"写到一半被打断"这个最常见的成因。
            try? resumeData.write(to: resumeDataURL, options: .atomic)
        }
        Task { @MainActor in
            self.activeContinuation?.resume(throwing: error)
            self.activeContinuation = nil
        }
    }
}

/// 单个分段的进度回调。每个分段一个实例，省掉"这次回调属于哪一段"的映射。
private final class SegmentProgressDelegate: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    private let onReceived: @Sendable (Int64) -> Void

    init(onReceived: @escaping @Sendable (Int64) -> Void) {
        self.onReceived = onReceived
    }

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64,
        totalBytesExpectedToWrite: Int64
    ) {
        onReceived(totalBytesWritten)
    }

    /// `download(for:delegate:)` 自己负责搬走临时文件，这里不需要做任何事；
    /// 但协议要求实现它。
    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didFinishDownloadingTo location: URL
    ) {}
}

/// 汇总各分段进度并节流上报。
///
/// 节流阈值与单连接路径一致（变化 ≥0.5% 或距上次 ≥200ms）：241MB 分四段会产生上万次
/// 回调，不加过滤地全部跳到 MainActor，`AppCoordinator` 那边会跟着做上万次全量 UI 刷新。
private final class SegmentProgress: @unchecked Sendable {
    private let lock = NSLock()
    private let resumedBytes: [Int64]
    private var receivedBytes: [Int64]
    private let totalBytes: Int64
    private let report: @Sendable (Double) -> Void
    private var lastReportedFraction: Double = -1
    private var lastReportTime: CFAbsoluteTime = 0

    init(resumedBytes: [Int64], totalBytes: Int64, report: @escaping @Sendable (Double) -> Void) {
        self.resumedBytes = resumedBytes
        self.receivedBytes = Array(repeating: 0, count: resumedBytes.count)
        self.totalBytes = max(totalBytes, 1)
        self.report = report
    }

    /// 立刻上报一次当前进度（用于续传时让界面马上显示已完成的部分，而不是从 0 跳起）。
    func reportNow() {
        let fraction = currentFraction()
        lock.lock()
        lastReportedFraction = fraction
        lastReportTime = CFAbsoluteTimeGetCurrent()
        lock.unlock()
        emit(fraction)
    }

    func update(segment: Int, received: Int64) {
        lock.lock()
        guard segment >= 0, segment < receivedBytes.count else { lock.unlock(); return }
        receivedBytes[segment] = received
        let done = zip(resumedBytes, receivedBytes).reduce(Int64(0)) { $0 + $1.0 + $1.1 }
        let fraction = min(1.0, Double(done) / Double(totalBytes))
        let now = CFAbsoluteTimeGetCurrent()
        let shouldReport = fraction >= 1.0
            || lastReportedFraction < 0
            || fraction - lastReportedFraction >= 0.005
            || now - lastReportTime >= 0.2
        if shouldReport {
            lastReportedFraction = fraction
            lastReportTime = now
        }
        lock.unlock()
        guard shouldReport else { return }
        emit(fraction)
    }

    private func currentFraction() -> Double {
        lock.lock()
        let done = zip(resumedBytes, receivedBytes).reduce(Int64(0)) { $0 + $1.0 + $1.1 }
        lock.unlock()
        return min(1.0, Double(done) / Double(totalBytes))
    }

    /// 上报闭包自己负责跳到 MainActor（见 `runSegments` 里的构造点）。
    private func emit(_ fraction: Double) {
        report(fraction)
    }
}
