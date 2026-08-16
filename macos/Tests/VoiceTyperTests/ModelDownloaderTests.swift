import XCTest
@testable import VoiceTyper

/// 完整的网络下载流程（断点续传、并发下载任务）依赖 URLSessionDownloadTask 的
/// 系统级行为，不适合用 URLProtocol 打桩做确定性单测；这里覆盖可以纯函数化验证的部分：
/// sha256 校验、pin 常量的自洽性。真正的下载路径由 `scripts/fetch_model.sh` 与
/// 手动验收覆盖（见 macos/DESIGN.md §7、§10 P5）。
final class ModelDownloaderTests: XCTestCase {
    func testSha256MatchesKnownFile() throws {
        let tempURL = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let content = Data("hello voicetyper".utf8)
        try content.write(to: tempURL)
        defer { try? FileManager.default.removeItem(at: tempURL) }

        // 用系统 shasum 独立算一遍，避免测试和实现共用同一套哈希代码互相掩盖 bug。
        let expected = try shell("shasum -a 256 '\(tempURL.path)' | awk '{print $1}'")
            .trimmingCharacters(in: .whitespacesAndNewlines)

        XCTAssertTrue(ModelDownloader.sha256Matches(tempURL, expected))
        XCTAssertFalse(ModelDownloader.sha256Matches(tempURL, String(repeating: "0", count: 64)))
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
