using System;
using System.IO;
using System.Text.Json;
using VoiceTyper.Core;
using VoiceTyper.Llm;
using VoiceTyper.Support;
using Xunit;
using YamlDotNet.Serialization;

namespace VoiceTyper.Tests;

/// <summary>
/// TRACE-6：校对效果诊断记录配置往返、路径解析、JSONL Schema v1、滚动备份与清空测试。
/// </summary>
public class CorrectionTraceLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public CorrectionTraceLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "vt-trace-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void ConfigDefaultsRoundTripAndControlCharFallback()
    {
        var cfg = new AppConfig();
        Assert.False(cfg.Llm.TraceLog);
        Assert.Equal("", cfg.Llm.TraceLogPath);

        cfg.Llm.TraceLog = true;
        cfg.Llm.TraceLogPath = Path.Combine(_tempDir, "custom.jsonl");

        var yaml = new SerializerBuilder().Build().Serialize(cfg);
        var loaded = new DeserializerBuilder().IgnoreUnmatchedProperties().Build().Deserialize<AppConfig>(yaml).Validated();
        Assert.True(loaded.Llm.TraceLog);
        Assert.Equal(cfg.Llm.TraceLogPath, loaded.Llm.TraceLogPath);

        // 旧配置缺少两字段时自动回落默认
        var legacy = new DeserializerBuilder().IgnoreUnmatchedProperties().Build()
            .Deserialize<AppConfig>("llm:\n  enabled: true\n  model: gpt-4o-mini\n").Validated();
        Assert.False(legacy.Llm.TraceLog);
        Assert.Equal("", legacy.Llm.TraceLogPath);

        var dirty = new AppConfig();
        dirty.Llm.TraceLogPath = Path.Combine(_tempDir, "bad\0path.jsonl");
        Assert.Equal("", dirty.Validated().Llm.TraceLogPath);
    }

    [Fact]
    public void ResolvePathRules()
    {
        var fallback = Path.Combine(_tempDir, "default", AppConstants.TraceLogFileName);
        Assert.Equal(fallback, CorrectionTraceLogger.ResolvePath("", fallback));
        Assert.Equal(fallback, CorrectionTraceLogger.ResolvePath("relative/trace.jsonl", fallback));
        Assert.Equal(fallback, CorrectionTraceLogger.ResolvePath(Path.Combine(_tempDir, "bad\n.jsonl"), fallback));

        var existingDir = Path.Combine(_tempDir, "dir_exists");
        Directory.CreateDirectory(existingDir);
        Assert.Equal(Path.Combine(existingDir, AppConstants.TraceLogFileName), CorrectionTraceLogger.ResolvePath(existingDir, fallback));
    }

    private static CorrectionTraceRecord SampleRecord(string sessionId) => new()
    {
        Ts = "2026-11-28T20:15:32.418+08:00",
        SessionId = sessionId,
        AppVersion = "3.6.1",
        AudioSec = 6.4,
        Asr = new CorrectionTraceRecord.AsrSection
        {
            LanguageSetting = "auto",
            DecodeMode = "beam",
            VocabHits = 1,
            AsrMs = 112,
            DecodeMs = 0.9,
            RawText = "密等性",
            ReplacedText = "密等性",
        },
        Hints = new CorrectionTraceRecord.HintsSection
        {
            MarginThreshold = 2.5,
            RawCount = 1,
            ActiveCount = 1,
            DroppedByVocab = 0,
            Items = new[]
            {
                new CorrectionTraceRecord.HintItem
                {
                    Surface = "密等",
                    Alternatives = new[] { "幂等", "密度" },
                    BestMargin = 0.62,
                },
            },
        },
        Llm = new CorrectionTraceRecord.LlmSection
        {
            Model = "gpt-4o-mini",
            Temperature = 0.0,
            GlossaryCount = 2,
            HintsSent = true,
            Result = "corrected",
            FallbackReason = null,
            ThinkingParam = "sent",
            LlmMs = 640,
            PromptTokens = 512,
            CompletionTokens = 24,
            CachedTokens = 448,
            OutputText = "幂等性。",
            FinalText = "幂等性。",
        },
    };

    [Fact]
    public void AppendRotationUpdatePathAndClear()
    {
        var logPath = Path.Combine(_tempDir, "sub", "correction_trace.jsonl");
        var logger = new CorrectionTraceLogger(logPath, logPath, maxBytes: 400, backupCount: 2);

        logger.Append(SampleRecord("0001"));
        var written = File.Exists(logPath + ".1") ? logPath + ".1" : logPath;
        Assert.True(File.Exists(written));
        var line = File.ReadAllText(written).Trim();
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("windows", doc.RootElement.GetProperty("platform").GetString());
        Assert.Equal("0001", doc.RootElement.GetProperty("session_id").GetString());

        logger.Append(SampleRecord("0002"));
        logger.Append(SampleRecord("0003"));
        Assert.True(File.Exists(logPath + ".1"));
        Assert.True(File.Exists(logPath + ".2"));
        Assert.False(File.Exists(logPath + ".3"));

        // 切换路径不删旧文件
        var altPath = Path.Combine(_tempDir, "alt.jsonl");
        logger.UpdatePath(altPath);
        logger.Append(SampleRecord("0004"));
        Assert.True(File.Exists(logPath + ".1"));
        Assert.True(File.Exists(altPath) || File.Exists(altPath + ".1"));

        // 清空仅删除当前路径
        logger.UpdatePath(logPath);
        logger.Clear();
        Assert.False(File.Exists(logPath));
        Assert.False(File.Exists(logPath + ".1"));
        Assert.False(File.Exists(logPath + ".2"));
        Assert.True(File.Exists(altPath) || File.Exists(altPath + ".1"));
    }
}
