using VoiceTyper.Core;
using VoiceTyper.Services;
using Xunit;

namespace VoiceTyper.Tests;

public class WindowsFocusInspectorTests
{
    [Fact]
    public async Task HungProvider_TimesOut_WithoutStartingMoreQueries_AndCancellationPropagates()
    {
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var calls = 0;
        var inspector = new WindowsFocusInspector(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            gate.Wait();
            return FocusSafety.Normal;
        }, TimeSpan.FromMilliseconds(30));
        var target = new ForegroundTarget(new IntPtr(1), 1);
        try
        {
            var first = inspector.InspectAsync(target, CancellationToken.None);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(FocusSafety.Unknown, await first);
            Assert.Equal(FocusSafety.Unknown, await inspector.InspectAsync(target, CancellationToken.None));
            Assert.Equal(1, calls);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspector.InspectAsync(target, cancelled.Token));
        }
        finally { gate.Set(); }
    }
}
