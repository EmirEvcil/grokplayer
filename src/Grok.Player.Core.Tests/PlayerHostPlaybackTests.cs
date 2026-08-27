using Grok.Player.Core.Native;
using Grok.Player.Core.Player;
using Grok.Player.Core.Tests.Fakes;
using Grok.Player.Core.Tests.Support;

namespace Grok.Player.Core.Tests;

public sealed class PlayerHostPlaybackTests
{
    [Fact]
    public void Play_pause_stop_without_media_are_guarded()
    {
        using var host = CreateLoaded(out _, open: false);
        Assert.Throws<InvalidOperationException>(() => host.Play());
        Assert.Throws<InvalidOperationException>(() => host.Pause());
        host.Stop();
        Assert.Equal(PlayerState.Idle, host.State);
    }

    [Fact]
    public void Pause_and_play_toggle_pause_property()
    {
        using var host = CreateLoaded(out var fake);

        host.Pause();
        host.ProcessPendingEvents();
        Assert.Equal(PlayerState.Paused, host.State);
        Assert.True(host.IsPaused);
        Assert.True(host.CanPlay);
        Assert.False(host.CanPause);

        host.Play();
        host.ProcessPendingEvents();
        Assert.Equal(PlayerState.Playing, host.State);
        Assert.False(host.IsPaused);
        Assert.Contains(fake.Lifecycle, item => item == "property:pause=True");
        Assert.Contains(fake.Lifecycle, item => item == "property:pause=False");
    }

    [Fact]
    public void TogglePause_switches_both_ways()
    {
        using var host = CreateLoaded(out _);
        host.TogglePause();
        host.ProcessPendingEvents();
        Assert.Equal(PlayerState.Paused, host.State);
        host.TogglePause();
        host.ProcessPendingEvents();
        Assert.Equal(PlayerState.Playing, host.State);
    }

    [Fact]
    public void Stop_unloads_media_and_returns_to_idle()
    {
        using var host = CreateLoaded(out var fake);
        host.Seek(TimeSpan.FromSeconds(40));
        host.ProcessPendingEvents();

        host.Stop();
        host.ProcessPendingEvents();

        Assert.Equal(PlayerState.Idle, host.State);
        Assert.Equal(TimeSpan.Zero, host.Position);
        Assert.Null(host.MediaPath);
        Assert.Null(host.Duration);
        Assert.False(host.HasMedia);
        Assert.False(host.CanPlay);
        Assert.Contains(fake.Commands, c => c is ["stop"]);
    }

    [Fact]
    public void Play_after_ended_seeks_back_to_start()
    {
        using var host = CreateLoaded(out var fake);
        fake.Enqueue(MpvEvent.EndFile(MpvEndFileReason.Eof));
        host.ProcessPendingEvents();
        Assert.Equal(PlayerState.Ended, host.State);

        host.Play();
        host.ProcessPendingEvents();

        Assert.Equal(PlayerState.Playing, host.State);
        Assert.Contains(fake.Commands, c => c.Length == 3 && c[0] == "seek" && c[1] == "0");
    }

    [Fact]
    public void End_of_file_sets_ended_and_raises()
    {
        using var host = CreateLoaded(out var fake);
        var ended = false;
        host.MediaEnded += (_, _) => ended = true;
        fake.Enqueue(MpvEvent.EndFile(MpvEndFileReason.Eof));
        host.ProcessPendingEvents();

        Assert.True(ended);
        Assert.Equal(PlayerState.Ended, host.State);
        Assert.Equal(host.Duration, host.Position);
    }

    [Fact]
    public void Mid_file_eof_after_seek_does_not_end_playback()
    {
        using var host = CreateLoaded(out var fake);
        var ended = false;
        host.MediaEnded += (_, _) => ended = true;
        host.Seek(TimeSpan.FromSeconds(40));
        host.ProcessPendingEvents();
        fake.Enqueue(MpvEvent.EndFile(MpvEndFileReason.Eof));
        host.ProcessPendingEvents();

        Assert.False(ended);
        Assert.NotEqual(PlayerState.Ended, host.State);
    }

    [Fact]
    public void Double_dispose_is_safe()
    {
        var host = CreateLoaded(out var fake);
        host.Dispose();
        host.Dispose();
        Assert.Equal(1, fake.Lifecycle.Count(item => item == "terminate"));
    }

    [Fact]
    public void Dispose_detaches_wid_then_terminates()
    {
        var fake = new FakeMpvNative();
        var host = new PlayerHost(fake, new PlayerHostOptions
        {
            Headless = true,
            UseBackgroundEventLoop = false,
            VideoOutput = "null",
            AudioOutput = "null",
            WindowHandle = 42
        });

        host.Dispose();

        var widZero = fake.Lifecycle.FindIndex(item => item == "property:wid=0");
        var terminate = fake.Lifecycle.FindIndex(item => item == "terminate");
        Assert.True(widZero >= 0);
        Assert.True(terminate > widZero);
    }

    private static PlayerHost CreateLoaded(out FakeMpvNative fake, bool open = true)
    {
        fake = new FakeMpvNative();
        var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        if (!open)
        {
            return host;
        }

        var path = TestMedia.CreateTempFile();
        host.Open(path);
        host.ProcessPendingEvents();
        return host;
    }
}
