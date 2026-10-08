using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.UI;
using Xunit;

namespace VoiceTyper.Tests;

/// <summary>
/// HUD 逐像素分层渲染的帧级断言：从 <c>RenderPreviewFrame</c> 取直通 alpha 的整帧，
/// 验证"背景随 ui.opacity 变化、文字不随之变淡"的语义，以及圆角的抗锯齿边缘。
/// 全程 <c>SuppressPresentationForTest</c>，不在真实屏幕上弹窗。
/// </summary>
public class RecordingHudFrameTests
{
    private static void OnUiThread(Action test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { Application.EnableVisualStyles(); test(); }
            catch (Exception caught) { error = caught; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static RecordingHud NewHud(double opacity = 1.0)
    {
        var hud = new RecordingHud(new UIConfig { Opacity = opacity, HudPosition = "bottom_center" })
        {
            SuppressPresentationForTest = true,
        };
        hud.ShowRecording(null);
        hud.ShowPreview("今天测试语音浮窗渲染，这是一段较长的预览文本。");
        return hud;
    }

    /// <summary>找最亮的"内容"像素（文字 / 波形，白色系且近乎不透明）。</summary>
    private static (int Alpha, int Red) BrightestContentPixel(Bitmap frame)
    {
        var best = (Alpha: 0, Red: 0);
        var rect = new Rectangle(0, 0, frame.Width, frame.Height);
        var bits = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bits.Stride];
            for (int y = 0; y < bits.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, row.Length);
                for (int x = 0; x < bits.Width; x++)
                {
                    // 32bppArgb 的内存布局是 BGRA。
                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2], a = row[x * 4 + 3];
                    if (r > 200 && g > 200 && b > 200 && a > best.Alpha) best = (a, r);
                }
            }
        }
        finally
        {
            frame.UnlockBits(bits);
        }
        return best;
    }

    private static int AlphaAt(Bitmap frame, int x, int y)
    {
        var pixel = frame.GetPixel(x, y);
        return pixel.A;
    }

    [Fact]
    public void BackgroundScalesWithOpacityButContentStaysOpaque() => OnUiThread(() =>
    {
        using (var hud = NewHud(opacity: 1.0))
        using (var frame = hud.RenderPreviewFrame())
        {
            // 420px 档位内、状态文字与计时之间的一段纯背景（Relayout 后窗口宽 420、高约 81）。
            Assert.Equal(420, frame.Width);
            var bg = AlphaAt(frame, 300, 24);
            Assert.InRange(bg, 250, 255);
            var content = BrightestContentPixel(frame);
            Assert.True(content.Alpha >= 240, $"内容像素应近乎不透明，实际 alpha={content.Alpha}");
        }

        using (var hud = NewHud(opacity: 0.4))
        using (var frame = hud.RenderPreviewFrame())
        {
            var bg = AlphaAt(frame, 300, 24);
            Assert.InRange(bg, 92, 112); // 0.4 × 255 ≈ 102，留 ±10 容差
            var content = BrightestContentPixel(frame);
            // 旧路径（整窗透明）会把文字一起乘到 ~0.4；新路径文字仍近乎不透明。
            Assert.True(content.Alpha >= 235, $"文字不应随背景不透明度变淡，实际 alpha={content.Alpha}");
        }
    });

    [Fact]
    public void RoundedCornersAreAntiAliased() => OnUiThread(() =>
    {
        using var hud = NewHud();
        using var frame = hud.RenderPreviewFrame();
        Assert.Equal(0, AlphaAt(frame, 0, 0)); // 圆角外完全透明
        // 圆角弧线上应存在"半透明"像素（Region 裁剪时代这里只有 0/255 的硬边）。
        int partial = 0;
        for (int x = 0; x < 20; x++)
        {
            for (int y = 0; y < 20; y++)
            {
                var a = AlphaAt(frame, x, y);
                if (a > 0 && a < 255) partial++;
            }
        }
        Assert.True(partial >= 4, $"圆角边缘应有抗锯齿过渡像素，实际 {partial} 个");
    });

    /// <summary>淡出应在约 3 拍（≈100ms）内完成并进入 Hidden——用私有 AnimationTick 驱动，
    /// 不依赖消息循环与真实窗口。</summary>
    [Fact]
    public void FadeOutCompletesWithinThreeTicks() => OnUiThread(() =>
    {
        using var hud = NewHud();
        var hudType = typeof(RecordingHud);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        hudType.GetField("_fadeAlpha", flags)!.SetValue(hud, 1f);
        hudType.GetField("_fadeTarget", flags)!.SetValue(hud, 0f);
        hudType.GetField("_hiding", flags)!.SetValue(hud, true);
        var tick = hudType.GetMethod("AnimationTick", flags)!;

        string Phase() => hudType.GetField("_phase", flags)!.GetValue(hud)!.ToString()!;
        for (int i = 0; i < 3; i++) tick.Invoke(hud, null);
        Assert.Equal("Hidden", Phase());
        var fade = (float)hudType.GetField("_fadeAlpha", flags)!.GetValue(hud)!;
        Assert.Equal(0f, fade, 3);
    });

    /// <summary>环境变量 VOICETYPER_HUD_PREVIEW 显式开启时输出各状态 PNG，供人工核对。</summary>
    [SkippableFact]
    public void RenderHudStatePreviews() => OnUiThread(() =>
    {
        var directory = Environment.GetEnvironmentVariable("VOICETYPER_HUD_PREVIEW");
        Skip.If(string.IsNullOrEmpty(directory), "显式设置 VOICETYPER_HUD_PREVIEW 才输出");
        Directory.CreateDirectory(directory!);

        using var hud = new RecordingHud(new UIConfig { Opacity = 0.9, HudPosition = "bottom_center" })
        {
            SuppressPresentationForTest = true,
        };
        void Snap(string name)
        {
            using var frame = hud.RenderPreviewFrame();
            frame.Save(Path.Combine(directory!, $"hud-{name}.png"), ImageFormat.Png);
        }

        hud.ShowRecording("Jabra Evolve2 65");
        hud.UpdateLevel(0.4f); hud.UpdateLevel(0.25f); hud.UpdateLevel(0.5f); hud.UpdateLevel(0.3f);
        Snap("recording");

        hud.ShowRecording(null);
        hud.ShowPreview("今天我们来看一下语音识别浮窗的显示效果，这段文字比较长，用来触发两行预览与宽度档位的增长，看看尾部截断的省略号。");
        Snap("recording-preview");

        hud.ShowRecording(null);
        hud.SetRecognizing();
        Snap("recognizing");

        hud.ShowRecording(null);
        hud.SetRecognizing();
        hud.SetCorrecting();
        Snap("correcting");

        hud.ShowRecording(null);
        hud.ShowSuccess();
        Snap("success");

        hud.ShowRecording(null);
        hud.ShowError("服务异常");
        Snap("error");

        hud.ShowRecording(null);
        hud.ShowNoSpeech();
        Snap("nospeech");

        hud.HideHud();
    });

    /// <summary>环境变量 VOICETYPER_HUD_SMOKE 显式开启时，在真实屏幕上走一次分层窗口
    /// （Show + UpdateLayeredWindow），验证本机没有落入实心回退路径。会在屏幕上短暂显示 HUD。</summary>
    [SkippableFact]
    public void LayeredWindowSmokeOnRealScreen() => OnUiThread(() =>
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VOICETYPER_HUD_SMOKE")),
            "显式设置 VOICETYPER_HUD_SMOKE 才弹真实窗口");
        using var hud = new RecordingHud(new UIConfig { Opacity = 0.9, HudPosition = "bottom_center" });
        hud.ShowRecording("Smoke Test");
        Application.DoEvents();
        Thread.Sleep(300);
        Application.DoEvents();
        Assert.True(hud.Visible, "HUD 应已显示");
        var broken = (bool)typeof(RecordingHud)
            .GetField("_layeredBroken", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(hud)!;
        Assert.False(broken, "UpdateLayeredWindow 应在本机可用，不应落入实心回退");
        hud.HideHud();
        Application.DoEvents();
    });
}
