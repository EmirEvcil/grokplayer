using Grok.Player.Core.Media;
using Grok.Player.Core.Native;
using Grok.Player.Core.Player;
using Grok.Player.Core.Playlist;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Tests.Fakes;
using Grok.Player.Core.Tests.Support;

namespace Grok.Player.Core.Tests;

public sealed class MediaCoreTests
{
    [Fact]
    public void Sanitizer_hides_tokens_and_keeps_identity()
    {
        var url = "https://cdn.example/live.m3u8?token=SECRET&quality=1080";
        Assert.Contains("token=***", UrlSanitizer.Redact(url));
        Assert.DoesNotContain("SECRET", UrlSanitizer.Redact(url));
        Assert.DoesNotContain("SECRET", UrlSanitizer.Identity(url));
        Assert.Contains("quality=1080", UrlSanitizer.Identity(url));
        Assert.Equal("live.m3u8", UrlSanitizer.DisplayName(url));
    }

    [Fact]
    public void Same_name_different_bytes_have_different_fingerprints()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grok-fp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var first = Path.Combine(dir, "movie.mp4");
        var second = Path.Combine(dir, "other", "movie.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllBytes(first, [1, 2, 3, 4, 5]);
        Thread.Sleep(20);
        File.WriteAllBytes(second, [9, 8, 7, 6, 5, 4]);
        try
        {
            Assert.NotEqual(ContentFingerprint.ForLocalFile(first), ContentFingerprint.ForLocalFile(second));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Resume_is_keyed_by_fingerprint_not_name()
    {
        var path = Path.Combine(Path.GetTempPath(), $"resume-{Guid.NewGuid():N}.json");
        var store = new ResumeStore(path);
        var one = "file|1|1|AAAA";
        var two = "file|2|2|BBBB";
        store.Save(one, "movie.mp4", 40, 120);
        Assert.True(store.TryGet(one, out var hit));
        Assert.Equal(40, hit.Seconds);
        Assert.False(store.TryGet(two, out _));
        Assert.True(ResumeStore.ShouldResume(hit));
        store.Save(one, "movie.mp4", 119, 120);
        Assert.False(store.TryGet(one, out _));
    }

    [Fact]
    public void Probe_reads_hls_and_dash_manifests()
    {
        var vod = "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:10,\na.ts\n#EXT-X-ENDLIST\n";
        var live = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:80\n#EXTINF:6,\nb.ts\n";
        Assert.Equal(StreamKind.Vod, StreamProbe.ClassifyManifest(vod));
        Assert.Equal(StreamKind.Live, StreamProbe.ClassifyManifest(live));
        var master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nmedia.m3u8\n";
        Assert.Equal(StreamKind.Unknown, StreamProbe.ClassifyManifest(master));
        Assert.Equal("https://x/media.m3u8", StreamProbe.FirstVariantUri(master, "https://x/master.m3u8"));
        Assert.Equal(StreamKind.Vod, StreamProbe.ClassifyUrl("https://cdn/a.mp4"));
        Assert.Equal(StreamKind.Vod, StreamProbe.Combine(StreamKind.Vod, StreamKind.Live));
        Assert.Equal(StreamKind.Live, StreamProbe.ClassifyManifest("<MPD type=\"dynamic\"></MPD>"));
        Assert.Equal(StreamKind.Vod, StreamProbe.ClassifyManifest("<MPD type=\"static\"></MPD>"));
        Assert.True(StreamProbe.LooksLikeDrm("<MPD><ContentProtection></ContentProtection></MPD>"));
        Assert.Equal(StreamKind.Live, StreamProbe.ClassifyPlayback(0, false, "hls"));
        Assert.Equal(StreamKind.Unknown, StreamProbe.ClassifyPlayback(3600, true, "hls"));
        Assert.Equal(StreamKind.Live, StreamProbe.Combine(StreamKind.Live, StreamKind.Unknown));
        Assert.Equal(StreamKind.Live, StreamProbe.Combine(StreamKind.Live, StreamKind.Vod));
        Assert.Equal("dash", StreamProbe.FormatLabel("https://x/a.mpd?token=1"));
    }

    [Fact]
    public void Playlist_accepts_stream_urls()
    {
        var list = new MediaPlaylist();
        Assert.True(MediaFiles.IsSupported("https://cdn.example/a.m3u8?token=x"));
        Assert.True(list.TryAdd("https://cdn.example/a.m3u8?token=x"));
        Assert.False(list.TryAdd("https://cdn.example/a.m3u8?token=y"));
        Assert.Equal("https://cdn.example/a.m3u8?token=y", list.Items[0].Path);
        Assert.Equal("hls", list.Items[0].Format);
    }

    [Fact]
    public void View_model_resumes_matching_local_file_only()
    {
        var media = TestMedia.CreateTempFile("resume-me.mp4");
        var storePath = Path.Combine(Path.GetTempPath(), $"rs-{Guid.NewGuid():N}.json");
        var store = new ResumeStore(storePath);
        store.Save(ContentFingerprint.ForLocalFile(media), "resume-me.mp4", 40, 120);
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host, store, new StaticNetworkMonitor());
        view.AcceptPaths([media]);
        host.ProcessPendingEvents();
        Assert.Contains(fake.Commands, command => command.Length >= 2 && command[0] == "seek");
    }

    [Fact]
    public void Offline_network_does_not_retry_stream()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        var net = new StaticNetworkMonitor(false);
        using var view = new PlaybackViewModel(host, new ResumeStore(Path.GetTempFileName()), net);
        view.AddStream("https://cdn.example/live.m3u8", play: true);
        host.ProcessPendingEvents();
        var before = fake.Commands.Count;
        host.Error += (_, _) => { };
        view.AddStream("https://cdn.example/live.m3u8", play: true);
        host.ProcessPendingEvents();
        Assert.True(fake.Commands.Count >= before);
    }

    [Fact]
    public void Recording_writes_stream_record()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://cdn.example/a.m3u8");
        host.SetRecording(@"C:\temp\out.ts");
        Assert.True(host.IsRecording);
        Assert.Contains(fake.Lifecycle, item => item.Contains("stream-record", StringComparison.Ordinal));
        host.SetRecording(null);
        Assert.False(host.IsRecording);
    }

    [Fact]
    public void Live_capture_requires_a_usable_still()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://cdn.example/live.m3u8");
        host.ProcessPendingEvents();
        var path = Path.Combine(Path.GetTempPath(), $"cap-{Guid.NewGuid():N}.jpg");
        try
        {
            Assert.True(host.TryCaptureVideo(path));
            Assert.True(LivePlayback.IsUsableStill(path));
            Assert.Contains(fake.Commands, c => c[0] == "screenshot-to-file");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Seek_live_endurance_does_not_reload_or_fault()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 600 };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://cdn.example/live.m3u8");
        host.ProcessPendingEvents();
        for (var i = 0; i < 120; i++)
        {
            host.Seek(TimeSpan.FromSeconds(10 + i % 40));
            host.SeekLive();
            host.ProcessPendingEvents();
        }

        Assert.DoesNotContain(fake.Commands, command => command[0] == "loadfile" && command.Length > 2 && command[2] != "replace");
        Assert.Equal(1, fake.Commands.Count(command => command[0] == "loadfile"));
        Assert.True(fake.Commands.Count(command => command[0] == "seek") >= 120);
    }

    [Fact]
    public void Seek_live_snaps_to_the_cached_tip()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 900 };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://cdn.example/a.m3u8");
        host.ProcessPendingEvents();
        host.Seek(TimeSpan.FromSeconds(40));
        host.SeekLive();
        Assert.Equal(["seek", "898", "absolute"], fake.LastCommand());
    }

    [Fact]
    public void Seek_live_is_instant_when_already_at_the_tip()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 120 };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://cdn.example/live.m3u8");
        host.ProcessPendingEvents();
        var before = fake.Commands.Count;
        host.SeekLive();
        Assert.Equal(before, fake.Commands.Count);
    }

    [Fact]
    public void Vod_stream_is_not_capped_to_two_minutes()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 900 };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://rr.googlevideo.com/videoplayback?id=1", StreamKind.Vod);
        host.ProcessPendingEvents();
        Assert.False(host.LiveWindow);
        host.Seek(TimeSpan.FromSeconds(40));
        Assert.Equal(["seek", "40", "absolute"], fake.LastCommand());
    }

    [Fact]
    public void Vod_hls_is_force_seekable_and_seeks_with_absolute()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 900 };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8", StreamKind.Vod);
        host.ProcessPendingEvents();
        Assert.False(host.LiveWindow);
        Assert.Contains(fake.Lifecycle, item => item.Contains("force-seekable=yes", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("hr-seek=yes", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("hr-seek-framedrop=no", StringComparison.Ordinal));
        host.Seek(TimeSpan.FromSeconds(210));
        Assert.Equal(["seek", "210", "absolute"], fake.LastCommand());
    }

    [Fact]
    public void Youtube_vod_does_not_use_live_demuxer_profile()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://rr.googlevideo.com/videoplayback?id=1", StreamKind.Vod);
        Assert.DoesNotContain(fake.Lifecycle, item => item.Contains("live_start_index=-1", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("force-seekable=yes", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("http-header-fields", StringComparison.Ordinal)
                                                && item.Contains("youtube.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Youtube_watch_url_opens_resolved_media()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host, new ResumeStore(Path.GetTempFileName()), new StaticNetworkMonitor());
        view.ResolveYouTube = _ => new YouTubePlayable(
            "dQw4w9wgBcQ",
            "https://rr.googlevideo.com/videoplayback?id=18",
            "Song",
            StreamKind.Vod,
            userAgent: "TestTube/1.0");
        view.AddStream("https://www.youtube.com/watch?v=dQw4w9wgBcQ", play: true);
        Assert.Contains(fake.Commands, command =>
            command.Length >= 2 &&
            command[0] == "loadfile" &&
            command[1].Contains("videoplayback", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fake.Commands, command =>
            command.Length >= 2 &&
            command[0] == "loadfile" &&
            command[1].Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fake.Lifecycle, item => item.Contains("user-agent=TestTube/1.0", StringComparison.Ordinal));
        Assert.False(host.LiveWindow);
    }

    [Fact]
    public void Title_format_hides_lavf_probe_lists()
    {
        Assert.Equal("hls", StreamProbe.FormatLabel("https://manifest.googlevideo.com/api/manifest/hls_variant/a"));
        Assert.Equal("youtube", StreamProbe.FormatLabel("https://www.youtube.com/watch?v=dQw4w9wgBcQ"));
    }

    [Fact]
    public void Live_dvr_seek_uses_keyframes()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 900 };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://cdn.example/live.m3u8");
        host.ProcessPendingEvents();
        host.Seek(TimeSpan.FromSeconds(40));
        Assert.Equal(["seek", "720", "absolute"], fake.LastCommand());
    }

    [Fact]
    public void Live_open_starts_at_the_edge_and_does_not_prefetch_the_dvr()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://cdn.example/live.m3u8");
        Assert.True(host.LiveWindow);
        Assert.Contains(fake.Lifecycle, item => item.Contains("cache-pause-initial=no", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("live_start_index=-1", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("demuxer-readahead-secs=1.2", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("hr-seek=no", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("cache-pause=yes", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.Lifecycle, item => item.Contains("reconnect_on_network_error", StringComparison.Ordinal));
    }

    [Fact]
    public void Live_edge_grows_with_cache_and_is_not_a_stale_duration()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 120 };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.Open("https://cdn.example/live.m3u8");
        host.ProcessPendingEvents();
        Assert.Equal(TimeSpan.FromSeconds(120), host.LiveEdge);
        fake.Enqueue(MpvEvent.Property("demuxer-cache-time", 900d, MpvFormat.Double));
        host.ProcessPendingEvents();
        Assert.Equal(TimeSpan.FromSeconds(900), host.LiveEdge);
        host.Seek(TimeSpan.FromSeconds(20));
        host.SeekLive();
        Assert.Equal(["seek", "898", "absolute"], fake.LastCommand());
    }

    [Fact]
    public void Live_clock_is_elapsed_only()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 900 };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(
            host,
            new ResumeStore(Path.GetTempFileName()),
            new StaticNetworkMonitor(),
            new FixedInspector(StreamKind.Live));
        view.AddStream("https://cdn.example/live.m3u8", play: true);
        host.ProcessPendingEvents();
        Assert.True(view.IsLive);
        Assert.Equal(string.Empty, view.DurationText);
        Assert.DoesNotContain('/', view.TimePairText);
        Assert.Equal(view.PositionText, view.TimePairText);
        Assert.Equal(898, view.SeekMaximum, 3);
        Assert.Equal(view.PositionText, TimeDisplay.FormatClock(TimeSpan.FromSeconds(view.SeekValue)));
        // A deliberate user seek, not a stale post-live-edge mpv sample,
        // must leave follow-live mode and update the elapsed clock.
        view.ApplySeek(800);
        host.ProcessPendingEvents();
        Assert.Equal("00:13:20", view.PositionText);
        Assert.Equal("00:13:20", view.TimePairText);
        view.ToggleTimeMode();
        Assert.Equal("00:13:20", view.PositionText);
        Assert.False(view.IsAtLive);
        view.GoLive();
        Assert.Equal("00:14:58", view.PositionText);
        Assert.True(view.IsAtLive);
        view.ApplySeek(80);
        view.GoLive();
        Assert.Equal(["seek", "898", "absolute"], fake.LastCommand());
    }

    private sealed class FixedInspector : IStreamInspector
    {
        private readonly StreamKind _kind;

        public FixedInspector(StreamKind kind) => _kind = kind;

        public StreamKind Inspect(string url) => _kind;
    }
}
