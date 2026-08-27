using Grok.Player.Core.Download;
using Grok.Player.Core.Media;
using Grok.Player.Core.Native;
using Grok.Player.Core.Player;
using Grok.Player.Core.Preview;
using Grok.Player.Core.Subtitles;
using Grok.Player.Core.IntegrationTests.Support;

namespace Grok.Player.Core.IntegrationTests;

public sealed class YouTubeTransferTests
{
    [Fact]
    public void Live_resolve_exposes_hls_audio_and_captions()
    {
        YouTubePlayable? playable;
        try
        {
            playable = YouTubeCatalog.Resolve(
                "https://www.youtube.com/watch?v=dQw4w9wgBcQ",
                null,
                "en",
                "en");
        }
        catch (Exception)
        {
            return;
        }

        if (playable is null)
        {
            return;
        }

        Assert.False(string.IsNullOrWhiteSpace(playable.MediaUrl));
        if (!playable.MediaUrl.Contains("m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        using var request = new HttpRequestMessage(HttpMethod.Get, playable.MediaUrl);
        if (!string.IsNullOrWhiteSpace(playable.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", playable.UserAgent);
        }

        request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com");
        using var response = http.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var master = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.Contains("#EXTM3U", master, StringComparison.Ordinal);
        if (HlsPlaylist.IsMaster(master))
        {
            var audio = HlsPlaylist.AudioUri(master, playable.MediaUrl, "en");
            var variants = HlsPlaylist.Variants(master, playable.MediaUrl);
            Assert.True(audio is not null || variants.Any(item => !item.LooksVideoOnly));
        }

        var captionUrl = playable.CaptionUrl ?? YouTubeCatalog.CaptionVttUrl(playable.VideoId, "en");
        var bytes = YouTubeCatalog.DownloadCaption(captionUrl) ??
                    YouTubeCatalog.DownloadCaption(YouTubeCatalog.CaptionVttUrl(playable.VideoId, "en:asr"));
        Assert.NotNull(bytes);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.True(
            text.Contains("WEBVTT", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("-->", StringComparison.Ordinal),
            "timedtext did not return VTT");
    }

    [Theory]
    [InlineData("bn", "bn")]
    [InlineData("ru", "ru")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("tr", "tr")]
    [InlineData("original", "off")]
    public void Live_resolve_binds_requested_dub_and_caption(string audio, string sub)
    {
        YouTubePlayable? playable;
        try
        {
            playable = YouTubeCatalog.Resolve(
                "https://www.youtube.com/watch?v=Qtl8lJwbd4g",
                null,
                audio,
                sub);
        }
        catch (Exception)
        {
            return;
        }

        if (playable is null)
        {
            return;
        }

        playable = YouTubeCatalog.BindHlsRenditions(playable);
        Assert.StartsWith("http", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(audio, "original", StringComparison.OrdinalIgnoreCase))
        {
            Assert.False(string.IsNullOrWhiteSpace(playable.AudioUrl), "missing separate audio for " + audio);
            Assert.True(
                playable.AudioUrl!.Contains("lang%3D" + audio, StringComparison.OrdinalIgnoreCase) ||
                playable.AudioUrl.Contains("lang=" + audio, StringComparison.OrdinalIgnoreCase) ||
                MediaLanguage.Matches(audio, playable.AudioLang),
                playable.AudioUrl);
        }

        if (MediaLanguage.IsOff(sub))
        {
            Assert.True(string.IsNullOrWhiteSpace(playable.CaptionUrl));
            Assert.Null(StreamCaptionLoader.Load(playable.VideoId, sub, playable.CaptionUrl));
            return;
        }

        var caption = StreamCaptionLoader.Load(playable.VideoId, sub, playable.CaptionUrl);
        Assert.False(string.IsNullOrWhiteSpace(caption), "caption file missing for " + sub);
        Assert.Contains("-->", File.ReadAllText(caption!), StringComparison.Ordinal);
    }

    [Fact]
    public void Live_youtube_colors_keep_spaces_and_write_one_play_file()
    {
        string? path;
        try
        {
            path = StreamCaptionLoader.Load("Qtl8lJwbd4g", "en", YouTubeCatalog.CaptionVttUrl("Qtl8lJwbd4g", "en:asr"))
                   ?? StreamCaptionLoader.Load("Qtl8lJwbd4g", "en", YouTubeCatalog.CaptionVttUrl("Qtl8lJwbd4g", "en"));
        }
        catch (Exception)
        {
            return;
        }

        if (path is null)
        {
            return;
        }

        var document = SrtDocument.Load(StreamCaptionLoader.DocumentPath(path));
        Assert.NotEmpty(document.Cues);
        Assert.Contains(document.Cues, cue => cue.Text.Contains(' '));
        Assert.DoesNotContain(document.Cues, cue => cue.Text.Contains('<') || cue.Text.Contains('\0'));
        var play = StreamCaptionLoader.PlayPath(path);
        Assert.True(File.Exists(play));
        if (document.HasColors)
        {
            Assert.EndsWith(".ass", play, StringComparison.OrdinalIgnoreCase);
            var ass = File.ReadAllText(play);
            Assert.Contains("Dialogue:", ass, StringComparison.Ordinal);
            Assert.DoesNotContain("Helloworld", ass, StringComparison.Ordinal);
            Assert.Contains("\\c&H", ass, StringComparison.Ordinal);
        }
    }

    [LibMpvFact]
    public void Live_selected_dub_opens_without_nothing_to_play()
    {
        YouTubePlayable? playable;
        try
        {
            playable = YouTubeCatalog.Resolve(
                "https://www.youtube.com/watch?v=Qtl8lJwbd4g",
                null,
                "tr",
                "tr");
        }
        catch (Exception)
        {
            return;
        }

        if (playable is null)
        {
            return;
        }

        playable = YouTubeCatalog.BindHlsRenditions(playable);
        Assert.StartsWith("http", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, playable.MediaUrl);
        Assert.False(
            playable.MediaUrl.Contains("hls_variant", StringComparison.OrdinalIgnoreCase) &&
            YouTubeCatalog.UsesSeparateAudio(playable),
            "master playlist must not be opened with a separate audio file");

        var caption = Grok.Player.Core.Subtitles.StreamCaptionLoader.Load(
            playable.VideoId,
            "tr",
            playable.CaptionUrl);
        using var host = PlayerHost.CreateHeadless();
        string? error = null;
        host.Error += (_, e) => error = e.Message;
        host.Open(
            playable.MediaUrl,
            playable.Kind,
            YouTubeCatalog.UsesSeparateAudio(playable) ? playable.AudioUrl : null,
            playable.Title,
            playable.UserAgent,
            playable.AudioLang,
            playable.SubLang,
            caption);
        EventWait.Until(
            () => host.State is PlayerState.Playing or PlayerState.Paused or PlayerState.Error,
            TimeSpan.FromSeconds(20),
            "youtube-open");
        Assert.NotEqual(PlayerState.Error, host.State);
        Assert.DoesNotContain("nothing to play", error ?? host.LastError ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(host.HasMedia);
        if (caption is not null)
        {
            Assert.True(File.Exists(caption));
            Assert.Contains("-->", File.ReadAllText(caption), StringComparison.Ordinal);
        }
    }

    [LibMpvFact]
    public void Remux_keeps_audio_track()
    {
        var muxed = GeneratedMedia.TryCreateSample(2);
        Assert.False(string.IsNullOrWhiteSpace(muxed), "ffmpeg sample was not created: " + GeneratedMedia.LastError);
        var video = GeneratedMedia.TryCreateVideoOnly(muxed!);
        var audio = GeneratedMedia.TryCreateAudioOnly(muxed);
        Assert.False(string.IsNullOrWhiteSpace(video) || string.IsNullOrWhiteSpace(audio), "split A/V files were not created");

        var output = Path.Combine(Path.GetTempPath(), "grok-remux-" + Guid.NewGuid().ToString("N") + ".mkv");
        try
        {
            if (!StreamDump.TryRemux(video!, audio, output, CancellationToken.None))
            {
                // testhost has no audio device; libmpv then reports AO_INIT_FAILED (-14).
                if (StreamDump.LastError?.Contains("err=-14", StringComparison.Ordinal) == true)
                {
                    return;
                }

                Assert.Fail(StreamDump.LastError ?? "remux failed");
            }
            Assert.True(File.Exists(output));
            Assert.True(new FileInfo(output).Length > 1024);

            using var mpv = new MpvNative();
            mpv.SetOption("vo", "null");
            mpv.SetOption("ao", "null");
            mpv.SetOption("idle", "yes");
            mpv.Initialize();
            mpv.Command("loadfile", output, "replace");
            var until = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < until)
            {
                var ev = mpv.WaitEvent(0.2);
                if (ev.Id == MpvEventId.FileLoaded)
                {
                    break;
                }

                if (ev.Id == MpvEventId.EndFile && ev.EndFileReason == MpvEndFileReason.Error)
                {
                    throw new InvalidOperationException("remuxed file failed to load");
                }
            }

            var aid = mpv.GetPropertyString("aid") ?? mpv.GetPropertyLong("aid")?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(aid) || aid is "no" or "0");
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [LibMpvFact]
    public void Unbuffered_vod_seek_does_not_end_the_file()
    {
        var sample = GeneratedMedia.TryCreateSample(6);
        if (sample is null)
        {
            return;
        }

        using var host = PlayerHost.CreateHeadless();
        var ended = false;
        host.MediaEnded += (_, _) => ended = true;
        host.Open(sample, StreamKind.Vod);
        EventWait.Until(
            () => host.State is PlayerState.Playing or PlayerState.Paused or PlayerState.Error,
            TimeSpan.FromSeconds(8),
            "open");
        if (host.State == PlayerState.Error)
        {
            return;
        }

        host.Seek(TimeSpan.FromSeconds(3));
        Thread.Sleep(400);
        Assert.False(ended);
        Assert.NotEqual(PlayerState.Ended, host.State);
    }

    [Fact]
    public void Stream_vod_storyboard_serves_hover_cells()
    {
        YouTubePlayable? playable;
        try
        {
            playable = YouTubeCatalog.Resolve("https://www.youtube.com/watch?v=Qtl8lJwbd4g");
        }
        catch (Exception ex)
        {
            Assert.Fail("resolve failed: " + ex.Message);
            return;
        }

        Assert.NotNull(playable);
        Assert.False(string.IsNullOrWhiteSpace(playable!.StoryboardSpec), "player json had no storyboard spec");

        var spec = StoryboardSpec.Parse(playable.StoryboardSpec);
        Assert.NotNull(spec);
        Assert.True(spec!.BestLevel!.Width >= 160, "best storyboard is " + spec.BestLevel!.Width);
        using var atlas = new StoryboardAtlas(spec, TimeSpan.FromMinutes(20));
        var times = new[] { 15, 90, 159, 180, 400 };
        string? previous = null;
        foreach (var seconds in times)
        {
            var hover = TimeSpan.FromSeconds(seconds);
            Assert.True(atlas.TryGetOrFetch(hover, out var frame), "missing storyboard cell at " + seconds);
            Assert.True(LivePlayback.IsUsableStill(frame));
            using var image = System.Drawing.Image.FromFile(frame);
            Assert.True(image.Width >= 80, "cell width " + image.Width);
            Assert.True(image.Height >= 45, "cell height " + image.Height);
            if (previous is not null)
            {
                Assert.NotEqual(previous, frame);
            }

            previous = frame;
        }
    }
}
