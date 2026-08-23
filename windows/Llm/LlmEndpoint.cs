using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace VoiceTyper.Llm;

/// <summary>
/// 把用户填写的 LLM Base URL 结构化解析为最终请求地址，供设置页校验与
/// <see cref="LlmCorrector"/> 复用同一份规则，避免非法输入在请求期才触发异常（F-03）。
/// C# 直译自 <c>macos/Sources/VoiceTyper/LLM/LLMEndpoint.swift</c>。
/// </summary>
internal static class LlmEndpoint
{
    internal enum ErrorKind { None, Malformed, InsecurePlaintextHost }

    /// <summary>
    /// 解析结果：<see cref="Url"/> 非空即成功；失败时 <see cref="ErrorMessage"/> 给出可直接
    /// 展示给用户的文案。
    /// </summary>
    internal readonly struct ResolveResult
    {
        public Uri? Url { get; }
        public ErrorKind Error { get; }
        private readonly string? _insecureHost;

        public ResolveResult(Uri? url, ErrorKind error, string? insecureHost)
        {
            Url = url;
            Error = error;
            _insecureHost = insecureHost;
        }

        public string ErrorMessage => Error switch
        {
            ErrorKind.Malformed => "Base URL 格式不合法，请检查协议头（http/https）与地址。",
            ErrorKind.InsecurePlaintextHost =>
                $"{_insecureHost} 是公网地址，明文 HTTP 只允许本机或局域网地址，公网请使用 https://。",
            _ => "",
        };
    }

    /// <summary>
    /// 允许 https 用于任意 host；http 仅放行回环/私网地址（本机或局域网跑的
    /// Ollama/vLLM 等本地模型服务是常见场景，不应误伤）——明文 HTTP 下识别文本与
    /// <c>Authorization: Bearer &lt;key&gt;</c> 全程明文，放行任意公网 host 等于把两者
    /// 交给链路上任何中间人（R2-07）。若输入已包含 <c>/chat/completions</c> 后缀则不重复追加。
    /// </summary>
    public static ResolveResult Resolve(string baseUrl)
    {
        var trimmed = (baseUrl ?? "").Trim();
        while (trimmed.EndsWith('/'))
        {
            trimmed = trimmed[..^1];
        }

        if (trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace))
        {
            return new ResolveResult(null, ErrorKind.Malformed, null);
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return new ResolveResult(null, ErrorKind.Malformed, null);
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != "http" && scheme != "https")
        {
            return new ResolveResult(null, ErrorKind.Malformed, null);
        }

        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
        {
            return new ResolveResult(null, ErrorKind.Malformed, null);
        }

        if (scheme == "http" && !IsAllowedPlaintextHost(host))
        {
            return new ResolveResult(null, ErrorKind.InsecurePlaintextHost, host);
        }

        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            builder.Path = builder.Path.TrimEnd('/') + "/chat/completions";
        }
        return new ResolveResult(builder.Uri, ErrorKind.None, null);
    }

    /// <summary>便利入口：忽略具体失败原因，仅返回可用的请求地址。</summary>
    public static Uri? ChatCompletionsUrl(string baseUrl) => Resolve(baseUrl).Url;

    private static bool IsAllowedPlaintextHost(string rawHost)
    {
        var host = rawHost.ToLowerInvariant();
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        if (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local"))
        {
            return true;
        }
        if (host == "::1")
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            bool isLoopback = b[0] == 127;
            bool isPrivate = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
            bool isLinkLocal = b[0] == 169 && b[1] == 254;
            return isLoopback || isPrivate || isLinkLocal;
        }

        return false;
    }
}
