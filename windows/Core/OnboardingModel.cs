using System;
using System.IO;
using VoiceTyper.Asr;
using VoiceTyper.Support;

namespace VoiceTyper.Core;

/// <summary>
/// 首启引导是否已完成的持久化记录。
///
/// 刻意放在 <c>%LOCALAPPDATA%\VoiceTyper\onboarding.txt</c> 而不是 <c>config.yaml</c>：这不是用户配置，
/// 而是应用自己的一次性状态，不该出现在配置文件里让用户去看、去改（也不随域漫游）。
/// </summary>
internal static class OnboardingRecord
{
    /// <summary>引导流程本身的版本。将来若引导步骤发生实质性变化（新增了必须让老用户也看到的内容），
    /// 把这个数字加一即可让所有人再走一次，而不需要另造一个开关。</summary>
    public const int CurrentFlowVersion = 1;

    public static string DefaultPath => Path.Combine(AppConstants.LocalDataDirectory, "onboarding.txt");

    public static bool IsCompleted(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var version) && version >= CurrentFlowVersion;
        }
        catch
        {
            // 读不出来按"没走过"处理：多看一次引导的代价远小于让新用户错过它。
            return false;
        }
    }

    public static void MarkCompleted(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, CurrentFlowVersion.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Warn("onboarding", $"记录引导完成状态失败（下次启动会再次出现）: {ex.Message}");
        }
    }
}

internal enum OnboardingStep { Welcome = 0, Microphone = 1, Model = 2, Trial = 3 }

/// <summary>「试一试」这一步的实时结果。</summary>
internal enum OnboardingTrialKind { Idle, Recording, Recognizing, Succeeded, NoSpeech, Cancelled, Failed, Blocked }

internal readonly record struct OnboardingTrialState(OnboardingTrialKind Kind, string? Detail = null)
{
    public static OnboardingTrialState Idle => new(OnboardingTrialKind.Idle);
}

/// <summary>
/// 引导窗口把听写生命周期需要的信息压缩成这几个事件，由协调器推送进来。
/// 引导层不直接持有控制器，避免出现第二条驱动听写的路径。
/// </summary>
internal abstract record OnboardingDictationEvent
{
    public sealed record RecordingStarted : OnboardingDictationEvent;
    public sealed record Level(float Value) : OnboardingDictationEvent;
    public sealed record Recognizing : OnboardingDictationEvent;
    public sealed record Inserted(string Text) : OnboardingDictationEvent;
    public sealed record EmptyResult : OnboardingDictationEvent;
    public sealed record Cancelled : OnboardingDictationEvent;
    public sealed record Failed(string Message) : OnboardingDictationEvent;
    public sealed record Blocked(string Reason) : OnboardingDictationEvent;
}

/// <summary>
/// 首启引导的流程与试用状态（纯逻辑，无 UI，可单测）。对应 macOS 的 <c>OnboardingViewModel</c>。
///
/// 「试一试」是整个引导的重点：麦克风可用 + 模型下载完成<b>并不等于能用</b>。真实链路是
/// 「热键被捕获 → 麦克风出声 → 识别出文本 → 文本插进目标应用」，其中任何一环坏掉，用户看到的现象
/// 都是同一个——"按了没反应"。这一步用一次真实听写把整条链跑通，并在失败时指出断在哪一环。
/// </summary>
internal sealed class OnboardingModel
{
    public OnboardingStep Step { get; private set; } = OnboardingStep.Welcome;
    public MicProbeResult Mic { get; set; } = MicProbeResult.Unknown;
    public AsrState AsrState { get; set; } = AsrState.Unloaded;
    public double? DownloadProgress { get; set; }
    public string? DownloadError { get; set; }
    public string HotkeyDisplay { get; set; } = "Ctrl+F2";
    public HotkeyMode HotkeyMode { get; set; } = HotkeyMode.Hold;
    public OnboardingTrialState Trial { get; private set; } = OnboardingTrialState.Idle;

    /// <summary>本次试用录音里出现过的最大电平。用于在识别结果为空时区分"没出声"与"识别不出"。</summary>
    private float _trialPeakLevel;

    public Action? OnStepChanged;
    public Action? OnRefreshStatus;
    public Action? OnFinish;

    public bool IsMicReady => Mic is MicProbeResult.Available or MicProbeResult.Silent;
    public bool IsModelReady => AsrState is AsrState.Ready or AsrState.SuspendedForIdle;
    public bool CanRunTrial => IsMicReady && IsModelReady;

    public string? TrialBlockingHint
    {
        get
        {
            if (CanRunTrial) return null;
            return !IsMicReady
                ? L10n.T("麦克风还不可用，请回到上一步处理。")
                : L10n.T("语音模型还没准备好，请等待或回到上一步重试下载。");
        }
    }

    public bool CanGoBack => Step != OnboardingStep.Welcome;

    public string PrimaryActionTitle => Step switch
    {
        OnboardingStep.Trial => Trial.Kind == OnboardingTrialKind.Succeeded ? L10n.T("完成") : L10n.T("先跳过"),
        _ => L10n.T("下一步"),
    };

    public void GoBack()
    {
        if (Step == OnboardingStep.Welcome) return;
        Step = (OnboardingStep)((int)Step - 1);
        AfterStepChange();
    }

    public void GoForward()
    {
        if (Step == OnboardingStep.Trial)
        {
            Finish();
            return;
        }
        Step = (OnboardingStep)((int)Step + 1);
        AfterStepChange();
    }

    public void Finish()
    {
        OnboardingRecord.MarkCompleted();
        OnFinish?.Invoke();
    }

    /// <summary>每次切换步骤都重新探测一次：用户很可能刚从系统设置切回来。</summary>
    private void AfterStepChange()
    {
        ResetTrial();
        OnStepChanged?.Invoke();
        OnRefreshStatus?.Invoke();
    }

    public void ResetTrial()
    {
        Trial = OnboardingTrialState.Idle;
        _trialPeakLevel = 0;
    }

    /// <returns>试用状态是否发生变化（调用方据此决定是否刷新界面）。</returns>
    public bool Handle(OnboardingDictationEvent dictationEvent)
    {
        // 只有停在「试一试」这一步时才把听写事件解释成试用结果；用户在别的步骤顺手按了热键
        // （完全合法）不应该污染试用状态。
        if (Step != OnboardingStep.Trial) return false;

        switch (dictationEvent)
        {
            case OnboardingDictationEvent.RecordingStarted:
                _trialPeakLevel = 0;
                Trial = new(OnboardingTrialKind.Recording);
                return true;
            case OnboardingDictationEvent.Level level:
                if (Trial.Kind != OnboardingTrialKind.Recording) return false;
                _trialPeakLevel = Math.Max(_trialPeakLevel, level.Value);
                return false;
            case OnboardingDictationEvent.Recognizing:
                Trial = new(OnboardingTrialKind.Recognizing);
                return true;
            case OnboardingDictationEvent.Inserted inserted:
                Trial = new(OnboardingTrialKind.Succeeded, inserted.Text);
                return true;
            case OnboardingDictationEvent.EmptyResult:
                Trial = _trialPeakLevel < AppConstants.SilenceRmsThreshold
                    ? new(OnboardingTrialKind.NoSpeech)
                    : new(OnboardingTrialKind.Failed, L10n.T("采到了声音，但没有识别出文字。请靠近麦克风、放慢一点再试一次。"));
                return true;
            case OnboardingDictationEvent.Cancelled:
                Trial = new(OnboardingTrialKind.Cancelled);
                return true;
            case OnboardingDictationEvent.Failed failed:
                Trial = new(OnboardingTrialKind.Failed, failed.Message);
                return true;
            case OnboardingDictationEvent.Blocked blocked:
                Trial = new(OnboardingTrialKind.Blocked, blocked.Reason);
                return true;
            default:
                return false;
        }
    }

    /// <summary>试用结果的标题 / 说明。集中在这里，避免界面里散落 switch。</summary>
    public (string Title, string Detail)? TrialSummary => Trial.Kind switch
    {
        OnboardingTrialKind.Idle => null,
        OnboardingTrialKind.Recording => (L10n.T("正在录音…"),
            L10n.F("说一句话，然后{0}。", HotkeyMode == HotkeyMode.Hold ? L10n.T("松开热键") : L10n.T("再按一次热键"))),
        OnboardingTrialKind.Recognizing => (L10n.T("识别中…"), L10n.T("本地引擎正在处理这段音频。")),
        OnboardingTrialKind.Succeeded => (L10n.T("全部跑通了"), L10n.F("已插入：{0}", Trial.Detail ?? "")),
        OnboardingTrialKind.NoSpeech => (L10n.T("没有采到声音"),
            L10n.T("热键与识别链路是通的，但这段录音几乎是静音。请检查麦克风是否被静音、「设置 → 系统 → 声音 → 输入」里选中的设备是否正确，然后再试一次。")),
        OnboardingTrialKind.Cancelled => (L10n.T("已取消"), L10n.T("按 Esc 可以随时取消，识别结果不会被插入。再试一次吧。")),
        OnboardingTrialKind.Failed => (L10n.T("没有成功"), Trial.Detail ?? ""),
        OnboardingTrialKind.Blocked => (L10n.T("还不能开始"), Trial.Detail ?? ""),
        _ => null,
    };
}
