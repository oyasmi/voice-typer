using VoiceTyper.Asr;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.App;

/// <summary>就绪判定的输入快照：模型准备、暂停与听写这几条线此刻各自的事实。</summary>
/// <param name="Dictating">是否有一次听写正在进行（控制器的 <c>HasActiveDictation</c>）。
/// 是否打断听写、是否保留听写中的显示状态都以它为准，不看会被错误提示覆盖的显示状态。</param>
/// <param name="Current">当前显示状态；听写进行中时原样保留。</param>
internal readonly record struct ReadinessSnapshot(
    AsrState Asr,
    string? AsrFailureMessage,
    bool Paused,
    bool Downloading,
    double DownloadProgress,
    string? DownloadError,
    bool PreloadOnLaunch,
    bool AutoDownloadAttempted,
    bool Dictating,
    AppStateInfo Current);

/// <summary>判定后要对识别引擎做的事。</summary>
internal enum EngineStep { None, Preload, PrepareWithoutLoading }

/// <summary>热键监听的门禁：<see cref="Block"/> = 照常监听但按下只给提示，<see cref="Release"/> = 解除门禁可以听写。</summary>
internal enum HotkeyGate { Unchanged, Block, Release }

/// <param name="State">应当显示的状态。</param>
/// <param name="StartAutomaticDownload">本进程第一次发现缺模型：先自动下载，再按下载中的事实重新判定。</param>
/// <param name="StopDictation">模型已不可用，进行中的听写必须拆掉，否则会留下一段永远收不了尾的会话。</param>
/// <param name="PresentModelGuidance">把设置窗口的语音模型页摆到用户面前（同一未就绪期内只抢一次焦点，由协调器去重）。</param>
internal readonly record struct ReadinessDecision(
    AppStateInfo State,
    HotkeyGate Gate,
    EngineStep Engine = EngineStep.None,
    bool StartAutomaticDownload = false,
    bool StopDictation = false,
    bool HideHud = false,
    bool PresentModelGuidance = false);

/// <summary>
/// 就绪状态机的纯判定部分（对应 macOS <c>AppCoordinator.computeTargetState</c>）：给定快照，算出显示状态、
/// 热键门禁与要执行的动作。动作由 <see cref="AppCoordinator"/> 执行；这里不碰任何服务，便于单测。
/// </summary>
internal static class ReadinessPolicy
{
    public static ReadinessDecision Decide(in ReadinessSnapshot s)
    {
        // 模型准备与暂停完全独立：暂停听写也不阻断首次缺模型时的自动下载。
        var autoDownload = !s.AutoDownloadAttempted && !s.Downloading && s.Asr == AsrState.ModelMissing;
        if (s.Paused) return new(AppStateInfo.Paused, HotkeyGate.Unchanged, StartAutomaticDownload: autoDownload);
        if (s.Downloading) return new(AppStateInfo.DownloadingModelWith(s.DownloadProgress), HotkeyGate.Block);

        return s.Asr switch
        {
            // 「启动时预加载」关闭时不借机把引擎拉进内存，只确认文件在不在。
            AsrState.Unloaded => s.PreloadOnLaunch
                ? new(AppStateInfo.ModelLoading, HotkeyGate.Block, EngineStep.Preload)
                : new(AppStateInfo.ModelLoading, HotkeyGate.Unchanged, EngineStep.PrepareWithoutLoading),
            // 空闲卸载后首次按热键：引擎一边加载、录音一边进行（会话先攒音频，就绪后回灌），
            // 不能把进行中的听写显示覆盖成"未就绪"，也不必门禁。
            AsrState.Loading => s.Dictating
                ? new(s.Current, HotkeyGate.Unchanged)
                : new(AppStateInfo.ModelLoading, HotkeyGate.Block),
            AsrState.ModelMissing => new(AppStateInfo.ModelMissing, HotkeyGate.Block,
                StartAutomaticDownload: autoDownload, StopDictation: s.Dictating, HideHud: true, PresentModelGuidance: true),
            AsrState.Failed => new(AppStateInfo.ErrorWith(s.AsrFailureMessage ?? L10n.T("未知错误")), HotkeyGate.Block,
                StopDictation: s.Dictating, HideHud: true),
            // Ready 与空闲卸载（SuspendedForIdle）都算就绪：卸载后的重新加载由下次按热键按需触发。
            _ => new(s.Dictating ? s.Current : AppStateInfo.Idle, HotkeyGate.Release),
        };
    }

    /// <summary>
    /// 未就绪的原因文案：具体说出缺什么，而不是笼统的"无法使用"。下载失败后的退避等待期优先给
    /// 带重试进度的说明，而不是让用户以为要自己去处理。就绪时为 null。
    /// </summary>
    public static string? BlockedReason(in ReadinessSnapshot s)
    {
        if (s.Downloading)
        {
            return L10n.F("语音模型正在下载（{0}%），完成后即可开始听写。", (int)(s.DownloadProgress * 100));
        }
        return s.Asr switch
        {
            AsrState.ModelMissing => s.DownloadError ?? L10n.T("语音模型还没有下载，无法开始听写。"),
            AsrState.Failed => L10n.F("语音模型加载失败：{0}", s.AsrFailureMessage ?? L10n.T("未知错误")),
            AsrState.Loading or AsrState.Unloaded => L10n.T("识别引擎正在加载，请稍候再试。"),
            _ => null,
        };
    }
}
