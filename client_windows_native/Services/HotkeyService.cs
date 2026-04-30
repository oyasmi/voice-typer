using System.Runtime.InteropServices;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.Services;

/// <summary>
/// 全局热键服务。使用 SetWindowsHookEx(WH_KEYBOARD_LL) 低级键盘钩子。
/// 与 macOS Swift 版的 CGEventTap 方案对应。
///
/// 关键设计:
/// - 钩子安装在 UI 线程，回调也在 UI 线程执行（通过消息循环）
/// - _hookDelegate 必须持有强引用，防止 GC 回收导致崩溃
///   （与 Swift 版修复的 TapContext 弱引用问题同一类问题）
/// - 回调必须尽快返回，避免阻塞全局键盘输入
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    public event Action? OnPress;
    public event Action? OnRelease;

    private IntPtr _hookHandle;
    private NativeInterop.LowLevelKeyboardProc? _hookDelegate;
    private bool _isActive;
    private uint _targetVk;
    private HashSet<int> _requiredModifierVks = new();
    private bool _isRunning;

    /// <summary>启动热键监听</summary>
    public void Start(HotkeyConfig config)
    {
        Stop();

        _targetVk = ResolveKeyCode(config.Key);
        if (_targetVk == 0)
        {
            AppLog.Error($"不支持的热键: {config.Key}");
            return;
        }

        _requiredModifierVks = ResolveModifiers(config.Modifiers);

        // 必须持有委托引用，防止被 GC 回收
        _hookDelegate = HookCallback;
        _hookHandle = NativeInterop.SetWindowsHookEx(
            NativeInterop.WH_KEYBOARD_LL,
            _hookDelegate,
            IntPtr.Zero,
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            AppLog.Error($"安装键盘钩子失败，错误码: {error}");
            _hookDelegate = null;
            return;
        }

        _isRunning = true;
        AppLog.Info($"热键监听已启动: {FormatHotkey(config)}");
    }

    /// <summary>停止热键监听</summary>
    public void Stop()
    {
        _isRunning = false;
        _isActive = false;

        if (_hookHandle != IntPtr.Zero)
        {
            NativeInterop.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        _hookDelegate = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isRunning)
        {
            var kbd = Marshal.PtrToStructure<NativeInterop.KBDLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;

            if (msg is NativeInterop.WM_KEYDOWN or NativeInterop.WM_SYSKEYDOWN)
            {
                if (!_isActive && kbd.vkCode == _targetVk && AreModifiersPressed())
                {
                    _isActive = true;
                    try { OnPress?.Invoke(); }
                    catch (Exception ex) { AppLog.Error("热键按下回调错误", ex); }
                }
            }
            else if (msg is NativeInterop.WM_KEYUP or NativeInterop.WM_SYSKEYUP)
            {
                if (_isActive && kbd.vkCode == _targetVk)
                {
                    _isActive = false;
                    try { OnRelease?.Invoke(); }
                    catch (Exception ex) { AppLog.Error("热键释放回调错误", ex); }
                }
            }
        }

        // 必须调用 CallNextHookEx 传递事件，否则会阻断全局键盘
        return NativeInterop.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>使用 GetAsyncKeyState 检查修饰键当前状态</summary>
    private bool AreModifiersPressed()
    {
        foreach (var vk in _requiredModifierVks)
        {
            // GetAsyncKeyState 返回值的高位表示当前是否按下
            if ((NativeInterop.GetAsyncKeyState(vk) & 0x8000) == 0)
                return false;
        }
        return true;
    }

    /// <summary>将配置中的修饰键名解析为虚拟键码</summary>
    private static HashSet<int> ResolveModifiers(List<string> modifiers)
    {
        var vks = new HashSet<int>();

        foreach (var mod in modifiers)
        {
            switch (mod.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    vks.Add(NativeInterop.VK_CONTROL);
                    break;
                case "alt":
                    vks.Add(NativeInterop.VK_MENU);
                    break;
                case "shift":
                    vks.Add(NativeInterop.VK_SHIFT);
                    break;
                case "win":
                case "cmd":
                case "command":
                    // 通用 Win 键：检查左 Win 即可（GetAsyncKeyState 会响应任意一侧）
                    vks.Add(NativeInterop.VK_LWIN);
                    break;
                case "win_l":
                    vks.Add(NativeInterop.VK_LWIN);
                    break;
                case "win_r":
                    vks.Add(NativeInterop.VK_RWIN);
                    break;
            }
        }

        return vks;
    }

    /// <summary>将配置中的键名解析为虚拟键码</summary>
    private static uint ResolveKeyCode(string key)
    {
        var k = key.ToLowerInvariant();

        // 字母键 A-Z
        if (k.Length == 1 && k[0] >= 'a' && k[0] <= 'z')
            return (uint)(k[0] - 'a' + 0x41);

        // 数字键 0-9
        if (k.Length == 1 && k[0] >= '0' && k[0] <= '9')
            return (uint)(k[0] - '0' + 0x30);

        return k switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "enter" => 0x0D,
            "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73,
            "f5" => 0x74, "f6" => 0x75, "f7" => 0x76, "f8" => 0x77,
            "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
            _ => 0
        };
    }

    /// <summary>格式化热键用于显示</summary>
    internal static string FormatHotkey(HotkeyConfig config)
    {
        var parts = new List<string>(config.Modifiers);
        parts.Add(config.Key);
        return string.Join("+", parts).ToUpper();
    }

    public void Dispose() => Stop();
}
