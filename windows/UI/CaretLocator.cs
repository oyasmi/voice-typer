using System;
using System.Drawing;
using System.Runtime.InteropServices;
using static VoiceTyper.Support.NativeMethods;

namespace VoiceTyper.UI;

/// <summary>
/// 取前台窗口的文字插入点（系统插入符）在屏幕上的位置，供 HUD「跟随光标」定位到正在输入的地方。
///
/// 只有用系统插入符的应用才取得到（记事本、各类 Win32 / RichEdit 输入框等）；Chrome / Electron / UWP
/// 等自绘输入框不上报插入符，调用方应回落到鼠标位置。取不到不是错误，不记日志。
/// </summary>
internal static class CaretLocator
{
    /// <summary>成功时 <paramref name="caret"/> 是插入点矩形（屏幕坐标）。</summary>
    public static bool TryGetScreenRect(out Rectangle caret)
    {
        caret = default;
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            var threadId = GetWindowThreadProcessId(foreground, out _);
            if (threadId == 0) return false;

            var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(threadId, ref info) || info.hwndCaret == IntPtr.Zero) return false;
            if (!IsUsable(info.rcCaret)) return false;

            var topLeft = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
            var bottomRight = new POINT { X = info.rcCaret.Right, Y = info.rcCaret.Bottom };
            if (!ClientToScreen(info.hwndCaret, ref topLeft) || !ClientToScreen(info.hwndCaret, ref bottomRight)) return false;

            var rect = Rectangle.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
            // 插入符被隐藏或应用没有正确维护时，坐标会落在所有屏幕之外：当作取不到。
            if (!System.Windows.Forms.SystemInformation.VirtualScreen.IntersectsWith(rect)) return false;
            caret = rect;
            return true;
        }
        catch (Exception)
        {
            // 取插入点只是定位的优化，任何失败都回落到鼠标位置。
            return false;
        }
    }

    /// <summary>应用没有真正放置插入符时矩形是空的（全 0 或高度为 0）。</summary>
    internal static bool IsUsable(RECT rect) => rect.Bottom > rect.Top && rect.Right >= rect.Left;
}
