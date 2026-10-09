using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceTyper.Core;

/// <summary>
/// 最近若干次听写的精简耗时记录，只放在内存里，供设置页「诊断与帮助」显示。内容来自
/// <see cref="DictationMetrics.BriefLine"/>，只含数字与枚举，不含任何识别文本。只在 UI 线程使用。
/// </summary>
internal sealed class DictationHistory
{
    public const int Capacity = 20;

    private readonly List<string> _lines = new();

    public void Add(DateTime localTime, DictationMetrics metrics)
    {
        _lines.Add(localTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + metrics.BriefLine());
        if (_lines.Count > Capacity) _lines.RemoveAt(0);
    }

    /// <summary>从新到旧。</summary>
    public IReadOnlyList<string> NewestFirst()
    {
        var copy = new List<string>(_lines);
        copy.Reverse();
        return copy;
    }
}

/// <summary>
/// 「复制诊断信息」的正文：环境事实 + 最近听写的耗时。用于把真机上的表现一次性反馈出来，
/// 因此<b>绝不包含识别文本、API Key、设备名、文件路径</b>——调用方只传数字、布尔与枚举名。
/// </summary>
internal static class DiagnosticsReport
{
    public static string Build(IReadOnlyList<(string Key, string Value)> facts, IReadOnlyList<string> recentNewestFirst)
    {
        var text = new StringBuilder();
        text.AppendLine("VoiceTyper diagnostics");
        foreach (var (key, value) in facts) text.Append(key).Append(": ").AppendLine(value);
        text.AppendLine();
        text.AppendLine($"recent dictations (newest first, up to {DictationHistory.Capacity})");
        if (recentNewestFirst.Count == 0) text.AppendLine("(none)");
        foreach (var line in recentNewestFirst) text.AppendLine(line);
        return text.ToString();
    }
}
