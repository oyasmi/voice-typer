using System.Collections.Generic;
using System.Drawing;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

/// <summary>
/// 窗口界面用的字体：按界面语言选字体族，并按 (字体族, 字号, 粗细) 共享实例。
/// 此前每个标签都 <c>new Font</c>，设置页光标签就有几十个互不相同的 GDI+ 字体对象。
/// 实例随进程存活，不要 Dispose；只在 UI 线程使用。
/// </summary>
internal static class UiFonts
{
    private static readonly Dictionary<(string Family, float Size, bool Bold), Font> Cache = new();

    /// <summary>中文界面用微软雅黑 UI；英文界面用 Segoe UI（雅黑的西文字形偏宽，英文长句更容易撑破布局）。</summary>
    public static string Family => L10n.Current == AppLanguage.Zh ? "Microsoft YaHei UI" : "Segoe UI";

    public static Font Get(float size, bool bold = false)
    {
        var key = (Family, size, bold);
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var font))
            {
                font = new Font(key.Family, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
                Cache[key] = font;
            }
            return font;
        }
    }
}
