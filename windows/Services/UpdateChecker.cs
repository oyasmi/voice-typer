using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Support;

namespace VoiceTyper.Services;

/// <summary>
/// 手动触发的版本检查：读 GitHub Releases 的 latest 接口，与本地版本比较。对应 macOS 的 <c>UpdateChecker</c>。
///
/// <b>刻意不做后台自动检查</b>：自动检查意味着这个"音频只在本机处理"的工具会在用户不知情时定期联网，
/// 与产品定位相悖；而"用户根本不知道有新版本"这个问题，一个托盘菜单项就能解决掉大半。
/// 只在用户主动点「检查更新…」时发一次请求，不落盘任何状态、不上报任何信息。
/// </summary>
internal static class UpdateChecker
{
    public sealed record Release(string Version, string PageUrl);

    public abstract record Outcome
    {
        public sealed record UpToDate(string Current) : Outcome;
        public sealed record UpdateAvailable(Release Release) : Outcome;
        /// <summary>拿到了发布信息但无法解析出版本号：不猜测，直接把发布页给用户自己判断。</summary>
        public sealed record Indeterminate(string PageUrl) : Outcome;
    }

    private const string LatestReleaseUrl = "https://api.github.com/repos/oyasmi/voice-typer/releases/latest";

    public static async Task<Outcome> CheckForUpdateAsync(
        string? currentVersion = null, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        currentVersion ??= AppConstants.Version;
        var ownsClient = http is null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            // GitHub API 要求带 User-Agent，缺失时可能直接 403。
            request.Headers.UserAgent.ParseAdd($"VoiceTyper/{currentVersion}");

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(L10n.F("GitHub 返回 HTTP {0}", (int)response.StatusCode));
            }
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseRelease(body, currentVersion);
        }
        finally
        {
            if (ownsClient) http.Dispose();
        }
    }

    /// <summary>解析 latest 接口的响应体（纯逻辑，可单测）。</summary>
    internal static Outcome ParseRelease(string json, string currentVersion)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(L10n.T("无法解析 GitHub 的响应"));
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(L10n.T("无法解析 GitHub 的响应"));
            }
            var root = doc.RootElement;
            var pageUrl = root.TryGetProperty("html_url", out var htmlUrl) && htmlUrl.ValueKind == JsonValueKind.String
                ? htmlUrl.GetString()!
                : AppConstants.RepositoryUrl + "/releases";

            var tag = root.TryGetProperty("tag_name", out var tagEl) && tagEl.ValueKind == JsonValueKind.String
                ? tagEl.GetString()
                : null;
            var latest = tag is null ? null : VersionComponents(tag);
            var current = VersionComponents(currentVersion);
            if (tag is null || latest is null || current is null) return new Outcome.Indeterminate(pageUrl);

            return Compare(latest, current) > 0
                ? new Outcome.UpdateAvailable(new Release(tag, pageUrl))
                : new Outcome.UpToDate(currentVersion);
        }
    }

    /// <summary>
    /// 从任意 tag 文本里取出第一串点分十进制数（<c>v3.2.1</c> / <c>3.2.1</c> / <c>windows-3.2.1-beta</c>
    /// 都能命中）。取不到就返回 null，由调用方回落到"直接给发布页链接"，不做猜测性比较。
    /// </summary>
    internal static List<int>? VersionComponents(string raw)
    {
        var components = new List<int>();
        int? current = null;
        bool started = false;

        foreach (var ch in raw)
        {
            if (ch is >= '0' and <= '9')
            {
                current = (current ?? 0) * 10 + (ch - '0');
                started = true;
            }
            else if (ch == '.')
            {
                if (current is { } value)
                {
                    components.Add(value);
                    current = null;
                }
                else if (started)
                {
                    break; // "3.." 这类：数字段已经结束，不再往后找。
                }
                // 还没开始收集数字时（如 "v." 前缀里的点）忽略。
            }
            else if (started)
            {
                break;
            }
        }
        if (current is { } last) components.Add(last);
        return components.Count == 0 ? null : components;
    }

    /// <summary>逐段比较，缺失的段按 0 处理（<c>3.2</c> 与 <c>3.2.0</c> 等价）。</summary>
    internal static int Compare(IReadOnlyList<int> lhs, IReadOnlyList<int> rhs)
    {
        var count = Math.Max(lhs.Count, rhs.Count);
        for (int i = 0; i < count; i++)
        {
            var left = i < lhs.Count ? lhs[i] : 0;
            var right = i < rhs.Count ? rhs[i] : 0;
            if (left != right) return left > right ? 1 : -1;
        }
        return 0;
    }
}
