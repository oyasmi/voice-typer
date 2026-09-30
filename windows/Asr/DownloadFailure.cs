using System.ComponentModel;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using VoiceTyper.Support;

namespace VoiceTyper.Asr;

/// <summary>下载专用的错误分类与脱敏；不改变其他模块的日志内容。</summary>
internal static class DownloadFailure
{
    public static bool CanRetry(Exception error) => !IsTlsFailure(error) && error switch
    {
        ModelDownloadException model => model.CanRetry,
        HttpRequestException http => http.HttpRequestError is not
            (HttpRequestError.SecureConnectionError or HttpRequestError.UserAuthenticationError
            or HttpRequestError.ProxyTunnelError),
        AuthenticationException or UnauthorizedAccessException => false,
        _ => true,
    };

    public static string Summary(Exception error) => IsTlsFailure(error)
        ? L10n.T("安全连接失败，请检查系统时间、代理和证书信任。") : error switch
    {
        ModelDownloadException => error.Message,
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
            L10n.T("无法解析下载服务器地址，请检查网络和 DNS。"),
        HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError } =>
            L10n.T("代理连接失败，请检查系统代理设置。"),
        UnauthorizedAccessException => L10n.T("无法写入模型目录，请检查目录权限。"),
        _ => L10n.T("下载连接中断，请检查网络后重试。"),
    };

    private static bool IsTlsFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is AuthenticationException
                or HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }) return true;
        return false;
    }

    public static string Diagnostics(Exception error)
    {
        var lines = new List<string>();
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            var kind = current is HttpRequestException http ? $"; category={http.HttpRequestError}" : "";
            var native = current is Win32Exception win32 ? $"; native=0x{win32.NativeErrorCode:X8}" : "";
            lines.Add($"{current.GetType().Name}; hresult=0x{current.HResult:X8}{kind}{native}: {Redact(current.Message)}");
            if (current is ModelDownloadException { Diagnostics.Length: > 0 } model) lines.Add(Redact(model.Diagnostics));
        }
        return string.Join("\n", lines);
    }

    // 预签名 URL、URL 内凭据和查询参数不写日志，也不进入剪贴板诊断信息。
    internal static string Redact(string message) => Regex.Replace(message, @"https?://[^\s<>""']+", match =>
        Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}/…" : "[URL]");
}
