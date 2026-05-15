namespace VoiceTyper.Core;

internal enum AppState
{
    Booting,
    SetupRequired,
    Idle,
    Recording,
    Recognizing,
    Inserting,
    Error,
}

internal readonly record struct AppStateInfo(AppState State, string? Message = null)
{
    public string MenuTitle => State switch
    {
        AppState.Booting => "启动中",
        AppState.SetupRequired => "需要完成设置",
        AppState.Idle => "就绪",
        AppState.Recording => "录音中...",
        AppState.Recognizing => "识别中...",
        AppState.Inserting => "输入中...",
        AppState.Error => string.IsNullOrEmpty(Message) ? "错误" : $"错误：{Message}",
        _ => State.ToString(),
    };

    public static AppStateInfo Idle => new(AppState.Idle);
    public static AppStateInfo Booting => new(AppState.Booting);
    public static AppStateInfo Recording => new(AppState.Recording);
    public static AppStateInfo Recognizing => new(AppState.Recognizing);
    public static AppStateInfo Inserting => new(AppState.Inserting);
    public static AppStateInfo SetupRequired => new(AppState.SetupRequired);
    public static AppStateInfo ErrorWith(string message) => new(AppState.Error, message);
}
