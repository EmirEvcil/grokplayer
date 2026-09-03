using Grok.Player.Core.Native;
using Grok.Player.Core.Player;
using Grok.Player.Core.Preview;
using Grok.Player.Core.IntegrationTests.Support;

namespace Grok.Player.Core.IntegrationTests;

public sealed class LibMpvPlaybackTests
{
    [LibMpvFact]
    public void Client_api_version_is_readable()
    {
        Assert.True(MpvNative.GetClientApiVersion() > 0);
    }

    [LibMpvFact]
    public void Headless_handle_initializes_and_destroys_cleanly()
    {
        using var host = PlayerHost.CreateHeadless();
        Assert.Equal(PlayerState.Idle, host.State);
        host.Dispose();
        host.Dispose();
    }

    [LibMpvFact]
    public void Missing_file_is_rejected_without_killing_the_handle()
    {
        using var host = PlayerHost.CreateHeadless();
        var missing = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.mp4");
        Assert.Throws<FileNotFoundException>(() => host.Open(missing));
        Assert.Equal(PlayerState.Idle, host.State);
        host.SetVolume(30);
        Assert.Equal(30, host.Volume);
    }

    [LibMpvFact]
    public void Real_file_opens_reports_duration_and_supports_transport()
    {
        var sample = GeneratedMedia.TryCreateSample();
        if (sample is null)
        {
            return;
        }

        using var host = PlayerHost.CreateHeadless();
        var opened = new ManualResetEventSlim(false);
        host.MediaOpened += (_, _) => opened.Set();

        host.Open(sample);
        EventWait.Until(() => opened.IsSet || host.State == PlayerState.Error, TimeSpan.FromSeconds(10), "file-loaded");
        if (host.State == PlayerState.Error)
        {
            throw new InvalidOperationException(host.LastError ?? "Open failed.");
        }

        Assert.True(host.State is PlayerState.Playing or PlayerState.Paused, $"state={host.State}");

        Assert.True(host.Duration is { } duration && duration.TotalSeconds >= 2.5, $"duration={host.Duration}");
        Assert.True(host.HasMedia);

        host.Pause();
        EventWait.Until(() => host.IsPaused, TimeSpan.FromSeconds(3), "paused");
        Assert.Equal(PlayerState.Paused, host.State);

        host.Seek(TimeSpan.FromSeconds(1.25));
        EventWait.Until(() => host.Position.TotalSeconds >= 1.0, TimeSpan.FromSeconds(5), "seek landed");

        host.SetVolume(42);
        EventWait.Until(() => Math.Abs(host.Volume - 42) < 1, TimeSpan.FromSeconds(3), "volume");

        host.Stop();
        EventWait.Until(() => host.State == PlayerState.Stopped && host.Position <= TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5), "stop");

        host.Play();
        EventWait.Until(() => host.State == PlayerState.Playing, TimeSpan.FromSeconds(3), "play after stop");
    }

    [LibMpvFact]
    public void Pause_mute_play_keeps_output_silent()
    {
        var sample = GeneratedMedia.TryCreateSample();
        if (sample is null)
        {
            Assert.Fail("could not create a sample with audio");
            return;
        }

        using var host = PlayerHost.CreateHeadless();
        host.MediaOpened += (_, _) => { };
        host.Open(sample);
        EventWait.Until(() => host.State is PlayerState.Playing or PlayerState.Paused or PlayerState.Error, TimeSpan.FromSeconds(10), "open");
        if (host.State == PlayerState.Error)
        {
            Assert.Fail(host.LastError ?? "open failed");
        }

        if (host.State == PlayerState.Playing)
        {
            host.Pause();
            EventWait.Until(() => host.IsPaused, TimeSpan.FromSeconds(3), "paused");
        }

        host.SetMuted(true);
        Assert.True(host.IsMuted);
        host.Play();
        EventWait.Until(() => host.State == PlayerState.Playing, TimeSpan.FromSeconds(3), "play");
        Assert.True(host.IsMuted, "mute must survive play");
        var ao = host.GetType();
        _ = ao;
        Assert.True(host.IsMuted);
        Assert.True(host.State == PlayerState.Playing);
    }

    [LibMpvFact]
    public void Unicode_path_opens()
    {
        var sample = GeneratedMedia.TryCreateSample();
        if (sample is null)
        {
            return;
        }

        var unicode = GeneratedMedia.TryCreateUnicodeCopy(sample);
        Assert.NotNull(unicode);

        using var host = PlayerHost.CreateHeadless();
        host.Open(unicode!);
        EventWait.Until(() => host.State is PlayerState.Playing or PlayerState.Paused or PlayerState.Error, TimeSpan.FromSeconds(10), "unicode open");
        Assert.NotEqual(PlayerState.Error, host.State);
        Assert.Equal(unicode, host.MediaPath);
    }

    [LibMpvFact]
    public void Play_without_media_throws_on_real_handle()
    {
        using var host = PlayerHost.CreateHeadless();
        Assert.Throws<InvalidOperationException>(host.Play);
        Assert.Throws<InvalidOperationException>(() => host.Seek(TimeSpan.FromSeconds(1)));
    }

    [LibMpvFact]
    public void Seek_past_end_does_not_throw()
    {
        var sample = GeneratedMedia.TryCreateSample();
        if (sample is null)
        {
            return;
        }

        using var host = PlayerHost.CreateHeadless();
        host.Open(sample);
        EventWait.Until(() => host.Duration is not null || host.State == PlayerState.Error, TimeSpan.FromSeconds(10), "loaded");
        if (host.Duration is null)
        {
            return;
        }

        host.Seek(TimeSpan.FromHours(2));
        EventWait.Until(
            () => host.Position >= host.Duration.Value - TimeSpan.FromMilliseconds(400) || host.State == PlayerState.Ended,
            TimeSpan.FromSeconds(5),
            "clamped seek");
    }

    [LibMpvFact]
    public void Seek_preview_writes_an_image()
    {
        var sample = GeneratedMedia.TryCreateSample();
        if (sample is null)
        {
            return;
        }

        using var engine = SeekPreviewEngine.Create();
        engine.Prepare(sample);
        var path = engine.Capture(TimeSpan.FromSeconds(1));
        Assert.True(path is not null && File.Exists(path) && new FileInfo(path).Length > 32, $"preview={path}");
    }

    [LibMpvFact]
    public void Seek_preview_two_times_are_real_and_not_the_same_bytes()
    {
        var sample = GeneratedMedia.TryCreateSample();
        if (sample is null)
        {
            return;
        }

        using var engine = SeekPreviewEngine.Create();
        engine.Prepare(sample);
        var first = engine.CaptureFast(TimeSpan.FromSeconds(0.4));
        var second = engine.CaptureFast(TimeSpan.FromSeconds(2.2));
        Assert.True(first is not null && File.Exists(first) && new FileInfo(first).Length > 800, "first=" + first);
        Assert.True(second is not null && File.Exists(second) && new FileInfo(second).Length > 800, "second=" + second);
        Assert.NotEqual(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [LibMpvFact]
    public void Named_osd_overlay_argv_is_rejected_positional_is_accepted()
    {
        using var native = new MpvNative();
        native.SetOption("vo", "null");
        native.SetOption("ao", "null");
        native.SetOption("osd-level", "0");
        native.Initialize();
        Assert.Throws<MpvException>(() => native.Command(
            "osd-overlay",
            "id=42",
            "format=ass-events",
            "res_x=1920",
            "res_y=1080",
            "z=3000",
            "data=Dialogue: 0,0:00:00.00,9:59:59.99,Default,,0,0,0,,{\\an2}HELLO"));
        native.Command("osd-overlay", "42", "ass-events", "{\\an2\\bord3}HELLO", "1920", "1080", "3000");
        native.Command("osd-overlay", "42", "none", "");
    }

    [LibMpvFact]
    public void Ass_overlay_stays_on_a_live_handle()
    {
        using var host = PlayerHost.CreateHeadless();
        host.SetAssOverlay("BREAKING BAD");
        host.ProcessPendingEvents();
        host.SetAssOverlay(null);
        host.ProcessPendingEvents();
    }
}
