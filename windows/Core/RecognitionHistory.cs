using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceTyper.Core;

/// <summary>最近 20 条非空的最终识别文本。只在 UI 线程使用，只存内存，不进入日志或诊断报告。</summary>
internal sealed class RecognitionHistory
{
    public const int Capacity = 20;
    private readonly List<Entry> _entries = new();

    internal sealed record Entry(DateTime Time, string Text);

    public string? LatestText => _entries.Count == 0 ? null : _entries[^1].Text;

    public void Add(DateTime localTime, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _entries.Add(new Entry(localTime, text));
        if (_entries.Count > Capacity) _entries.RemoveAt(0);
    }

    /// <summary>返回独立快照，按录入顺序倒序，避免系统时间调整改变顺序。</summary>
    public IReadOnlyList<Entry> NewestFirst() => _entries.AsEnumerable().Reverse().ToArray();

    /// <summary>按录入顺序正序拼接，只复制正文，保留各条内容原有的空白。</summary>
    public string AllText() => string.Join("\r\n", _entries.Select(entry => entry.Text));
}
