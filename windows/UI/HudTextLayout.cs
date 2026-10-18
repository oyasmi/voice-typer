using System;
using System.Collections.Generic;

namespace VoiceTyper.UI;

/// <summary>
/// HUD 预览文本的排版（纯逻辑，测量函数由调用方注入，可脱离 GDI+ 单测）。对应 macOS 的
/// <c>tailFitting</c>：预览最多显示 <c>maxLines</c> 行，放不下时只保留<b>尾部</b>并在开头补省略号——
/// "边说边校对识别结果"看的永远是最新说的那几个字。
/// </summary>
internal static class HudTextLayout
{
    public const string LeadingEllipsis = "…";

    /// <summary>预览文本只需要看最后一段；先截尾再排版，避免超长会话里每次都对全文测量。</summary>
    public const int MaxConsideredChars = 400;

    /// <summary>
    /// 把 <paramref name="text"/> 排成至多 <paramref name="maxLines"/> 行。放不下时取能放进去的最长后缀，
    /// 首行以省略号开头。返回的行数在 1…maxLines（文本为空时返回空数组）。
    /// </summary>
    /// <param name="measure">返回一段文本的像素宽度。</param>
    public static string[] FitTail(string text, int maxLines, float maxWidth, Func<string, float> measure)
    {
        if (string.IsNullOrWhiteSpace(text) || maxLines < 1 || maxWidth <= 0) return Array.Empty<string>();

        text = text.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (text.Length > MaxConsideredChars) text = text[^MaxConsideredChars..];

        // 二分 × 逐字符折行会对同一批字符重复测量成千上万次，而 GDI+ 的 MeasureString 在 UI 线程上并不便宜。
        // 每个不同的字符只量一次，之后按字符宽度累加（比例字体的字距调整在 HUD 的字号下可以忽略；
        // 调用方留了余量，见 RecordingHud.Relayout）。
        var rawMeasure = measure;
        var widths = new Dictionary<char, float>();
        float Sum(string s)
        {
            float total = 0;
            foreach (var ch in s)
            {
                if (!widths.TryGetValue(ch, out var w))
                {
                    w = rawMeasure(ch.ToString());
                    widths[ch] = w;
                }
                total += w;
            }
            return total;
        }
        measure = Sum;

        var whole = Wrap(text, maxLines, maxWidth, measure);
        if (whole is not null) return whole.ToArray();

        // 放不下：二分找最长后缀，使 "…" + 后缀 恰好能排进 maxLines 行。
        int lo = 1, hi = text.Length - 1;
        List<string>? best = null;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var candidate = LeadingEllipsis + text[^mid..];
            var lines = Wrap(candidate, maxLines, maxWidth, measure);
            if (lines is not null)
            {
                best = lines;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return (best ?? new List<string> { LeadingEllipsis }).ToArray();
    }

    /// <summary>
    /// 贪心折行，与 macOS 标签的按词换行一致：英文单词、数字、网址这类连续的非中日韩字符不从中间断开，
    /// 只在空格之后或中日韩字符两侧断；一个词比整行还宽时才退回逐字符断。超过 <paramref name="maxLines"/> 行返回 null。
    /// 行与行拼起来仍是原文（空格留在上一行末尾，不可见），<see cref="RecordingHud"/> 的尾部淡显按字符数倒数依赖这一点。
    /// </summary>
    private static List<string>? Wrap(string text, int maxLines, float maxWidth, Func<string, float> measure)
    {
        var lines = new List<string>();
        int start = 0;
        // 当前行内最近一个可断开的位置（在该下标之前断开）；-1 表示还没有。
        int lastBreak = -1;
        int i = 0;
        while (i < text.Length)
        {
            if (i > start && CanBreakBefore(text, i)) lastBreak = i;
            if (i > start && measure(text[start..(i + 1)]) > maxWidth)
            {
                int cut = lastBreak > start ? lastBreak : i;
                if (cut == i && char.IsLowSurrogate(text[i]) && i - 1 > start) cut = i - 1;
                lines.Add(text[start..cut]);
                if (lines.Count >= maxLines) return null;
                // 只有逐字符断（整行是一个词）时才可能落在空格上，行首不留空格。
                while (cut < text.Length && text[cut] == ' ') cut++;
                start = cut;
                lastBreak = -1;
                i = start;
                continue;
            }
            i++;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>不出现在行首的句读：中文与英文的点号、闭括号。</summary>
    private const string NoLineStartChars = "，。、；：？！）」』》】〉…,.;:!?)]}%";

    /// <summary>能否在 <c>text[i - 1]</c> 与 <c>text[i]</c> 之间换行。</summary>
    private static bool CanBreakBefore(string text, int i)
    {
        char prev = text[i - 1], cur = text[i];
        if (char.IsLowSurrogate(cur)) return false;
        // 空格不放到行首：在它之后断，让它挂在上一行末尾。
        if (cur == ' ' || NoLineStartChars.Contains(cur)) return false;
        return prev == ' ' || IsCjk(prev) || IsCjk(cur);
    }

    /// <summary>中日韩文字与全角符号（U+2E80 起，含假名、谚文与全角标点）：字与字之间都能断行。</summary>
    private static bool IsCjk(char c) => c >= '⺀' && !char.IsSurrogate(c);

    /// <summary>状态行里输入设备名的显示上限，超出截断——设备名可以很长
    /// （"Jabra Evolve2 65 立体声耳机"），不能让它把计时挤出可视区。</summary>
    public const int InputDeviceNameLimit = 14;

    public static string TruncateDeviceName(string name)
    {
        var trimmed = (name ?? "").Trim();
        return trimmed.Length <= InputDeviceNameLimit ? trimmed : trimmed[..InputDeviceNameLimit].TrimEnd() + "…";
    }

    /// <summary>把音量（线性 RMS）映射到 0…1 的条高：−50 dBFS 以下为 0，0 dBFS 为 1。
    /// 语音的典型电平在 −40…−15 dBFS，线性显示会几乎看不出变化。</summary>
    public static float LevelToBarHeight(float rms)
    {
        if (!(rms > 1e-5f)) return 0;
        var db = 20 * MathF.Log10(rms);
        return Math.Clamp((db + 50f) / 50f, 0f, 1f);
    }
}
