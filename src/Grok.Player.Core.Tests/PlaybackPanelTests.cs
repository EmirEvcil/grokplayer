using Grok.Player.Core.Player;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Tests.Fakes;
using Grok.Player.Core.Tests.Support;

namespace Grok.Player.Core.Tests;

public sealed class PlaybackPanelTests
{
    [Fact]
    public void Speed_clamps_and_writes_property()
    {
        Assert.Equal(0.2, PlaybackSpec.ClampSpeed(0));
        Assert.Equal(12, PlaybackSpec.ClampSpeed(40));
        Assert.Equal(1, PlaybackSpec.ClampSpeed(double.NaN));

        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.NudgeSpeed(0.3);
        Assert.Equal(1.3, view.Speed, 3);
        Assert.Contains(fake.Lifecycle, item => item.StartsWith("property:speed=", StringComparison.Ordinal));
        view.SetSpeed(1);
        Assert.Equal(1, view.Speed);
    }

    [Fact]
    public void Ab_loop_rejects_inverted_points_and_clears()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        host.Open(TestMedia.CreateTempFile("ab.mp4"));
        host.ProcessPendingEvents();
        view.ApplySeek(10);
        Assert.True(view.MarkLoopA());
        host.Seek(TimeSpan.FromSeconds(4));
        host.ProcessPendingEvents();
        Assert.False(view.MarkLoopB());
        view.ApplySeek(20);
        Assert.True(view.MarkLoopB());
        Assert.Equal(10, view.LoopA!.Value.TotalSeconds, 3);
        Assert.Equal(20, view.LoopB!.Value.TotalSeconds, 3);
        Assert.Contains(fake.Lifecycle, item => item.StartsWith("property:ab-loop-a=", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.StartsWith("property:ab-loop-b=", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item == "property:ab-loop-count=inf");
        view.ClearLoopPoints();
        Assert.Null(view.LoopA);
        Assert.Null(view.LoopB);
        Assert.Contains(fake.Lifecycle, item => item == "property:ab-loop-a=no");
        Assert.Contains(fake.Lifecycle, item => item == "property:ab-loop-count=0");
    }

    [Fact]
    public void Seek_cannot_leave_the_ab_range()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        host.Open(TestMedia.CreateTempFile("range.mp4"));
        host.ProcessPendingEvents();
        view.ApplySeek(10);
        Assert.True(view.MarkLoopA());
        view.ApplySeek(4);
        Assert.Equal(10, view.SeekValue, 3);
        view.SeekBy(TimeSpan.FromSeconds(-30));
        Assert.Equal(10, view.SeekValue, 3);
        view.ApplySeek(40);
        Assert.True(view.MarkLoopB());
        view.ApplySeek(90);
        Assert.Equal(40, view.SeekValue, 3);
    }
}
