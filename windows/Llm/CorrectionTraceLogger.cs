using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceTyper.Asr;
using VoiceTyper.Support;

namespace VoiceTyper.Llm;

/// <summary>校对会话在内存中暂存的轻量诊断草稿（TRACE-4）；仅在 <c>traceEnabled == true</c> 时构造。</summary>
internal sealed class CorrectionTraceDraft
{
    public required string AsrRawText { get; init; }
    public required string AsrReplacedText { get; init; }
    public required double MarginThreshold { get; init; }
    public required int RawHintsCount { get; init; }
    public required int DroppedByVocab { get; init; }
    public required IReadOnlyList<AcousticHint> ActiveHints { get; init; }
    public required int GlossaryCount { get; init; }
    public string? LlmOutputText { get; set; }
    public LlmCorrector.TokenUsage? TokenUsage { get; set; }
    public LlmCorrector.ThinkingParameterUsage? ThinkingParameter { get; set; }
    public string? FallbackReason { get; set; }
}

/// <summary>
/// 校对效果诊断记录 Schema v1（TRACE-3），以单行紧凑 JSON 写入独立本机 <c>.jsonl</c> 文件。
/// <b>白名单红线</b>：严禁包含 API Key、base_url、完整词汇表、音频样本、前台窗口标题或剪贴板内容。
/// </summary>
internal sealed class CorrectionTraceRecord
{
    public sealed class AsrSection
    {
        [JsonPropertyName("language_setting")] public required string LanguageSetting { get; init; }
        [JsonPropertyName("decode_mode")] public required string DecodeMode { get; init; }
        [JsonPropertyName("vocab_hits")] public required int VocabHits { get; init; }
        [JsonPropertyName("asr_ms")] public required int AsrMs { get; init; }
        [JsonPropertyName("decode_ms")] public required double DecodeMs { get; init; }
        [JsonPropertyName("raw_text")] public required string RawText { get; init; }
        [JsonPropertyName("replaced_text")] public required string ReplacedText { get; init; }
    }

    public sealed class HintItem
    {
        [JsonPropertyName("surface")] public required string Surface { get; init; }
        [JsonPropertyName("alternatives")] public required IReadOnlyList<string> Alternatives { get; init; }
        [JsonPropertyName("best_margin")] public required double BestMargin { get; init; }
    }

    public sealed class HintsSection
    {
        [JsonPropertyName("margin_threshold")] public required double MarginThreshold { get; init; }
        [JsonPropertyName("raw_count")] public required int RawCount { get; init; }
        [JsonPropertyName("active_count")] public required int ActiveCount { get; init; }
        [JsonPropertyName("dropped_by_vocab")] public required int DroppedByVocab { get; init; }
        [JsonPropertyName("items")] public required IReadOnlyList<HintItem> Items { get; init; }
    }

    public sealed class LlmSection
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("temperature")] public required double Temperature { get; init; }
        [JsonPropertyName("glossary_count")] public required int GlossaryCount { get; init; }
        [JsonPropertyName("hints_sent")] public required bool HintsSent { get; init; }
        [JsonPropertyName("result")] public required string Result { get; init; }
        [JsonPropertyName("fallback_reason")] public string? FallbackReason { get; init; }
        [JsonPropertyName("thinking_param")] public string? ThinkingParam { get; init; }
        [JsonPropertyName("llm_ms")] public required int LlmMs { get; init; }
        [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; init; }
        [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; init; }
        [JsonPropertyName("cached_tokens")] public int? CachedTokens { get; init; }
        [JsonPropertyName("output_text")] public string? OutputText { get; init; }
        [JsonPropertyName("final_text")] public required string FinalText { get; init; }
    }

    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("ts")] public required string Ts { get; init; }
    [JsonPropertyName("session_id")] public required string SessionId { get; init; }
    [JsonPropertyName("app_version")] public required string AppVersion { get; init; }
    [JsonPropertyName("platform")] public string Platform { get; init; } = "windows";
    [JsonPropertyName("audio_sec")] public required double AudioSec { get; init; }
    [JsonPropertyName("asr")] public required AsrSection Asr { get; init; }
    [JsonPropertyName("hints")] public required HintsSection Hints { get; init; }
    [JsonPropertyName("llm")] public required LlmSection Llm { get; init; }
}

/// <summary>
/// 独立本机校对诊断记录写入器（TRACE-2）。
/// 默认写入 <c>%TEMP%\VoiceTyper\correction_trace.jsonl</c>，单文件上限 5 MB，保留最多 2 个备份
/// （<c>.1</c> / <c>.2</c>），总容量硬顶 15 MB。独立锁串行写入，与 <see cref="AppLog"/> 彻底隔离；
/// 任何 IO 失败只向主日志记脱敏错误类型并冷却 60 秒，绝不阻断上屏或泄露用户文本。
/// </summary>
internal sealed class CorrectionTraceLogger
{
    public const long DefaultMaxBytes = 5 * 1024 * 1024;
    public const int DefaultBackupCount = 2;
    public static readonly TimeSpan DefaultReopenCooldown = TimeSpan.FromSeconds(60);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    private readonly long _maxBytes;
    private readonly int _backupCount;
    private readonly TimeSpan _reopenCooldown;
    private readonly string _defaultFilePath;

    private string _currentFilePath;
    private DateTimeOffset? _retryAfter;

    public CorrectionTraceLogger(
        string? configuredPath = null,
        string? defaultFilePath = null,
        long maxBytes = DefaultMaxBytes,
        int backupCount = DefaultBackupCount,
        TimeSpan? reopenCooldown = null)
    {
        _defaultFilePath = string.IsNullOrWhiteSpace(defaultFilePath)
            ? AppConstants.DefaultTraceLogFilePath
            : defaultFilePath;
        _maxBytes = maxBytes;
        _backupCount = backupCount;
        _reopenCooldown = reopenCooldown ?? DefaultReopenCooldown;
        _currentFilePath = ResolvePath(configuredPath, _defaultFilePath);
    }

    /// <summary>
    /// 路径解析纯函数（TRACE-1 §3.1）：
    /// 空串 / 含控制字符 / 相对路径 → 回落默认路径；支持环境变量展开；
    /// 以目录分隔符结尾或指向已存在目录 → 自动拼接 <c>correction_trace.jsonl</c>。
    /// </summary>
    public static string ResolvePath(string? configuredPath, string? defaultFilePath = null)
    {
        var fallback = string.IsNullOrWhiteSpace(defaultFilePath)
            ? AppConstants.DefaultTraceLogFilePath
            : defaultFilePath;
        var trimmed = (configuredPath ?? "").Trim();
        if (trimmed.Length == 0) return fallback;
        foreach (var ch in trimmed)
        {
            if (ch < 0x20 || ch == 0x7F)
            {
                AppLog.Warn("llm", "校对诊断日志路径含控制字符，已回落默认路径");
                return fallback;
            }
        }
        var expanded = Environment.ExpandEnvironmentVariables(trimmed);
        if (!Path.IsPathFullyQualified(expanded))
        {
            AppLog.Warn("llm", "校对诊断日志路径为相对路径，已回落默认路径");
            return fallback;
        }
        bool endsWithSep = expanded.EndsWith('\\') || expanded.EndsWith('/');
        if (endsWithSep || Directory.Exists(expanded))
        {
            return Path.Combine(expanded, AppConstants.TraceLogFileName);
        }
        return expanded;
    }

    public string TargetPath
    {
        get { lock (_lock) return _currentFilePath; }
    }

    public void UpdatePath(string? configuredPath)
    {
        var resolved = ResolvePath(configuredPath, _defaultFilePath);
        lock (_lock)
        {
            if (!string.Equals(_currentFilePath, resolved, StringComparison.OrdinalIgnoreCase))
            {
                _currentFilePath = resolved;
                _retryAfter = null;
            }
        }
    }

    /// <summary>追加一条诊断记录。任何异常仅向主日志记脱敏错误类型，绝不抛错、绝不打印记录内容。</summary>
    public void Append(CorrectionTraceRecord record)
    {
        lock (_lock)
        {
            if (_retryAfter is { } until && DateTimeOffset.UtcNow < until) return;
            try
            {
                var line = JsonSerializer.Serialize(record, JsonOptions) + "\n";
                var dir = Path.GetDirectoryName(_currentFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(_currentFilePath) && new FileInfo(_currentFilePath).Length >= _maxBytes)
                {
                    RotateLocked();
                }

                File.AppendAllText(_currentFilePath, line, Utf8NoBom);
                if (new FileInfo(_currentFilePath).Length >= _maxBytes)
                {
                    RotateLocked();
                }
                _retryAfter = null;
            }
            catch (Exception ex)
            {
                _retryAfter = DateTimeOffset.UtcNow + _reopenCooldown;
                AppLog.Warn("llm", $"写入校对诊断日志失败: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>删除当前生效路径的主文件及全部 <c>.1</c>/<c>.2</c> 滚动备份（TRACE-2）。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _retryAfter = null;
            DeleteIfExists(_currentFilePath);
            for (int i = 1; i <= Math.Max(1, _backupCount); i++)
            {
                DeleteIfExists(_currentFilePath + "." + i);
            }
        }
    }

    private void RotateLocked()
    {
        if (_backupCount <= 0)
        {
            DeleteIfExists(_currentFilePath);
            return;
        }
        DeleteIfExists(_currentFilePath + "." + _backupCount);
        for (int i = _backupCount - 1; i >= 1; i--)
        {
            var src = _currentFilePath + "." + i;
            var dst = _currentFilePath + "." + (i + 1);
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        }
        if (File.Exists(_currentFilePath))
        {
            File.Move(_currentFilePath, _currentFilePath + ".1", overwrite: true);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("llm", $"清空校对诊断日志失败: {ex.GetType().Name}");
        }
    }

    public static string Iso8601Timestamp(DateTimeOffset? now = null) =>
        (now ?? DateTimeOffset.Now).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
}
