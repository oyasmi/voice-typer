import Darwin
import Foundation

/// 校对会话在内存中暂存的轻量诊断草稿（TRACE-4）；仅在 `traceEnabled == true` 时构造。
struct CorrectionTraceDraft: Equatable, Sendable {
    let asrRawText: String
    let asrReplacedText: String
    let marginThreshold: Double
    let rawHintsCount: Int
    let droppedByVocab: Int
    let activeHints: [AcousticHint]
    let glossaryCount: Int
    var llmOutputText: String?
    var tokenUsage: LLMCorrector.TokenUsage?
    var thinkingParameter: LLMCorrector.ThinkingParameterUsage?
}

/// 校对效果诊断记录 Schema v1（TRACE-3），以单行紧凑 JSON 写入独立本机 `.jsonl` 文件。
/// **白名单红线**：严禁包含 API Key、base_url、完整词汇表、音频样本、前台窗口标题或剪贴板内容。
struct CorrectionTraceRecord: Encodable, Equatable, Sendable {
    struct ASRSection: Encodable, Equatable, Sendable {
        let languageSetting: String
        let decodeMode: String
        let vocabHits: Int
        let asrMs: Int
        let decodeMs: Double
        let rawText: String
        let replacedText: String

        enum CodingKeys: String, CodingKey {
            case languageSetting = "language_setting"
            case decodeMode = "decode_mode"
            case vocabHits = "vocab_hits"
            case asrMs = "asr_ms"
            case decodeMs = "decode_ms"
            case rawText = "raw_text"
            case replacedText = "replaced_text"
        }
    }

    struct HintItem: Encodable, Equatable, Sendable {
        let surface: String
        let alternatives: [String]
        let bestMargin: Double

        enum CodingKeys: String, CodingKey {
            case surface
            case alternatives
            case bestMargin = "best_margin"
        }
    }

    struct HintsSection: Encodable, Equatable, Sendable {
        let marginThreshold: Double
        let rawCount: Int
        let activeCount: Int
        let droppedByVocab: Int
        let items: [HintItem]

        enum CodingKeys: String, CodingKey {
            case marginThreshold = "margin_threshold"
            case rawCount = "raw_count"
            case activeCount = "active_count"
            case droppedByVocab = "dropped_by_vocab"
            case items
        }
    }

    struct LLMSection: Encodable, Equatable, Sendable {
        let model: String
        let temperature: Double
        let glossaryCount: Int
        let hintsSent: Bool
        let result: String
        let fallbackReason: String?
        let thinkingParam: String?
        let llmMs: Int
        let promptTokens: Int?
        let completionTokens: Int?
        let cachedTokens: Int?
        let outputText: String?
        let finalText: String

        enum CodingKeys: String, CodingKey {
            case model
            case temperature
            case glossaryCount = "glossary_count"
            case hintsSent = "hints_sent"
            case result
            case fallbackReason = "fallback_reason"
            case thinkingParam = "thinking_param"
            case llmMs = "llm_ms"
            case promptTokens = "prompt_tokens"
            case completionTokens = "completion_tokens"
            case cachedTokens = "cached_tokens"
            case outputText = "output_text"
            case finalText = "final_text"
        }

        func encode(to encoder: Encoder) throws {
            var c = encoder.container(keyedBy: CodingKeys.self)
            try c.encode(model, forKey: .model)
            try c.encode(temperature, forKey: .temperature)
            try c.encode(glossaryCount, forKey: .glossaryCount)
            try c.encode(hintsSent, forKey: .hintsSent)
            try c.encode(result, forKey: .result)
            try c.encode(fallbackReason, forKey: .fallbackReason)
            try c.encode(thinkingParam, forKey: .thinkingParam)
            try c.encode(llmMs, forKey: .llmMs)
            try c.encode(promptTokens, forKey: .promptTokens)
            try c.encode(completionTokens, forKey: .completionTokens)
            try c.encode(cachedTokens, forKey: .cachedTokens)
            try c.encode(outputText, forKey: .outputText)
            try c.encode(finalText, forKey: .finalText)
        }
    }

    let schemaVersion: Int = 1
    let ts: String
    let sessionId: String
    let appVersion: String
    let platform: String = "macos"
    let audioSec: Double
    let asr: ASRSection
    let hints: HintsSection
    let llm: LLMSection

    enum CodingKeys: String, CodingKey {
        case schemaVersion = "schema_version"
        case ts
        case sessionId = "session_id"
        case appVersion = "app_version"
        case platform
        case audioSec = "audio_sec"
        case asr
        case hints
        case llm
    }
}

/// 独立本机校对诊断记录写入器（TRACE-2）。
///
/// - 默认写入 `/tmp/voice_typer/correction_trace.jsonl`（目录 `0700`、文件 `0600`），
///   打开前校验非符号链接与属主 `getuid()`；
/// - 单文件上限 5 MB，最多保留 2 个滚动备份（`.1` / `.2`），总上限 15 MB；
/// - 串行异步写入，IO 失败冷却 60 秒且仅向主日志记脱敏错误类型，绝不阻断上屏或泄露文本。
actor CorrectionTraceLogger {
    static let defaultMaxBytes: UInt64 = 5 * 1024 * 1024
    static let defaultBackupCount = 2
    static let defaultReopenCooldown: TimeInterval = 60

    private let maxBytes: UInt64
    private let backupCount: Int
    private let reopenCooldown: TimeInterval
    private let defaultFileURL: URL
    private let fileManager: FileManager

    private var currentFileURL: URL
    private var retryAfter: Date?

    init(
        configuredPath: String = "",
        defaultFileURL: URL = URL(fileURLWithPath: AppConstants.defaultTraceLogDirectory)
            .appendingPathComponent(AppConstants.defaultTraceLogFileName),
        maxBytes: UInt64 = CorrectionTraceLogger.defaultMaxBytes,
        backupCount: Int = CorrectionTraceLogger.defaultBackupCount,
        reopenCooldown: TimeInterval = CorrectionTraceLogger.defaultReopenCooldown,
        fileManager: FileManager = .default
    ) {
        self.defaultFileURL = defaultFileURL
        self.maxBytes = maxBytes
        self.backupCount = backupCount
        self.reopenCooldown = reopenCooldown
        self.fileManager = fileManager
        self.currentFileURL = Self.resolvePath(configuredPath, defaultURL: defaultFileURL, fileManager: fileManager)
    }

    /// 路径解析纯函数（TRACE-1 §3.1）：
    /// - 空串或含控制字符或相对路径 → 回落 `defaultURL`；
    /// - 支持 `~` 展开；
    /// - 以 `/` 结尾或已存在且为目录 → 自动拼接 `correction_trace.jsonl`。
    nonisolated static func resolvePath(
        _ configuredPath: String,
        defaultURL: URL = URL(fileURLWithPath: AppConstants.defaultTraceLogDirectory)
            .appendingPathComponent(AppConstants.defaultTraceLogFileName),
        fileManager: FileManager = .default
    ) -> URL {
        let trimmed = configuredPath.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return defaultURL }
        if trimmed.unicodeScalars.contains(where: { $0.value < 0x20 || $0.value == 0x7F }) {
            AppLog.llm.warning("校对诊断日志路径含控制字符，已回落默认路径")
            return defaultURL
        }
        let expanded = (trimmed as NSString).expandingTildeInPath
        guard expanded.hasPrefix("/") else {
            AppLog.llm.warning("校对诊断日志路径为相对路径，已回落默认路径")
            return defaultURL
        }
        let endsWithSlash = trimmed.hasSuffix("/")
        var isDir: ObjCBool = false
        let exists = fileManager.fileExists(atPath: expanded, isDirectory: &isDir)
        let baseURL = URL(fileURLWithPath: expanded)
        if endsWithSlash || (exists && isDir.boolValue) {
            return baseURL.appendingPathComponent(AppConstants.defaultTraceLogFileName)
        }
        return baseURL
    }

    /// 当前生效的日志文件路径。
    var targetURL: URL { currentFileURL }

    /// 更新配置的目标路径（不自动删旧路径的历史文件，TRACE-4）。
    func updatePath(_ configuredPath: String) {
        let resolved = Self.resolvePath(configuredPath, defaultURL: defaultFileURL, fileManager: fileManager)
        if resolved != currentFileURL {
            currentFileURL = resolved
            retryAfter = nil
        }
    }

    /// 异步追加一条诊断记录。任何异常仅向主日志记脱敏错误分类，绝不抛错、绝不打印记录内容。
    func append(_ record: CorrectionTraceRecord) {
        if let retryAfter, Date() < retryAfter { return }
        do {
            let lineData = try encodeLine(record)
            try prepareAndWrite(lineData)
            retryAfter = nil
        } catch {
            retryAfter = Date().addingTimeInterval(reopenCooldown)
            AppLog.llm.warning("写入校对诊断日志失败: \(String(describing: type(of: error)), privacy: .public)")
        }
    }

    /// 删除当前生效路径的主文件及全部 `.1` / `.2` 滚动备份（TRACE-2）。
    func clear() {
        retryAfter = nil
        let targets = [currentFileURL] + (1...max(1, backupCount)).map { backupURL(index: $0) }
        for url in targets where fileManager.fileExists(atPath: url.path) {
            do {
                try fileManager.removeItem(at: url)
            } catch {
                AppLog.llm.warning("清空校对诊断日志失败: \(String(describing: type(of: error)), privacy: .public)")
            }
        }
    }

    /// 供「在访达中显示」调用：确保父目录安全存在（0700）并返回目标文件 URL。
    func prepareParentDirectoryForReveal() -> URL {
        let dir = currentFileURL.deletingLastPathComponent()
        try? ensureSecureDirectory(dir)
        return currentFileURL
    }

    // MARK: - 内部实现

    private enum SecurityError: Error {
        case symlinkRejected
        case ownerMismatch
        case openFailed(Int32)
        case writeFailed(Int32)
    }

    private func encodeLine(_ record: CorrectionTraceRecord) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.withoutEscapingSlashes]
        var data = try encoder.encode(record)
        data.append(0x0A) // '\n'
        return data
    }

    private func prepareAndWrite(_ lineData: Data) throws {
        let dirURL = currentFileURL.deletingLastPathComponent()
        try ensureSecureDirectory(dirURL)

        // 启动或写入前若主文件已达上限，先滚动。
        if let size = regularFileSizeIfOwned(at: currentFileURL.path), size >= maxBytes {
            try rotateFiles()
        }

        // 用 O_NOFOLLOW 打开（拒绝符号链接）并设置 0600 权限。
        let fd = open(currentFileURL.path, O_WRONLY | O_CREAT | O_APPEND | O_NOFOLLOW, S_IRUSR | S_IWUSR)
        guard fd >= 0 else {
            throw SecurityError.openFailed(errno)
        }
        defer { close(fd) }

        var st = stat()
        guard fstat(fd, &st) == 0 else {
            throw SecurityError.openFailed(errno)
        }
        guard (st.st_mode & S_IFMT) == S_IFREG else {
            throw SecurityError.symlinkRejected
        }
        guard st.st_uid == getuid() else {
            throw SecurityError.ownerMismatch
        }
        _ = fchmod(fd, S_IRUSR | S_IWUSR)

        try lineData.withUnsafeBytes { rawBuf in
            guard let base = rawBuf.baseAddress else { return }
            var remaining = rawBuf.count
            var offset = 0
            while remaining > 0 {
                let written = Darwin.write(fd, base.advanced(by: offset), remaining)
                if written < 0 {
                    if errno == EINTR { continue }
                    throw SecurityError.writeFailed(errno)
                }
                remaining -= written
                offset += written
            }
        }

        var postStat = stat()
        if fstat(fd, &postStat) == 0, UInt64(max(0, postStat.st_size)) >= maxBytes {
            try? rotateFiles()
        }
    }

    private func ensureSecureDirectory(_ dirURL: URL) throws {
        let path = dirURL.path
        var st = stat()
        if lstat(path, &st) == 0 {
            guard (st.st_mode & S_IFMT) == S_IFDIR else {
                throw SecurityError.symlinkRejected
            }
            guard st.st_uid == getuid() else {
                throw SecurityError.ownerMismatch
            }
            // 仅对默认 /tmp 目录强制收紧 0700，避免改动用户自定义目录（如 ~/Desktop）权限。
            if path == defaultFileURL.deletingLastPathComponent().path {
                _ = chmod(path, S_IRWXU)
            }
            return
        }
        try fileManager.createDirectory(
            at: dirURL,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700]
        )
        var created = stat()
        guard lstat(path, &created) == 0,
              (created.st_mode & S_IFMT) == S_IFDIR,
              created.st_uid == getuid()
        else {
            throw SecurityError.ownerMismatch
        }
    }

    private func regularFileSizeIfOwned(at path: String) -> UInt64? {
        var st = stat()
        guard lstat(path, &st) == 0 else { return nil }
        guard (st.st_mode & S_IFMT) == S_IFREG, st.st_uid == getuid() else { return nil }
        return UInt64(max(0, st.st_size))
    }

    private func backupURL(index: Int) -> URL {
        URL(fileURLWithPath: currentFileURL.path + ".\(index)")
    }

    private func rotateFiles() throws {
        guard backupCount > 0 else {
            try? fileManager.removeItem(at: currentFileURL)
            return
        }
        let oldest = backupURL(index: backupCount)
        if fileManager.fileExists(atPath: oldest.path) {
            try fileManager.removeItem(at: oldest)
        }
        if backupCount > 1 {
            for idx in stride(from: backupCount - 1, through: 1, by: -1) {
                let src = backupURL(index: idx)
                let dst = backupURL(index: idx + 1)
                if fileManager.fileExists(atPath: src.path) {
                    try fileManager.moveItem(at: src, to: dst)
                }
            }
        }
        if fileManager.fileExists(atPath: currentFileURL.path) {
            try fileManager.moveItem(at: currentFileURL, to: backupURL(index: 1))
        }
    }

    /// 格式化本地时区 ISO-8601 时间戳（精确到毫秒），用于 `CorrectionTraceRecord.ts`。
    nonisolated static func iso8601Timestamp(_ date: Date = Date()) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.timeZone = .current
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }
}
