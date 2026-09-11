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
    /// <summary>录音中按下 Esc 主动取消。在 UI 线程触发。</summary>
    public Action? OnCancel;
    /// <summary>钩子健康状态变化（R3-5）：false = 钩子已失效且自愈失败中（应向用户显示不可用），
    /// true = （重新）安装成功。在 UI 线程触发。</summary>
    public Action<bool>? OnHealthChanged;

    /// <summary>
    /// 钩子存活性自愈检查周期。<c>WH_KEYBOARD_LL</c> 的回调若超过
    /// <c>HKEY_CURRENT_USER\Control Panel\Desktop\LowLevelHooksTimeout</c> 未返回，系统会
    /// 直接把钩子卸掉且不通知——现象与 macOS 的 tapDisabledByTimeout 一致：热键突然永久失效。
    /// 具体超时毫秒数随 Windows 版本而变，未在真机上核实，不在此写死。回调始终立即返回、
    /// 业务异步投递（见 <see cref="HookCallback"/> 使用 <see cref="UiDispatcher.PostAsync"/>）。
    /// </summary>
    private const int HealthCheckIntervalMs = 30_000;

    private IntPtr _hookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;  // 保活，避免 GC
    private HotkeyConfig? _hotkey;
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

        _stateMachine = new HotkeyStateMachine(vk, BuildExpected(hotkey.Modifiers));
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
                UiDispatcher.PostAsync(() => OnPress?.Invoke());
                break;
            case HotkeyAction.Release:
                UiDispatcher.PostAsync(() => OnRelease?.Invoke());
                break;
            case HotkeyAction.Cancel:
                UiDispatcher.PostAsync(() => OnCancel?.Invoke());
                break;
        }

        // 被接管的主键 down/repeat/up 与生效的 Esc 一律消费掉，不再下发给前台应用；
        // 修饰键事件必须继续传递，否则会破坏其他应用看到的修饰键 down/up 配对（R2-1）。
        return consume ? (IntPtr)1 : CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
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
