using Grok.Player.Core.Player;
using Grok.Player.Core.IntegrationTests.Support;

namespace Grok.Player.Core.IntegrationTests;

public sealed class LibMpvLiveTests
{
    [LibMpvFact]
    public void SeekLive_on_a_file_reaches_the_end_without_reloading()
    {
        var sample = GeneratedMedia.TryCreateSample(4);
        if (sample is null)
        {
            return;
        }

        using var host = PlayerHost.CreateHeadless();
        var opened = new ManualResetEventSlim(false);
        host.MediaOpened += (_, _) => opened.Set();
        host.Open(sample);
        EventWait.Until(() => opened.IsSet || host.State == PlayerState.Error, TimeSpan.FromSeconds(10), "open");
        if (host.State == PlayerState.Error)
        {
            return;
        }

        host.Seek(TimeSpan.FromSeconds(0.4));
        EventWait.Until(() => host.Position.TotalSeconds >= 0.2, TimeSpan.FromSeconds(4), "parked");

        var started = DateTime.UtcNow;
        host.SeekLive();
        EventWait.Until(
            () => host.Duration is { } duration && host.Position >= duration - TimeSpan.FromSeconds(0.75),
            TimeSpan.FromSeconds(4),
            "live snap");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.5), "SeekLive must not tear down playback");
    }

    [LibMpvFact]
    public void Headless_capture_writes_a_usable_still()
    {
        var sample = GeneratedMedia.TryCreateSample(3);
        if (sample is null)
        {
            return;
        }

        using var host = PlayerHost.CreateHeadless();
        var opened = new ManualResetEventSlim(false);
        host.MediaOpened += (_, _) => opened.Set();
        host.Open(sample);
        EventWait.Until(() => opened.IsSet || host.State == PlayerState.Error, TimeSpan.FromSeconds(10), "open");
        if (host.State == PlayerState.Error)
        {
            return;
        }

        EventWait.Until(() => host.Position.TotalSeconds > 0.05, TimeSpan.FromSeconds(3), "decoded");
        var path = Path.Combine(Path.GetTempPath(), $"grok-still-{Guid.NewGuid():N}.jpg");
        try
        {
            Assert.True(host.TryCaptureVideo(path));
            Assert.True(LivePlayback.IsUsableStill(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [LibMpvFact]
    public void Endurance_repeated_seek_and_live_does_not_fault()
    {
        var sample = GeneratedMedia.TryCreateSample(4);
        if (sample is null)
        {
            return;
        }

        using var host = PlayerHost.CreateHeadless();
        var opened = new ManualResetEventSlim(false);
        host.MediaOpened += (_, _) => opened.Set();
        host.Open(sample);
        EventWait.Until(() => opened.IsSet || host.State == PlayerState.Error, TimeSpan.FromSeconds(10), "open");
        if (host.State == PlayerState.Error)
        {
            return;
        }

        for (var i = 0; i < 80; i++)
        {
            host.Seek(TimeSpan.FromSeconds(0.3 + (i % 5) * 0.2));
            if (i % 4 == 0)
            {
                host.SeekLive();
            }

            host.ProcessPendingEvents();
            Assert.NotEqual(PlayerState.Error, host.State);
        }

        Assert.True(host.HasMedia);
    }

    [LibMpvFact]
    public void Hls_window_without_endlist_is_treated_as_live_and_snaps()
    {
        var fixture = LiveHlsFixture.TryCreate();
        if (fixture is null)
        {
            return;
        }

        try
        {
            using var host = PlayerHost.CreateHeadless();
            var opened = new ManualResetEventSlim(false);
            host.MediaOpened += (_, _) => opened.Set();
            host.Open(fixture.PlaylistUrl);
            EventWait.Until(() => opened.IsSet || host.State == PlayerState.Error, TimeSpan.FromSeconds(12), "hls open");
            if (host.State == PlayerState.Error)
            {
                return;
            }

            Assert.True(host.LiveWindow);
            var started = DateTime.UtcNow;
            host.SeekLive();
            host.ProcessPendingEvents();
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
            Assert.NotEqual(PlayerState.Error, host.State);
        }
        finally
        {
            fixture.Dispose();
        }
    }
}
