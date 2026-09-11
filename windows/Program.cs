using System;
using System.Threading;
using System.Windows.Forms;
using VoiceTyper.App;
using VoiceTyper.Support;

namespace VoiceTyper;

internal static class Program
{
    // 会话范围（Local\）而非 Global\：不跨登录会话互斥，同机多用户可各自独立运行（R4-1）。
    private const string MutexName = "Local\\VoiceTyper.Unified.SingleInstance";

    [STAThread]
    private static int Main()
    {
        // 单例保护；与 client_windows_native（VoiceTyperClient）用不同的 mutex 名，
        // 两个 App 可以同时装但不建议同时跑（见 windows/DESIGN.md §11 "两个 App 同时安装"）。
        // 托盘菜单与设置窗口都是带着文案构造出来的，界面语言必须在这之前定下来。
        L10n.Bootstrap();

        using var mutex = TryCreateSingleInstanceMutex(out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                L10n.T("VoiceTyper 已经在运行（请检查系统托盘）。"),
                "VoiceTyper",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return 0;
        }

        try
        {
            AppLog.Initialize();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VoiceTyper] AppLog.Initialize 失败: {ex.Message}");
        }

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch { /* 已在 app.manifest 中声明，这里失败也无所谓 */ }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        WindowsFormsSynchronizationContext.AutoInstall = true;
        if (SynchronizationContext.Current is null)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
        UiDispatcher.Install();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppLog.Error("app", "未处理的 UI 线程异常", e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) AppLog.Error("app", "未处理的后台线程异常", ex);
        };

        try
        {
            Application.Run(new TrayApplicationContext());
        }
        catch (Exception ex)
        {
            AppLog.Error("app", "Application.Run 异常退出", ex);
            try { MessageBox.Show(L10n.F("VoiceTyper 启动失败：\n\n{0}", ex.Message), "VoiceTyper", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch { }
            return 1;
        }

        return 0;
    }

    /// <summary>创建会话范围单实例互斥体。命名冲突 / 权限不足时退化为"不做单例保护"
    /// 而非拒绝启动（R4-1）。</summary>
    private static Mutex? TryCreateSingleInstanceMutex(out bool createdNew)
    {
        try
        {
            return new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
            or System.Threading.WaitHandleCannotBeOpenedException
            or System.IO.IOException)
        {
            Console.Error.WriteLine($"[VoiceTyper] 单实例互斥体创建失败，跳过单例保护: {ex.Message}");
            createdNew = true;
            return null;
        }
    }
}
