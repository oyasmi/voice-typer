using System;
using System.Collections.Generic;
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
/// 两条下载路径（对应 macOS <c>ModelDownloader.swift</c>）：
/// <list type="bullet">
/// <item><b>单连接</b>：<c>.part</c> 文件本身的现有长度就是续传状态——下次直接发
///   <c>Range: bytes=&lt;len&gt;-</c>。经过实测的必经之路。</item>
/// <item><b>分段并行</b>（≥ 32MB 的文件，即 230MB 的模型权重）：先用 <c>Range: bytes=0-0</c> 探测总长，
///   均分四段并行下载到各自的 <c>.segN</c> 文件（续传同样只看文件长度），最后按序拼成 <c>.part</c>。
///   分段只是优化，<b>绝不能让一个优化把首次安装唯一的必经之路带崩</b>：任何异常（服务不支持 Range、
///   长度对不上、网络错误……）都会永久停用分段并落回单连接。</item>
/// </list>
/// 两条路径最终都以固定 sha256 校验为准。
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

    private const string ModelScopeEndpoint = "https://www.modelscope.cn/api/v1/models/iic/SenseVoiceSmall-onnx/repo";
    private static readonly string[] HuggingFaceMirrors =
    {
        "https://huggingface.co/DennisHuang648/SenseVoiceSmall-onnx/resolve/main",
        "https://hf-mirror.com/DennisHuang648/SenseVoiceSmall-onnx/resolve/main",
    };

    /// <summary>达到此大小的文件才值得分段：小文件的额外请求开销大于并行收益。</summary>
    internal const long SegmentedMinimumBytes = 32L * 1024 * 1024;
    internal const int SegmentCount = 4;

    // 单请求整体超时不设上限——大文件 + Range 续传下"总时长"没有合理常数。
    // 改用响应头超时 + 正文滑动无进度超时来杀死真正卡死的连接（R3-4）。
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly FileSpec[] _files;
    private readonly string _destination;
    private readonly Func<string, IReadOnlyList<Uri>> _urlsFor;
    private readonly long _segmentedMinimumBytes;
    private bool _segmentedDisabled;
    private bool _disposed;

    // 响应头必须在此时限内到达：ModelScope 302 → OSS 重定向 + TLS 握手实测个位数秒级，
    // 30s 已是"连接确实卡死"而非慢网络的信号。
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);
    // 正文读取的滑动无进度超时：只要有任意字节到达就重新计时；完全静默 30s = 连接已死，
    // 交外层重试。取 30s 兼顾移动网络的短暂拥塞与"不让用户对着卡住的进度条干等几分钟"。
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    public ModelDownloader() : this(null, null, null, null, null) { }

    /// <summary>测试入口：注入假的 HTTP 处理器、文件清单、落点与地址，脱离真实网络。</summary>
    internal ModelDownloader(HttpMessageHandler? handler, FileSpec[]? files, string? destination,
        Func<string, Uri>? urlFor, long? segmentedMinimumBytes,
        Func<string, IReadOnlyList<Uri>>? urlsFor = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _files = files ?? Files;
        _destination = destination ?? ModelLocator.DownloadDestination;
        _urlsFor = urlsFor ?? (urlFor is null
            ? RemoteUrls
            : name => new[] { urlFor(name) });
        _segmentedMinimumBytes = segmentedMinimumBytes ?? SegmentedMinimumBytes;
    }

    /// <summary>
    /// 下载全部文件到 <see cref="ModelLocator.DownloadDestination"/>；已存在且校验通过的文件跳过。
    /// </summary>
    /// <param name="onProgress">总体进度回调（0…1）。</param>
    public async Task DownloadAllAsync(Action<double> onProgress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_destination);

        // 把调用方 token 与内部取消源关联：Cancel() 或调用方取消都会立刻中断阻塞中的
        // SendAsync / ReadAsync / WriteAsync（R3-4）。
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        var token = linkedCts.Token;

        long totalBytes = _files.Sum(f => f.SizeHint);
        long completedBytes = 0;
        foreach (var spec in _files)
        {
            token.ThrowIfCancellationRequested();

            var destPath = Path.Combine(_destination, spec.Name);
            if (File.Exists(destPath) && Sha256Matches(destPath, spec.Sha256))
            {
                completedBytes += spec.SizeHint;
                onProgress(Math.Min(1.0, (double)completedBytes / totalBytes));
                continue;
            }

            var baseBytes = completedBytes;
            await DownloadOneAsync(spec, fileProgress =>
            {
                var overall = baseBytes + spec.SizeHint * fileProgress;
                onProgress(Math.Min(1.0, overall / totalBytes));
            }, token).ConfigureAwait(false);

            completedBytes += spec.SizeHint;
            onProgress(Math.Min(1.0, (double)completedBytes / totalBytes));
        }
    }

    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task DownloadOneAsync(FileSpec spec, Action<double> onProgress, CancellationToken ct)
    {
        var urls = _urlsFor(spec.Name).Distinct().ToArray();
        if (urls.Length == 0)
        {
            throw new ModelDownloadException(L10n.F("没有可用的模型下载地址：{0}", spec.Name));
        }

        Exception? lastError = null;
        for (var index = 0; index < urls.Length; index++)
        {
            try
            {
                await DownloadOneFromUrlAsync(spec, onProgress, ct, urls[index]).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (index + 1 < urls.Length)
                {
                    AppLog.Warn("model", $"下载 {spec.Name} 当前地址失败，切换备用地址 ({index + 2}/{urls.Length})：{ex.Message}");
                }
            }
        }

        throw lastError ?? new ModelDownloadException(L10n.F($"下载 {0} 失败。", spec.Name));
    }

    private async Task DownloadOneFromUrlAsync(FileSpec spec, Action<double> onProgress,
        CancellationToken ct, Uri url)
    {
        var destPath = Path.Combine(_destination, spec.Name);
        var partPath = Path.Combine(_destination, spec.Name + ".part");

        // 大文件先试分段并行。失败不抛出，而是停用分段并落回下面那条经过实测的单连接路径。
        if (!_segmentedDisabled && spec.SizeHint >= _segmentedMinimumBytes)
        {
            try
            {
                await DownloadSegmentedAsync(spec, partPath, onProgress, ct, url).ConfigureAwait(false);
                if (Sha256Matches(partPath, spec.Sha256))
                {
                    InstallPart(spec, partPath, destPath);
                    return;
                }
                AppLog.Warn("model", $"分段下载 {spec.Name} 校验失败，改用单连接重下");
                TryDelete(partPath);
                RemoveSegmentFiles(spec);
                _segmentedDisabled = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warn("model", $"分段下载 {spec.Name} 失败，停用分段并改用单连接: {ex.Message}");
                TryDelete(partPath);
                _segmentedDisabled = true;
            }
        }

        Exception? lastError = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // 第二次本地重试强制丢弃已有的 .part、从零开始：陈旧或与本次失败相关的残留
                // 会让"重试"精确重放同一个失败，跨越一次 app 重启也不会自愈（R4-03）。
                if (attempt > 0) TryDelete(partPath);
                await PerformDownloadAsync(spec, partPath, onProgress, ct, url).ConfigureAwait(false);
                if (!Sha256Matches(partPath, spec.Sha256))
                {
                    TryDelete(partPath);
                    lastError = new ModelDownloadException(L10n.F("文件 {0} 校验失败，可能是下载损坏，请重试。", spec.Name));
                    continue;
                }
                InstallPart(spec, partPath, destPath);
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
        throw lastError ?? new ModelDownloadException(L10n.F("下载 {0} 失败。", spec.Name));
    }

    private void InstallPart(FileSpec spec, string partPath, string destPath)
    {
        TryDelete(destPath);
        File.Move(partPath, destPath);
        RemoveSegmentFiles(spec); // 该文件的分段残留（若有）一并清掉
    }

    // ─── 单连接 ───────────────────────────────────────────────────

    private async Task PerformDownloadAsync(FileSpec spec, string partPath, Action<double> onProgress,
        CancellationToken ct, Uri url)
    {
        long existingLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (existingLength >= spec.SizeHint) existingLength = 0; // 陈旧的超大 .part：从头重下更安全

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await SendWithHeaderTimeoutAsync(request, spec.Name, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // 服务端认为 .part 的长度已越界：残留数据不可信，丢掉让下一次从头开始。
            TryDelete(partPath);
            throw new ModelDownloadException(L10n.F("下载 {0} 失败（HTTP {1}）。", spec.Name, (int)response.StatusCode));
        }

        bool resumed = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existingLength > 0 && !resumed)
        {
            // 服务端未按 Range 响应（少见，但要兜底）：从头开始，避免把新内容错误地追加到旧数据后面。
            existingLength = 0;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new ModelDownloadException(L10n.F("下载 {0} 失败（HTTP {1}）。", spec.Name, (int)response.StatusCode));
        }

        var totalLength = response.Content.Headers.ContentRange?.Length
            ?? response.Content.Headers.ContentLength
            ?? spec.SizeHint;

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            partPath,
            existingLength > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1 << 20,
            useAsync: true);

        var reporter = new ProgressReporter(onProgress);
        long written = existingLength;

        // 滑动无进度超时：每次成功读到字节就把定时器往后推 StallTimeout（R3-4）。
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stallCts.CancelAfter(StallTimeout);
        var buffer = new byte[1 << 16];

        while (true)
        {
            int read = await ReadWithStallTimeoutAsync(httpStream, buffer, stallCts, spec.Name, ct).ConfigureAwait(false);
            if (read <= 0) break;
            stallCts.CancelAfter(StallTimeout);

            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            if (totalLength > 0) reporter.Report(Math.Min(1.0, (double)written / totalLength), force: written >= totalLength);
        }
    }

    // ─── 分段并行 ─────────────────────────────────────────────────

    private sealed class SegmentedUnsupportedException : Exception
    {
        public SegmentedUnsupportedException(string message) : base(message) { }
    }

    private string SegmentPath(FileSpec spec, int index) => Path.Combine(_destination, $"{spec.Name}.seg{index}");

    private void RemoveSegmentFiles(FileSpec spec)
    {
        for (int i = 0; i < SegmentCount; i++) TryDelete(SegmentPath(spec, i));
    }

    /// <summary>把 <c>[0, total)</c> 均分成 <paramref name="count"/> 段的闭区间；余数摊给最后一段，
    /// 保证覆盖完整且互不重叠。</summary>
    internal static (long Start, long End)[] SegmentRanges(long total, int count)
    {
        if (total <= 0 || count <= 0) return Array.Empty<(long, long)>();
        if (total <= count) return new[] { (0L, total - 1) };
        var chunk = total / count;
        var ranges = new (long Start, long End)[count];
        for (int i = 0; i < count; i++)
        {
            var start = i * chunk;
            ranges[i] = (start, i == count - 1 ? total - 1 : start + chunk - 1);
        }
        return ranges;
    }

    private async Task DownloadSegmentedAsync(FileSpec spec, string partPath, Action<double> onProgress,
        CancellationToken ct, Uri url)
    {
        // 探测总长：HEAD 在该端点上返回 404，所以用 "bytes=0-0" 的 GET 从 Content-Range 里读。
        long total;
        using (var probe = new HttpRequestMessage(HttpMethod.Get, url))
        {
            probe.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await SendWithHeaderTimeoutAsync(probe, spec.Name, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new SegmentedUnsupportedException($"探测请求未返回 206（{(int)response.StatusCode}）");
            }
            total = response.Content.Headers.ContentRange?.Length ?? 0;
        }
        if (total <= 0) throw new SegmentedUnsupportedException("响应缺少总长度");
        if (total != spec.SizeHint)
        {
            // 长度与清单不符：文件已变更，分段没有意义，交单连接路径按 sha256 裁决。
            throw new SegmentedUnsupportedException($"总长度 {total} 与清单 {spec.SizeHint} 不符");
        }

        var ranges = SegmentRanges(total, SegmentCount);
        var done = new long[ranges.Length];
        for (int i = 0; i < ranges.Length; i++)
        {
            var path = SegmentPath(spec, i);
            var expected = ranges[i].End - ranges[i].Start + 1;
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            // 磁盘上的分段比它应有的长度还大：说明是上一次用了不同切分留下的垃圾，丢掉重来。
            if (size > expected) { TryDelete(path); size = 0; }
            done[i] = size;
        }

        var reporter = new ProgressReporter(onProgress);
        reporter.Report(done.Sum() / (double)total, force: true);

        using var segmentsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = ranges.Select((range, index) => Task.Run(async () =>
        {
            try
            {
                await DownloadSegmentAsync(spec, index, range, done, total, reporter, segmentsCts.Token, url).ConfigureAwait(false);
            }
            catch
            {
                segmentsCts.Cancel(); // 一段失败，其余没有继续的意义
                throw;
            }
        }, CancellationToken.None)).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // 优先抛出「不支持分段」这类有信息量的错误，而不是被取消连累出的 OperationCanceledException。
            var unsupported = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions)
                .OfType<SegmentedUnsupportedException>().FirstOrDefault();
            if (unsupported is not null) throw unsupported;
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            var real = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions).FirstOrDefault();
            throw real ?? new ModelDownloadException(L10n.F("下载 {0} 失败。", spec.Name));
        }

        AssembleSegments(spec, ranges, partPath, total);
        onProgress(1.0);
    }

    private async Task DownloadSegmentAsync(FileSpec spec, int index, (long Start, long End) range, long[] done, long total,
        ProgressReporter reporter, CancellationToken ct, Uri url)
    {
        var expected = range.End - range.Start + 1;
        var resumed = Interlocked.Read(ref done[index]);
        if (resumed >= expected) return; // 这段上次已经下完了

        var offset = range.Start + resumed;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(offset, range.End);

        using var response = await SendWithHeaderTimeoutAsync(request, spec.Name, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new SegmentedUnsupportedException($"分段 {index} 未返回 206（{(int)response.StatusCode}）");
        }
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange is not null && (contentRange.From != offset || contentRange.To != range.End))
        {
            throw new SegmentedUnsupportedException($"分段 {index} 的 Content-Range 与请求不符");
        }

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // bufferSize=1：不做 FileStream 内部缓冲，写入后长度即真实进度，续传只需看文件长度。
        await using var fileStream = new FileStream(SegmentPath(spec, index),
            resumed > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, useAsync: true);

        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stallCts.CancelAfter(StallTimeout);
        var buffer = new byte[1 << 16];
        long received = 0;
        var remaining = expected - resumed;

        while (received < remaining)
        {
            int read = await ReadWithStallTimeoutAsync(httpStream, buffer, stallCts, spec.Name, ct).ConfigureAwait(false);
            if (read <= 0) break;
            stallCts.CancelAfter(StallTimeout);

            // 服务端多给的字节（不该发生）不写入，避免污染相邻分段。
            var take = (int)Math.Min(read, remaining - received);
            await fileStream.WriteAsync(buffer.AsMemory(0, take), ct).ConfigureAwait(false);
            received += take;
            var sum = 0L;
            Interlocked.Add(ref done[index], take);
            for (int i = 0; i < done.Length; i++) sum += Interlocked.Read(ref done[i]);
            reporter.Report(Math.Min(1.0, sum / (double)total));
        }

        if (received < remaining)
        {
            throw new ModelDownloadException(L10n.F("下载 {0} 失败（连接提前结束）。", spec.Name));
        }
    }

    private void AssembleSegments(FileSpec spec, (long Start, long End)[] ranges, string partPath, long total)
    {
        // 拼接前就把"长度对不上"拦下来：否则要等算完 241MB 的 sha256 才发现。
        for (int i = 0; i < ranges.Length; i++)
        {
            var expected = ranges[i].End - ranges[i].Start + 1;
            var path = SegmentPath(spec, i);
            if (!File.Exists(path) || new FileInfo(path).Length != expected)
            {
                throw new ModelDownloadException(L10n.F("文件 {0} 校验失败，可能是下载损坏，请重试。", spec.Name));
            }
        }

        using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20))
        {
            for (int i = 0; i < ranges.Length; i++)
            {
                using var input = new FileStream(SegmentPath(spec, i), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
                input.CopyTo(output);
            }
        }
        if (new FileInfo(partPath).Length != total)
        {
            throw new ModelDownloadException(L10n.F("文件 {0} 校验失败，可能是下载损坏，请重试。", spec.Name));
        }
        RemoveSegmentFiles(spec);
    }

    // ─── 公共小工具 ───────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendWithHeaderTimeoutAsync(HttpRequestMessage request, string name, CancellationToken ct)
    {
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(HeaderTimeout);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelDownloadException(L10n.F("下载 {0} 连接超时（{1:F0}s 未响应），将重试。", name, HeaderTimeout.TotalSeconds));
        }
    }

    private static async Task<int> ReadWithStallTimeoutAsync(Stream stream, byte[] buffer, CancellationTokenSource stallCts, string name, CancellationToken ct)
    {
        try
        {
            return await stream.ReadAsync(buffer, stallCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelDownloadException(L10n.F("下载 {0} 停滞（{1:F0}s 无数据），将重试。", name, StallTimeout.TotalSeconds));
        }
    }

    /// <summary>
    /// 进度回调节流：每读 64KB 就回调一次的话，241MB 的模型文件约 3800 次，每次都触发一次全量 UI 刷新
    /// （托盘 + 设置窗口）代价过高。节流到"变化 ≥0.5% 或距上次 ≥200ms"，首尾不节流。多段并行时
    /// 从多个线程调用，内部加锁。
    /// </summary>
    private sealed class ProgressReporter
    {
        private const double MinDelta = 0.005;
        private const long MinIntervalMs = 200;

        private readonly Action<double> _report;
        private readonly object _lock = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _last = -1;

        public ProgressReporter(Action<double> report) => _report = report;

        public void Report(double fraction, bool force = false)
        {
            lock (_lock)
            {
                if (!force && _last >= 0 && fraction - _last < MinDelta && _clock.ElapsedMilliseconds < MinIntervalMs) return;
                _last = fraction;
                _clock.Restart();
                _report(fraction);
            }
        }
    }

    private static IReadOnlyList<Uri> RemoteUrls(string fileName)
    {
        var urls = new List<Uri>
        {
            new($"{ModelScopeEndpoint}?Revision=master&FilePath={Uri.EscapeDataString(fileName)}"),
        };

        foreach (var mirror in HuggingFaceMirrors)
        {
            urls.Add(new Uri($"{mirror}/{Uri.EscapeDataString(fileName)}?download=true"));
        }

        return urls;
    }

    public static bool Sha256Matches(string path, string expectedHex)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return string.Equals(Convert.ToHexString(hash), expectedHex, StringComparison.OrdinalIgnoreCase);
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
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _http.Dispose();
        _cts.Dispose();
    }
}
