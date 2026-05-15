using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.Services;

/// <summary>
/// WebSocket 流式语音识别客户端。一次录音对应一个实例，不复用。
/// 生命周期：Connect → (SendAudio × N → Finalize) → [OnFinal/OnError] → Close。
///
/// 所有事件回调通过 <see cref="UiDispatcher"/> 投递回 UI 线程。
/// </summary>
internal sealed class StreamingASRClient : IDisposable
{
    public Action<string>? OnPartial;
    public Action<string>? OnFinal;
    public Action<string>? OnError;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _closed;
    private bool _finalReceived;

    public async Task ConnectAsync(ServerConfig server, IReadOnlyList<string> hotwords, bool llmRecorrect, CancellationToken ct = default)
    {
        if (_ws is not null) throw new InvalidOperationException("StreamingASRClient 已连接");

        var scheme = "ws";
        var url = $"{scheme}://{server.Host}:{server.Port}/recognize/stream?llm_recorrect={(llmRecorrect ? "true" : "false")}";

        _ws = new ClientWebSocket();
        var trimmedKey = (server.ApiKey ?? "").Trim();
        if (trimmedKey.Length > 0)
        {
            _ws.Options.SetRequestHeader("Authorization", $"Bearer {trimmedKey}");
        }

        _cts = new CancellationTokenSource();
        var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct).Token;

        try
        {
            await _ws.ConnectAsync(new Uri(url), linkedCt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("network", "WS 连接失败", ex);
            try { _ws.Dispose(); } catch { }
            _ws = null;
            _cts.Dispose();
            _cts = null;
            throw;
        }

        // 发送 start 帧
        var startPayload = new Dictionary<string, object?>
        {
            ["type"] = "start",
            ["hotwords"] = string.Join(" ", hotwords),
            ["sample_rate"] = AppConstants.TargetSampleRate,
        };
        await SendJsonAsync(startPayload, linkedCt).ConfigureAwait(false);

        // 启动接收循环
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public void SendAudio(byte[] data)
    {
        if (_closed || _ws is null) return;
        _ = SendAudioAsync(data);
    }

    public void FinalizeStream()
    {
        if (_closed || _ws is null) return;
        _ = SendJsonAsync(new Dictionary<string, object?> { ["type"] = "finalize" }, CancellationToken.None);
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;

        try { _cts?.Cancel(); } catch { }
        var ws = _ws;
        _ws = null;

        if (ws is not null)
        {
            // 异步、最佳努力关闭；不等待
            _ = Task.Run(async () =>
            {
                try
                {
                    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                    {
                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeoutCts.Token).ConfigureAwait(false);
                    }
                }
                catch { /* swallow */ }
                finally
                {
                    try { ws.Dispose(); } catch { }
                }
            });
        }

        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    public void Dispose() => Close();

    private async Task SendAudioAsync(byte[] data)
    {
        var ws = _ws;
        if (ws is null || _closed) return;

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed) return;
            if (ws.State != WebSocketState.Open) return;
            await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                AppLog.Warn("network", $"音频帧发送失败: {ex.Message}");
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendJsonAsync(Dictionary<string, object?> payload, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || _closed) return;

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed) return;
            if (ws.State != WebSocketState.Open) return;
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                AppLog.Warn("network", $"控制帧发送失败: {ex.Message}");
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null) return;

        var buffer = new byte[16 * 1024];
        var sb = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                HandleMessage(sb.ToString());
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (Exception ex)
        {
            if (!_closed && !_finalReceived)
            {
                AppLog.Warn("network", $"WS receive 失败: {ex.Message}");
                UiDispatcher.Post(() => OnError?.Invoke("连接中断"));
            }
        }
    }

    private void HandleMessage(string raw)
    {
        ServerMessage msg;
        try
        {
            msg = ServerMessage.Parse(raw);
        }
        catch (Exception ex)
        {
            AppLog.Warn("network", $"解析服务端消息失败: {ex.Message}");
            return;
        }

        switch (msg.Type)
        {
            case ServerMessageType.Partial:
                if (!string.IsNullOrEmpty(msg.Text))
                {
                    UiDispatcher.Post(() => OnPartial?.Invoke(msg.Text!));
                }
                break;
            case ServerMessageType.Final:
                _finalReceived = true;
                AppLog.Info("network", $"final: text={Truncate(msg.Text ?? "", 80)} asr={msg.AsrElapsed} llm={msg.LlmElapsed}");
                UiDispatcher.Post(() => OnFinal?.Invoke(msg.Text ?? ""));
                break;
            case ServerMessageType.Error:
                AppLog.Error("network", $"服务端错误: {msg.Code} {msg.Message}");
                UiDispatcher.Post(() => OnError?.Invoke(msg.Message ?? "服务端错误"));
                break;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";

    // ─── 服务端消息 ─────────────────────────────────────────────────

    private enum ServerMessageType { Unknown, Partial, Final, Error }

    private readonly struct ServerMessage
    {
        public ServerMessageType Type { get; init; }
        public string? Text { get; init; }
        public int Seq { get; init; }
        public double? AsrElapsed { get; init; }
        public double? LlmElapsed { get; init; }
        public string? Code { get; init; }
        public string? Message { get; init; }

        public static ServerMessage Parse(string raw)
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp))
            {
                return new ServerMessage { Type = ServerMessageType.Unknown };
            }
            var type = typeProp.GetString();

            return type switch
            {
                "partial" => new ServerMessage
                {
                    Type = ServerMessageType.Partial,
                    Text = root.TryGetProperty("text", out var t) ? t.GetString() : "",
                    Seq = root.TryGetProperty("seq", out var s) && s.TryGetInt32(out var sv) ? sv : 0,
                },
                "final" => new ServerMessage
                {
                    Type = ServerMessageType.Final,
                    Text = root.TryGetProperty("text", out var t) ? t.GetString() : "",
                    AsrElapsed = root.TryGetProperty("asrElapsed", out var a) && a.TryGetDouble(out var av) ? av : null,
                    LlmElapsed = root.TryGetProperty("llmElapsed", out var l) && l.TryGetDouble(out var lv) ? lv : null,
                },
                "error" => new ServerMessage
                {
                    Type = ServerMessageType.Error,
                    Code = root.TryGetProperty("code", out var c) ? c.GetString() : "unknown",
                    Message = root.TryGetProperty("message", out var m) ? m.GetString() : "",
                },
                _ => new ServerMessage { Type = ServerMessageType.Unknown },
            };
        }
    }
}
