namespace VoiceTyper.Core;

/// <summary>应用状态枚举，与 macOS Swift 版完全一致</summary>
internal enum AppState
{
    /// <summary>启动中</summary>
    Booting,
    /// <summary>需要配置</summary>
    SetupRequired,
    /// <summary>就绪</summary>
    Idle,
    /// <summary>录音中</summary>
    Recording,
    /// <summary>识别中</summary>
    Recognizing,
    /// <summary>正在输入</summary>
    Inserting,
    /// <summary>错误</summary>
    Error
}

internal static class AppStateExtensions
{
    /// <summary>状态的菜单显示文字</summary>
    public static string MenuTitle(this AppState state) => state switch
    {
        AppState.Booting => "启动中...",
        AppState.SetupRequired => "需要配置",
        AppState.Idle => "就绪",
        AppState.Recording => "录音中...",
        AppState.Recognizing => "识别中...",
        AppState.Inserting => "正在输入...",
        AppState.Error => "错误",
        _ => "未知"
    };
}
