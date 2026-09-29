using System;
using System.IO;
using VoiceTyper.Asr;
using VoiceTyper.Core;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>首启引导的流程与试用状态（对应 macOS <c>OnboardingViewModelTests</c>）。</summary>
public class OnboardingModelTests
{
    /// <summary>OnFinish 会写完成记录；测试里不能碰用户真实的记录文件，所以先把 Model 走到「完成」前一步，
    /// 只在专门的用例里验证 Record（用临时路径）。</summary>
    private static OnboardingModel AtTrial()
    {
        var model = new OnboardingModel { Mic = MicProbeResult.Available, AsrState = AsrState.Ready };
        model.GoForward();
        model.GoForward();
        model.GoForward();
        Assert.Equal(OnboardingStep.Trial, model.Step);
        return model;
    }

    [Fact]
    public void StepNavigation_GoesForwardAndBack_WithRefreshOnEachChange()
    {
        var model = new OnboardingModel();
        int refreshes = 0;
        model.OnRefreshStatus = () => refreshes++;

        Assert.False(model.CanGoBack);
        model.GoForward();
        Assert.Equal(OnboardingStep.Microphone, model.Step);
        Assert.True(model.CanGoBack);
        model.GoBack();
        Assert.Equal(OnboardingStep.Welcome, model.Step);
        Assert.Equal(2, refreshes); // 用户很可能刚从系统设置切回来：每次换步都重新探测
    }

    [Fact]
    public void PrimaryTitle_ChangesOnTrialStep()
    {
        var model = AtTrial();
        Assert.Equal("先跳过", model.PrimaryActionTitle);
        model.Handle(new OnboardingDictationEvent.Inserted("你好"));
        Assert.Equal("完成", model.PrimaryActionTitle);
    }

    [Fact]
    public void CanRunTrial_RequiresMicAndModel()
    {
        var model = new OnboardingModel { Mic = MicProbeResult.Available, AsrState = AsrState.Ready };
        Assert.True(model.CanRunTrial);
        Assert.Null(model.TrialBlockingHint);

        model.Mic = MicProbeResult.AccessDenied;
        Assert.False(model.CanRunTrial);
        Assert.Contains("麦克风", model.TrialBlockingHint);

        model.Mic = MicProbeResult.Available;
        model.AsrState = AsrState.ModelMissing;
        Assert.Contains("模型", model.TrialBlockingHint);

        model.AsrState = AsrState.SuspendedForIdle; // 空闲卸载后仍算就绪
        Assert.True(model.CanRunTrial);
    }

    [Fact]
    public void Events_OutsideTrialStep_DoNotPolluteTrialState()
    {
        var model = new OnboardingModel();
        Assert.False(model.Handle(new OnboardingDictationEvent.RecordingStarted()));
        Assert.Equal(OnboardingTrialKind.Idle, model.Trial.Kind);
    }

    [Fact]
    public void HappyPath_RecordingRecognizingSucceeded()
    {
        var model = AtTrial();
        model.HotkeyMode = HotkeyMode.Hold;

        Assert.True(model.Handle(new OnboardingDictationEvent.RecordingStarted()));
        Assert.Equal(OnboardingTrialKind.Recording, model.Trial.Kind);
        Assert.Contains("松开热键", model.TrialSummary!.Value.Detail);

        Assert.True(model.Handle(new OnboardingDictationEvent.Recognizing()));
        Assert.True(model.Handle(new OnboardingDictationEvent.Inserted("你好世界")));
        Assert.Equal(OnboardingTrialKind.Succeeded, model.Trial.Kind);
        Assert.Contains("你好世界", model.TrialSummary!.Value.Detail);
    }

    [Fact]
    public void ToggleMode_ExplainsHowToStop()
    {
        var model = AtTrial();
        model.HotkeyMode = HotkeyMode.Toggle;
        model.Handle(new OnboardingDictationEvent.RecordingStarted());
        Assert.Contains("再按一次热键", model.TrialSummary!.Value.Detail);
    }

    [Fact]
    public void EmptyResult_DistinguishesSilenceFromUnrecognizable()
    {
        var silent = AtTrial();
        silent.Handle(new OnboardingDictationEvent.RecordingStarted());
        silent.Handle(new OnboardingDictationEvent.Level(0.0005f));
        silent.Handle(new OnboardingDictationEvent.EmptyResult());
        Assert.Equal(OnboardingTrialKind.NoSpeech, silent.Trial.Kind);

        var loud = AtTrial();
        loud.Handle(new OnboardingDictationEvent.RecordingStarted());
        loud.Handle(new OnboardingDictationEvent.Level(0.05f));
        loud.Handle(new OnboardingDictationEvent.EmptyResult());
        Assert.Equal(OnboardingTrialKind.Failed, loud.Trial.Kind);
    }

    [Fact]
    public void LevelsOutsideRecording_AreIgnored()
    {
        var model = AtTrial();
        Assert.False(model.Handle(new OnboardingDictationEvent.Level(0.5f)));
        model.Handle(new OnboardingDictationEvent.RecordingStarted());
        Assert.False(model.Handle(new OnboardingDictationEvent.Level(0.5f))); // 电平不改变可见状态，只累计峰值
    }

    [Fact]
    public void CancelledFailedAndBlocked_AreReflected()
    {
        var model = AtTrial();
        model.Handle(new OnboardingDictationEvent.Cancelled());
        Assert.Equal(OnboardingTrialKind.Cancelled, model.Trial.Kind);
        model.Handle(new OnboardingDictationEvent.Failed("boom"));
        Assert.Equal("boom", model.TrialSummary!.Value.Detail);
        model.Handle(new OnboardingDictationEvent.Blocked("模型下载中"));
        Assert.Equal(OnboardingTrialKind.Blocked, model.Trial.Kind);
    }

    [Fact]
    public void ChangingStep_ResetsTrial()
    {
        var model = AtTrial();
        model.Handle(new OnboardingDictationEvent.Inserted("好"));
        model.GoBack();
        model.GoForward();
        Assert.Equal(OnboardingTrialKind.Idle, model.Trial.Kind);
    }

    [Fact]
    public void Record_PersistsCompletion_ToTheGivenPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voicetyper-onb-{Guid.NewGuid():N}", "onboarding.txt");
        try
        {
            Assert.False(OnboardingRecord.IsCompleted(path));
            OnboardingRecord.MarkCompleted(path);
            Assert.True(OnboardingRecord.IsCompleted(path));

            // 将来流程版本升级：旧记录不再算完成。
            File.WriteAllText(path, (OnboardingRecord.CurrentFlowVersion - 1).ToString());
            Assert.False(OnboardingRecord.IsCompleted(path));
            File.WriteAllText(path, "garbage");
            Assert.False(OnboardingRecord.IsCompleted(path));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
        }
    }
}

public class MicPermissionProbeTests
{
    [Fact]
    public void DigitalSilence_IsReportedAsSilent()
    {
        // Windows 在「让桌面应用访问你的麦克风」关闭时不让打开失败，而是悄悄送零。
        Assert.Equal(MicProbeResult.Silent, MicPermissionProbe.Classify(callbackCount: 20, peakLevel: 0f));
    }

    [Fact]
    public void AnyNoise_MeansTheMicrophoneWorks()
    {
        Assert.Equal(MicProbeResult.Available, MicPermissionProbe.Classify(20, 0.0002f));
    }

    [Fact]
    public void TooFewCallbacks_IsNotEnoughEvidence()
    {
        Assert.Equal(MicProbeResult.Available, MicPermissionProbe.Classify(0, 0f));
        Assert.Equal(MicProbeResult.Available, MicPermissionProbe.Classify(MicPermissionProbe.MinimumCallbacks - 1, 0f));
    }

    [Fact]
    public void Silent_DoesNotBlockTheTrialStep()
    {
        // 少数带降噪门的虚拟 / USB 麦克风空闲时也输出全零，不能因此把用户挡在「试一试」之外。
        var model = new OnboardingModel { Mic = MicProbeResult.Silent, AsrState = VoiceTyper.Asr.AsrState.Ready };
        Assert.True(model.CanRunTrial);
    }
}
