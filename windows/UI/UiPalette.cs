using System.Drawing;

namespace VoiceTyper.UI;

/// <summary>
/// 设置窗口与引导窗口共用的语义色板。此前颜色散落在各处（SeaGreen / Firebrick / DarkGoldenrod 等
/// 系统色深浅不一），这里统一成一组可维护的 token：改主题只动这一个文件。
/// 所有颜色为浅色主题；HUD 的深色配色不在此表（见 <see cref="RecordingHud"/>）。
/// </summary>
internal static class UiPalette
{
    // ─ 品牌与交互 ──────────────────────────────────────────────
    public static readonly Color Accent = Color.FromArgb(38, 99, 218);        // #2663DA
    public static readonly Color AccentHover = Color.FromArgb(59, 118, 232);  // #3B76E8
    public static readonly Color AccentPressed = Color.FromArgb(30, 82, 184); // #1E52B8

    // ─ 语义色（成功 / 警告 / 错误）──────────────────────────────
    public static readonly Color Success = Color.FromArgb(31, 143, 77);              // #1F8F4D
    public static readonly Color Warning = Color.FromArgb(157, 93, 0);               // #9D5D00
    public static readonly Color WarningBackground = Color.FromArgb(255, 246, 229);  // #FFF6E5
    public static readonly Color Error = Color.FromArgb(209, 52, 56);                // #D13438

    // ─ 文本 ────────────────────────────────────────────────────
    public static readonly Color TextPrimary = Color.FromArgb(34, 43, 58);   // #222B3A
    public static readonly Color TextSecondary = Color.FromArgb(99, 110, 128); // #636E80
    public static readonly Color TextDisabled = Color.FromArgb(154, 163, 176); // #9AA3B0

    // ─ 表面与分隔 ──────────────────────────────────────────────
    public static readonly Color PageBackground = Color.FromArgb(247, 248, 250);        // #F7F8FA
    public static readonly Color SidebarBackground = Color.FromArgb(239, 242, 247);     // #EFF2F7
    public static readonly Color NavText = Color.FromArgb(52, 62, 80);                  // #343E50
    public static readonly Color NavSelectedBackground = Color.FromArgb(222, 232, 251); // #DEECFB
    public static readonly Color NavHoverBackground = Color.FromArgb(228, 233, 241);    // #E4E9F1
    public static readonly Color Card = Color.White;
    public static readonly Color CardBorder = Color.FromArgb(228, 232, 239);            // #E4E8EF
    public static readonly Color ControlBorder = Color.FromArgb(213, 220, 230);         // #D5DCE6
    public static readonly Color SecondaryButtonHover = Color.FromArgb(243, 245, 249);  // #F3F5F9
    public static readonly Color SecondaryButtonPressed = Color.FromArgb(233, 237, 243);// #E9EDF3
    public static readonly Color DisabledBackground = Color.FromArgb(241, 243, 246);    // #F1F3F6
}
