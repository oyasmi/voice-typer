using VoiceTyper.App;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// 就绪判定（<see cref="ReadinessPolicy"/>）：显示状态、热键门禁与要执行的动作。历轮评审的就绪类缺陷
/// （WT-01/WT-03/VW-11/VW-13）都出在这一层，这里把每个分支钉住。文案随界面语言变化，只断言结构与关键片段。
/// </summary>
public class ReadinessPolicyTests
{
    private static ReadinessSnapshot Snapshot(
        AsrState asr = AsrState.Ready, bool paused = false, bool downloading = false, double progress = 0,
        string? downloadError = null, bool preload = true, bool autoDownloadAttempted = true,
        bool dictating = false, AppStateInfo? current = null, string? failure = null) =>
        new(asr, failure, paused, downloading, progress, downloadError, preload, autoDownloadAttempted, dictating,
            current ?? AppStateInfo.Idle);

    [Fact]
    public void Paused_TakesPrecedence_ButFirstMissingModelStillStartsDownload()
    {
        var decision = ReadinessPolicy.Decide(Snapshot(AsrState.ModelMissing, paused: true, autoDownloadAttempted: false));

        Assert.Equal(AppStateInfo.Paused, decision.State);
        Assert.Equal(HotkeyGate.Unchanged, decision.Gate);
        Assert.True(decision.StartAutomaticDownload);
        Assert.False(decision.PresentModelGuidance);
    }

    [Fact]
    public void Downloading_ShowsProgress_AndBlocksHotkey()
    {
        var decision = ReadinessPolicy.Decide(Snapshot(AsrState.ModelMissing, downloading: true, progress: 0.42,
            autoDownloadAttempted: false));

        Assert.Equal(AppStateInfo.DownloadingModelWith(0.42), decision.State);
        Assert.Equal(HotkeyGate.Block, decision.Gate);
        Assert.False(decision.StartAutomaticDownload);
        Assert.Contains("42", ReadinessPolicy.BlockedReason(Snapshot(AsrState.ModelMissing, downloading: true, progress: 0.42)));
    }

    [Fact]
    public void ModelMissing_StartsAutomaticDownloadOnlyOnce_AndPresentsGuidance()
    {
        var first = ReadinessPolicy.Decide(Snapshot(AsrState.ModelMissing, autoDownloadAttempted: false));
        Assert.True(first.StartAutomaticDownload);

        var later = ReadinessPolicy.Decide(Snapshot(AsrState.ModelMissing));
        Assert.False(later.StartAutomaticDownload);
        Assert.Equal(AppStateInfo.ModelMissing, later.State);
        Assert.Equal(HotkeyGate.Block, later.Gate);
        Assert.True(later.HideHud);
        Assert.True(later.PresentModelGuidance);
        Assert.False(later.StopDictation);
    }

    [Fact]
    public void ModelMissing_PrefersDownloadErrorAsBlockedReason()
    {
        const string error = "network down; retrying";
        Assert.Equal(error, ReadinessPolicy.BlockedReason(Snapshot(AsrState.ModelMissing, downloadError: error)));
        Assert.NotNull(ReadinessPolicy.BlockedReason(Snapshot(AsrState.ModelMissing)));
    }

    [Fact]
    public void EngineUnusable_StopsActiveDictation()
    {
        Assert.True(ReadinessPolicy.Decide(Snapshot(AsrState.ModelMissing, dictating: true)).StopDictation);

        var failed = ReadinessPolicy.Decide(Snapshot(AsrState.Failed, dictating: true, failure: "bad model"));
        Assert.True(failed.StopDictation);
        Assert.True(failed.HideHud);
        Assert.Equal(HotkeyGate.Block, failed.Gate);
        Assert.Equal(AppStateInfo.ErrorWith("bad model"), failed.State);
        Assert.Contains("bad model", ReadinessPolicy.BlockedReason(Snapshot(AsrState.Failed, failure: "bad model")));
    }

    [Fact]
    public void Loading_KeepsActiveDictationDisplay_WithoutGating()
    {
        // 空闲卸载后首次按热键：引擎边加载边录音，进行中的听写不能被"未就绪"覆盖（VW-13）。
        var during = ReadinessPolicy.Decide(Snapshot(AsrState.Loading, dictating: true, current: AppStateInfo.Recording));
        Assert.Equal(AppStateInfo.Recording, during.State);
        Assert.Equal(HotkeyGate.Unchanged, during.Gate);
        Assert.False(during.StopDictation);

        var idle = ReadinessPolicy.Decide(Snapshot(AsrState.Loading));
        Assert.Equal(AppStateInfo.ModelLoading, idle.State);
        Assert.Equal(HotkeyGate.Block, idle.Gate);
        Assert.NotNull(ReadinessPolicy.BlockedReason(Snapshot(AsrState.Loading)));
    }

    [Fact]
    public void Unloaded_PreloadsOnlyWhenPreloadOnLaunchIsOn()
    {
        var preload = ReadinessPolicy.Decide(Snapshot(AsrState.Unloaded, preload: true));
        Assert.Equal(EngineStep.Preload, preload.Engine);
        Assert.Equal(HotkeyGate.Block, preload.Gate);

        var lazy = ReadinessPolicy.Decide(Snapshot(AsrState.Unloaded, preload: false));
        Assert.Equal(EngineStep.PrepareWithoutLoading, lazy.Engine);
        Assert.Equal(HotkeyGate.Unchanged, lazy.Gate);
        Assert.Equal(AppStateInfo.ModelLoading, lazy.State);
    }

    [Fact]
    public void ReadyAndIdleSuspended_ReleaseGate_AndKeepActiveDictationDisplay()
    {
        foreach (var asr in new[] { AsrState.Ready, AsrState.SuspendedForIdle })
        {
            var idle = ReadinessPolicy.Decide(Snapshot(asr, current: AppStateInfo.ErrorWith("previous dictation failed")));
            Assert.Equal(AppStateInfo.Idle, idle.State);
            Assert.Equal(HotkeyGate.Release, idle.Gate);
            Assert.Equal(EngineStep.None, idle.Engine);
            Assert.Null(ReadinessPolicy.BlockedReason(Snapshot(asr)));

            var dictating = ReadinessPolicy.Decide(Snapshot(asr, dictating: true, current: AppStateInfo.Recognizing));
            Assert.Equal(AppStateInfo.Recognizing, dictating.State);
            Assert.Equal(HotkeyGate.Release, dictating.Gate);
        }
    }
}
