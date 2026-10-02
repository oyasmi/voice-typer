using System;
using System.IO;
using System.Text;

namespace VoiceTyper.Support;

internal enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// 极简文件日志，单 writer + 同步刷盘。
/// 启动时与运行中写入后文件达到 2MB 时，按序号重命名滚动（最多保留
/// <see cref="BackupCount"/> 个备份），总容量上限为 8MB（2MB × (BackupCount + 1)）。
/// </summary>
internal static class AppLog
{
    private const long DefaultMaxBytes = 2 * 1024 * 1024; // 2 MB
    private const int BackupCount = 3;

    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static bool _initialized;
    private static string _path = AppConstants.LogFilePath;
    private static long _maxBytes = DefaultMaxBytes;

    public static void Initialize() => Initialize(AppConstants.LogFilePath, DefaultMaxBytes);

    /// <summary>测试入口：指定日志文件路径与滚动阈值。重复调用会先关闭现有 writer 再重新初始化。</summary>
    internal static void Initialize(string path, long maxBytes)
    {
        lock (_lock)
        {
            if (_initialized) return;
            _path = path;
            _maxBytes = maxBytes;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RotateIfNeeded(path);
                _writer = OpenWriter(path);
            }
            catch (Exception ex)
            {
                // 日志自身故障不能递归写日志，只留一条调试输出；后续 Write 会跳过。
                System.Diagnostics.Debug.WriteLine($"[AppLog] 初始化失败: {ex.Message}");
                _writer = null;
            }

            _initialized = true;
        }

        Info("app", $"=== VoiceTyper {AppConstants.Version} 启动 ===");
    }

    /// <summary>测试用：关闭 writer 并回到未初始化状态。</summary>
    internal static void ResetForTests()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
            _initialized = false;
        }
    }

    private static StreamWriter OpenWriter(string path)
    {
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
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
                if (_writer is null) return;
                _writer.WriteLine(line);
                // 追加模式下 Position 即文件长度，不产生额外系统调用。
                if (_writer.BaseStream.Position >= _maxBytes) RollWriterLocked();
            }
            catch (Exception ex)
            {
                // 日志故障不能传染到调用方，也不能递归写日志：关闭写入并留一条调试输出。
                System.Diagnostics.Debug.WriteLine($"[AppLog] 写入失败，停止写日志: {ex.Message}");
                try { _writer?.Dispose(); } catch { /* 已在失败路径，只求关闭 */ }
                _writer = null;
            }
        }
    }

    /// <summary>必须在持有 <see cref="_lock"/> 时调用：关闭当前文件、滚动备份、重新打开。</summary>
    private static void RollWriterLocked()
    {
        _writer?.Dispose();
        _writer = null;
        RotateIfNeeded(_path, force: true);
        _writer = OpenWriter(_path);
    }

    private static void RotateIfNeeded(string path, bool force = false)
    {
        try
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if (!force && info.Length < _maxBytes) return;

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
        catch (Exception ex)
        {
            // 滚动失败不影响主流程；不能递归写日志，只留调试输出。
            System.Diagnostics.Debug.WriteLine($"[AppLog] 日志滚动失败: {ex.Message}");
        }
    }
}
