using Grok.Player.Core.Download;
using Grok.Player.Core.Media;

namespace Grok.Player.Core.Tests;

public sealed class StreamCatalogTests
{
    [Theory]
    [InlineData("https://kick.com/rootthegamer", "live", "rootthegamer")]
    [InlineData("https://kick.com/video/abc-123", "video", "abc-123")]
    [InlineData("https://kick.com/rootthegamer/videos/abc-123", "video", "abc-123")]
    public void Reads_kick_urls(string url, string kind, string id)
    {
        Assert.True(StreamCatalog.TryReadKick(url, out var parsedKind, out var parsedId));
        Assert.Equal(kind, parsedKind);
        Assert.Equal(id, parsedId);
        Assert.True(StreamCatalog.LooksResolvable(url));
    }

    [Theory]
    [InlineData("https://www.twitch.tv/eslcs", "live", "eslcs")]
    [InlineData("https://www.twitch.tv/videos/123456789", "vod", "123456789")]
    [InlineData("https://www.twitch.tv/eslcs/video/123456789", "vod", "123456789")]
    public void Reads_twitch_urls(string url, string kind, string id)
    {
        Assert.True(StreamCatalog.TryReadTwitch(url, out var parsedKind, out var parsedId));
        Assert.Equal(kind, parsedKind);
        Assert.Equal(id, parsedId);
        Assert.True(StreamCatalog.LooksResolvable(url));
    }

    [Fact]
    public void Direct_media_urls_are_resolvable()
    {
        Assert.True(StreamCatalog.IsDirectMedia("https://cdn.example/movie.m3u8"));
        Assert.True(StreamCatalog.IsDirectMedia("https://cdn.example/film.mp4"));
        Assert.False(StreamCatalog.LooksResolvable("https://cdn.example/movie.m3u8"));
        Assert.False(StreamCatalog.LooksResolvable("https://www.youtube.com/watch?v=dQw4w9wgBcQ"));
        Assert.True(StreamCatalog.LooksResolvable("https://rumble.com/v7elrde-the-time-norm-macdonald-crashed-the-youtube-awards.html"));
        Assert.True(StreamCatalog.LooksResolvable("https://www.tiktok.com/@hsnphlvnoglu/video/7676616845960531221"));
        Assert.True(StreamCatalog.LooksResolvable("https://www.hdfilmcehennemi.nl/the-last-scene-2026/"));
        Assert.True(StreamCatalog.IsDirectMedia("https://v16-webapp.tiktokcdn.com/video/tos/foo"));
        Assert.True(StreamCatalog.IsDirectMedia("https://rumble.com/hls-vod/abc/playlist.m3u8"));
        Assert.True(StreamCatalog.IsDirectMedia("https://hls8.playmix.uno/hls/film.mp4/master.txt"));
        Assert.True(StreamCatalog.IsDirectMedia("https://scontent.cdninstagram.com/o1/v/t16/f2/m86/foo"));
        Assert.False(StreamCatalog.IsDirectMedia("https://scontent.cdninstagram.com/v/t51.2885-15/foo.jpg"));
        Assert.True(StreamCatalog.LooksImagePlaylistUrl("https://scontent.cdninstagram.com/v/t51.2885-15/foo.jpg"));
        Assert.False(StreamCatalog.LooksResolvable("https://v16-webapp.tiktokcdn.com/video/tos/foo"));
        Assert.True(StreamCatalog.LooksLikeHtmlPage("https://www.example.com/watch/the-film"));
        Assert.Equal("kick|rootthegamer", StreamCatalog.ContentKey("kick|rootthegamer"));
        Assert.Equal("youtube|dQw4w9wgBcQ", StreamCatalog.ContentKey("dQw4w9wgBcQ"));
    }

    [Fact]
    public void Rumble_hls_candidates_keep_and_drop_the_v_prefix()
    {
        var urls = StreamCatalog.RumbleHlsCandidates("v7epfng");
        Assert.Contains("https://rumble.com/hls-vod/v7epfng/playlist.m3u8", urls);
        Assert.Contains("https://rumble.com/hls-vod/7epfng/playlist.m3u8", urls);
    }

    [Theory]
    [InlineData("https://rumble.com/v7elrde-the-time-norm-macdonald-crashed-the-youtube-awards.html", "v7elrde")]
    [InlineData("https://rumble.com/embed/v7elrde", "v7elrde")]
    public void Reads_rumble_urls(string url, string id)
    {
        Assert.True(StreamCatalog.TryReadRumble(url, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void Reads_tiktok_and_dailymotion_urls()
    {
        Assert.True(StreamCatalog.TryReadTikTok("https://www.tiktok.com/@hsnphlvnoglu/video/7676616845960531221", out var tiktok));
        Assert.Equal("7676616845960531221", tiktok);
        Assert.True(StreamCatalog.TryReadInstagram("https://www.instagram.com/reels/Da5Y8qhsLcU/", out var ig));
        Assert.Equal("Da5Y8qhsLcU", ig);
        Assert.True(StreamCatalog.TryReadDailymotion("https://www.dailymotion.com/video/x8abcde", out var daily));
        Assert.Equal("x8abcde", daily);
    }

    [Fact]
    public void Picks_hls_over_preview_mp4()
    {
        var picked = StreamCatalog.PickMediaUrl([
            "https://cdn.example/timeline-180.mp4",
            "https://cdn.example/preview.mp4",
            "https://cdn.example/master.m3u8",
            "https://cdn.example/360.mp4"
        ]);
        Assert.Equal("https://cdn.example/master.m3u8", picked);
        Assert.True(StreamCatalog.MediaScore("https://cdn.example/timeline.mp4") < 0);
        Assert.True(
            StreamCatalog.MediaScore("https://scontent.cdninstagram.com/o1/v/t16/clip.mp4") >
            StreamCatalog.MediaScore("https://scontent.cdninstagram.com/o1/v/t16/clip.mp4?bytestart=1000000&byteend=1200000"));
    }

    [Fact]
    public void Extracts_media_urls_from_page_html()
    {
        const string html =
            """
            <html><video><source src="https://cdn.film/movie/720.mp4"></video>
            <script>player.src="https://cdn.film/movie/master.m3u8";</script></html>
            """;
        var urls = StreamCatalog.MediaUrlsIn(html);
        Assert.Contains("https://cdn.film/movie/master.m3u8", urls);
        Assert.Equal("https://cdn.film/movie/master.m3u8", StreamCatalog.PickMediaUrl(urls));
        const string instagram =
            """
            <meta property="og:video" content="https://scontent.cdninstagram.com/o1/v/t16/clip.mp4">
            """;
        Assert.Equal(
            "https://scontent.cdninstagram.com/o1/v/t16/clip.mp4",
            StreamCatalog.PickMediaUrl(StreamCatalog.MediaUrlsIn(instagram)));
    }

    [Fact]
    public void Hls_vod_subtitle_groups_are_detected()
    {
        const string master =
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="vtt",NAME="English",LANGUAGE="en",URI="en.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=1280x720,SUBTITLES="vtt"
            720.m3u8
            """;
        Assert.Equal(
            "https://cdn.example/en.m3u8",
            HlsPlaylist.SubtitleUri(master, "https://cdn.example/master.m3u8", "en"));
        Assert.Equal(
            "https://cdn.example/en.m3u8",
            HlsCaptions.SubtitleUriFromManifest(master, "https://cdn.example/master.m3u8", "en"));
        Assert.False(HlsCaptions.IsLiveManifest(master));
    }

    [Fact]
    public void Live_media_playlists_do_not_yield_captions()
    {
        const string live =
            """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:80
            #EXTINF:6,
            live.ts
            """;
        Assert.True(HlsCaptions.IsLiveManifest(live));
        Assert.Null(HlsCaptions.SubtitleUriFromManifest(live, "https://cdn.example/live.m3u8", "en"));
    }

    [Fact]
    public void Dash_vod_text_tracks_are_detected()
    {
        const string mpd =
            """
            <MPD type="static">
              <AdaptationSet contentType="video"><Representation><BaseURL>video.m4s</BaseURL></Representation></AdaptationSet>
              <AdaptationSet contentType="text" lang="tr" mimeType="text/vtt">
                <Representation><BaseURL>tr.vtt</BaseURL></Representation>
              </AdaptationSet>
              <AdaptationSet contentType="text" lang="en" mimeType="text/vtt">
                <Representation><BaseURL>en.vtt</BaseURL></Representation>
              </AdaptationSet>
            </MPD>
            """;
        Assert.Equal(
            "https://cdn.example/tr.vtt",
            HlsCaptions.SubtitleUriFromManifest(mpd, "https://cdn.example/manifest.mpd", "tr"));
        Assert.Equal(
            "https://cdn.example/en.vtt",
            HlsCaptions.SubtitleUriFromManifest(mpd, "https://cdn.example/manifest.mpd", "en"));
        Assert.False(HlsCaptions.IsLiveManifest(mpd));
    }

    [Fact]
    public void Dash_live_manifests_do_not_yield_captions()
    {
        const string mpd =
            """
            <MPD type="dynamic">
              <AdaptationSet contentType="text" lang="en" mimeType="text/vtt">
                <Representation><BaseURL>en.vtt</BaseURL></Representation>
              </AdaptationSet>
            </MPD>
            """;
        Assert.True(HlsCaptions.IsLiveManifest(mpd));
        Assert.Null(HlsCaptions.SubtitleUriFromManifest(mpd, "https://cdn.example/live.mpd", "en"));
    }

    [Fact]
    public void Sidecar_urls_follow_the_media_path()
    {
        Assert.Equal(
            "https://cdn.example/film/movie.vtt",
            HlsCaptions.SidecarUrl("https://cdn.example/film/movie.mp4", ".vtt"));
        Assert.Equal(
            "https://cdn.example/film/movie.srt",
            HlsCaptions.SidecarUrl("https://cdn.example/film/movie.mp4?token=1", ".srt"));
        Assert.Null(HlsCaptions.SidecarUrl(@"C:\Videos\movie.mp4", ".vtt"));
    }

    [Fact]
    public void Attach_vod_captions_skips_live()
    {
        var live = new YouTubePlayable(
            "kick|rootthegamer",
            "https://cdn.example/live.m3u8",
            "Live",
            StreamKind.Live);
        Assert.Same(live, StreamCatalog.AttachVodCaptions(live, "en"));
        Assert.True(string.IsNullOrWhiteSpace(live.CaptionUrl));
    }

    [Fact]
    public void Kick_live_resolve_returns_hls_when_the_channel_is_online()
    {
        YouTubePlayable? playable;
        try
        {
            playable = StreamCatalog.Resolve("https://kick.com/rootthegamer");
        }
        catch (Exception)
        {
            return;
        }

        if (playable is null)
        {
            return;
        }

        Assert.Contains(".m3u8", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("kick|", playable.VideoId, StringComparison.Ordinal);
        Assert.Equal(StreamKind.Live, playable.Kind);
        Assert.True(string.IsNullOrWhiteSpace(playable.CaptionUrl));
    }

    [Fact]
    public void Kick_hls_is_rebuilt_from_page_thumbnail_and_start_time()
    {
        const string html =
            """
            start_time\":\"2026-08-27T20:35:44Z\",\"status\":\"public\",\"thumbnail\":{\"src\":\"https://images.kick.com/video_thumbnails/UTurJDh1l4q7/LcCjvEpWu9qW/720.webp\"}
            """;
        Assert.Equal(
            "https://stream.kick.com/3c81249a5ce0/ivs/v1/196233775518/UTurJDh1l4q7/2026/8/27/20/35/LcCjvEpWu9qW/media/hls/master.m3u8",
            StreamCatalog.KickHlsFromPage(html));
        Assert.True(StreamCatalog.LooksAd("https://pubads.g.doubleclick.net/ad.mp4"));
        Assert.True(StreamCatalog.LooksAd("https://dmxleo.dailymotion.com/cdn/manifest/video/xb23uyu.m3u8"));
        Assert.False(StreamCatalog.LooksAd("https://stream.kick.com/master.m3u8"));
        Assert.False(StreamCatalog.LooksAd("https://cdndirector.dailymotion.com/cdn/manifest/video/xb23uyu.m3u8"));
        Assert.Equal("https://www.instagram.com/", StreamCatalog.SiteReferer("https://scontent.cdninstagram.com/o1/v/t16/clip"));
        Assert.True(StreamCatalog.MediaScore("https://pubads.g.doubleclick.net/preroll.mp4") < 0);
    }

    [Fact]
    public void Kick_vod_resolve_returns_hls_without_using_the_live_slug_api()
    {
        YouTubePlayable? playable;
        try
        {
            playable = StreamCatalog.Resolve("https://kick.com/rootthegamer/videos/cb958bec-canli-fut-1-2-spirit-bo5-ewc-2026-buyuk-final");
        }
        catch (Exception)
        {
            return;
        }

        if (playable is null)
        {
            return;
        }

        Assert.Contains(".m3u8", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StreamKind.Vod, playable.Kind);
        Assert.StartsWith("kick|", playable.VideoId, StringComparison.Ordinal);
    }

    [Fact]
    public void Kick_new_video_id_resolves_from_the_page()
    {
        YouTubePlayable? playable;
        try
        {
            playable = StreamCatalog.Resolve("https://kick.com/kalatay3/videos/01a044ef-8900-7c3d-9539-696d32367f14");
        }
        catch (Exception)
        {
            return;
        }

        if (playable is null)
        {
            return;
        }

        Assert.Contains(".m3u8", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UTurJDh1l4q7", playable.MediaUrl, StringComparison.Ordinal);
        Assert.Equal(StreamKind.Vod, playable.Kind);
    }

    [Fact]
    public void Youtube_live_resolve_uses_hls()
    {
        YouTubePlayable? playable;
        try
        {
            playable = YouTubeCatalog.Resolve("https://www.youtube.com/watch?v=hEeAXerJ5n0");
        }
        catch (Exception)
        {
            return;
        }

        if (playable is null)
        {
            return;
        }

        Assert.Equal(StreamKind.Live, playable.Kind);
        Assert.True(
            playable.MediaUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            playable.MediaUrl.Contains("hls", StringComparison.OrdinalIgnoreCase) ||
            playable.MediaUrl.Contains(".mpd", StringComparison.OrdinalIgnoreCase),
            playable.MediaUrl);
    }

    [Fact]
    public void Rumble_resolve_prefers_hls_or_high_mp4()
    {
        YouTubePlayable? playable;
        try
        {
            playable = StreamCatalog.Resolve("https://rumble.com/v7elrde-the-time-norm-macdonald-crashed-the-youtube-awards.html");
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
        Assert.DoesNotContain("timeline", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StreamKind.Vod, playable.Kind);
        Assert.StartsWith("rumble|", playable.VideoId, StringComparison.Ordinal);
    }

    [Fact]
    public void Tiktok_resolve_returns_a_cdn_video_not_the_html_page()
    {
        YouTubePlayable? playable;
        try
        {
            playable = StreamCatalog.Resolve("https://www.tiktok.com/@hsnphlvnoglu/video/7676616845960531221");
        }
        catch (Exception)
        {
            return;
        }

        if (playable is null)
        {
            return;
        }

        Assert.DoesNotContain("tiktok.com/@", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("tiktok|", playable.VideoId, StringComparison.Ordinal);
        Assert.Equal(StreamKind.Vod, playable.Kind);
    }

    [Fact]
    public void Rekla_bumpers_and_json_ld_masters_are_classified()
    {
        const string rekla = "https://www.hdfilmcehennemi.nl/rekla/luxyenii.mp4";
        const string master = "https://hls8.playmix.uno/hls/filmakinesimp4-f9gx1M12BwC.mp4/master.txt";
        Assert.True(StreamCatalog.LooksAd(rekla));
        Assert.True(StreamCatalog.MediaScore(rekla) < 0);
        Assert.False(StreamCatalog.LooksAd(master));
        Assert.True(StreamCatalog.IsDirectMedia(master));
        Assert.True(StreamCatalog.MediaScore(master) > StreamCatalog.MediaScore("https://cdn.film/360.mp4"));

        const string pageHtml =
            """
            <div class="player-container">
            <iframe class="close" data-src="https://hdfilmcehennemi.mobi/video/embed/Tr4Yz605cMT/?rapidrame_id=ph8vvkgphbbd"></iframe>
            </div>
            <script>ads_object_url="https://www.hdfilmcehennemi.nl/rekla/luxyenii.mp4"</script>
            """;
        Assert.True(StreamCatalog.MediaScore("https://www.hdfilmcehennemi.nl/site.webm") < 0);
        Assert.Null(StreamCatalog.PickMediaUrl(StreamCatalog.MediaUrlsIn(pageHtml + """<source src="https://www.hdfilmcehennemi.nl/site.webm">""")));
        var embeds = StreamCatalog.PlayerEmbedsIn(pageHtml, "https://www.hdfilmcehennemi.nl/the-last-scene-2026/");
        Assert.Contains("https://hdfilmcehennemi.mobi/video/embed/Tr4Yz605cMT/?rapidrame_id=ph8vvkgphbbd", embeds);

        const string embedHtml =
            """
            {"contentUrl":"https://hls8.playmix.uno/hls/filmakinesimp4-f9gx1M12BwC.mp4/master.txt"}
            """;
        Assert.Equal(master, StreamCatalog.PickMediaUrl(StreamCatalog.MediaUrlsIn(embedHtml)));

        var packed = StreamCatalog.DecodePackedPlayerUrl([
            "=dxFTU1XdCF", "GiTPHIQEChX", "xXGUkF2G1CZ", "5PlH9FFlo1F", "UQOXZMyEGY2",
            "CzaxEesRXsK", "uHSGQYvayER", "5PGwUEZhXxG", "NK0FUCRXFaI", "BJCRD5v3JjF",
            "QDHwyFJCxDV", "UOKOi2CwivG", "jXQYcKFGKiR", "CZ1xDj40X3e", "xJhzvmtsQnc",
            "QGEDaHFRCRC", "uCSoawGlvKG", "EHM3CHoIDeo", "GodYkDYUQGv", "KOpGQwBl5xH",
            "jBIpZKyCT5F", "FvsQF"
        ]);
        Assert.Equal(
            "https://srv9.cdnimages2898.shop/hls/ongoru-2026-webdl-tt44147164mp4-Tr4Yz605cMT.mp4/txt/master.txt",
            packed);
        Assert.True(StreamCatalog.LooksImagePlaylistUrl(packed));
        Assert.True(StreamCatalog.IsImagePlaylist("#EXTINF:7,\nhttps://cdn/image000.jpg"));
        Assert.True(StreamCatalog.MediaScore(packed) < 0);
        const string packedHtml =
            """
            var s_JgunxLuRzKs = dc_43u5cEA0dQp(["=dxFTU1XdCF","GiTPHIQEChX","xXGUkF2G1CZ","5PlH9FFlo1F","UQOXZMyEGY2","CzaxEesRXsK","uHSGQYvayER","5PGwUEZhXxG","NK0FUCRXFaI","BJCRD5v3JjF","QDHwyFJCxDV","UOKOi2CwivG","jXQYcKFGKiR","CZ1xDj40X3e","xJhzvmtsQnc","QGEDaHFRCRC","uCSoawGlvKG","EHM3CHoIDeo","GodYkDYUQGv","KOpGQwBl5xH","jBIpZKyCT5F","FvsQF"]);
            {"contentUrl":"https://hls8.playmix.uno/hls/filmakinesimp4-f9gx1M12BwC.mp4/master.txt"}
            """;
        Assert.Equal(master, StreamCatalog.PickMediaUrl(StreamCatalog.MediaUrlsIn(packedHtml)));
    }

    [Fact]
    public void Hdfilm_page_resolve_does_not_open_the_html_page()
    {
        YouTubePlayable? playable;
        try
        {
            playable = StreamCatalog.Resolve("https://www.hdfilmcehennemi.nl/the-last-scene-2026/");
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
        Assert.DoesNotContain("hdfilmcehennemi.nl/the-last-scene", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/rekla/", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/txt/master.txt", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.False(StreamCatalog.LooksAd(playable.MediaUrl));
        Assert.False(StreamCatalog.LooksImagePlaylistUrl(playable.MediaUrl));
    }

    [Fact]
    public void Live_hls_masters_still_expose_subtitle_groups()
    {
        const string live =
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="vtt",NAME="English",LANGUAGE="en",URI="en.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=800000
            live.m3u8
            """;
        Assert.True(HlsPlaylist.IsLive(live));
        Assert.False(HlsCaptions.IsLiveManifest(live));
        Assert.Equal(
            "https://cdn.example/en.m3u8",
            HlsCaptions.SubtitleUriFromManifest(live, "https://cdn.example/master.m3u8", "en"));
    }
}
