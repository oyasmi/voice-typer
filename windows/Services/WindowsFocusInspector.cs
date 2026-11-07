using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using VoiceTyper.Core;
using VoiceTyper.Support;
using static VoiceTyper.Support.NativeMethods;

namespace VoiceTyper.Services;

/// <summary>只查询密码属性，不读取控件文本。UIA 跨进程调用放在 MTA 后台线程，UI 最多异步等 200ms。</summary>
internal sealed class WindowsFocusInspector : IFocusInspector
{
    // 跨控制器共用门禁：有缺陷的 UIA provider 可能永不返回，不能每次热键都再泄漏一条线程。
    private static int _queryRunning;
    private readonly Func<ForegroundTarget, FocusSafety> _query;
    private readonly TimeSpan _timeout;

    internal WindowsFocusInspector(Func<ForegroundTarget, FocusSafety>? query = null, TimeSpan? timeout = null)
    {
        _query = query ?? QueryFocus;
        _timeout = timeout ?? TimeSpan.FromMilliseconds(200);
    }

    public async Task<FocusSafety> InspectAsync(ForegroundTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _queryRunning, 1, 0) != 0) return FocusSafety.Unknown;
        var query = Task.Run(() =>
        {
            try
            {
                return _query(target);
            }
            catch (Exception ex)
            {
                AppLog.Debug("focus", $"密码框检查不可用：{ex.GetType().Name}");
                return FocusSafety.Unknown;
            }
            finally { Interlocked.Exchange(ref _queryRunning, 0); }
        }, CancellationToken.None);
        try { return await query.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            AppLog.Debug("focus", "密码框检查超时");
            return FocusSafety.Unknown;
        }
    }

    private static FocusSafety QueryFocus(ForegroundTarget target)
    {
        if (GetForegroundWindow() != target.Window) return FocusSafety.Unknown;
        var element = AutomationElement.FocusedElement;
        if (element is null || element.Current.ProcessId != (int)target.ProcessId) return FocusSafety.Unknown;
        var value = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, ignoreDefaultValue: true);
        if (GetForegroundWindow() != target.Window) return FocusSafety.Unknown;
        return value is bool password ? (password ? FocusSafety.Password : FocusSafety.Normal) : FocusSafety.Unknown;
    }
}
