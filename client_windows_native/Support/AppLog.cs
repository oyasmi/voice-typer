namespace VoiceTyper.Support;

/// <summary>简易日志，写入文件 + 控制台</summary>
internal static class AppLog
{
    private static readonly string LogPath;
    private static readonly object Lock = new();

    static AppLog()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Constants.ConfigDirectoryName, "logs");
        Directory.CreateDirectory(logDir);
        LogPath = Path.Combine(logDir, $"voicetyper_{DateTime.Now:yyyyMMdd}.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warning(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {level} - {message}";
        Console.WriteLine(line);
        lock (Lock)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch { /* 日志写入失败不应影响主流程 */ }
        }
    }
}
