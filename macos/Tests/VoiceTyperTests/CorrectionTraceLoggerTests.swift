import XCTest
import Yams
@testable import VoiceTyper

/// TRACE-6：校对效果诊断记录配置、路径解析、JSONL 写入、滚动、权限与符号链接防御测试。
final class CorrectionTraceLoggerTests: XCTestCase {
    private var tempDir: URL!

    override func setUpWithError() throws {
        tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("vt-trace-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        if let tempDir {
            try? FileManager.default.removeItem(at: tempDir)
        }
    }

    func testConfigDefaultsRoundTripAndControlCharFallback() throws {
        let store = ConfigStore(baseDirectoryOverride: tempDir)
        var config = AppConfig()
        XCTAssertFalse(config.llm.traceLog)
        XCTAssertEqual(config.llm.traceLogPath, "")

        config.llm.traceLog = true
        config.llm.traceLogPath = "/tmp/custom_trace.jsonl"
        try store.save(config: config)
        let loaded = try store.loadOrCreate()
        XCTAssertTrue(loaded.llm.traceLog)
        XCTAssertEqual(loaded.llm.traceLogPath, "/tmp/custom_trace.jsonl")

        // 控制字符应回落为空串
        var dirty = AppConfig()
        dirty.llm.traceLogPath = "/tmp/bad\u{0000}path.jsonl"
        XCTAssertEqual(dirty.validated().llm.traceLogPath, "")

        // 旧 YAML 缺少两字段时自动回落默认
        let legacyYAML = """
        llm:
          enabled: true
          model: "gpt-4o-mini"
        """
        let decoded = try YAMLDecoder().decode(AppConfig.self, from: legacyYAML)
        XCTAssertFalse(decoded.llm.traceLog)
        XCTAssertEqual(decoded.llm.traceLogPath, "")
    }

    func testResolvePathRules() throws {
        let fallback = tempDir.appendingPathComponent("default/correction_trace.jsonl")
        XCTAssertEqual(CorrectionTraceLogger.resolvePath("", defaultURL: fallback), fallback)
        XCTAssertEqual(CorrectionTraceLogger.resolvePath("relative/path.jsonl", defaultURL: fallback), fallback)
        XCTAssertEqual(CorrectionTraceLogger.resolvePath("/tmp/bad\nline.jsonl", defaultURL: fallback), fallback)

        let dirPath = tempDir.appendingPathComponent("existing_dir", isDirectory: true)
        try FileManager.default.createDirectory(at: dirPath, withIntermediateDirectories: true)
        XCTAssertEqual(
            CorrectionTraceLogger.resolvePath(dirPath.path, defaultURL: fallback),
            dirPath.appendingPathComponent(AppConstants.defaultTraceLogFileName)
        )
        XCTAssertEqual(
            CorrectionTraceLogger.resolvePath(tempDir.path + "/trailing/", defaultURL: fallback),
            tempDir.appendingPathComponent("trailing").appendingPathComponent(AppConstants.defaultTraceLogFileName)
        )
    }

    private func sampleRecord(sessionId: String = "a3f1") -> CorrectionTraceRecord {
        CorrectionTraceRecord(
            ts: "2026-11-28T20:15:32.418+08:00",
            sessionId: sessionId,
            appVersion: "3.6.1",
            audioSec: 6.4,
            asr: .init(
                languageSetting: "auto",
                decodeMode: "beam",
                vocabHits: 1,
                asrMs: 112,
                decodeMs: 0.9,
                rawText: "密等性",
                replacedText: "密等性"
            ),
            hints: .init(
                marginThreshold: 2.5,
                rawCount: 1,
                activeCount: 1,
                droppedByVocab: 0,
                items: [.init(surface: "密等", alternatives: ["幂等", "密度"], bestMargin: 0.62)]
            ),
            llm: .init(
                model: "gpt-4o-mini",
                temperature: 0.0,
                glossaryCount: 2,
                hintsSent: true,
                result: "corrected",
                fallbackReason: nil,
                thinkingParam: "sent",
                llmMs: 640,
                promptTokens: 512,
                completionTokens: 24,
                cachedTokens: 448,
                outputText: "幂等性。",
                finalText: "幂等性。"
            )
        )
    }

    func testAppendRotationPermissionsClearAndSymlinkRejection() async throws {
        let logURL = tempDir.appendingPathComponent("sub/correction_trace.jsonl")
        let logger = CorrectionTraceLogger(
            configuredPath: logURL.path,
            defaultFileURL: logURL,
            maxBytes: 400,
            backupCount: 2
        )

        await logger.append(sampleRecord(sessionId: "0001"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: logURL.path)
            || FileManager.default.fileExists(atPath: logURL.path + ".1"))

        // 验证 0600 权限与合法单行 JSONL
        let writtenPath = FileManager.default.fileExists(atPath: logURL.path + ".1") ? logURL.path + ".1" : logURL.path
        let attrs = try FileManager.default.attributesOfItem(atPath: writtenPath)
        let perms = (attrs[.posixPermissions] as? NSNumber)?.intValue
        XCTAssertEqual(perms, 0o600)

        let line = try String(contentsOfFile: writtenPath, encoding: .utf8).trimmingCharacters(in: .newlines)
        let obj = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any])
        XCTAssertEqual(obj["schema_version"] as? Int, 1)
        XCTAssertEqual(obj["platform"] as? String, "macos")
        XCTAssertEqual(obj["session_id"] as? String, "0001")

        // 追加多条触发 .1 / .2 滚动
        await logger.append(sampleRecord(sessionId: "0002"))
        await logger.append(sampleRecord(sessionId: "0003"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: logURL.path + ".1"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: logURL.path + ".2"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: logURL.path + ".3"))

        // clear() 删除主文件与全部备份
        await logger.clear()
        XCTAssertFalse(FileManager.default.fileExists(atPath: logURL.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: logURL.path + ".1"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: logURL.path + ".2"))

        // 符号链接拒绝写入
        let realTarget = tempDir.appendingPathComponent("real.txt")
        try "untouched".write(to: realTarget, atomically: true, encoding: .utf8)
        let symlinkURL = tempDir.appendingPathComponent("link.jsonl")
        try FileManager.default.createSymbolicLink(at: symlinkURL, withDestinationURL: realTarget)
        await logger.updatePath(symlinkURL.path)
        await logger.append(sampleRecord(sessionId: "9999"))
        XCTAssertEqual(try String(contentsOf: realTarget, encoding: .utf8), "untouched")
    }
}
