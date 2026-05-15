using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.Support;
using static VoiceTyper.Support.NativeMethods;

namespace VoiceTyper.Services;

internal sealed class HotkeyServiceException : Exception
{
    public HotkeyServiceException(string message) : base(message) { }
}

/// <summary>
/// 全局热键监听。基于 <c>WH_KEYBOARD_LL</c> 低级钩子，进程范围内只允许一个实例。
/// 必须在 UI 线程（即拥有消息泵的线程）上 Start，因为低级钩子的回调通过该线程的消息队列分发。
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    /// <summary>热键按下（首次按下，去重过 auto-repeat）。在 UI 线程触发。</summary>
    public Action? OnPress;
    /// <summary>热键松开。在 UI 线程触发。</summary>
    public Action? OnRelease;

    private IntPtr _hookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;  // 保活，避免 GC
    private HotkeyConfig? _hotkey;
    private int _targetVk;
    private ModifierMask _expectedModifiers;
    private bool _isActive;

    [Flags]
    private enum ModifierMask
    {
        None = 0,
        Ctrl = 1,
        Alt = 2,
        Shift = 4,
        Win = 8,
    }

    public void Start(HotkeyConfig hotkey)
    {
        Stop();

        var vk = MapKeyToVk(hotkey.Key);
        if (vk == 0)
        {
            throw new HotkeyServiceException($"不支持的热键主键: {hotkey.Key}");
        }

        _hotkey = hotkey.Clone();
        _targetVk = vk;
        _expectedModifiers = BuildExpected(hotkey.Modifiers);
        _isActive = false;

        _proc = HookCallback;
        var moduleHandle = GetModuleHandleW(null);
        _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, moduleHandle, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            _proc = null;
            throw new HotkeyServiceException($"安装键盘钩子失败 (Win32 error {err})");
        }

        AppLog.Info("hotkey", $"热键监听启动: {hotkey.DisplayString}");
    }

    public void Stop()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _proc = null;
        _hotkey = null;
        _targetVk = 0;
        _expectedModifiers = ModifierMask.None;
        _isActive = false;
    }

    public void Dispose() => Stop();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _hotkey is null)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var msg = wParam.ToInt32();
        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // 忽略 SendInput 注入的事件（我们自己注入 Ctrl+V 时不希望被自己截到）
        // 标准的 KBDLLHOOKSTRUCT.flags 的 LLKHF_INJECTED = 0x10
        const uint LLKHF_INJECTED = 0x10;
        if ((data.flags & LLKHF_INJECTED) != 0)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

        if (!isKeyDown && !isKeyUp)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        int vk = (int)data.vkCode;

        if (isKeyDown && vk == _targetVk)
        {
            // 修饰键必须严格匹配（不多不少）
            var currentMods = ReadCurrentModifiers();
            if (currentMods == _expectedModifiers && !_isActive)
            {
                _isActive = true;
                UiDispatcher.Post(() => OnPress?.Invoke());
            }
        }
        else if (isKeyUp && vk == _targetVk && _isActive)
        {
            _isActive = false;
            UiDispatcher.Post(() => OnRelease?.Invoke());
        }
        else if (isKeyUp && _isActive && IsModifierVk(vk))
        {
            // 用户按住组合键时先松开了修饰键。此时录音应当作"松开"处理。
            var afterRelease = ReadCurrentModifiersExcluding(vk);
            if (afterRelease != _expectedModifiers)
            {
                _isActive = false;
                UiDispatcher.Post(() => OnRelease?.Invoke());
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static ModifierMask BuildExpected(List<string> modifiers)
    {
        var mask = ModifierMask.None;
        foreach (var m in modifiers)
        {
            switch (m.Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mask |= ModifierMask.Ctrl;
                    break;
                case "alt":
                case "option":
                    mask |= ModifierMask.Alt;
                    break;
                case "shift":
                    mask |= ModifierMask.Shift;
                    break;
                case "win":
                case "win_l":
                case "win_r":
                case "super":
                case "command":
                case "cmd":
                    mask |= ModifierMask.Win;
                    break;
            }
        }
        return mask;
    }

    private static ModifierMask ReadCurrentModifiers()
    {
        var mask = ModifierMask.None;
        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) mask |= ModifierMask.Ctrl;
        if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) mask |= ModifierMask.Alt;
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) mask |= ModifierMask.Shift;
        if (((GetAsyncKeyState(VK_LWIN) | GetAsyncKeyState(VK_RWIN)) & 0x8000) != 0) mask |= ModifierMask.Win;
        return mask;
    }

    private static ModifierMask ReadCurrentModifiersExcluding(int releasedVk)
    {
        var mask = ReadCurrentModifiers();
        switch (releasedVk)
        {
            case VK_CONTROL: mask &= ~ModifierMask.Ctrl; break;
            case VK_MENU: mask &= ~ModifierMask.Alt; break;
            case VK_SHIFT: mask &= ~ModifierMask.Shift; break;
            case VK_LWIN:
            case VK_RWIN:
                mask &= ~ModifierMask.Win;
                break;
        }
        return mask;
    }

    private static bool IsModifierVk(int vk) =>
        vk == VK_CONTROL || vk == VK_MENU || vk == VK_SHIFT || vk == VK_LWIN || vk == VK_RWIN
        || vk == 0xA0 || vk == 0xA1 // LSHIFT, RSHIFT
        || vk == 0xA2 || vk == 0xA3 // LCONTROL, RCONTROL
        || vk == 0xA4 || vk == 0xA5; // LMENU, RMENU

    private static int MapKeyToVk(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        var k = key.Trim().ToLowerInvariant();

        // 单字符
        if (k.Length == 1)
        {
            var ch = char.ToUpperInvariant(k[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9') return ch;
        }

        // 命名键
        return k switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "backspace" => 0x08,
            "insert" => 0x2D,
            "delete" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "page_up" => 0x21,
            "pagedown" or "page_down" => 0x22,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            "f1" => 0x70,
            "f2" => 0x71,
            "f3" => 0x72,
            "f4" => 0x73,
            "f5" => 0x74,
            "f6" => 0x75,
            "f7" => 0x76,
            "f8" => 0x77,
            "f9" => 0x78,
            "f10" => 0x79,
            "f11" => 0x7A,
            "f12" => 0x7B,
            "capslock" or "caps_lock" => 0x14,
            _ => 0,
        };
    }
}
