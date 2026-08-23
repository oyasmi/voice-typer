import XCTest
@testable import VoiceTyper

/// 完整的网络下载流程（断点续传、并发下载任务）依赖 URLSessionDownloadTask 的
/// 系统级行为，不适合用 URLProtocol 打桩做确定性单测；这里覆盖可以纯函数化验证的部分：
/// sha256 校验、pin 常量的自洽性，以及用 URLProtocol 打桩可以确定性覆盖的失败路径：
/// HTTP 非 2xx、取消后不再重试、sha256 不匹配（F-11/F-12）。真正的下载路径由
/// `scripts/fetch_model.sh` 与手动验收覆盖（见 macos/DESIGN.md §7、§10 P5）。
@MainActor
final class ModelDownloaderTests: XCTestCase {
    func testSha256MatchesKnownFile() async throws {
        let tempURL = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let content = Data("hello voicetyper".utf8)
        try content.write(to: tempURL)
        defer { try? FileManager.default.removeItem(at: tempURL) }

        // 用系统 shasum 独立算一遍，避免测试和实现共用同一套哈希代码互相掩盖 bug。
        let expected = try shell("shasum -a 256 '\(tempURL.path)' | awk '{print $1}'")
            .trimmingCharacters(in: .whitespacesAndNewlines)

        let matches = await ModelDownloader.sha256Matches(tempURL, expected)
        XCTAssertTrue(matches)
        let mismatches = await ModelDownloader.sha256Matches(tempURL, String(repeating: "0", count: 64))
        XCTAssertFalse(mismatches)
    }

    func testFileSpecsHaveDistinctNamesAndPositiveSizes() {
        let names = Set(ModelDownloader.files.map(\.name))
        XCTAssertEqual(names.count, ModelDownloader.files.count, "文件名不应重复")
        for spec in ModelDownloader.files {
            XCTAssertEqual(spec.sha256.count, 64, "\(spec.name) 的 sha256 长度应为 64")
            XCTAssertGreaterThan(spec.sizeHint, 0)
        }
        XCTAssertEqual(ModelDownloader.totalBytes, ModelDownloader.files.reduce(0) { $0 + $1.sizeHint })
    }

    func testSmallFilesDownloadBeforeLargeModel() {
        // 小文件先行：网络/端点问题能在花掉 230MB 流量之前就暴露（见 DESIGN.md §4.4）。
        let onnxIndex = ModelDownloader.files.firstIndex { $0.name == "model_quant.onnx" }
        XCTAssertEqual(onnxIndex, ModelDownloader.files.count - 1, "model_quant.onnx 应排在下载顺序最后")
    }

    func testAutomaticDownloadPolicyStartsOnceWhenModelIsMissing() {
        var policy = AutomaticModelDownloadPolicy()

        XCTAssertFalse(policy.shouldStart(for: .unloaded, isDownloading: false))
        XCTAssertFalse(policy.hasAttempted)
        XCTAssertFalse(policy.shouldStart(for: .modelMissing, isDownloading: true))
        XCTAssertFalse(policy.hasAttempted)
        XCTAssertTrue(policy.shouldStart(for: .modelMissing, isDownloading: false))
        XCTAssertTrue(policy.hasAttempted)
        XCTAssertFalse(policy.shouldStart(for: .modelMissing, isDownloading: false))
    }

    // MARK: - 打桩下载路径

    private func tempDestination() throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    private func stubbedSessionConfiguration() -> URLSessionConfiguration {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [DownloadStubURLProtocol.self]
        return config
    }

    override func tearDown() {
        DownloadStubURLProtocol.handler = nil
        super.tearDown()
    }

    func testHTTPErrorStatusIsSurfacedAndNotWrittenToDisk() async throws {
        let dir = try tempDestination()
        defer { try? FileManager.default.removeItem(at: dir) }
        let spec = ModelDownloader.FileSpec(name: "config.yaml", sha256: String(repeating: "0", count: 64), sizeHint: 100)

        DownloadStubURLProtocol.handler = { _ in (404, Data("not found".utf8)) }
        let downloader = ModelDownloader(
            sessionConfiguration: stubbedSessionConfiguration(),
            fileSpecs: [spec],
            downloadDestination: dir
        )

        do {
            try await downloader.downloadAll { _ in }
            XCTFail("非 2xx 响应应抛出错误")
        } catch let error as ModelDownloader.DownloadError {
            if case .httpStatus(let name, let code) = error {
                XCTAssertEqual(name, "config.yaml")
                XCTAssertEqual(code, 404)
            } else {
                XCTFail("期望 httpStatus 错误，实际是 \(error)")
            }
        }
        XCTAssertFalse(
            FileManager.default.fileExists(atPath: dir.appendingPathComponent(spec.name).path),
            "非 2xx 响应体不应被当成模型文件落盘"
        )
    }

    func testFirstDownloadMovesVerifiedFileIntoEmptyDestination() async throws {
        let dir = try tempDestination()
        defer { try? FileManager.default.removeItem(at: dir) }
        let content = Data("valid-content".utf8)
        let spec = ModelDownloader.FileSpec(
            name: "config.yaml",
            sha256: "b60048f0ad4e6c4a795cb5596f9f5246e360ebba8b7a0246a9b45c5f44ff5bec",
            sizeHint: Int64(content.count)
        )

        DownloadStubURLProtocol.handler = { _ in (200, content) }
        let downloader = ModelDownloader(
            sessionConfiguration: stubbedSessionConfiguration(),
            fileSpecs: [spec],
            downloadDestination: dir
        )

        try await downloader.downloadAll { _ in }

        let destination = dir.appendingPathComponent(spec.name)
        XCTAssertEqual(try Data(contentsOf: destination), content)
        XCTAssertFalse(FileManager.default.fileExists(atPath: destination.appendingPathExtension("part").path))
    }

    func testChecksumMismatchIsRetriedThenFails() async throws {
        let dir = try tempDestination()
        defer { try? FileManager.default.removeItem(at: dir) }
        let spec = ModelDownloader.FileSpec(name: "am.mvn", sha256: String(repeating: "f", count: 64), sizeHint: 10)

        let attemptCounter = AttemptCounter()
        DownloadStubURLProtocol.handler = { _ in
            attemptCounter.increment()
            return (200, Data("wrong-content".utf8))
        }
        let downloader = ModelDownloader(
            sessionConfiguration: stubbedSessionConfiguration(),
            fileSpecs: [spec],
            downloadDestination: dir
        )

        do {
            try await downloader.downloadAll { _ in }
            XCTFail("sha256 不匹配应抛出错误")
        } catch let error as ModelDownloader.DownloadError {
            guard case .checksumMismatch(let name) = error else {
                return XCTFail("期望 checksumMismatch 错误，实际是 \(error)")
            }
            XCTAssertEqual(name, "am.mvn")
        }
        XCTAssertEqual(attemptCounter.count, 2, "校验失败应重试一次后放弃")
    }

    func testCancelDuringRetryStopsImmediately() async throws {
        let dir = try tempDestination()
        defer { try? FileManager.default.removeItem(at: dir) }
        let spec = ModelDownloader.FileSpec(name: "tokens.json", sha256: String(repeating: "f", count: 64), sizeHint: 10)

        let downloader = ModelDownloader(
            sessionConfiguration: stubbedSessionConfiguration(),
            fileSpecs: [spec],
            downloadDestination: dir
        )
        let attemptCounter = AttemptCounter()
        DownloadStubURLProtocol.handler = { _ in
            attemptCounter.increment()
            // 阻塞到 cancel() 真正在 MainActor 上执行完成，保证 isCancelled 在这次
            // 响应返回、下一轮重试检查之前就已置位——避免"轮询后再取消"引入的时间窗竞态。
            let semaphore = DispatchSemaphore(value: 0)
            Task { @MainActor in
                downloader.cancel()
                semaphore.signal()
            }
            semaphore.wait()
            return (200, Data("wrong-content".utf8)) // 校验必然失败，触发重试循环
        }

        do {
            try await downloader.downloadAll { _ in }
            XCTFail("取消后应抛出错误")
        } catch let error as ModelDownloader.DownloadError {
            guard case .cancelled = error else {
                return XCTFail("期望 cancelled 错误，实际是 \(error)")
            }
        }
        XCTAssertEqual(attemptCounter.count, 1, "取消后不应发起第二次尝试")
    }

    // 注：cancel()/performDownload 围绕真实 resume data 的行为（R4-02/R4-03，见
    // ModelDownloader.swift 的对应注释）没有配套单测——实测过程中确认，只要预先在磁盘上
    // 放一份非本次会话产出的 `.resume` 文件，`downloadOne` 第一次尝试就会把它喂给
    // `downloadTask(withResumeData:)`，而系统对不是自己刚刚产出的续传数据长期缺乏
    // 健壮的容错：无论是完全无关的字节，还是结构正确但语义不对的 plist，都会在
    // CFNetwork 内部直接让进程 abort，不是可捕获的 Swift 错误。这正是本文件顶部
    // doc comment 里"断点续传...不适合用 URLProtocol 打桩做确定性单测"这句话想说的事，
    // 用合成数据去验证这条路径反而会让测试本身变成一个偶发崩溃源。这两处修复改为靠
    // 代码审查 + 手工验证覆盖（见 macos/DESIGN.md §7 的模型下载验收步骤）。

    private func shell(_ command: String) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/bash")
        process.arguments = ["-c", command]
        let pipe = Pipe()
        process.standardOutput = pipe
        try process.run()
        process.waitUntilExit()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        return String(data: data, encoding: .utf8) ?? ""
    }
}

/// 线程安全计数器：DownloadStubURLProtocol 的 handler 在 URLSession 内部线程上运行。
final class AttemptCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0

    func increment() {
        lock.lock(); value += 1; lock.unlock()
    }

    var count: Int {
        lock.lock(); defer { lock.unlock() }
        return value
    }
}

/// 拦截下载任务请求，交给测试用例设置的 handler 决定响应状态码与响应体。
final class DownloadStubURLProtocol: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var handler: ((URLRequest) -> (Int, Data))?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let handler = Self.handler else {
            client?.urlProtocol(self, didFailWithError: URLError(.unknown))
            return
        }
        let (status, body) = handler(request)
        let response = HTTPURLResponse(
            url: request.url!, statusCode: status, httpVersion: "HTTP/1.1", headerFields: nil
        )!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}
