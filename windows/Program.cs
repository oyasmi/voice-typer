using System;
using System.Threading;
using System.Windows.Forms;
using VoiceTyper.App;
using VoiceTyper.Support;

namespace VoiceTyper;

internal static class Program
{
    private const string MutexName = "Global\\VoiceTyper.Unified.SingleInstance";

    [STAThread]
    private static int Main()
    {
        // 单例保护；与 client_windows_native（VoiceTyperClient）用不同的 mutex 名，
        // 两个 App 可以同时装但不建议同时跑（见 windows/DESIGN.md §11 "两个 App 同时安装"）。
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "VoiceTyper 已经在运行（请检查系统托盘）。",
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
            try { MessageBox.Show($"VoiceTyper 启动失败：\n\n{ex.Message}", "VoiceTyper", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch { }
            return 1;
        }

        return 0;
    }
}
