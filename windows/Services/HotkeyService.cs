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
internal sealed class HotkeyService : IHotkeyListening
{
    /// <summary>热键按下（首次按下，去重过 auto-repeat）。在 UI 线程触发。</summary>
    public Action? OnPress { get; set; }
    /// <summary>热键松开。在 UI 线程触发。</summary>
    public Action? OnRelease { get; set; }
    /// <summary>录音中按下 Esc 主动取消。在 UI 线程触发。</summary>
    public Action? OnCancel { get; set; }
    /// <summary>单独修饰键被用作组合快捷键，本次录音应静默丢弃。在 UI 线程触发。</summary>
    public Action? OnGestureCancelled { get; set; }
    /// <summary>钩子健康状态变化（R3-5）：false = 钩子已失效且自愈失败中（应向用户显示不可用），
    /// true = （重新）安装成功。在 UI 线程触发。</summary>
    public Action<bool>? OnHealthChanged { get; set; }

    private bool _acceptsCancelWhenInactive;
    /// <inheritdoc/>
    public bool AcceptsCancelWhenInactive
    {
        get => _acceptsCancelWhenInactive;
        set
        {
            _acceptsCancelWhenInactive = value;
            if (_stateMachine is not null) _stateMachine.AcceptsCancelWhenInactive = value;
        }
    }

    /// <summary>
    /// 钩子存活性自愈检查周期。<c>WH_KEYBOARD_LL</c> 的回调若超过
    /// <c>HKEY_CURRENT_USER\Control Panel\Desktop\LowLevelHooksTimeout</c> 未返回，系统会
    /// 直接把钩子卸掉且不通知——现象与 macOS 的 tapDisabledByTimeout 一致：热键突然永久失效。
    /// 具体超时毫秒数随 Windows 版本而变，未在真机上核实，不在此写死。回调始终立即返回、
    /// 业务异步投递（见 <see cref="HookCallback"/> 使用 <see cref="UiDispatcher.PostAsync"/>）。
    /// </summary>
    private const int HealthCheckIntervalMs = 30_000;

    /// <summary>掩码哑键的虚拟键码。0xFF 未被任何键盘布局映射、应用与输入法都不产生字符，
    /// 是 AutoHotkey 等工具做"修饰键单击掩码"的惯用值。</summary>
    private const ushort VK_NOOP_MASK = 0xFF;

    private IntPtr _hookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;  // 保活，避免 GC
    /// <summary>仅单独修饰键热键才安装的鼠标钩子：按住修饰键期间点了鼠标 = 组合用法，作废本次手势。</summary>
    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _mouseProc;
    private HotkeyConfig? _hotkey;
    /// <summary>当前热键目标键的 vkCode（自愈重装用 MapKeyToVk 重算，与钩子同生命周期）。</summary>
    private int _targetVk;
    /// <summary>右 Alt 作为单独修饰键时为 true：干净手势开始需要注入哑键掩掉菜单栏激活。</summary>
    private bool _needsMenuBarMask;
    /// <summary>按键判定逻辑抽到 <see cref="HotkeyStateMachine"/>（纯逻辑、可单测，R2-1）。
    /// 钩子回调只做注入过滤 + 委派 + 把动作异步投递到 UI 线程。</summary>
    private HotkeyStateMachine? _stateMachine;
    private System.Windows.Forms.Timer? _healthTimer;
    /// <summary>最近一次钩子回调被系统调用的时间戳（<see cref="Environment.TickCount"/>）。</summary>
    private int _lastHookActivityTick;
    /// <summary>连续两次健康检查都可疑才真正重装——降低 GetLastInputInfo 含鼠标带来的误判影响（R3-5）。</summary>
    private bool _healthSuspectLastTick;
    private int _recoveryFailures;
    private int _lastRecoveryAttemptTick;
    private bool _lastReportedHealthy = true;

    /// <summary>钩子静默这么久 + 期间确有输入才怀疑被摘钩。取 4 个周期（约 2 分钟），
    /// 远比"用户只是没打字"保守；GetLastInputInfo 无法区分键鼠，故一次多余重装视为可接受
    /// （定时器不受影响、按键状态会被清理）。</summary>
    private const int SilenceSuspectMs = 4 * HealthCheckIntervalMs;

    public void Start(HotkeyConfig hotkey)
    {
        Stop();

        var vk = MapKeyToVk(hotkey.Key);
        if (vk == 0)
        {
            throw new HotkeyServiceException(L10n.F("不支持的热键主键: {0}", hotkey.Key));
        }

        _hotkey = hotkey.Clone();
        _recoveryFailures = 0;
        _healthSuspectLastTick = false;
        InstallHook(vk, hotkey);

        // 健康检查定时器的生命周期独立于钩子实例：重装失败也不销毁它，靠它按退避重试（R3-5）。
        if (_healthTimer is null)
        {
            _healthTimer = new System.Windows.Forms.Timer { Interval = HealthCheckIntervalMs };
            _healthTimer.Tick += (_, _) => CheckHookHealth();
        }
        _healthTimer.Start();
        ReportHealth(true);

        AppLog.Info("hotkey", $"热键监听启动: {hotkey.DisplayString}");
    }

    /// <summary>安装（或重装）低级键盘钩子。失败抛 <see cref="HotkeyServiceException"/>。</summary>
    private void InstallHook(int vk, HotkeyConfig hotkey)
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        if (_mouseHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
            _mouseProc = null;
        }

        _stateMachine = hotkey.IsModifierOnly
            ? HotkeyStateMachine.ForModifierOnly(vk)
            : new HotkeyStateMachine(vk, BuildExpected(hotkey.Modifiers));
        _stateMachine.AcceptsCancelWhenInactive = _acceptsCancelWhenInactive;
        _targetVk = vk;
        _needsMenuBarMask = hotkey.IsModifierOnly && ModifierHotkeys.NeedsMenuBarMask(hotkey.Key);
        // 只在安装这一刻取一次 GetAsyncKeyState 快照播种物理修饰键（区别于"在事件里推断释放"）。
        SeedPhysicalModifierState();

        _proc = HookCallback;
        var moduleHandle = GetModuleHandleW(null);
        _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, moduleHandle, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            _proc = null;
            throw new HotkeyServiceException(L10n.F("安装键盘钩子失败 (Win32 error {0})", err));
        }
        _lastHookActivityTick = Environment.TickCount;

        if (hotkey.IsModifierOnly)
        {
            _mouseProc = MouseHookCallback;
            _mouseHookHandle = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, GetModuleHandleW(null), 0);
            if (_mouseHookHandle == IntPtr.Zero)
            {
                // 不致命：只是「按住右 Ctrl 再点鼠标」不会作废本次录音。
                AppLog.Warn("hotkey", $"安装鼠标钩子失败 (Win32 error {Marshal.GetLastWin32Error()})，Ctrl+点击将不会作废录音");
                _mouseProc = null;
            }
        }
    }

    private void ReportHealth(bool healthy)
    {
        if (healthy == _lastReportedHealthy) return;
        _lastReportedHealthy = healthy;
        var cb = OnHealthChanged;
        if (cb is not null) UiDispatcher.PostAsync(() => cb(healthy));
    }

    public void Stop()
    {
        _healthTimer?.Stop();
        _healthTimer?.Dispose();
        _healthTimer = null;

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _proc = null;
        if (_mouseHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
        }
        _mouseProc = null;
        _hotkey = null;
        _stateMachine = null;
        _lastReportedHealthy = true;
    }

    /// <summary>
    /// 钩子存活性自愈（R3-5）：
    /// <list type="bullet">
    /// <item>句柄已丢失（上次重装失败）→ 按退避重试。</item>
    /// <item>句柄仍在但长时间零回调、且期间确有输入 → 连续两次可疑才重装
    ///   （<c>GetLastInputInfo</c> 含鼠标，无法区分键鼠，取保守窗口 + 双次确认降低误判）。</item>
    /// </list>
    /// 重装失败不销毁定时器，下个周期继续按退避重试，并把不可用状态报给上层。
    /// </summary>
    private void CheckHookHealth()
    {
        var hotkey = _hotkey;
        if (hotkey is null) return; // 已 Stop

        var vk = MapKeyToVk(hotkey.Key);
        if (vk == 0) return;

        bool hookMissing = _hookHandle == IntPtr.Zero;
        bool suspect = false;

        if (!hookMissing)
        {
            var lastInput = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lastInput)) { _healthSuspectLastTick = false; return; }

            var now = Environment.TickCount;
            var sinceUserInputMs = unchecked((uint)now - lastInput.dwTime);
            var sinceHookActivityMs = unchecked((uint)(now - _lastHookActivityTick));

            bool suspectNow = sinceUserInputMs < HealthCheckIntervalMs && sinceHookActivityMs >= SilenceSuspectMs;
            suspect = suspectNow && _healthSuspectLastTick;
            _healthSuspectLastTick = suspectNow;
            if (!suspect) return;
        }

        // 退避：连续失败越多，下一次尝试间隔越长（上限 5 分钟）。
        var backoffMs = Math.Min(HealthCheckIntervalMs << Math.Min(_recoveryFailures, 4), 5 * 60_000);
        if (_recoveryFailures > 0 && unchecked((uint)(Environment.TickCount - _lastRecoveryAttemptTick)) < backoffMs)
        {
            return;
        }
        _lastRecoveryAttemptTick = Environment.TickCount;

        AppLog.Warn("hotkey", hookMissing ? "键盘钩子句柄已丢失，尝试重新安装" : "键盘钩子长时间无回调，尝试重新安装");

        bool wasEngaged = _stateMachine?.IsEngaged ?? false;
        try
        {
            InstallHook(vk, hotkey);
            _recoveryFailures = 0;
            _healthSuspectLastTick = false;
            AppLog.Info("hotkey", "全局键盘钩子已重新安装");
            // 协调正在进行的听写正常收尾：新钩子不会再送出旧那次按下的 OnRelease。
            if (wasEngaged)
            {
                var cancel = OnCancel;
                if (cancel is not null) UiDispatcher.PostAsync(() => cancel());
            }
            ReportHealth(true);
        }
        catch (Exception ex)
        {
            _recoveryFailures++;
            AppLog.Error("hotkey", $"重新安装全局键盘钩子失败（第 {_recoveryFailures} 次）", ex);
            ReportHealth(false);
        }
    }

    public void Dispose() => Stop();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _hotkey is null)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        _lastHookActivityTick = Environment.TickCount;

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
        var (action, consume) = _stateMachine?.OnKey(vk, isKeyDown) ?? (HotkeyAction.None, false);

        switch (action)
        {
            case HotkeyAction.Press:
                // 右 Alt 的干净手势刚开始：在 Alt down 送达前台应用之前/之后紧跟一对哑键，
                // 让系统把这次 Alt 记为"按住期间出现过别的键"，松开时不进入菜单栏模式。
                // 必须在钩子回调里同步注入，保证排在用户松开 Alt 之前（见 SendMenuBarMask 注释）。
                if (_needsMenuBarMask) SendMenuBarMask();
                UiDispatcher.PostAsync(() => OnPress?.Invoke());
                break;
            case HotkeyAction.Release:
                UiDispatcher.PostAsync(() => OnRelease?.Invoke());
                break;
            case HotkeyAction.Cancel:
                UiDispatcher.PostAsync(() => OnCancel?.Invoke());
                break;
            case HotkeyAction.GestureCancel:
                UiDispatcher.PostAsync(() => OnGestureCancelled?.Invoke());
                break;
        }

        // 被接管的主键 down/repeat/up 与生效的 Esc 一律消费掉，不再下发给前台应用；
        // 修饰键事件必须继续传递，否则会破坏其他应用看到的修饰键 down/up 配对（R2-1）。
        return consume ? (IntPtr)1 : CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>
    /// 为右 Alt 的干净单击注入掩码：紧跟在（已放行的）Alt down 之后补一对哑键
    /// （VK 0xFF down + up）。Windows 在<b>松开</b>孤立 Alt 时才激活前台窗口的菜单栏，
    /// 且"按住期间出现过其他键"即视为组合用法、不激活——哑键正是利用这一点，它本身
    /// 不映射任何字符、不影响修饰键状态。
    /// 不采用"吞掉 Alt 的 keyup"：低级钩子吞掉的事件不会更新 GetAsyncKeyState 的异步
    /// 键状态表（2026-10-08 本机实测：放行 down、吞 up 会把 VK_MENU 永久卡在按下），
    /// TextInsertionService 的修饰键检查与其后所有 Ctrl+V 都会被误判破坏。掩码方案对
    /// 按键流零干预：AltGr 组合字符、右 Alt+Tab 全部照常工作。GUI_INMENUMODE 实测证据
    /// 见 <see cref="ModifierHotkeys"/> 的类注释。失败只记日志：掩码缺失的后果退化为
    /// 松开右 Alt 时菜单栏被激活（老问题），热键与听写本身不受影响。
    /// </summary>
    private static void SendMenuBarMask()
    {
        var inputs = new[]
        {
            MakeMaskInput(keyUp: false),
            MakeMaskInput(keyUp: true),
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            AppLog.Warn("hotkey", $"注入右 Alt 菜单栏掩码失败 (Win32 error {Marshal.GetLastWin32Error()})，本次松开右 Alt 可能激活菜单栏");
        }
    }

    /// <summary>哑键没有扫描码映射（MapVirtualKeyW 返回 0），连原始输入层都最安静。</summary>
    private static INPUT MakeMaskInput(bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = VK_NOOP_MASK,
                wScan = (ushort)MapVirtualKeyW(VK_NOOP_MASK, 0),
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN or WM_MOUSEWHEEL or WM_MOUSEHWHEEL
                && _stateMachine?.OnMouseButton() == HotkeyAction.GestureCancel)
            {
                UiDispatcher.PostAsync(() => OnGestureCancelled?.Invoke());
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static HotkeyModifiers BuildExpected(List<string> modifiers)
    {
        var mask = HotkeyModifiers.None;
        foreach (var m in modifiers)
        {
            switch (m.Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mask |= HotkeyModifiers.Ctrl;
                    break;
                case "alt":
                case "option":
                    mask |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    mask |= HotkeyModifiers.Shift;
                    break;
                case "win":
                case "win_l":
                case "win_r":
                case "super":
                case "command":
                case "cmd":
                    mask |= HotkeyModifiers.Win;
                    break;
            }
        }
        return mask;
    }

    /// <summary>安装钩子这一刻取一次系统快照，播种已按住的修饰键（应对自愈重装时用户仍按着键）。</summary>
    private void SeedPhysicalModifierState()
    {
        if (_stateMachine is null) return;
        foreach (var vk in HotkeyStateMachine.AllModifierVks)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                _stateMachine.SeedModifier(vk);
            }
        }
    }

    /// <summary>供 <see cref="Core.AppConfig.Validated"/> 复用：主键是否在支持表内。</summary>
    public static bool IsSupportedKey(string key) => MapKeyToVk(key) != 0;

    /// <summary>命名键的规范名 ↔ 虚拟键码。规范名是设置页录制时写进配置的名字；
    /// <see cref="MapKeyToVk"/> 另外接受几个别名（return / escape / page_up 等）。</summary>
    private static readonly (string Name, int Vk)[] NamedKeys =
    {
        ("space", 0x20), ("tab", 0x09), ("enter", 0x0D), ("esc", 0x1B), ("backspace", 0x08),
        ("insert", 0x2D), ("delete", 0x2E), ("home", 0x24), ("end", 0x23), ("pageup", 0x21), ("pagedown", 0x22),
        ("up", 0x26), ("down", 0x28), ("left", 0x25), ("right", 0x27),
        ("f1", 0x70), ("f2", 0x71), ("f3", 0x72), ("f4", 0x73), ("f5", 0x74), ("f6", 0x75),
        ("f7", 0x76), ("f8", 0x77), ("f9", 0x78), ("f10", 0x79), ("f11", 0x7A), ("f12", 0x7B),
        ("capslock", 0x14),
    };

    private static readonly (string Alias, string Canonical)[] KeyAliases =
    {
        ("return", "enter"), ("escape", "esc"), ("page_up", "pageup"), ("page_down", "pagedown"), ("caps_lock", "capslock"),
    };

    /// <summary>虚拟键码 → 配置里的规范键名；不支持的键返回 null。设置页「录制热键」用它把按下的键翻译成配置值。</summary>
    public static string? NameForVirtualKey(int vk)
    {
        if (vk is >= 'A' and <= 'Z') return ((char)vk).ToString().ToLowerInvariant();
        if (vk is >= '0' and <= '9') return ((char)vk).ToString();
        foreach (var (name, code) in NamedKeys)
        {
            if (code == vk) return name;
        }
        return null;
    }

    private static int MapKeyToVk(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        var k = key.Trim().ToLowerInvariant();

        // 单独修饰键
        if (ModifierHotkeys.IsModifierOnlyKey(k)) return ModifierHotkeys.VirtualKey(k);

        // 单字符
        if (k.Length == 1)
        {
            var ch = char.ToUpperInvariant(k[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9') return ch;
        }

        foreach (var (alias, canonical) in KeyAliases)
        {
            if (k == alias) { k = canonical; break; }
        }
        foreach (var (name, code) in NamedKeys)
        {
            if (name == k) return code;
        }
        return 0;
    }
}
