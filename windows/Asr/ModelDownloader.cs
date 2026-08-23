using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Support;

namespace VoiceTyper.Asr;

internal sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message) : base(message) { }
}

/// <summary>
/// 首次启动时从 ModelScope 拉取 SenseVoice-Small 权重。端点与 sha256 已实测验证
/// （见 windows/DESIGN.md §5.2）：
/// <c>https://www.modelscope.cn/api/v1/models/iic/SenseVoiceSmall-onnx/repo?Revision=master&amp;FilePath=&lt;file&gt;</c>，
/// 大文件走 302 → OSS，<c>Range</c> 请求返回 206，断点续传可用。<c>HEAD</c> 请求返回 404
/// （已实测），因此绝不能用 HEAD 探测文件大小，只能从 GET 响应头读。
///
/// 与 macOS 侧的 <c>ModelDownloader.swift</c> 相比更简单：不需要额外的 <c>.resume</c> 副文件，
/// <c>.part</c> 文件本身的现有长度就是续传状态——下次直接发 <c>Range: bytes=&lt;len&gt;-</c>。
/// </summary>
internal sealed class ModelDownloader : IDisposable
{
    public sealed record FileSpec(string Name, string Sha256, long SizeHint);

    /// <summary>小文件先行：网络/端点问题能在花掉 230MB 流量之前就暴露。</summary>
    public static readonly FileSpec[] Files =
    {
        new("config.yaml", "f71e239ba36705564b5bf2d2ffd07eece07b8e3f2bbf6d2c99d8df856339ac19", 1_855),
        new("am.mvn", "29b3c740a2c0cfc6b308126d31d7f265fa2be74f3bb095cd2f143ea970896ae5", 11_203),
        new("tokens.json", "a2594fc1474e78973149cba8cd1f603ebed8c39c7decb470631f66e70ce58e97", 352_000),
        new("model_quant.onnx", "21dc965f689a78d1604717bf561e40d5a236087c85a95584567835750549e822", 241_216_270),
    };

    public static long TotalBytes => Files.Sum(f => f.SizeHint);

    private const string EndpointBase = "https://www.modelscope.cn/api/v1/models/iic/SenseVoiceSmall-onnx/repo";

    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private volatile bool _cancelled;
    private bool _disposed;

    /// <summary>
    /// 下载全部 4 个文件到 <see cref="ModelLocator.DownloadDestination"/>；已存在且校验通过的文件跳过。
    /// </summary>
    /// <param name="onProgress">总体进度回调（0…1）。</param>
    public async Task DownloadAllAsync(Action<double> onProgress, CancellationToken ct = default)
    {
        _cancelled = false;
        var dir = ModelLocator.DownloadDestination;
        Directory.CreateDirectory(dir);

        long completedBytes = 0;
        foreach (var spec in Files)
        {
            ct.ThrowIfCancellationRequested();
            if (_cancelled) throw new OperationCanceledException();

            var destPath = Path.Combine(dir, spec.Name);
            if (File.Exists(destPath) && Sha256Matches(destPath, spec.Sha256))
            {
                completedBytes += spec.SizeHint;
                onProgress(Math.Min(1.0, (double)completedBytes / TotalBytes));
                continue;
            }

            var baseBytes = completedBytes;
            await DownloadOneAsync(spec, dir, fileProgress =>
            {
                var overall = baseBytes + spec.SizeHint * fileProgress;
                onProgress(Math.Min(1.0, overall / TotalBytes));
            }, ct).ConfigureAwait(false);

            completedBytes += spec.SizeHint;
            onProgress(Math.Min(1.0, (double)completedBytes / TotalBytes));
        }
    }

    public void Cancel() => _cancelled = true;

    private async Task DownloadOneAsync(FileSpec spec, string dir, Action<double> onProgress, CancellationToken ct)
    {
        var destPath = Path.Combine(dir, spec.Name);
        var partPath = Path.Combine(dir, spec.Name + ".part");

        Exception? lastError = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await PerformDownloadAsync(spec, partPath, onProgress, ct).ConfigureAwait(false);
                if (!Sha256Matches(partPath, spec.Sha256))
                {
                    TryDelete(partPath);
                    lastError = new ModelDownloadException($"文件 {spec.Name} 校验失败，可能是下载损坏，请重试。");
                    continue;
                }
                TryDelete(destPath);
                File.Move(partPath, destPath);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt == 0)
                {
                    AppLog.Warn("model", $"下载 {spec.Name} 第一次尝试失败，重试: {ex.Message}");
                }
            }
        }
        throw lastError ?? new ModelDownloadException($"下载 {spec.Name} 失败。");
    }

    private async Task PerformDownloadAsync(FileSpec spec, string partPath, Action<double> onProgress, CancellationToken ct)
    {
        long existingLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (existingLength >= spec.SizeHint) existingLength = 0; // 陈旧的超大 .part：从头重下更安全

        using var request = new HttpRequestMessage(HttpMethod.Get, RemoteUrl(spec.Name));
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        bool resumed = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existingLength > 0 && !resumed)
        {
            // 服务端未按 Range 响应（少见，但要兜底）：从头开始，避免把新内容错误地追加到旧数据后面。
            existingLength = 0;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new ModelDownloadException($"下载 {spec.Name} 失败（HTTP {(int)response.StatusCode}）。");
        }

        var totalLength = response.Content.Headers.ContentRange?.Length
            ?? response.Content.Headers.ContentLength
            ?? spec.SizeHint;

        // 这个方法只从后台线程（AppCoordinator 的 Task.Run）调用，没有 UI 同步上下文需要保留，
        // 所以这里不必用 ConfigureAwait(false)。
        await using var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            partPath,
            existingLength > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1 << 20,
            useAsync: true);

        var buffer = new byte[1 << 16];
        long written = existingLength;
        int read;
        // 每读 64KB 就回调一次：241MB 的模型文件约 3800 次，每次都触发一次全量 UI 刷新
        // （托盘 + 设置窗口）代价过高。节流到"变化 ≥0.5% 或距上次 ≥200ms"，首尾两次不节流
        // （对齐 macOS b94b31e/R3-09）。
        const double MinProgressDelta = 0.005;
        const long MinReportIntervalMs = 200;
        double lastReportedProgress = -1;
        var reportStopwatch = Stopwatch.StartNew();

        while ((read = await httpStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (_cancelled) throw new OperationCanceledException();
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            if (totalLength <= 0) continue;

            var progress = Math.Min(1.0, (double)written / totalLength);
            var isFirst = lastReportedProgress < 0;
            var isLast = written >= totalLength;
            if (isFirst || isLast
                || progress - lastReportedProgress >= MinProgressDelta
                || reportStopwatch.ElapsedMilliseconds >= MinReportIntervalMs)
            {
                lastReportedProgress = progress;
                reportStopwatch.Restart();
                onProgress(progress);
            }
        }
    }

    private static string RemoteUrl(string fileName) =>
        $"{EndpointBase}?Revision=master&FilePath={Uri.EscapeDataString(fileName)}";

    public static bool Sha256Matches(string path, string expectedHex)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            var hex = Convert.ToHexString(hash);
            return string.Equals(hex, expectedHex, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
