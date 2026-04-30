using System;
using System.IO.Pipes;
using System.Threading;
using System.Windows.Forms;
using VoiceTyper.App;

namespace VoiceTyper;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // 单实例检查（全局 Mutex 防重复启动）
        using var mutex = new Mutex(true, @"Global\VoiceTyper.SingleInstance", out bool created);
        if (!created)
        {
            // 已有实例运行中 → 通知其打开设置窗口
            ActivateExistingInstance();
            return;
        }

        ApplicationConfiguration.Initialize();
        using var coordinator = new AppCoordinator();
        Application.Run(coordinator);
    }

    /// <summary>通过 Named Pipe 通知已运行的实例显示设置窗口</summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", "VoiceTyper.Activate", PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            writer.Write("activate");
            writer.Flush();
        }
        catch
        {
            // 连接失败则静默退出
        }
    }
}
