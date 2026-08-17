using System;
using System.IO;
using System.Text;
using System.Threading;

namespace VoiceTyper.Support;

internal enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// 极简文件日志，单 writer + 后台线程刷盘。
/// 启动时若日志超过 maxBytes 则做一次按时间戳重命名的滚动（最多保留 3 个备份）。
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 2 * 1024 * 1024; // 2 MB
    private const int BackupCount = 3;

    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static bool _initialized;

    public static void Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return;

            try
            {
                Directory.CreateDirectory(AppConstants.ConfigDirectory);
                RotateIfNeeded(AppConstants.LogFilePath);

                var fs = new FileStream(
                    AppConstants.LogFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read
                );
                _writer = new StreamWriter(fs, new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
            }
            catch
            {
                // 日志初始化失败时静默；后续 Write 会跳过。
                _writer = null;
            }

            _initialized = true;
        }

        Info("app", $"=== VoiceTyper {AppConstants.Version} 启动 ===");
    }

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message);
    public static void Info(string category, string message) => Write(LogLevel.Info, category, message);
    public static void Warn(string category, string message) => Write(LogLevel.Warn, category, message);
    public static void Error(string category, string message) => Write(LogLevel.Error, category, message);
    public static void Error(string category, string message, Exception ex) =>
        Write(LogLevel.Error, category, $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(LogLevel level, string category, string message)
    {
        if (!_initialized) Initialize();

        var line = string.Format(
            "{0:yyyy-MM-dd HH:mm:ss.fff} [{1,-5}] [{2,-8}] [t{3,-3}] {4}",
            DateTime.Now,
            level.ToString().ToUpperInvariant(),
            category,
            Environment.CurrentManagedThreadId,
            message
        );

        lock (_lock)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // 写入失败静默处理；避免日志故障传染到调用方。
            }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if (info.Length < MaxBytes) return;

            // 旧 backup 后移；最老的丢弃
            for (int i = BackupCount - 1; i >= 1; i--)
            {
                var src = $"{path}.{i}";
                var dst = $"{path}.{i + 1}";
                if (File.Exists(src))
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(src, dst);
                }
            }

            var firstBackup = $"{path}.1";
            if (File.Exists(firstBackup)) File.Delete(firstBackup);
            File.Move(path, firstBackup);
        }
        catch
        {
            // 滚动失败不影响主流程
        }
    }
}
