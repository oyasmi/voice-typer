namespace VoiceTyper.Core;

internal enum AppState
{
    Booting,
    SetupRequired,
    /// <summary>权限齐全，但本地识别模型尚未就绪：本机既无内置也无侧载模型副本。</summary>
    ModelMissing,
    /// <summary>正在下载模型；进度见 <see cref="AppStateInfo.Progress"/>（0…1）。</summary>
    DownloadingModel,
    /// <summary>模型文件已就绪，正在加载进内存（ONNX session 构建等）。</summary>
    ModelLoading,
    Idle,
    Recording,
    Recognizing,
    Inserting,
    Error,
}

internal readonly record struct AppStateInfo(AppState State, string? Message = null, double Progress = 0)
{
    public string MenuTitle => State switch
    {
        AppState.Booting => "启动中",
        AppState.SetupRequired => "需要完成设置",
        AppState.ModelMissing => "需要下载语音模型",
        AppState.DownloadingModel => $"下载模型 {(int)(Progress * 100)}%",
        AppState.ModelLoading => "模型加载中…",
        AppState.Idle => "就绪",
        AppState.Recording => "录音中...",
        AppState.Recognizing => "识别中...",
        AppState.Inserting => "输入中...",
        AppState.Error => string.IsNullOrEmpty(Message) ? "错误" : $"错误：{Message}",
        _ => State.ToString(),
    };

    public static AppStateInfo Booting => new(AppState.Booting);
    public static AppStateInfo SetupRequired => new(AppState.SetupRequired);
    public static AppStateInfo ModelMissing => new(AppState.ModelMissing);
    public static AppStateInfo DownloadingModelWith(double progress) => new(AppState.DownloadingModel, Progress: progress);
    public static AppStateInfo ModelLoading => new(AppState.ModelLoading);
    public static AppStateInfo Idle => new(AppState.Idle);
    public static AppStateInfo Recording => new(AppState.Recording);
    public static AppStateInfo Recognizing => new(AppState.Recognizing);
    public static AppStateInfo Inserting => new(AppState.Inserting);
    public static AppStateInfo ErrorWith(string message) => new(AppState.Error, message);
}
