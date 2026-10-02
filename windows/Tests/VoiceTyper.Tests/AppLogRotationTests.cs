using System;
using System.IO;
using System.Linq;
using VoiceTyper.Support;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// AppLog 是进程级静态单例；这个类的测试会重定向它的输出路径，必须与其它可能写日志的测试串行，
/// 所以放进同一个 Collection 并关闭并行。
/// </summary>
[CollectionDefinition("AppLog", DisableParallelization = true)]
public class AppLogCollection { }

[Collection("AppLog")]
public class AppLogRotationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "voicetyper-applog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        AppLog.ResetForTests();
        AppLog.Initialize(); // 恢复默认路径，避免影响其它测试
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void RunningProcess_RollsLogWhenThresholdExceeded_AndBoundsTotalFiles()
    {
        AppLog.ResetForTests();
        var path = Path.Combine(_directory, "voice_typer.log");
        AppLog.Initialize(path, maxBytes: 4 * 1024);

        var line = new string('x', 200);
        for (int i = 0; i < 400; i++) AppLog.Info("test", line); // 约 80KB，远超阈值

        var files = Directory.GetFiles(_directory, "voice_typer.log*");
        Assert.Contains(path + ".1", files);
        Assert.True(files.Length <= 4, $"日志文件数应不超过 4 个（当前 + 3 备份），实际 {files.Length}");
        Assert.True(new FileInfo(path).Length < 4 * 1024 + 512, "当前日志文件应已被滚动到阈值以下");
    }
}
