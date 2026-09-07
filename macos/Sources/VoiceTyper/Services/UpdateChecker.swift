import Foundation

/// 手动触发的版本检查：读 GitHub Releases 的 latest 接口，与本地
/// `CFBundleShortVersionString` 比较。
///
/// **刻意不做后台自动检查**，也不引入 Sparkle：
/// - 自动检查意味着这个"音频只在本机处理"的工具会在用户不知情时定期联网，与产品定位相悖；
/// - Sparkle 需要 appcast 托管 + 更新签名密钥管理这整套发布基础设施（macos/DESIGN.md 已
///   把它划在范围外）。而"用户根本不知道有新版本"这个问题，一个菜单项就能解决掉大半。
///
/// 因此这里只在用户主动点「检查更新…」时发一次请求，不落盘任何状态、不上报任何信息。
enum UpdateChecker {
    struct Release: Equatable {
        let version: String
        let pageURL: URL
    }

    enum Outcome: Equatable {
        case upToDate(current: String)
        case updateAvailable(Release)
        /// 拿到了发布信息但无法解析出版本号：不猜测，直接把发布页给用户自己判断。
        case indeterminate(pageURL: URL)
    }

    enum CheckError: LocalizedError {
        case httpStatus(Int)
        case malformedResponse

        var errorDescription: String? {
            switch self {
            case .httpStatus(let code):
                return "GitHub 返回 HTTP \(code)"
            case .malformedResponse:
                return "无法解析 GitHub 的响应"
            }
        }
    }

    private static let latestReleaseURL = URL(
        string: "https://api.github.com/repos/oyasmi/voice-typer/releases/latest"
    )!

    static func checkForUpdate(currentVersion: String = AppConstants.version) async throws -> Outcome {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 10
        let session = URLSession(configuration: configuration)
        defer { session.finishTasksAndInvalidate() }

        var request = URLRequest(url: latestReleaseURL)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        // GitHub API 要求带 User-Agent，缺失时可能直接 403。
        request.setValue("VoiceTyper/\(currentVersion)", forHTTPHeaderField: "User-Agent")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CheckError.malformedResponse
        }
        guard (200..<300).contains(http.statusCode) else {
            throw CheckError.httpStatus(http.statusCode)
        }
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw CheckError.malformedResponse
        }

        let pageURL = (json["html_url"] as? String).flatMap(URL.init(string:))
            ?? AppConstants.repositoryURL.appendingPathComponent("releases")

        guard let tag = json["tag_name"] as? String,
              let latest = versionComponents(from: tag),
              let current = versionComponents(from: currentVersion) else {
            return .indeterminate(pageURL: pageURL)
        }

        if compare(latest, current) == .orderedDescending {
            return .updateAvailable(Release(version: tag, pageURL: pageURL))
        }
        return .upToDate(current: currentVersion)
    }

    // MARK: - 版本号解析

    /// 从任意 tag 文本里取出第一串点分十进制数（`v3.2.1` / `3.2.1` / `macos-3.2.1-beta`
    /// 都能命中）。取不到就返回 nil，由调用方回落到"直接给发布页链接"，不做猜测性比较。
    static func versionComponents(from raw: String) -> [Int]? {
        var components: [Int] = []
        var current: Int?
        var started = false

        for character in raw {
            if character.isASCII, character.isNumber, let value = character.wholeNumberValue {
                current = (current ?? 0) * 10 + value
                started = true
            } else if character == "." {
                if let value = current {
                    components.append(value)
                    current = nil
                } else if started {
                    // "3.." 这类：数字段已经结束，不再往后找。
                    break
                }
                // 还没开始收集数字时（如 "v." 前缀里的点）忽略。
            } else if started {
                break
            }
        }
        if let value = current {
            components.append(value)
        }
        return components.isEmpty ? nil : components
    }

    /// 逐段比较，缺失的段按 0 处理（`3.2` 与 `3.2.0` 等价）。
    static func compare(_ lhs: [Int], _ rhs: [Int]) -> ComparisonResult {
        let count = max(lhs.count, rhs.count)
        for index in 0..<count {
            let left = index < lhs.count ? lhs[index] : 0
            let right = index < rhs.count ? rhs[index] : 0
            if left > right { return .orderedDescending }
            if left < right { return .orderedAscending }
        }
        return .orderedSame
    }
}
