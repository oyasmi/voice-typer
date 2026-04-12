using System.Text.Json;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.Services;

/// <summary>
/// 语音识别 HTTP 客户端。
/// 使用内置 HttpClient（替代 Python 版的 tornado）。
/// 修复了 Python 版的 hotwords percent-encoding bug：直接发送 UTF-8 原文。
/// </summary>
internal sealed class ASRClient : IDisposable
{
    private readonly HttpClient _client = new();

    /// <summary>检查服务端是否可用</summary>
    public async Task<bool> HealthCheckAsync(ServerConfig server)
    {
        try
        {
            var url = $"http://{server.Host}:{server.Port}/health";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request, server);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _client.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warning($"健康检查失败: HTTP {(int)response.StatusCode}");
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var ready = doc.RootElement.TryGetProperty("ready", out var readyProp) && readyProp.GetBoolean();

            AppLog.Info($"健康检查: ready={ready}");
            return ready;
        }
        catch (Exception ex)
        {
            AppLog.Warning($"健康检查失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>发送音频数据进行识别</summary>
    public async Task<string> RecognizeAsync(
        byte[] audioData, List<string> hotwords, ServerConfig server)
    {
        var url = $"http://{server.Host}:{server.Port}/recognize" +
                  $"?llm_recorrect={(server.LlmRecorrect ? "true" : "false")}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new ByteArrayContent(audioData);
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        // 修正: 直接发送 UTF-8 原文（不做 percent-encoding）
        if (hotwords.Count > 0)
            request.Headers.TryAddWithoutValidation("X-Hotwords", string.Join(" ", hotwords));

        ApplyAuth(request, server);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(server.Timeout));
        var response = await _client.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);

        // 校验 HTTP 状态码（对齐 macOS Swift 版修复后的行为）
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Warning($"识别请求失败: HTTP {(int)response.StatusCode} - {body}");
            return "";
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var text)
            ? text.GetString() ?? ""
            : "";
    }

    /// <summary>添加鉴权头（仅非本地地址且有 API Key 时）</summary>
    private static void ApplyAuth(HttpRequestMessage request, ServerConfig server)
    {
        if (server.Host != "127.0.0.1" && !string.IsNullOrEmpty(server.ApiKey))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.ApiKey);
        }
    }

    public void Dispose() => _client.Dispose();
}
