using System;
using System.Collections.Generic;

namespace VoiceTyper.UI;

/// <summary>
/// HUD 预览文本的排版与浮窗宽度档位（纯逻辑，测量函数由调用方注入，可脱离 GDI+ 单测）。对应 macOS 的
/// <c>lastLines</c> / <c>targetWidth</c>：预览最多显示 <c>maxLines</c> 行，超出时只显示最后几行——
/// "边说边校对识别结果"看的永远是最新说的那几个字。
/// </summary>
internal static class HudTextLayout
{
    /// <summary>起步宽度（@96DPI）。状态行实测更宽时放宽，至多 <see cref="CompactMaxWidth"/>。</summary>
    public const int CompactMinWidth = 400;
    public const int CompactMaxWidth = 460;
    /// <summary>最宽一档：约 40 个汉字一行，中文舒适行长的上限。起步宽度放不下一行时直接跳到这里，之后换行。
    /// 只有两档：中间档比起步只宽几个字，多跳一次换不来什么。</summary>
    public const int MaxWidth = 600;

    /// <summary>
    /// 选浮窗宽度（@96DPI，未做屏幕夹逼）：预览单行放得进紧凑宽度就用紧凑宽度，否则用最宽档。
    /// 结果不小于 <paramref name="floor"/>——一次显示期间只增不减，预览被修正变短时不回缩。
    /// 浮窗从中心向两边变宽而文字左对齐，每变一次文字整体左移一下，逐字跟随时会一直在挪。
    /// </summary>
    /// <param name="statusRowWidth">状态行内容（含左右内缩）实测宽度。</param>
    /// <param name="previewLineWidth">预览排成一行所需的浮窗宽度（含左右内缩）；没有预览时为 0。</param>
    public static int ChooseWidth(float statusRowWidth, float previewLineWidth, int floor)
    {
        var compact = Math.Clamp((int)Math.Ceiling(statusRowWidth), CompactMinWidth, CompactMaxWidth);
        var target = previewLineWidth > compact ? MaxWidth : compact;
        return Math.Max(target, floor);
    }

    /// <summary>
    /// 把 <paramref name="text"/> 从头按词换行，超过 <paramref name="maxLines"/> 行时只返回最后 <paramref name="maxLines"/> 行
    /// （文本为空时返回空数组）。
    /// 为什么从头排、按整行滚动，而不是逐字丢掉开头再加省略号：逐字丢开头时，每来一个字所有行的断行位置都会变，
    /// 整段文字像蛇一样往前爬。从头排版时已经显示过的行断行不变，第四行出现的同时第一行整行消失。
    /// </summary>
    /// <param name="measure">返回一段文本的像素宽度。</param>
    public static string[] LastLines(string text, int maxLines, float maxWidth, Func<string, float> measure)
    {
        if (string.IsNullOrWhiteSpace(text) || maxLines < 1 || maxWidth <= 0) return Array.Empty<string>();

        text = text.Trim().Replace('\r', ' ').Replace('\n', ' ');

        // 逐字符折行会对同一批字符重复测量，而 GDI+ 的 MeasureString 在 UI 线程上并不便宜。
        // 每个不同的字符只量一次，之后按字符宽度累加（比例字体的字距调整在 HUD 的字号下可以忽略；
        // 调用方留了余量，见 RecordingHud.Relayout）。单段录音上限 120 秒，全文排版的量级是几百个字符。
        var widths = new Dictionary<char, float>();
        float CharWidth(char ch)
        {
            if (!widths.TryGetValue(ch, out var w))
            {
                w = measure(ch.ToString());
                widths[ch] = w;
            }
            return w;
        }

        var lines = Wrap(text, maxWidth, CharWidth);
        return lines.Count <= maxLines ? lines.ToArray() : lines.GetRange(lines.Count - maxLines, maxLines).ToArray();
    }

    /// <summary>
    /// 贪心折行，与 macOS 标签的按词换行一致：英文单词、数字、网址这类连续的非中日韩字符不从中间断开，
    /// 只在空格之后或中日韩字符两侧断；一个词比整行还宽时才退回逐字符断。
    /// 行与行拼起来仍是原文（空格留在上一行末尾，不可见），<see cref="RecordingHud"/> 的尾部淡显按字符数倒数依赖这一点。
    /// </summary>
    private static List<string> Wrap(string text, float maxWidth, Func<char, float> charWidth)
    {
        var lines = new List<string>();
        int start = 0;
        // 当前行内最近一个可断开的位置（在该下标之前断开）；-1 表示还没有。
        int lastBreak = -1;
        float lineWidth = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (i > start && CanBreakBefore(text, i)) lastBreak = i;
            lineWidth += charWidth(text[i]);
            if (i > start && lineWidth > maxWidth)
            {
                int cut = lastBreak > start ? lastBreak : i;
                if (cut == i && char.IsLowSurrogate(text[i]) && i - 1 > start) cut = i - 1;
                lines.Add(text[start..cut]);
                // 只有逐字符断（整行是一个词）时才可能落在空格上，行首不留空格。
                while (cut < text.Length && text[cut] == ' ') cut++;
                start = cut;
                lastBreak = -1;
                lineWidth = 0;
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
