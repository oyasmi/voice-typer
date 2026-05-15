using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.Services;

internal sealed class ASRClientException : Exception
{
    public ASRClientException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 非流式（HTTP POST /recognize）语音识别客户端。
/// 每次识别新建一个 <see cref="ASRClient"/>，调用 <see cref="RecognizeAsync"/>。
/// </summary>
internal sealed class ASRClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ServerConfig _server;

    public ASRClient(ServerConfig server)
    {
        _server = server;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, server.Timeout)),
        };
    }

    public async Task<string> RecognizeAsync(byte[] audioData, string hotwords, bool llmRecorrect, CancellationToken ct = default)
    {
        var url = $"http://{_server.Host}:{_server.Port}/recognize?llm_recorrect={(llmRecorrect ? "true" : "false")}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new ByteArrayContent(audioData);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var trimmedKey = (_server.ApiKey ?? "").Trim();
        if (trimmedKey.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", trimmedKey);
        }

        if (!string.IsNullOrEmpty(hotwords))
        {
            request.Headers.Add("X-Hotwords", Uri.EscapeDataString(hotwords));
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ASRClientException("识别请求超时");
        }
        catch (HttpRequestException ex)
        {
            throw new ASRClientException($"连接服务端失败: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ASRClientException($"服务返回 {(int)response.StatusCode}: {Truncate(body, 200)}");
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("text", out var textProp))
                {
                    return textProp.GetString() ?? "";
                }
                throw new ASRClientException("响应缺少 text 字段");
            }
            catch (JsonException ex)
            {
                throw new ASRClientException($"响应解析失败: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// 健康检查；返回 ready 字段（默认 false）。任何异常都视为不可用。
    /// </summary>
    public static async Task<bool> HealthCheckAsync(ServerConfig server, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{server.Host}:{server.Port}/health");
            var trimmed = (server.ApiKey ?? "").Trim();
            if (trimmed.Length > 0)
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", trimmed);
            }
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("ready", out var ready) && ready.GetBoolean();
        }
        catch (Exception ex)
        {
            AppLog.Debug("network", $"健康检查失败: {ex.Message}");
            return false;
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";

    public void Dispose() => _http.Dispose();
}
