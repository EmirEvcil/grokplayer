using Grok.Player.Core.Download;
using Grok.Player.Core.Media;
using Grok.Player.Core.Playlist;

namespace Grok.Player.Core.Tests;

public sealed class DownloadTests
{
    [Theory]
    [InlineData(1072, 1080)]
    [InlineData(1080, 1080)]
    [InlineData(720, 720)]
    [InlineData(1420, 1440)]
    [InlineData(0, 0)]
    public void Height_snaps_to_common_youtube_rungs(int raw, int expected)
    {
        Assert.Equal(expected, HlsPlaylist.NormalizeHeight(raw));
    }

    [Fact]
    public void Master_playlist_picks_requested_height()
    {
        var master =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360
            low.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720
            mid.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080
            high.m3u8
            """;
        var variants = HlsPlaylist.Variants(master, "https://cdn.example/master.m3u8");
        Assert.Equal(3, variants.Count);
        Assert.Equal("https://cdn.example/mid.m3u8", HlsPlaylist.Pick(variants, 720)!.Url);
        Assert.Equal("https://cdn.example/high.m3u8", HlsPlaylist.Pick(variants, 0)!.Url);
        Assert.Equal("https://cdn.example/low.m3u8", HlsPlaylist.Pick(variants, 360)!.Url);
    }

    [Fact]
    public void Quoted_resolution_and_muxed_variant_are_preferred()
    {
        var master =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION="1920x1080",CODECS="avc1.640028"
            video-only.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=4500000,RESOLUTION="1920x1080",CODECS="avc1.640028,mp4a.40.2"
            muxed.m3u8
            """;
        var variants = HlsPlaylist.Variants(master, "https://cdn.example/master.m3u8");
        Assert.Equal(1920, variants[0].Width);
        Assert.Equal(1080, variants[0].Height);
        Assert.True(variants[0].LooksVideoOnly);
        Assert.False(variants[1].LooksVideoOnly);
        Assert.Equal("https://cdn.example/muxed.m3u8", HlsPlaylist.Pick(variants, 1080)!.Url);
    }

    [Fact]
    public void Media_playlist_reads_segments_and_vod_marker()
    {
        var media =
            """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXTINF:6.0,
            a.ts
            #EXTINF:4.0,
            b.ts
            #EXT-X-ENDLIST
            """;
        Assert.False(HlsPlaylist.IsLive(media));
        var parsed = HlsPlaylist.Media(media, "https://cdn.example/play.m3u8");
        Assert.Equal(2, parsed.Segments.Count);
        Assert.Equal("https://cdn.example/a.ts", parsed.Segments[0].Url);
        Assert.Equal(6, parsed.Segments[0].Duration);
    }

    [Fact]
    public void Live_playlist_without_endlist_is_rejected_as_live()
    {
        var live = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\na.ts\n";
        Assert.True(HlsPlaylist.IsLive(live));
    }

    [Fact]
    public void Vod_items_are_downloadable_live_is_not()
    {
        var vod = new PlaylistItem("https://cdn.example/a.mp4", PlaylistKind.Stream);
        vod.StreamKind = StreamKind.Vod;
        var live = new PlaylistItem("https://cdn.example/live.m3u8", PlaylistKind.Stream);
        live.StreamKind = StreamKind.Live;
        var youtube = new PlaylistItem("https://www.youtube.com/watch?v=dQw4w9wgBcQ", PlaylistKind.Stream);
        Assert.True(DownloadManager.IsVod(vod));
        Assert.False(DownloadManager.IsVod(live));
        Assert.True(DownloadManager.IsVod(youtube));
        Assert.False(DownloadManager.IsVod(new PlaylistItem(@"C:\a.mp4")));
    }

    [Fact]
    public void Settings_roundtrip_folder_and_caps()
    {
        var path = Path.Combine(Path.GetTempPath(), "dl-set-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new DownloadSettings
        {
            Folder = @"D:\media\grok",
            MaxHeight = 720,
            MaxParallel = 3
        };
        settings.Save(path);
        var loaded = DownloadSettings.Load(path);
        Assert.Equal(@"D:\media\grok", loaded.Folder);
        Assert.Equal(720, loaded.MaxHeight);
        Assert.Equal(3, loaded.MaxParallel);
        Assert.Equal("mp4", loaded.Container);
        settings.Container = "MKV";
        settings.Save(path);
        Assert.Equal("mkv", DownloadSettings.Load(path).Container);
        File.Delete(path);
    }

    [Fact]
    public void Manager_enqueues_and_downloads_a_progressive_file()
    {
        var source = Path.Combine(Path.GetTempPath(), "src-" + Guid.NewGuid().ToString("N") + ".bin");
        var folder = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(source, [1, 2, 3, 4, 5, 6, 7, 8]);
        using var handler = new FileHandler(source);
        using var manager = new DownloadManager(new DownloadSettings { Folder = folder, MaxParallel = 1 }, handler);
        var job = manager.Enqueue("https://cdn.example/clip.mp4", "Clip", start: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && job.State is DownloadState.Queued or DownloadState.Running)
        {
            Thread.Sleep(20);
        }

        Assert.True(job.State == DownloadState.Completed, job.State + ": " + job.Error);
        Assert.True(File.Exists(job.OutputPath));
        Assert.Equal(8, new FileInfo(job.OutputPath).Length);

        manager.Delete(job.Id);
        Assert.DoesNotContain(manager.Jobs, item => item.Id == job.Id);
        Assert.False(File.Exists(job.OutputPath));
        Assert.DoesNotContain(manager.Jobs, item => item.State == DownloadState.Canceled);
        Directory.Delete(folder, true);
        File.Delete(source);
    }

    [Fact]
    public void Delete_of_done_job_with_leftover_token_removes_file()
    {
        var source = Path.Combine(Path.GetTempPath(), "src-" + Guid.NewGuid().ToString("N") + ".bin");
        var folder = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(source, [9, 8, 7, 6]);
        using var handler = new FileHandler(source);
        using var manager = new DownloadManager(new DownloadSettings { Folder = folder, MaxParallel = 1 }, handler);
        var job = manager.Enqueue("https://cdn.example/clip.mp4", "DoneClip", start: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && job.State is DownloadState.Queued or DownloadState.Running)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(DownloadState.Completed, job.State);
        var tokens = typeof(DownloadManager)
            .GetField("_tokens", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(tokens);
        var map = (System.Collections.IDictionary)tokens!.GetValue(manager)!;
        map[job.Id] = new CancellationTokenSource();
        var path = job.OutputPath;
        Assert.True(File.Exists(path));

        manager.Delete(job.Id);

        Assert.Empty(manager.Jobs);
        Assert.False(File.Exists(path));
        Directory.Delete(folder, true);
        File.Delete(source);
    }

    [Fact]
    public void Hls_audio_uri_matches_youtube_dub_codes()
    {
        var master =
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",NAME="English",DEFAULT=YES,LANGUAGE="en",URI="en.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",NAME="Turkish",DEFAULT=NO,LANGUAGE="tr",URI="tr.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080,CODECS="avc1.640028",AUDIO="audio"
            video.m3u8
            """;
        Assert.Equal("https://cdn.example/tr.m3u8", HlsPlaylist.AudioUri(master, "https://cdn.example/master.m3u8", "tr.3"));
        Assert.Equal("https://cdn.example/tr.m3u8", HlsPlaylist.AudioUri(master, "https://cdn.example/master.m3u8", "tr"));
        Assert.Equal("https://cdn.example/en.m3u8", HlsPlaylist.AudioUri(master, "https://cdn.example/master.m3u8", null));
    }

    [Fact]
    public void Audio_uri_does_not_fall_back_to_first_arabic_when_turkish_requested()
    {
        var master =
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="العربية - dubbed",LANGUAGE="ar",URI="ar.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="Türkçe - dubbed",LANGUAGE="tr",URI="tr.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="English - original",LANGUAGE="en",URI="en.m3u8"
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="vtt",NAME="English",LANGUAGE="en",URI="en.vtt"
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="vtt",NAME="Türkçe",LANGUAGE="tr",URI="tr.vtt"
            #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1280x720,CODECS="avc1.4D401F,mp4a.40.2",AUDIO="234",SUBTITLES="vtt"
            video.m3u8
            """;
        Assert.Equal("https://cdn.example/tr.m3u8", HlsPlaylist.AudioUri(master, "https://cdn.example/master.m3u8", "tr", "234"));
        Assert.Equal("https://cdn.example/tr.vtt", HlsPlaylist.SubtitleUri(master, "https://cdn.example/master.m3u8", "tr"));
        Assert.Null(HlsPlaylist.SubtitleUri(master, "https://cdn.example/master.m3u8", "de"));
    }

    [Fact]
    public void Audio_group_is_used_even_when_codecs_list_aac()
    {
        var master =
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="233",NAME="Default",DEFAULT=YES,URI="low-audio.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="Default",DEFAULT=YES,URI="high-audio.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2",AUDIO="234"
            video.m3u8
            """;
        var variants = HlsPlaylist.Variants(master, "https://cdn.example/master.m3u8");
        Assert.False(variants[0].LooksVideoOnly);
        Assert.Equal("234", variants[0].Audio);
        Assert.Equal(
            "https://cdn.example/high-audio.m3u8",
            HlsPlaylist.AudioUri(master, "https://cdn.example/master.m3u8", null, variants[0].Audio));
    }

    [Fact]
    public void Bind_master_without_language_keeps_the_remote_master()
    {
        var master =
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="العربية - dubbed",LANGUAGE="ar",DEFAULT=NO,AUTOSELECT=YES,URI="https://manifest.googlevideo.com/api/manifest/hls_playlist/ar.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="Türkçe - dubbed",LANGUAGE="tr",DEFAULT=NO,AUTOSELECT=YES,URI="https://manifest.googlevideo.com/api/manifest/hls_playlist/tr.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1280x720,CODECS="avc1.4D401F",AUDIO="234"
            https://manifest.googlevideo.com/api/manifest/hls_playlist/video.m3u8
            """;
        var playable = new YouTubePlayable(
            "Qtl8lJwbd4g",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/master.m3u8",
            "Escape",
            StreamKind.Vod);
        var bound = YouTubeCatalog.BindMaster(playable, master);
        Assert.Contains("hls_variant", bound.MediaUrl, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(bound.AudioUrl));
        Assert.False(YouTubeCatalog.UsesSeparateAudio(bound));
        Assert.Equal(
            "https://manifest.googlevideo.com/api/manifest/hls_playlist/tr.m3u8",
            HlsPlaylist.AudioUri(master, playable.MediaUrl, "tr", fallback: false));
        Assert.Null(HlsPlaylist.AudioUri(master, playable.MediaUrl, "de", fallback: false));
    }

    [Fact]
    public void Original_audio_falls_back_when_requested_dub_is_missing()
    {
        var master =
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="العربية - dubbed",LANGUAGE="ar",URI="https://x/ar.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="English - original",LANGUAGE="en",URI="https://x/en.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="Türkçe - dubbed",LANGUAGE="tr",URI="https://x/tr.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1280x720,CODECS="avc1.4D401F",AUDIO="234"
            https://x/video.m3u8
            """;
        Assert.Equal("https://x/en.m3u8", HlsPlaylist.AudioUri(master, "https://x/master.m3u8", "original", fallback: false));
        Assert.Equal("https://x/tr.m3u8", HlsPlaylist.AudioUri(master, "https://x/master.m3u8", "tr", fallback: false));
        var missing = new YouTubePlayable(
            "abcdefghijk",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/master.m3u8",
            "Clip",
            StreamKind.Vod,
            audioLang: "de");
        var bound = YouTubeCatalog.BindMaster(missing, master);
        Assert.Equal("https://x/en.m3u8", bound.AudioUrl);
        Assert.True(YouTubeCatalog.UsesSeparateAudio(bound));
    }

    [Fact]
    public void Bind_master_locks_the_requested_height()
    {
        var master =
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="English - original",LANGUAGE="en",URI="https://x/en.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720,CODECS="avc1.4D401F",AUDIO="234"
            https://x/720.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS="avc1.640028",AUDIO="234"
            https://x/1080.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=12000000,RESOLUTION=2560x1440,CODECS="hvc1.1.6.L120",AUDIO="234"
            https://x/1440.m3u8
            """;
        var playable = new YouTubePlayable(
            "abcdefghijk",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/master.m3u8",
            "Clip",
            StreamKind.Vod,
            audioLang: "en");
        var at1080 = YouTubeCatalog.BindMaster(playable, master, 1080);
        Assert.Equal("https://x/1080.m3u8", at1080.MediaUrl);
        var best = YouTubeCatalog.BindMaster(playable, master, 0);
        Assert.Equal("https://x/1440.m3u8", best.MediaUrl);
        var muxedMaster =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
            https://x/muxed.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080,CODECS="avc1.640028",AUDIO="234"
            https://x/video.m3u8
            """;
        var muxed = YouTubeCatalog.BindMaster(
            new YouTubePlayable("abcdefghijk", "https://x/master.m3u8", "Clip", StreamKind.Vod),
            muxedMaster,
            1080);
        Assert.Equal("https://x/muxed.m3u8", muxed.MediaUrl);
        Assert.True(string.IsNullOrWhiteSpace(muxed.AudioUrl));
    }

    [Fact]
    public void Bind_master_plays_remote_variant_with_selected_audio()
    {
        var master =
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="العربية - dubbed",LANGUAGE="ar",DEFAULT=NO,AUTOSELECT=YES,URI="https://manifest.googlevideo.com/api/manifest/hls_playlist/ar.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="Türkçe - dubbed",LANGUAGE="tr",DEFAULT=NO,AUTOSELECT=YES,URI="https://manifest.googlevideo.com/api/manifest/hls_playlist/tr.m3u8"
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="vtt",NAME="Türkçe",LANGUAGE="tr",URI="https://manifest.googlevideo.com/api/manifest/hls_timedtext/tr.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1280x720,CODECS="avc1.4D401F",AUDIO="234"
            https://manifest.googlevideo.com/api/manifest/hls_playlist/video.m3u8
            """;
        var playable = new YouTubePlayable(
            "Qtl8lJwbd4g",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/master.m3u8",
            "Escape",
            StreamKind.Vod,
            audioLang: "tr",
            subLang: "tr");
        var bound = YouTubeCatalog.BindMaster(playable, master);
        Assert.Equal("https://manifest.googlevideo.com/api/manifest/hls_playlist/video.m3u8", bound.MediaUrl);
        Assert.Equal("https://manifest.googlevideo.com/api/manifest/hls_playlist/tr.m3u8", bound.AudioUrl);
        Assert.True(YouTubeCatalog.UsesSeparateAudio(bound));
        Assert.StartsWith("https://", bound.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, bound.MediaUrl);
    }

    [Fact]
    public void Bind_master_resolves_bangla_russian_and_chinese()
    {
        var master =
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="Arabic - dubbed",LANGUAGE="ar",URI="https://x/ar.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="Bangla - dubbed",LANGUAGE="bn",URI="https://x/bn.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="Chinese (Simplified) - dubbed",LANGUAGE="zh-Hans",URI="https://x/zh-Hans.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="Chinese (Traditional) - dubbed",LANGUAGE="zh-Hant",URI="https://x/zh-Hant.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="English original",LANGUAGE="en",URI="https://x/en.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="Russian - dubbed",LANGUAGE="ru",URI="https://x/ru.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="Türkçe - dubbed",LANGUAGE="tr",URI="https://x/tr.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1280x720,CODECS="avc1.4D401F",AUDIO="234"
            https://x/video.m3u8
            """;
        foreach (var lang in new[] { "bn", "ru", "zh-Hans", "zh-Hant", "tr" })
        {
            var bound = YouTubeCatalog.BindMaster(
                new YouTubePlayable("Qtl8lJwbd4g", "https://x/master.m3u8", "Escape", StreamKind.Vod, audioLang: lang),
                master);
            Assert.Equal("https://x/" + lang + ".m3u8", bound.AudioUrl);
        }

        var original = YouTubeCatalog.BindMaster(
            new YouTubePlayable("Qtl8lJwbd4g", "https://x/master.m3u8", "Escape", StreamKind.Vod, audioLang: "original"),
            master);
        Assert.Equal("https://x/en.m3u8", original.AudioUrl);
        Assert.Equal("https://x/bn.m3u8", HlsPlaylist.AudioUri(master, "https://x/master.m3u8", "bn"));
        Assert.Equal("https://x/zh-Hans.m3u8", HlsPlaylist.AudioUri(master, "https://x/master.m3u8", "zh-Hans"));
        Assert.NotEqual(
            HlsPlaylist.AudioUri(master, "https://x/master.m3u8", "zh-Hans"),
            HlsPlaylist.AudioUri(master, "https://x/master.m3u8", "zh-Hant"));
    }

    [Fact]
    public void Pin_renditions_marks_selected_dub_and_caption_default()
    {
        var master =
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="العربية - dubbed",LANGUAGE="ar",DEFAULT=NO,AUTOSELECT=YES,URI="ar.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="234",NAME="Türkçe - dubbed",LANGUAGE="tr",DEFAULT=NO,AUTOSELECT=YES,URI="tr.m3u8"
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="vtt",NAME="English",LANGUAGE="en",DEFAULT=NO,AUTOSELECT=YES,URI="en.vtt"
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="vtt",NAME="Türkçe",LANGUAGE="tr",DEFAULT=NO,AUTOSELECT=YES,URI="tr.vtt"
            #EXT-X-STREAM-INF:BANDWIDTH=4000000,AUDIO="234",SUBTITLES="vtt"
            video.m3u8
            """;
        var pinned = HlsPlaylist.PinRenditions(master, "tr", "tr");
        Assert.Contains("LANGUAGE=\"tr\",DEFAULT=YES,AUTOSELECT=YES,URI=\"tr.m3u8\"", pinned, StringComparison.Ordinal);
        Assert.Contains("LANGUAGE=\"ar\",DEFAULT=NO,AUTOSELECT=NO,URI=\"ar.m3u8\"", pinned, StringComparison.Ordinal);
        Assert.Contains("LANGUAGE=\"tr\",DEFAULT=YES,AUTOSELECT=YES,URI=\"tr.vtt\"", pinned, StringComparison.Ordinal);
        Assert.Contains("LANGUAGE=\"en\",DEFAULT=NO,AUTOSELECT=NO,URI=\"en.vtt\"", pinned, StringComparison.Ordinal);
        Assert.DoesNotContain("LANGUAGE=\"ar\",DEFAULT=YES", pinned, StringComparison.Ordinal);
    }

    [Fact]
    public void Dump_plan_muxes_audio_into_mkv()
    {
        var opts = StreamDump.CreateOptions(@"D:\dl\clip.mp4", "https://cdn.example/tr.m3u8", "tr.3", 4_000_000);
        Assert.Equal(".mp4", Path.GetExtension(opts.Output), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("mp4", opts.Format);
        Assert.Equal("mkv", StreamDump.CreateOptions(@"D:\dl\clip.mkv", null, null, 0).Format);
        Assert.Equal("https://cdn.example/tr.m3u8", opts.AudioFile);
        Assert.Equal("tr", opts.AudioLang);
    }

    [Fact]
    public void Ffmpeg_remux_merges_video_and_audio()
    {
        var ffmpeg = FfmpegMux.Find();
        if (ffmpeg is null)
        {
            return;
        }

        var src = Path.Combine(Path.GetTempPath(), "grok-mux-src.mp4");
        var video = Path.Combine(Path.GetTempPath(), "grok-mux-v.mp4");
        var audio = Path.Combine(Path.GetTempPath(), "grok-mux-a.m4a");
        var dest = Path.Combine(Path.GetTempPath(), "grok-mux-out.mkv");
        Run(ffmpeg, $"-y -f lavfi -i testsrc=duration=1:size=160x120:rate=12 -f lavfi -i sine=frequency=440:duration=1 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"{src}\"");
        Run(ffmpeg, $"-y -i \"{src}\" -an -c:v copy \"{video}\"");
        Run(ffmpeg, $"-y -i \"{src}\" -vn -c:a copy \"{audio}\"");
        Assert.True(FfmpegMux.TryRemux(video, audio, dest), FfmpegMux.LastError);
        Assert.True(new FileInfo(dest).Length > 1024);
    }

    private static void Run(string file, string args)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        using var process = System.Diagnostics.Process.Start(start);
        Assert.NotNull(process);
        process!.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Hls_download_requests_the_dubbed_audio_playlist()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var handler = new HlsHandler();
        using var manager = new DownloadManager(new DownloadSettings { Folder = folder, MaxParallel = 1 }, handler);
        var job = manager.Enqueue("https://cdn.example/master.m3u8", "Dub", start: true, audioLang: "tr.3");
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until && job.State is DownloadState.Queued or DownloadState.Running)
        {
            Thread.Sleep(40);
        }

        Assert.Contains(handler.Requested, url => url.Contains("audio-tr.m3u8", StringComparison.OrdinalIgnoreCase));
        Directory.Delete(folder, true);
    }

    [Fact]
    public void Parallel_jobs_use_the_selected_download_height()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var manager = new DownloadManager(new DownloadSettings
        {
            Folder = folder,
            MaxHeight = 360,
            MaxParallel = 2
        });
        var first = manager.Enqueue("https://cdn.example/one.mp4", "One", start: false);
        var second = manager.Enqueue("https://cdn.example/two.mp4", "Two", start: false, audioLang: "tr", maxHeight: 0);
        Assert.Equal(360, first.MaxHeight);
        Assert.Equal(360, second.MaxHeight);
        Directory.Delete(folder, true);
    }

    [Fact]
    public void Download_writes_a_sidecar_subtitle()
    {
        var source = Path.Combine(Path.GetTempPath(), "src-" + Guid.NewGuid().ToString("N") + ".bin");
        var folder = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        var captions = Path.Combine(Path.GetTempPath(), "cap-" + Guid.NewGuid().ToString("N") + ".vtt");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(source, [1, 2, 3, 4, 5, 6, 7, 8]);
        File.WriteAllText(captions, "WEBVTT\nLanguage: English\n\n00:00:00.000 --> 00:00:01.000\nHello there\n");
        using var handler = new FileHandler(source);
        using var manager = new DownloadManager(new DownloadSettings { Folder = folder, MaxParallel = 1 }, handler);
        var job = manager.Enqueue(
            "https://cdn.example/clip.mp4",
            "ClipSubs",
            start: true,
            subLang: "en",
            captionUrl: captions);
        var until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until && job.State is DownloadState.Queued or DownloadState.Running)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(DownloadState.Completed, job.State);
        var sidecar = Path.ChangeExtension(job.OutputPath, null) + ".en.srt";
        Assert.True(File.Exists(sidecar), "missing " + sidecar);
        Assert.Contains("Hello there", File.ReadAllText(sidecar), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.ChangeExtension(job.OutputPath, ".srt")));
        Directory.Delete(folder, true);
        File.Delete(source);
        File.Delete(captions);
    }

    [Fact]
    public void Master_txt_and_proxy_urls_are_treated_as_hls()
    {
        Assert.True(DownloadManager.LooksLikeHls("https://hls8.playmix.uno/hls/film.mp4/master.txt"));
        Assert.True(DownloadManager.LooksLikeHls(
            "https://fastplay.mom/manifests/episode/master.txt?verify=123-proof"));
        Assert.True(DownloadManager.LooksLikeHls("https://cdn.example/playlist.txt"));
        Assert.False(DownloadManager.LooksLikeHls("https://cdn.example/clip.mp4"));
    }

    [Fact]
    public void Hls_download_follows_a_master_txt_playlist()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var handler = new HlsHandler();
        using var manager = new DownloadManager(new DownloadSettings { Folder = folder, MaxParallel = 1 }, handler);
        var job = manager.Enqueue("https://cdn.example/master.txt", "Packed", start: true);
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until && job.State is DownloadState.Queued or DownloadState.Running)
        {
            Thread.Sleep(40);
        }

        Assert.Contains(handler.Requested, url => url.Contains("video.m3u8", StringComparison.OrdinalIgnoreCase));
        Directory.Delete(folder, true);
    }

    [Fact]
    public void Download_sends_the_site_referer_not_youtube()
    {
        var source = Path.Combine(Path.GetTempPath(), "src-" + Guid.NewGuid().ToString("N") + ".bin");
        var folder = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(source, [1, 2, 3, 4, 5, 6, 7, 8]);
        using var handler = new HeaderHandler(source);
        using var manager = new DownloadManager(new DownloadSettings { Folder = folder, MaxParallel = 1 }, handler);
        var job = manager.Enqueue("https://proxy.dmcdn.net/clip.mp4", "Daily", start: true);
        var until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until && job.State is DownloadState.Queued or DownloadState.Running)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(DownloadState.Completed, job.State);
        Assert.Contains(handler.Referers, item => item.Contains("dailymotion.com", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.Referers, item => item.Contains("youtube.com", StringComparison.OrdinalIgnoreCase));
        Directory.Delete(folder, true);
        File.Delete(source);
    }

    [Fact]
    public void Playlist_stub_is_not_marked_complete()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var handler = new TextHandler("<html>not a video</html>");
        using var manager = new DownloadManager(new DownloadSettings { Folder = folder, MaxParallel = 1 }, handler);
        var job = manager.Enqueue("https://cdn.example/clip.mp4", "Stub", start: true);
        var until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until && job.State is DownloadState.Queued or DownloadState.Running)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Contains("video file", job.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(job.OutputPath));
        Directory.Delete(folder, true);
    }

    [Fact]
    public void Download_writes_every_language_sidecar()
    {
        var source = Path.Combine(Path.GetTempPath(), "src-" + Guid.NewGuid().ToString("N") + ".bin");
        var folder = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        var english = Path.Combine(Path.GetTempPath(), "en-" + Guid.NewGuid().ToString("N") + ".vtt");
        var turkish = Path.Combine(Path.GetTempPath(), "tr-" + Guid.NewGuid().ToString("N") + ".vtt");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(source, [1, 2, 3, 4, 5, 6, 7, 8]);
        File.WriteAllText(english, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello\n");
        File.WriteAllText(turkish, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nMerhaba\n");
        using var handler = new FileHandler(source);
        using var manager = new DownloadManager(new DownloadSettings { Folder = folder, MaxParallel = 1 }, handler);
        var job = manager.Enqueue(
            "https://cdn.example/clip.mp4",
            "BothSubs",
            start: true,
            subLang: "tr",
            captions:
            [
                new Grok.Player.Core.Launch.ExternalCaption("en", english, "English"),
                new Grok.Player.Core.Launch.ExternalCaption("tr", turkish, "Turkish")
            ]);
        var until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until && job.State is DownloadState.Queued or DownloadState.Running)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(DownloadState.Completed, job.State);
        var stem = Path.ChangeExtension(job.OutputPath, null);
        Assert.Contains("Hello", File.ReadAllText(stem + ".en.srt"), StringComparison.Ordinal);
        Assert.Contains("Merhaba", File.ReadAllText(stem + ".tr.srt"), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.ChangeExtension(job.OutputPath, ".srt")));
        Directory.Delete(folder, true);
        File.Delete(source);
        File.Delete(english);
        File.Delete(turkish);
    }

    [Fact]
    public void Sidecar_language_reads_asr_tag_from_filename()
    {
        Assert.Equal("tr:asr", Grok.Player.Core.Presentation.PlaybackViewModel.SidecarLanguage("tr-asr"));
        Assert.Equal("en:asr", Grok.Player.Core.Presentation.PlaybackViewModel.SidecarLanguage("en.asr"));
        Assert.Equal("tr", Grok.Player.Core.Presentation.PlaybackViewModel.SidecarLanguage("tr"));
    }

    [Fact]
    public void Selected_youtube_translate_is_kept_next_to_asr()
    {
        var job = new DownloadJob("https://www.youtube.com/watch?v=EmkUwzOG8HU", "Clip", @"D:\clip.mp4")
        {
            SubLang = "de",
            CaptionUrl = "https://www.youtube.com/api/timedtext?v=EmkUwzOG8HU&lang=tr&kind=asr&tlang=de&fmt=vtt"
        };
        DownloadManager.AddCaption(job, new Grok.Player.Core.Launch.ExternalCaption(
            "tr:asr",
            "https://www.youtube.com/api/timedtext?v=EmkUwzOG8HU&lang=tr&kind=asr&fmt=vtt",
            "Turkish"));
        DownloadManager.AddSelectedCaption(job);
        Assert.Contains(job.Captions, item => item.Language == "de" && YouTubeCatalog.CaptionUrlIsTranslate(item.Url));
        Assert.Contains(job.Captions, item => item.Language == "tr:asr");
        Assert.Equal(
            "https://www.dailymotion.com/video/xap6qz2",
            DownloadManager.PageReferer("https://www.dailymotion.com/", "https://www.dailymotion.com/video/xap6qz2"));
    }

    [Fact]
    public void Caption_sidecar_path_keeps_asr_off_windows_filename()
    {
        var dest = DownloadManager.LanguageSidecarPath(@"D:\media\clip.mp4", "tr:asr");
        Assert.Equal(@"D:\media\clip.tr-asr.srt", dest);
        Assert.DoesNotContain(':', Path.GetFileName(dest));
        Assert.Equal("tr-asr", DownloadManager.SafeLangTag("tr:asr"));
        Assert.Equal("tr", DownloadManager.SafeLangTag("tr"));
        Assert.Equal("tr", DownloadManager.SafeLangTag("tur"));
        Assert.EndsWith(".tr.srt", DownloadManager.LanguageSidecarPath(@"D:\media\clip.mp4", "tur"), StringComparison.Ordinal);
        Assert.EndsWith(".en.srt", DownloadManager.LanguageSidecarPath(@"D:\media\clip.mp4", "eng"), StringComparison.Ordinal);
    }

    [Fact]
    public void Error_page_title_is_not_used_as_the_download_name()
    {
        Assert.True(StreamCatalog.LooksLikeErrorTitle("404"));
        Assert.True(StreamCatalog.LooksLikeErrorTitle("403 Forbidden"));
        Assert.Equal(
            "breaking-bad-1-sezon-1-bolum-1-izle-16",
            StreamCatalog.UsableTitle(
                "404",
                "https://www.hdfilmcehennemi.now/bolum/breaking-bad-1-sezon-1-bolum-1-izle-16/"));
    }

    [Fact]
    public void Remux_writes_language_titles_for_each_audio()
    {
        var args = FfmpegMux.AudioMetadataArgs(
        [
            (@"D:\a.bin", "tur", "Türkçe"),
            (@"D:\b.bin", "eng", "English")
        ]);
        Assert.Contains("language=tur", args, StringComparison.Ordinal);
        Assert.Contains("language=eng", args, StringComparison.Ordinal);
        Assert.Contains("title=\"Türkçe\"", args, StringComparison.Ordinal);
        Assert.Contains("title=\"English\"", args, StringComparison.Ordinal);
    }

    [Fact]
    public void Hls_master_lists_every_audio_rendition()
    {
        const string master =
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",LANGUAGE="tur",NAME="Turkish",URI="audio-tr.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",LANGUAGE="eng",NAME="Original",URI="audio-en.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=800000,AUDIO="a"
            video.m3u8
            """;
        var tracks = HlsPlaylist.AudioTracks(master, "https://cdn.example/master.m3u8");
        Assert.Equal(2, tracks.Count);
        Assert.Contains(tracks, item => item.Language == "tur" && item.Url.EndsWith("audio-tr.m3u8", StringComparison.Ordinal));
        Assert.Contains(tracks, item => item.Language == "eng" && item.Url.EndsWith("audio-en.m3u8", StringComparison.Ordinal));
    }

    private sealed class HlsHandler : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Respond(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var url = request.RequestUri?.ToString() ?? "";
            Requested.Add(url);
            var body = url switch
            {
                "https://cdn.example/master.m3u8" or "https://cdn.example/master.txt" =>
                    "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"Turkish\",LANGUAGE=\"tr\",URI=\"audio-tr.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS=\"avc1.4d401e\",AUDIO=\"a\"\nvideo.m3u8\n",
                "https://cdn.example/video.m3u8" =>
                    "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2.0,\nv0.ts\n#EXT-X-ENDLIST\n",
                "https://cdn.example/audio-tr.m3u8" =>
                    "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2.0,\na0.ts\n#EXT-X-ENDLIST\n",
                _ => "ts"
            };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
        }
    }

    private sealed class HeaderHandler : HttpMessageHandler
    {
        private readonly string _path;

        public HeaderHandler(string path) => _path = path;

        public List<string> Referers { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Respond(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Referers.Add(request.Headers.Referrer?.ToString() ??
                         (request.Headers.TryGetValues("Referer", out var values)
                             ? string.Join(",", values)
                             : ""));
            var bytes = File.ReadAllBytes(_path);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentLength = bytes.Length;
            return response;
        }
    }

    private sealed class TextHandler : HttpMessageHandler
    {
        private readonly string _body;

        public TextHandler(string body) => _body = body;

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Respond();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond());

        private HttpResponseMessage Respond() =>
            new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_body)
            };
    }

    private sealed class FileHandler : HttpMessageHandler
    {
        private readonly string _path;

        public FileHandler(string path) => _path = path;

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Respond();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond());

        private HttpResponseMessage Respond()
        {
            var bytes = File.ReadAllBytes(_path);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentLength = bytes.Length;
            return response;
        }
    }
}
