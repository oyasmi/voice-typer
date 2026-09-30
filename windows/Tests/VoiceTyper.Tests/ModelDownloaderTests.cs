using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using VoiceTyper.Asr;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 下载器的单连接 / 分段并行 / 回落 / 续传 / 校验语义（对应 macOS <c>ModelDownloaderTests</c> +
/// <c>SegmentedDownloadTests</c>）。用内存里的 Range 服务代替真实网络：确定、快速、可构造各种异常服务端。
/// </summary>
public class ModelDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "voicetyper-dl-" + Guid.NewGuid().ToString("N"));

    public ModelDownloaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private sealed class RangeServer : HttpMessageHandler
    {
        public byte[] Data { get; set; }
        public bool HonorRanges { get; set; } = true;
        public bool CorruptBody { get; set; }
        public int ForbiddenRequests { get; set; }
        public HttpStatusCode? ForcedStatus { get; set; }
        public readonly List<(long? From, long? To)> Requests = new();
        public readonly List<Uri?> RequestUris = new();

        public RangeServer(byte[] data) => Data = data;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            lock (Requests) Requests.Add((range?.From, range?.To));
            lock (RequestUris) RequestUris.Add(request.RequestUri);

            if (ForcedStatus is { } forced) return Task.FromResult(new HttpResponseMessage(forced));
            if (ForbiddenRequests > 0)
            {
                ForbiddenRequests--;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            var body = CorruptBody ? Data.Select((b, i) => i == 5 ? (byte)(b ^ 0xFF) : b).ToArray() : Data;
            if (range is null || !HonorRanges)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
            }

            long from = range.From ?? 0;
            long to = Math.Min(range.To ?? body.Length - 1, body.Length - 1);
            if (from >= body.Length) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));

            var slice = body.AsSpan((int)from, (int)(to - from + 1)).ToArray();
            var content = new ByteArrayContent(slice);
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, body.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
    }

    private static byte[] MakeData(int length)
    {
        var data = new byte[length];
        new Random(1234).NextBytes(data);
        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private ModelDownloader Make(RangeServer server, byte[] data, long segmentedMinimum, string name = "model.bin")
    {
        var files = new[] { new ModelDownloader.FileSpec(name, Sha(data), data.Length) };
        return new ModelDownloader(server, files, _dir, _ => new Uri("https://example.invalid/file"), segmentedMinimum);
    }

    private static List<double> Track(out Action<double> callback)
    {
        var list = new List<double>();
        callback = p => { lock (list) list.Add(p); };
        return list;
    }

    [Fact]
    public async Task Segmented_DownloadsInFourRanges_AndAssemblesExactly()
    {
        var data = MakeData(400_000);
        var server = new RangeServer(data);
        using var downloader = Make(server, data, segmentedMinimum: 100_000);
        var progress = Track(out var report);

        await downloader.DownloadAllAsync(report);

        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_dir, "model.bin")));
        // 1 次探测 + 4 段
        Assert.Equal(5, server.Requests.Count);
        Assert.Equal((0L, 0L), (server.Requests[0].From!.Value, server.Requests[0].To!.Value));
        var starts = server.Requests.Skip(1).Select(r => r.From!.Value).OrderBy(x => x).ToArray();
        Assert.Equal(new long[] { 0, 100_000, 200_000, 300_000 }, starts);
        Assert.Empty(Directory.GetFiles(_dir, "*.seg*"));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
        Assert.Equal(1.0, progress.Last(), 6);
        Assert.All(progress, p => Assert.InRange(p, 0.0, 1.0));
    }

    [Fact]
    public async Task Segmented_FallsBackToSingleConnection_WhenServerIgnoresRange()
    {
        var data = MakeData(300_000);
        var server = new RangeServer(data) { HonorRanges = false };
        using var downloader = Make(server, data, segmentedMinimum: 100_000);

        await downloader.DownloadAllAsync(_ => { });

        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_dir, "model.bin")));
        // 探测被 200 拒绝后永久停用分段：之后只有一个不带 Range 的普通请求。
        Assert.Equal(2, server.Requests.Count);
        Assert.Null(server.Requests[1].From);
    }

    [Fact]
    public async Task SmallFiles_UseASingleRequest()
    {
        var data = MakeData(50_000);
        var server = new RangeServer(data);
        using var downloader = Make(server, data, segmentedMinimum: 100_000);

        await downloader.DownloadAllAsync(_ => { });

        Assert.Single(server.Requests);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_dir, "model.bin")));
    }

    [Fact]
    public async Task Segmented_ResumesFromExistingSegmentFiles()
    {
        var data = MakeData(400_000);
        // 第 0 段已有前 30KB，第 1 段已经完整。
        await File.WriteAllBytesAsync(Path.Combine(_dir, "model.bin.seg0"), data[..30_000]);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "model.bin.seg1"), data[100_000..200_000]);
        var server = new RangeServer(data);
        using var downloader = Make(server, data, segmentedMinimum: 100_000);

        await downloader.DownloadAllAsync(_ => { });

        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_dir, "model.bin")));
        var segmentStarts = server.Requests.Skip(1).Select(r => r.From!.Value).OrderBy(x => x).ToArray();
        Assert.Equal(new long[] { 30_000, 200_000, 300_000 }, segmentStarts); // 第 1 段没有再请求
    }

    [Fact]
    public async Task Segmented_DiscardsOversizedStaleSegments()
    {
        var data = MakeData(400_000);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "model.bin.seg0"), new byte[150_000]); // 比 100KB 的分段还长
        var server = new RangeServer(data);
        using var downloader = Make(server, data, segmentedMinimum: 100_000);

        await downloader.DownloadAllAsync(_ => { });

        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_dir, "model.bin")));
    }

    [Fact]
    public async Task SingleConnection_ResumesFromPartFile()
    {
        var data = MakeData(50_000);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "model.bin.part"), data[..20_000]);
        var server = new RangeServer(data);
        using var downloader = Make(server, data, segmentedMinimum: 1_000_000);

        await downloader.DownloadAllAsync(_ => { });

        Assert.Equal((20_000L, (long?)null), (server.Requests[0].From!.Value, server.Requests[0].To));
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_dir, "model.bin")));
    }

    [Fact]
    public async Task ChecksumMismatch_Throws_AndInstallsNothing()
    {
        var data = MakeData(50_000);
        var server = new RangeServer(data) { CorruptBody = true };
        using var downloader = Make(server, data, segmentedMinimum: 1_000_000);

        await Assert.ThrowsAsync<ModelDownloadException>(() => downloader.DownloadAllAsync(_ => { }));

        Assert.False(File.Exists(Path.Combine(_dir, "model.bin")));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
        Assert.True(server.Requests.Count >= 2); // 本地重试过一次
    }

    [Fact]
    public async Task Segmented_ChecksumMismatch_FallsBackAndStillFailsCleanly()
    {
        var data = MakeData(400_000);
        var server = new RangeServer(data) { CorruptBody = true };
        using var downloader = Make(server, data, segmentedMinimum: 100_000);

        await Assert.ThrowsAsync<ModelDownloadException>(() => downloader.DownloadAllAsync(_ => { }));

        Assert.False(File.Exists(Path.Combine(_dir, "model.bin")));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    [Fact]
    public async Task HttpError_ThrowsWithStatus()
    {
        var data = MakeData(1_000);
        var server = new RangeServer(data) { ForcedStatus = HttpStatusCode.ServiceUnavailable };
        using var downloader = Make(server, data, segmentedMinimum: 1_000_000);

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => downloader.DownloadAllAsync(_ => { }));
        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public async Task ForbiddenEndpoint_FallsBackToNextEndpoint()
    {
        var data = MakeData(1_000);
        var server = new RangeServer(data) { ForbiddenRequests = 2 };
        var urls = new[]
        {
            new Uri("https://blocked.example.invalid/model.bin"),
            new Uri("https://mirror.example.invalid/model.bin"),
        };
        var files = new[] { new ModelDownloader.FileSpec("model.bin", Sha(data), data.Length) };
        using var downloader = new ModelDownloader(
            server, files, _dir, urlFor: null, segmentedMinimumBytes: 1_000_000,
            urlsFor: _ => urls);

        await downloader.DownloadAllAsync(_ => { });

        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_dir, "model.bin")));
        Assert.Equal(urls[0], server.RequestUris[0]);
        Assert.Equal(urls[0], server.RequestUris[1]);
        Assert.Equal(urls[1], server.RequestUris[2]);
    }

    [Fact]
    public async Task ExistingValidFile_IsSkipped()
    {
        var data = MakeData(1_000);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "model.bin"), data);
        var server = new RangeServer(data);
        using var downloader = Make(server, data, segmentedMinimum: 1_000_000);
        var progress = Track(out var report);

        await downloader.DownloadAllAsync(report);

        Assert.Empty(server.Requests);
        Assert.Equal(1.0, progress.Last(), 6);
    }

    [Fact]
    public async Task Cancel_StopsDownload_AndKeepsResumableData()
    {
        var data = MakeData(400_000);
        var server = new RangeServer(data);
        using var downloader = Make(server, data, segmentedMinimum: 100_000);
        downloader.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAllAsync(_ => { }));
        Assert.False(File.Exists(Path.Combine(_dir, "model.bin")));
    }

    [Theory]
    [InlineData(400_000, 4)]
    [InlineData(10, 4)]
    [InlineData(4, 4)]
    [InlineData(3, 4)]
    [InlineData(241_216_270, 4)]
    public void SegmentRanges_CoverEverythingWithoutOverlap(long total, int count)
    {
        var ranges = ModelDownloader.SegmentRanges(total, count);
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(total - 1, ranges[^1].End);
        for (int i = 1; i < ranges.Length; i++) Assert.Equal(ranges[i - 1].End + 1, ranges[i].Start);
        Assert.Equal(total, ranges.Sum(r => r.End - r.Start + 1));
    }

    [Fact]
    public void SegmentRanges_RejectsEmptyInput()
    {
        Assert.Empty(ModelDownloader.SegmentRanges(0, 4));
        Assert.Empty(ModelDownloader.SegmentRanges(100, 0));
    }

    [Fact]
    public void PinnedManifest_HasExpectedShape()
    {
        Assert.Equal(new[] { "config.yaml", "am.mvn", "tokens.json", "model_quant.onnx" }, ModelDownloader.Files.Select(f => f.Name));
        Assert.All(ModelDownloader.Files, f => Assert.Equal(64, f.Sha256.Length));
        Assert.True(ModelDownloader.Files[^1].SizeHint >= ModelDownloader.SegmentedMinimumBytes); // 权重会走分段
    }
}
