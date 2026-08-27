using Grok.Player.Core.Media;
using Grok.Player.Core.Player;
using Grok.Player.Core.Tests.Fakes;
using Grok.Player.Core.Tests.Support;

namespace Grok.Player.Core.Tests;

public sealed class PlayerHostSeekTests
{
    [Fact]
    public void Seek_without_media_throws()
    {
        using var host = Create(out _, open: false);
        Assert.Throws<InvalidOperationException>(() => host.Seek(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Seek_sends_absolute_seconds_in_invariant_culture()
    {
        using var host = Create(out var fake);
        host.Seek(TimeSpan.FromSeconds(12.5));
        var command = fake.LastCommand();
        Assert.Equal("seek", command?[0]);
        Assert.Equal("12.5", command?[1]);
        Assert.Equal("absolute+exact", command?[2]);
    }

    [Fact]
    public void Seek_negative_becomes_zero()
    {
        using var host = Create(out var fake);
        host.Seek(TimeSpan.FromSeconds(-8));
        Assert.Equal("0", fake.LastCommand()?[1]);
        host.ProcessPendingEvents();
        Assert.Equal(TimeSpan.Zero, host.Position);
    }

    [Fact]
    public void Seek_past_duration_clamps_to_end()
    {
        using var host = Create(out var fake);
        host.Seek(TimeSpan.FromSeconds(9999));
        Assert.Equal("120", fake.LastCommand()?[1]);
        host.ProcessPendingEvents();
        Assert.Equal(TimeSpan.FromSeconds(120), host.Position);
    }

    [Fact]
    public void Seek_without_known_duration_still_sends_non_negative()
    {
        var fake = new FakeMpvNative { AutoLoad = true };
        fake.AutoDurationSeconds = 0;
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        var path = TestMedia.CreateTempFile();
        try
        {
            // Force unknown duration after load.
            host.Open(path);
            host.ProcessPendingEvents();
            // duration 0 is treated as unknown by host
            host.Seek(TimeSpan.FromSeconds(3));
            Assert.Equal("3", fake.LastCommand()?[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Network_vod_ignores_stale_time_after_seek()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 600 };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://cdn.example/vod.m3u8", StreamKind.Vod);
        host.ProcessPendingEvents();
        host.Seek(TimeSpan.FromSeconds(90));
        Assert.Equal(90, host.Position.TotalSeconds);
        fake.Enqueue(Grok.Player.Core.Native.MpvEvent.Property("time-pos", 88d, Grok.Player.Core.Native.MpvFormat.Double));
        host.ProcessPendingEvents();
        Assert.Equal(90, host.Position.TotalSeconds);
    }

    [Fact]
    public void Network_vod_seek_uses_absolute_not_exact()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 600 };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://cdn.example/vod.m3u8", StreamKind.Vod);
        host.ProcessPendingEvents();
        host.Seek(TimeSpan.FromSeconds(90));
        Assert.Equal(["seek", "90", "absolute"], fake.LastCommand());
    }

    [Fact]
    public void Seek_does_not_throw_when_mpv_rejects_the_command()
    {
        using var host = Create(out var fake);
        fake.ThrowOnCommand = true;
        host.Seek(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.Zero, host.Position);
    }

    [Fact]
    public void Go_live_reports_the_safe_live_target_instead_of_jumping_to_the_tip()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 120 };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://cdn.example/live.m3u8", StreamKind.Live);
        host.ProcessPendingEvents();
        host.Seek(TimeSpan.FromSeconds(60));

        host.SeekLive();

        Assert.Equal(["seek", "118", "absolute"], fake.LastCommand());
        Assert.Equal(118, host.Position.TotalSeconds, 3);
        Assert.True(host.IsFollowingLive);
        Assert.True(LivePlayback.IsAtLive(host.Position.TotalSeconds, 120));
    }

    [Fact]
    public void Manual_seek_leaves_follow_live_mode()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 120 };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://cdn.example/live.m3u8", StreamKind.Live);
        host.ProcessPendingEvents();
        Assert.True(host.IsFollowingLive);

        host.Seek(TimeSpan.FromSeconds(60));

        Assert.False(host.IsFollowingLive);
    }

    [Fact]
    public void Stale_position_after_go_live_does_not_push_the_playhead_back()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 120 };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://cdn.example/live.m3u8", StreamKind.Live);
        host.ProcessPendingEvents();
        host.Seek(TimeSpan.FromSeconds(60));
        host.SeekLive();

        fake.Enqueue(Grok.Player.Core.Native.MpvEvent.Property(
            "time-pos",
            60d,
            Grok.Player.Core.Native.MpvFormat.Double));
        host.ProcessPendingEvents();

        Assert.Equal(118, host.Position.TotalSeconds, 3);
        Assert.True(host.IsFollowingLive);
    }

    [Fact]
    public void CanSeek_requires_positive_duration()
    {
        using var idle = Create(out _, open: false);
        Assert.False(idle.CanSeek);

        using var loaded = Create(out _);
        Assert.True(loaded.CanSeek);
    }

    private static PlayerHost Create(out FakeMpvNative fake, bool open = true)
    {
        fake = new FakeMpvNative();
        var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        if (!open)
        {
            return host;
        }

        host.Open(TestMedia.CreateTempFile());
        host.ProcessPendingEvents();
        return host;
    }
}
