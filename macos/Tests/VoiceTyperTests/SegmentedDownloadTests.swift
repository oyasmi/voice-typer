import CryptoKit
import XCTest
@testable import VoiceTyper

/// 分段并行下载里可以确定性验证的部分：分段切分、`Content-Range` 解析、文件大小读取，
/// 以及"服务端不支持 Range 时必须回落单连接"这条安全网。
///
/// 真实网络行为（ModelScope 302 → OSS、206、断点续传）仍然只能靠手工验收，
/// 见 macos/DESIGN.md §11.3 的待验证清单。
@MainActor
final class SegmentedDownloadTests: XCTestCase {
    override func tearDown() {
        DownloadStubURLProtocol.handler = nil
        DownloadStubURLProtocol.responseHandler = nil
        super.tearDown()
    }

    // MARK: - 分段切分

    func testSegmentRangesCoverWholeFileWithoutOverlap() {
        let total: Int64 = 241_216_270
        let ranges = ModelDownloader.segmentRanges(totalBytes: total, segmentCount: 4)

        XCTAssertEqual(ranges.count, 4)
        XCTAssertEqual(ranges.first?.lowerBound, 0)
        XCTAssertEqual(ranges.last?.upperBound, total - 1)
        for (previous, next) in zip(ranges, ranges.dropFirst()) {
            XCTAssertEqual(next.lowerBound, previous.upperBound + 1, "分段之间不能有空隙或重叠")
        }
        let covered = ranges.reduce(Int64(0)) { $0 + ($1.upperBound - $1.lowerBound + 1) }
        XCTAssertEqual(covered, total, "所有分段长度之和必须等于文件总长")
    }

    func testSegmentRangesHandleRemainderAndTinyFiles() {
        // 不能整除时余数摊给最后一段。
        let ranges = ModelDownloader.segmentRanges(totalBytes: 10, segmentCount: 4)
        XCTAssertEqual(ranges.count, 4)
        XCTAssertEqual(ranges.last?.upperBound, 9)
        XCTAssertEqual(ranges.reduce(Int64(0)) { $0 + ($1.upperBound - $1.lowerBound + 1) }, 10)

        // 比分段数还小的文件退化成单段，不能切出空区间。
        XCTAssertEqual(ModelDownloader.segmentRanges(totalBytes: 3, segmentCount: 4), [0...2])
        XCTAssertTrue(ModelDownloader.segmentRanges(totalBytes: 0, segmentCount: 4).isEmpty)
    }

    // MARK: - Content-Range 解析

    func testContentRangeParsing() {
        XCTAssertEqual(ModelDownloader.totalBytes(fromContentRange: "bytes 0-0/241216270"), 241_216_270)
        XCTAssertEqual(ModelDownloader.totalBytes(fromContentRange: "bytes 100-199/1000"), 1000)
        // 总长未知时不能猜，必须返回 nil 以触发回落。
        XCTAssertNil(ModelDownloader.totalBytes(fromContentRange: "bytes 0-0/*"))
        XCTAssertNil(ModelDownloader.totalBytes(fromContentRange: "bytes 0-0"))
        XCTAssertNil(ModelDownloader.totalBytes(fromContentRange: ""))
    }

    // MARK: - 文件大小

    // MARK: - 回落

    /// 服务端不返回 206 时，分段路径必须安静地让位给单连接路径，而不是让下载整体失败。
    /// 这是整块优化的安全网：首次安装是唯一的必经之路，优化绝不能把它带崩。
    func testFallsBackToSingleConnectionWhenRangeIsNotSupported() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let content = Data("valid-content".utf8)
        // sizeHint 报得足够大以越过分段门槛，但服务端一律回 200（不支持 Range）。
        let spec = ModelDownloader.FileSpec(
            name: "config.yaml",
            sha256: "b60048f0ad4e6c4a795cb5596f9f5246e360ebba8b7a0246a9b45c5f44ff5bec",
            sizeHint: ModelDownloader.segmentedDownloadMinimumBytes + 1
        )

        let rangeRequests = AttemptCounter()
        DownloadStubURLProtocol.responseHandler = { request in
            if request.value(forHTTPHeaderField: "Range") != nil {
                rangeRequests.increment()
            }
            return (200, content, [:])
        }

        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [DownloadStubURLProtocol.self]
        let downloader = ModelDownloader(
            sessionConfiguration: configuration,
            fileSpecs: [spec],
            downloadDestination: dir
        )

        try await downloader.downloadAll { _ in }

        XCTAssertGreaterThan(rangeRequests.count, 0, "应当先探测一次 Range 支持")
        let destination = dir.appendingPathComponent(spec.name)
        XCTAssertEqual(try Data(contentsOf: destination), content, "回落到单连接后仍要正常装好文件")
        // 探测失败后不应留下任何分段碎片。
        for index in 0..<ModelDownloader.segmentCount {
            let segment = dir.appendingPathComponent("\(spec.name).seg\(index)")
            XCTAssertFalse(FileManager.default.fileExists(atPath: segment.path))
        }
    }

    /// 小文件根本不该走分段：三个小文件加起来不到 400KB，多开连接得不偿失。
    func testSmallFilesNeverProbeForRangeSupport() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let content = Data("valid-content".utf8)
        let spec = ModelDownloader.FileSpec(
            name: "config.yaml",
            sha256: "b60048f0ad4e6c4a795cb5596f9f5246e360ebba8b7a0246a9b45c5f44ff5bec",
            sizeHint: Int64(content.count)
        )

        let rangeRequests = AttemptCounter()
        DownloadStubURLProtocol.responseHandler = { request in
            if request.value(forHTTPHeaderField: "Range") != nil {
                rangeRequests.increment()
            }
            return (200, content, [:])
        }

        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [DownloadStubURLProtocol.self]
        let downloader = ModelDownloader(
            sessionConfiguration: configuration,
            fileSpecs: [spec],
            downloadDestination: dir
        )

        try await downloader.downloadAll { _ in }
        XCTAssertEqual(rangeRequests.count, 0, "小文件不应该发起 Range 探测")
    }

    // MARK: - 分块续传

    /// 分段里的某次请求断了：只重试这一块，不放弃分段、也不从头再来。
    func testFailedSegmentRequestIsRetriedWithoutFallingBack() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let content = Data((0..<100).map { UInt8($0) })
        let digest = SHA256.hash(data: content).map { String(format: "%02x", $0) }.joined()
        let spec = ModelDownloader.FileSpec(
            name: "model_quant.onnx", sha256: digest, sizeHint: ModelDownloader.segmentedDownloadMinimumBytes + 1
        )

        let requestedRanges = RangeLog()
        DownloadStubURLProtocol.responseHandler = { request in
            guard let header = request.value(forHTTPHeaderField: "Range"),
                  let bounds = Self.parseRange(header) else {
                return (200, content, [:]) // 不该走到：分段成功时不会回落单连接
            }
            let isFirstAttempt = requestedRanges.record(header)
            if header != "bytes=0-0", bounds.lowerBound == 50, isFirstAttempt {
                return nil // 第三段的第一次请求断网
            }
            let body = content.subdata(in: Int(bounds.lowerBound)..<Int(bounds.upperBound + 1))
            return (206, body, ["Content-Range": "bytes \(bounds.lowerBound)-\(bounds.upperBound)/\(content.count)"])
        }

        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [DownloadStubURLProtocol.self]
        let downloader = ModelDownloader(sessionConfiguration: configuration, fileSpecs: [spec], downloadDestination: dir)

        try await downloader.downloadAll { _ in }

        XCTAssertEqual(try Data(contentsOf: dir.appendingPathComponent(spec.name)), content)
        XCTAssertEqual(requestedRanges.count(of: "bytes=50-74"), 2, "断掉的那一块重试一次")
        XCTAssertEqual(requestedRanges.count(of: "bytes=0-24"), 1, "其他分段不受影响")
        XCTAssertFalse(
            FileManager.default.fileExists(atPath: dir.appendingPathComponent("\(spec.name).segmeta").path),
            "分段拼装完成后 .segmeta 也应一并清理"
        )
    }

    /// S-3 回归：分段下载中途失败（部分 .seg* 已落盘）后回落到单连接并成功完装时，
    /// 必须把残留的 .seg* 与 .segmeta 清理干净，不能在磁盘上永久泄漏百兆碎片。
    func testFallbackToSingleConnectionCleansUpOrphanedSegmentFiles() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let content = Data((0..<100).map { UInt8($0) })
        let digest = SHA256.hash(data: content).map { String(format: "%02x", $0) }.joined()
        let spec = ModelDownloader.FileSpec(
            name: "model_quant.onnx", sha256: digest, sizeHint: ModelDownloader.segmentedDownloadMinimumBytes + 1
        )

        DownloadStubURLProtocol.responseHandler = { request in
            guard let header = request.value(forHTTPHeaderField: "Range"),
                  let bounds = Self.parseRange(header) else {
                // 单连接回落请求：返回完整内容
                return (200, content, ["ETag": "\"v1\""])
            }
            // Range 探测与前两段正常返回 206，第三段返回 503 迫使分段任务失败回落（DownloadError 不走慢重试）。
            if header != "bytes=0-0", bounds.lowerBound == 50 {
                return (503, Data(), [:])
            }
            let body = content.subdata(in: Int(bounds.lowerBound)..<Int(bounds.upperBound + 1))
            return (206, body, [
                "Content-Range": "bytes \(bounds.lowerBound)-\(bounds.upperBound)/\(content.count)",
                "ETag": "\"v1\"",
            ])
        }

        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [DownloadStubURLProtocol.self]
        let downloader = ModelDownloader(sessionConfiguration: configuration, fileSpecs: [spec], downloadDestination: dir)

        try await downloader.downloadAll { _ in }

        XCTAssertEqual(try Data(contentsOf: dir.appendingPathComponent(spec.name)), content)
        for index in 0..<ModelDownloader.segmentCount {
            let segURL = dir.appendingPathComponent("\(spec.name).seg\(index)")
            XCTAssertFalse(FileManager.default.fileExists(atPath: segURL.path), "回落完装后不应残留 .seg\(index)")
        }
        XCTAssertFalse(
            FileManager.default.fileExists(atPath: dir.appendingPathComponent("\(spec.name).segmeta").path),
            "回落完装后不应残留 .segmeta"
        )
    }

    /// S-3 回归：服务端 ETag 变化时必须作废旧分段重下，避免新老字节混拼；ETag 一致时保留续传能力。
    func testSegmentResumeValidatesETagMetadata() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let oldContent = Data(repeating: 0xAA, count: 100)
        let newContent = Data((0..<100).map { UInt8($0) })
        let newDigest = SHA256.hash(data: newContent).map { String(format: "%02x", $0) }.joined()
        let spec = ModelDownloader.FileSpec(
            name: "model_quant.onnx", sha256: newDigest, sizeHint: ModelDownloader.segmentedDownloadMinimumBytes + 1
        )

        // 第一轮：分段在第三段失败，且单连接也因 503 失败，使旧版本（ETag "v1"）的 .seg0/.seg1 与 .segmeta 留在盘上。
        DownloadStubURLProtocol.responseHandler = { request in
            guard let header = request.value(forHTTPHeaderField: "Range"),
                  let bounds = Self.parseRange(header) else {
                return (503, Data(), [:])
            }
            if header != "bytes=0-0", bounds.lowerBound >= 50 {
                return (503, Data(), [:])
            }
            let body = oldContent.subdata(in: Int(bounds.lowerBound)..<Int(bounds.upperBound + 1))
            return (206, body, [
                "Content-Range": "bytes \(bounds.lowerBound)-\(bounds.upperBound)/\(oldContent.count)",
                "ETag": "\"v1\"",
            ])
        }

        let config1 = URLSessionConfiguration.ephemeral
        config1.protocolClasses = [DownloadStubURLProtocol.self]
        let firstAttempt = ModelDownloader(sessionConfiguration: config1, fileSpecs: [spec], downloadDestination: dir)
        do {
            try await firstAttempt.downloadAll { _ in }
            XCTFail("首轮预期失败以留下部分分段")
        } catch {}

        XCTAssertGreaterThan(ModelDownloader.fileSize(at: dir.appendingPathComponent("\(spec.name).seg0")), 0)

        // 第二轮：远端文件更新为 newContent（总长度仍是 100，但 ETag 变为 "v2"），应当丢弃旧分段从头拉取并校验通过。
        let secondRunRanges = RangeLog()
        DownloadStubURLProtocol.responseHandler = { request in
            guard let header = request.value(forHTTPHeaderField: "Range"),
                  let bounds = Self.parseRange(header) else {
                return (200, newContent, ["ETag": "\"v2\""])
            }
            _ = secondRunRanges.record(header)
            let body = newContent.subdata(in: Int(bounds.lowerBound)..<Int(bounds.upperBound + 1))
            return (206, body, [
                "Content-Range": "bytes \(bounds.lowerBound)-\(bounds.upperBound)/\(newContent.count)",
                "ETag": "\"v2\"",
            ])
        }

        let config2 = URLSessionConfiguration.ephemeral
        config2.protocolClasses = [DownloadStubURLProtocol.self]
        let secondAttempt = ModelDownloader(sessionConfiguration: config2, fileSpecs: [spec], downloadDestination: dir)
        try await secondAttempt.downloadAll { _ in }

        XCTAssertEqual(try Data(contentsOf: dir.appendingPathComponent(spec.name)), newContent)
        XCTAssertEqual(secondRunRanges.count(of: "bytes=0-24"), 1, "ETag 变化后第 0 段必须作废重下而非跳过")
    }

    private nonisolated static func parseRange(_ header: String) -> ClosedRange<Int64>? {
        let parts = header.replacingOccurrences(of: "bytes=", with: "").split(separator: "-")
        guard parts.count == 2, let lower = Int64(parts[0]), let upper = Int64(parts[1]), lower <= upper else {
            return nil
        }
        return lower...upper
    }

    // MARK: - 常量自洽

}

/// 记录每个 Range 请求头出现的次数（URLProtocol 回调在后台线程）。
private final class RangeLog: @unchecked Sendable {
    private let lock = NSLock()
    private var counts: [String: Int] = [:]

    /// 记一次，返回这是不是该区间的第一次请求。
    func record(_ header: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        counts[header, default: 0] += 1
        return counts[header] == 1
    }

    func count(of header: String) -> Int {
        lock.lock(); defer { lock.unlock() }
        return counts[header] ?? 0
    }
}
