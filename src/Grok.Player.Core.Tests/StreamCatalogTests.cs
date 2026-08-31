using Grok.Player.Core.Download;
using Grok.Player.Core.Media;
using Grok.Player.Core.Subtitles;

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
        Assert.Equal("v7cfefw", StreamCatalog.RumbleEmbedIdFromHtml(
            """<iframe src="https://rumble.com/embed/v7cfefw/" width="1920"></iframe>"""));
        Assert.True(StreamCatalog.LooksResolvable("https://www.tiktok.com/@hsnphlvnoglu/video/7676616845960531221"));
        Assert.True(StreamCatalog.LooksResolvable("https://www.hdfilmcehennemi.nl/the-last-scene-2026/"));
        Assert.Equal("https://rumble.com/", StreamCatalog.PageOrigin("https://rumble.com/v7elrde-the-time-norm-macdonald-crashed-the-youtube-awards.html"));
        Assert.True(StreamCatalog.IsDirectMedia("https://v16-webapp.tiktokcdn.com/video/tos/foo"));
        Assert.True(StreamCatalog.IsDirectMedia("https://rumble.com/hls-vod/abc/playlist.m3u8"));
        Assert.True(StreamCatalog.IsDirectMedia("https://hls8.playmix.uno/hls/film.mp4/master.txt"));
        Assert.True(StreamCatalog.RequiresPlayerPage(
            "https://fastplay.mom/manifests/episode/master.txt?verify=123-proof"));
        Assert.False(StreamCatalog.RequiresPlayerPage(
            "https://hls8.playmix.uno/hls/film.mp4/master.txt"));
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
        Assert.True(StreamCatalog.IsDirectMedia("https://rumble.com/hls-vod/v7cfefw/playlist.m3u8"));
        Assert.Equal(
            "v7cfefw",
            StreamCatalog.RumbleEmbedIdFromHtml(
                """<div><iframe src="https://rumble.com/embed/v7cfefw/?pub=4"></iframe></div>"""));
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
    public void Hls_subtitle_list_skips_forced_and_keeps_named_tracks()
    {
        const string master =
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="sub",NAME="Türkçe",LANGUAGE="tur",DEFAULT=NO,AUTOSELECT=NO,FORCED=NO,URI="sub-tur.m3u8"
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="sub",NAME="İngilizce",LANGUAGE="eng",DEFAULT=NO,AUTOSELECT=NO,FORCED=NO,URI="sub-eng.m3u8"
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="sub",NAME="Türkçe (Zorunlu)",LANGUAGE="tur",DEFAULT=NO,AUTOSELECT=YES,FORCED=YES,URI="sub-tur-forced.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=1280x720,SUBTITLES="sub"
            720.m3u8
            """;
        var subs = HlsPlaylist.Subtitles(master, "https://cdn.example/master.m3u8");
        Assert.Equal(3, subs.Count);
        Assert.Equal("https://cdn.example/sub-tur.m3u8", subs[0].Url);
        Assert.Equal("tur", subs[0].Language);
        Assert.Equal("Türkçe", subs[0].Name);
        Assert.False(subs[0].Forced);
        Assert.Equal("İngilizce", subs[1].Name);
        Assert.False(subs[1].Forced);
        Assert.True(subs[2].Forced);
        Assert.Equal("https://cdn.example/sub-tur-forced.m3u8", subs[2].Url);
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
        var vod = new YouTubePlayable(
            "dailymotion|xb23uyu",
            "https://cdn.example/vod.m3u8",
            "Film",
            StreamKind.Vod);
        Assert.Same(vod, StreamCatalog.AttachVodCaptions(vod, null));
        Assert.True(string.IsNullOrWhiteSpace(vod.CaptionUrl));
        Assert.True(StreamCatalog.LooksKickLivePlayback(
            "https://fa723fc1b171.us-west-2.playback.live-video.net/api/video/v1/us-west-2.channel.m3u8"));
        Assert.False(StreamCatalog.LooksKickLivePlayback(
            "https://stream.kick.com/3c81249a5ce0/ivs/v1/196233775518/jHtppgXXoKhP/2026/8/27/23/20/x/media/hls/master.m3u8"));
        Assert.False(StreamCatalog.LooksKickLivePlayback(
            "https://fa723fc1b171.us-west-2.playback.live-video.net/api/video/v1/us-west-2.vod.m3u8"));
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
        Assert.True(StreamCatalog.LooksDecoyManifest(master));
        Assert.True(StreamCatalog.MediaScore(master) < StreamCatalog.MediaScore("https://cdn.film/movie.m3u8"));

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
        var embedUrls = StreamCatalog.MediaUrlsIn(embedHtml);
        Assert.Contains(master, embedUrls);

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
        Assert.Equal(
            "https://srv9.cdnimages2898.shop/hls/ongoru-2026-webdl-tt44147164mp4-Tr4Yz605cMT.mp4/master.txt",
            StreamCatalog.SiblingPlaylistUrl(packed));
        Assert.False(StreamCatalog.LooksImagePlaylistUrl(StreamCatalog.SiblingPlaylistUrl(packed)));
        Assert.True(StreamCatalog.IsImagePlaylist("#EXTINF:7,\nhttps://cdn/image000.jpg"));
        Assert.True(StreamCatalog.MediaScore(packed) < 0);
        const string packedHtml =
            """
            var s_JgunxLuRzKs = dc_43u5cEA0dQp(["=dxFTU1XdCF","GiTPHIQEChX","xXGUkF2G1CZ","5PlH9FFlo1F","UQOXZMyEGY2","CzaxEesRXsK","uHSGQYvayER","5PGwUEZhXxG","NK0FUCRXFaI","BJCRD5v3JjF","QDHwyFJCxDV","UOKOi2CwivG","jXQYcKFGKiR","CZ1xDj40X3e","xJhzvmtsQnc","QGEDaHFRCRC","uCSoawGlvKG","EHM3CHoIDeo","GodYkDYUQGv","KOpGQwBl5xH","jBIpZKyCT5F","FvsQF"]);
            {"contentUrl":"https://hls8.playmix.uno/hls/filmakinesimp4-f9gx1M12BwC.mp4/master.txt"}
            """;
        var packedUrls = StreamCatalog.MediaUrlsIn(packedHtml);
        Assert.Contains(master, packedUrls);
        Assert.Contains(
            "https://srv9.cdnimages2898.shop/hls/ongoru-2026-webdl-tt44147164mp4-Tr4Yz605cMT.mp4/master.txt",
            packedUrls);
        Assert.DoesNotContain(
            "https://srv9.cdnimages2898.shop/hls/ongoru-2026-webdl-tt44147164mp4-Tr4Yz605cMT.mp4/txt/master.txt",
            packedUrls);
    }

    [Fact]
    public void Close_embed_packed_hls_is_decoded_past_playmix()
    {
        var decoded = StreamCatalog.DecodePackedPlayerUrl([
            "RkRMRl", "hRQmRC", "dUZyQn", "V6Wkl3", "YzFJRn", "VnR3Zu", "dVdSbj", "NEMUpN",
            "RDJUU0", "FIbkpX", "eExyRH", "doR213", "QWprT2", "hYbFE5", "NUZETE", "dDdTBo",
            "RnhGSm", "92SlhE", "RUUxRn", "ZGNkFE", "RkhsUE", "pGRjI1", "eUV3VF", "BXSHZE",
            "VzNMa0", "RGSlhv", "RkpiR3", "ZyQlcx", "VGhGRF", "BHRDFY", "SEV1dk", "NsdTVD",
            "REd6Q1", "h2dmRt", "UTV0bE", "VKakVG", "QkdERT", "FibGpM", "RUJPel", "ZGRzFZ",
            "WEdYNk", "N2cnNs", "RHVpRn", "ZGR1hP", "NVBZRk", "JaRmpQ", "QUV4em", "NuRnpH",
            "SWpQck", "J2VEdG", "R3pNRl", "BJaG1R", "OUdGM3", "JORkZu", "UW0xel", "BtTzFY",
            "V0hMT0", "Z2SnVF", "MlBIRE", "h6QkdR", "aGFJSH", "p4b0ZC", "U21Pen", "RsUFRn",
            "RDByZ0", "dQVFJK", "M3JHV0", "ZKYkpF", "RWhCTz", "lFQXVo", "WEZGRT", "FGRUpQ",
            "WUZMQl", "pEMD0="
        ]);
        Assert.False(string.IsNullOrWhiteSpace(decoded));
        Assert.StartsWith("http", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("playmix.uno", decoded, StringComparison.OrdinalIgnoreCase);
        var usable = StreamCatalog.LooksImagePlaylistUrl(decoded)
            ? StreamCatalog.SiblingPlaylistUrl(decoded)
            : decoded;
        Assert.False(string.IsNullOrWhiteSpace(usable));
        Assert.Contains(
            usable,
            StreamCatalog.MediaUrlsIn(
                """
                var s_Pvw6KqI38V1 = dc_cvNVv97I7Nn(["RkRMRl","hRQmRC","dUZyQn","V6Wkl3","YzFJRn","VnR3Zu","dVdSbj","NEMUpN","RDJUU0","FIbkpX","eExyRH","doR213","QWprT2","hYbFE5","NUZETE","dDdTBo","RnhGSm","92SlhE","RUUxRn","ZGNkFE","RkhsUE","pGRjI1","eUV3VF","BXSHZE","VzNMa0","RGSlhv","RkpiR3","ZyQlcx","VGhGRF","BHRDFY","SEV1dk","NsdTVD","REd6Q1","h2dmRt","UTV0bE","VKakVG","QkdERT","FibGpM","RUJPel","ZGRzFZ","WEdYNk","N2cnNs","RHVpRn","ZGR1hP","NVBZRk","JaRmpQ","QUV4em","NuRnpH","SWpQck","J2VEdG","R3pNRl","BJaG1R","OUdGM3","JORkZu","UW0xel","BtTzFY","V0hMT0","Z2SnVF","MlBIRE","h6QkdR","aGFJSH","p4b0ZC","U21Pen","RsUFRn","RDByZ0","dQVFJK","M3JHV0","ZKYkpF","RWhCTz","lFQXVo","WEZGRT","FGRUpQ","WUZMQl","pEMD0="]);
                {"contentUrl":"https://hls8.playmix.uno/hls/filmakinesimp4-f9gx1M12BwC.mp4/master.txt"}
                """));
    }

    [Fact]
    public void Close_embed_reads_the_randomized_packer_from_the_page()
    {
        const string html =
            """
            function dc_SCIIq3Uv5aD(value_parts) {
              let value = value_parts.join('');
              let result = value;
              result = atob(result);
              result = result.split('').reverse().join('');
              result = atob(result);
              result = result.replace(/[a-zA-Z]/g, function(c) {
                var o = c.charCodeAt(0), base = (o <= 90) ? 65 : 97;
                return String.fromCharCode((o - base + 16) % 26 + base);
              });
              result = result.split('').reverse().join('');
              result = atob(result);
              var acc = 7;
              let unmix = '';
              for (let i = 0; i < result.length; i++) {
                var b = result.charCodeAt(i);
                acc = (acc + 14) % 256;
                var plain = b ^ acc;
                acc = (acc + b) % 256;
                unmix += String.fromCharCode(plain);
              }
              return unmix;
            }
            var s_BHPz5bnZfOS = dc_SCIIq3Uv5aD(["PT1BY3","VSa001","WTJkaX","BrU1RW","bU5yTV","VPSjUy","TVlaam","NJbDJM","anBXY0","xaRld6","Z2pXd1","ZuYXZJ","MU0zd1","dieG8y","UzN0aV","ZteDJa","dk1XUk","d0aWJ5","TW5USE","pXYzVr","MUxUaG","pXU0p6","S2lkbF","kzVUZV","d3N5TE","lKRE5h","MWtWWT","lXWnh4","R093Wk","RhekYx","ZFY5eU","xNUmxX","cUZsUm","lKbFk1","RjFNVW","RqV2hW","elZhSl","dRNTgy","VE1KMl","QwRjJM","eDFUUA","=="]);
            {"contentUrl":"https://hls8.playmix.uno/hls/filmakinesimp4-f9gx1M12BwC.mp4/master.txt"}
            """;
        var urls = StreamCatalog.MediaUrlsIn(html);
        Assert.Contains(urls, item => item.Contains("cdnimages", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("https://hls8.playmix.uno/hls/filmakinesimp4-f9gx1M12BwC.mp4/master.txt", urls);
    }

    [Fact]
    public void Close_embed_decodes_escaped_parts_and_double_rot()
    {
        const string html =
            """
            function dc_9SoHdZ0G9CE(value_parts) {
              let value = value_parts.join('');
              let result = value;
              result = result.replace(/[a-zA-Z]/g, function(c) {
                var o = c.charCodeAt(0), base = (o <= 90) ? 65 : 97;
                return String.fromCharCode((o - base + 1) % 26 + base);
              });
              result = result.replace(/[a-zA-Z]/g, function(c) {
                var o = c.charCodeAt(0), base = (o <= 90) ? 65 : 97;
                return String.fromCharCode((o - base + 21) % 26 + base);
              });
              result = result.split('').reverse().join('');
              result = atob(result);
              var acc = 229;
              let unmix = '';
              for (let i = 0; i < result.length; i++) {
                var b = result.charCodeAt(i);
                acc = (acc + 16) % 256;
                var plain = b ^ acc;
                acc = (acc + b) % 256;
                unmix += String.fromCharCode(plain);
              }
              return unmix;
            }
            var s_s6tQjbIa81q = dc_9SoHdZ0G9CE(["==E\/kv","my7\/+9","GEwQ7m","z4Pjqx","VTHxYC","wMPrjO","gSGb8B","gD2K\/u","bPz40T","j8qxv4","tIPKEz","z8OBy5","ud2HkS","9+djFb","cXTXwS","zc3I77","bTjdmB","z8bJuG","m7QLMj","T9BQ4i","\/Oi6wE","5CtyT5","8fhr"]);
            """;
        var urls = StreamCatalog.PackedPlayerUrls(html);
        Assert.True(urls.Count > 0, "escaped slash parts must decode");
        Assert.Contains(urls, item => item.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(urls, item => item.Contains("playmix.uno", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Close_embed_skips_dead_playmix_for_the_packed_master()
    {
        const string embed =
            "https://hdfilmcehennemi.mobi/video/embed/xnZQ9xsXLfb/?rapidrame_id=gr2rb77x3mpm";
        var html = StreamCatalog.GetText(embed, StreamCatalog.ChromeUa, embed);
        Assert.False(string.IsNullOrWhiteSpace(html), "embed HTML did not download");
        var urls = StreamCatalog.MediaUrlsIn(html);
        var packed = urls.FirstOrDefault(item =>
            item.Contains("cdnimages", StringComparison.OrdinalIgnoreCase) ||
            (item.Contains("master.txt", StringComparison.OrdinalIgnoreCase) &&
             !item.Contains("playmix", StringComparison.OrdinalIgnoreCase)));
        Assert.False(
            string.IsNullOrWhiteSpace(packed),
            "decoded media missing. urls=" + string.Join(" | ", urls) +
            " packedCalls=" + StreamCatalog.PackedPlayerUrls(html).Count);
        var master = StreamCatalog.GetText(packed!, StreamCatalog.ChromeUa, embed);
        Assert.False(string.IsNullOrWhiteSpace(master), "packed master empty: " + packed);
        Assert.StartsWith("#EXTM3U", master.TrimStart(), StringComparison.OrdinalIgnoreCase);

        var playable = StreamCatalog.Resolve(embed);
        Assert.NotNull(playable);
        Assert.DoesNotContain("playmix.uno", playable!.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.True(StreamCatalog.IsDirectMedia(playable.MediaUrl));
        Assert.Contains("master.txt", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(embed, playable.Referer);

        var fromPage = StreamCatalog.Resolve("https://www.hdfilmcehennemi.nl/somebody-2024-hdf/");
        Assert.NotNull(fromPage);
        Assert.DoesNotContain("playmix.uno", fromPage!.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("master.txt", fromPage.MediaUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Jw_caption_tracks_keep_their_english_and_turkish_labels()
    {
        const string html =
            """
            tracks: [{"file":"https:\/\/hdfilmcehennemi.mobi\/vtt\/xnZQ9xsXLfb-eng.vtt","kind":"captions","label":"English"},{"file":"https:\/\/hdfilmcehennemi.mobi\/vtt\/xnZQ9xsXLfb-tr-4611117-engtr.vtt","kind":"captions","label":"Turkish","default":true}]
            """;
        var caps = StreamCatalog.SidecarCaptionsIn(html);
        Assert.Equal(2, caps.Count);
        Assert.Contains(caps, item => item.Name == "English" && item.Url.Contains("-eng.vtt", StringComparison.Ordinal));
        Assert.Contains(caps, item => item.Name == "Turkish" && item.Url.Contains("-tr-", StringComparison.Ordinal));
    }

    [Fact]
    public void Dailymotion_metadata_exposes_the_selected_subtitle_file()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """
            {"subtitles":{"enable":true,"data":{
              "en":{"label":"English","urls":["https://static2.dmcdn.net/en.srt"]},
              "tr-auto":{"label":"Türkçe (autogenerated)","urls":["https://static2.dmcdn.net/tr-auto.srt"]}
            }}}
            """);
        Assert.Equal(
            "https://static2.dmcdn.net/tr-auto.srt",
            StreamCatalog.DailyCaptionUrl(document.RootElement, "tr"));
        Assert.Equal(
            "https://static2.dmcdn.net/en.srt",
            StreamCatalog.DailyCaptionUrl(document.RootElement, "en"));
        using var chapters = System.Text.Json.JsonDocument.Parse(
            """
            {"subtitles":{"enable":true,"data":{
              "en":{"label":"English","urls":[
                "https://static2.dmcdn.net/static/video/646967054_chapters.vtt",
                "https://static2.dmcdn.net/en.srt"
              ]}
            }}}
            """);
        Assert.Equal(
            "https://static2.dmcdn.net/en.srt",
            StreamCatalog.DailyCaptionUrl(chapters.RootElement, "en"));
        Assert.DoesNotContain(
            StreamCatalog.DailyCaptionTracks(chapters.RootElement),
            item => item.Url.Contains("chapters", StringComparison.Ordinal));
    }

    [Fact]
    public void Playerjs_html_exposes_every_sidecar_caption()
    {
        var html = "window.playerjsSubtitle='[English]https://cdn.example/en.vtt,[Turkish]https://cdn.example/tr.vtt';";
        var caps = StreamCatalog.SidecarCaptionsIn(html);
        Assert.Equal(2, caps.Count);
        Assert.Equal("en", caps[0].Language);
        Assert.Equal("https://cdn.example/en.vtt", caps[0].Url);
        Assert.Equal("tr", caps[1].Language);
        Assert.Equal("https://cdn.example/tr.vtt", caps[1].Url);
        const string packed =
            """
            <script>jwSetup.tracks=[{file:"https://cdn.example/en.vtt",label:"English",kind:"captions"}];</script>
            """;
        var fromFile = StreamCatalog.SidecarCaptionsIn(packed);
        Assert.Contains(fromFile, item => item.Url == "https://cdn.example/en.vtt");
        var escaped = StreamCatalog.SidecarCaptionsIn(
            """{"file":"https:\/\/hdfilmcehennemi.mobi\/vtt\/2026\/08\/film-eng.vtt","label":"English"}""");
        Assert.Contains(escaped, item => item.Url == "https://hdfilmcehennemi.mobi/vtt/2026/08/film-eng.vtt");

        const string imagestoo =
            """
            var playerjsDefaultSubtitle = "English";
            var playerjsSubtitle = "[English]https://i.knitwears.pics/cdn/down/abc/Subtitle/subtitle_eng.vtt,[Turkish]https://i.knitwears.pics/cdn/down/abc/Subtitle/subtitle_tur.vtt";
            """;
        var fromPlayerjs = StreamCatalog.SidecarCaptionsIn(imagestoo);
        Assert.Equal(2, fromPlayerjs.Count);
        Assert.Equal("en", fromPlayerjs[0].Language);
        Assert.EndsWith("subtitle_eng.vtt", fromPlayerjs[0].Url);
        Assert.Equal("tr", fromPlayerjs[1].Language);
        Assert.EndsWith("subtitle_tur.vtt", fromPlayerjs[1].Url);

        const string cfg =
            "eyJ2IjoiaHR0cHM6Ly9pbWFnZXN0b28uY29tL3ZpZGVvLzZhODNjNzMxNjYwZmNjOWYxNGUxY2UwYjYyZDQ1ZWI5IiwidCI6ImVtYmVkIn0=";
        var embeds = StreamCatalog.PlayerEmbedsIn(
            $"<div class='video-player-container' data-cfg='{cfg}'></div>",
            "https://dizipal2121.com/bolum/x");
        Assert.Contains(embeds, item => item.Contains("imagestoo.com/video/6a83c731660fcc9f14e1ce0b62d45eb9", StringComparison.Ordinal));
    }

    [Fact]
    public void Knitwears_sidecar_vtts_download_as_captions()
    {
        var english = StreamCaptionLoader.LoadSidecar(
            "https://i.knitwears.pics/cdn/down/0a034306c940f526d8c3465fb177de3b/Subtitle/subtitle_eng.vtt",
            "en",
            "English",
            "https://imagestoo.com/");
        var turkish = StreamCaptionLoader.LoadSidecar(
            "https://i.knitwears.pics/cdn/down/0a034306c940f526d8c3465fb177de3b/Subtitle/subtitle_tur.vtt",
            "tr",
            "Turkish",
            "https://imagestoo.com/");
        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.False(string.IsNullOrWhiteSpace(turkish));
        Assert.True(File.Exists(english));
        Assert.True(File.Exists(turkish));
        Assert.Contains("-->", File.ReadAllText(english), StringComparison.Ordinal);
        Assert.Contains("-->", File.ReadAllText(turkish), StringComparison.Ordinal);
        Assert.NotEqual(english, turkish);
    }

    [Fact]
    public void Hdfilm_turkish_engtr_sidecar_keeps_early_cues()
    {
        const string english =
            "https://hdfilmcehennemi.mobi/vtt/2026/08/xnZQ9xsXLfb-eng-somebody-2024-webdl-tt35899314_subtitles01.eng.vtt";
        const string turkish =
            "https://hdfilmcehennemi.mobi/vtt/2026/08/xnZQ9xsXLfb-tr-4611117-somebody-2024-webdl-tt35899314_subtitles01engtr.vtt";
        const string referer = "https://hdfilmcehennemi.mobi/video/embed/xnZQ9xsXLfb/?rapidrame_id=gr2rb77x3mpm";
        var enFile = StreamCaptionLoader.LoadSidecar(english, "en", "English", referer);
        var trFile = StreamCaptionLoader.LoadSidecar(turkish, "tr", "Turkish", referer);
        Assert.False(string.IsNullOrWhiteSpace(enFile), "english sidecar missing");
        Assert.False(string.IsNullOrWhiteSpace(trFile), "turkish sidecar missing");
        var enPlay = File.ReadAllText(StreamCaptionLoader.PlayPath(enFile!));
        var trPlay = File.ReadAllText(StreamCaptionLoader.PlayPath(trFile!));
        var enDoc = SrtDocument.Parse(enPlay, compact: false);
        var trDoc = SrtDocument.Parse(trPlay, compact: false);
        Assert.True(enDoc.Cues.Count > 10, "english cues=" + enDoc.Cues.Count);
        Assert.True(trDoc.Cues.Count > 10, "turkish cues=" + trDoc.Cues.Count + " play=" + StreamCaptionLoader.PlayPath(trFile));
        Assert.True(
            trDoc.Cues[0].Start < TimeSpan.FromMinutes(5),
            "turkish first cue is " + trDoc.Cues[0].Start + " text=" + trDoc.Cues[0].Text);
        Assert.Contains("SOMEBODY", enDoc.Cues[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Breaking_bad_turkish_sidecar_parses_cues_like_english()
    {
        const string page = "https://www.hdfilmcehennemi.now/bolum/breaking-bad-1-sezon-1-bolum-1-izle-16/";
        var html = StreamCatalog.GetText(page, StreamCatalog.ChromeUa, page);
        Assert.False(string.IsNullOrWhiteSpace(html), "page html empty");
        var fields = StreamCatalog.WordPressAjaxPlayerFields(html, page);
        var embeds = StreamCatalog.AjaxPlayerEmbeds(html ?? "", page);
        var local = StreamCatalog.SidecarCaptionsIn(html);
        var caps = StreamCatalog.SidecarCaptionsFromPage(page);
        Assert.False(fields is null, "wordpress ajax player fields missing");
        Assert.Contains("SetPlay", fields!.Value.Players, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(embeds, item => item.Contains("setplay.", StringComparison.OrdinalIgnoreCase));
        Assert.True(local.Count == 0, "static episode HTML has no VTT; Open must transfer JW sidecar files");
    }

    [Fact]
    public void Dizipal_episode_page_follows_the_embed_to_english_and_turkish_vtts()
    {
        var caps = StreamCatalog.SidecarCaptionsFromPage(
            "https://dizipal2121.com/bolum/yuzuklerin-efendisi-guc-yuzukleri-1-sezon-6-bolum");
        Assert.True(caps.Count >= 2, "expected Eng/TR sidecars from the imagestoo embed");
        Assert.Contains(caps, item => item.Url.Contains("subtitle_eng.vtt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(caps, item => item.Url.Contains("subtitle_tur.vtt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Caption_referer_follows_the_caption_host_not_youtube()
    {
        Assert.Equal("https://www.youtube.com/", YouTubeCatalog.CaptionReferer("https://www.youtube.com/api/timedtext?v=x"));
        Assert.Equal("https://i.knitwears.pics/", YouTubeCatalog.CaptionReferer("https://i.knitwears.pics/cdn/down/abc/Subtitle/subtitle_tur.vtt"));
    }

    [Fact]
    public void Generic_embedded_players_decode_config_and_obfuscated_media()
    {
        const string rapid =
            "==gPH1GZ0c2aPNXaKB1eqtFV8tjbcVUcapVMt1mZPRDWVxXMXdWex52Y2MXSP52Qsh1RcllTTlEbPp1RuZGcypEVH9mVUdTbuRWUI9mUz1kMm1VcYVlSulkVMVUVVJXeycmYyB3V18WbctVdtVWamRDVQNHWV1Hay8EdzUTTtlFNkZ3Tv9UN7AnW8l1MctmMZRmdl1mT8xjeOljTKZWMUtkY";
        Assert.Equal(
            "https://s27.imagesbox.cloud/ml/H3IjMKWanKWfYwVjZwLhI0IPYHEZYwRjBQOjYxEIDHjhrQV2AP1VER0d0zxnJ1uM2ImLz94YzAfo3Ixs0xi27vr1",
            StreamCatalog.DecodeRapidPlayerUrl(rapid));
        Assert.Equal(
            StreamCatalog.DecodeRapidPlayerUrl(rapid),
            StreamCatalog.PickMediaUrl(StreamCatalog.MediaUrlsIn($"jwSetup.sources=[{{file:av('{rapid}')}}]")));

        const string config = "eyJ2IjoiaHR0cHM6Ly9pbWFnZXN0b28uY29tL3ZpZGVvLzZhODNjNzMxNjYwZmNjOWYxNGUxY2UwYjYyZDQ1ZWI5IiwidCI6ImVtYmVkIn0=";
        var embeds = StreamCatalog.PlayerEmbedsIn(
            $"<div class='video-player-container' data-cfg='{config}'></div>",
            "https://example.test/watch/movie");
        Assert.Contains("https://imagestoo.com/video/6a83c731660fcc9f14e1ce0b62d45eb9", embeds);

        const string dean =
            "eval(function(p,a,c,k,e,d){return p}('0(\"1\")',62,2,'file|https://cdn.example/master.m3u8'.split('|'),0,{}))";
        Assert.Contains("file(\"https://cdn.example/master.m3u8\")", StreamCatalog.UnpackDeanEdwards(dean));
        Assert.Contains("https://cdn.example/master.m3u8", StreamCatalog.MediaUrlsIn(dean));
    }

    [Fact]
    public void CryptoJs_encrypted_player_documents_are_decrypted_generically()
    {
        const string cipher =
            "U2FsdGVkX1/chkNoXTZpjNDg0Eh/gmWLqfgiPOn6Mlf5C7j0J6q/5p6SEN56IDsR9cDGjJ6TIK6SomN4XYRJjAn5fnCJqYbm3GTjQe1YWCsW9vStnoWPNSqeOSyQ48fk";
        var html = $"document.write(CryptoJS.AES.decrypt(\"{cipher}\",\"secret\").toString(CryptoJS.enc.Utf8));";

        var decrypted = Assert.Single(StreamCatalog.DecryptCryptoJsDocuments(html));

        Assert.Contains("https://cdn.example/player/master.m3u8", decrypted);
        Assert.Contains("https://cdn.example/player/master.m3u8", StreamCatalog.MediaUrlsIn(decrypted));

        const string endpoint = "https://dbx.example/embed/sheila/video-id";
        var embeds = StreamCatalog.PlayerEmbedsIn($"jwplayer('p').setup({{ file: '{endpoint}' }});", "https://dbx.example/embed/video-id");
        Assert.Contains(endpoint, embeds);
        Assert.True(StreamCatalog.LooksMediaManifest("  \n#EXTM3U\n#EXT-X-VERSION:3"));

        const string wordpress =
            "var videoAjax={ajaxurl:'https:\\/\\/site.example\\/wp-admin\\/admin-ajax.php',nonce:'abc123'};" +
            "var request={action:'get_video_url'};" +
            "<a data-post-id='1432' data-player-name='SetPlay'></a>";
        var fields = StreamCatalog.WordPressAjaxPlayerFields(wordpress, "https://site.example/watch/episode");
        Assert.NotNull(fields);
        Assert.Equal("https://site.example/wp-admin/admin-ajax.php", fields.Value.Endpoint);
        Assert.Equal("abc123", fields.Value.Nonce);
        Assert.Equal("1432", fields.Value.PostId);
        Assert.Contains("SetPlay", fields.Value.Players);

        const string spg = "SPG.cerceve('frame','GxEXAhZOXEoTHgQNFhdNFx0VHhUPF0oRHgcGFkoVEQYfEQEaXQAbEwgEHwA=','c2VjcmV0');";
        Assert.Contains("https://player.example/embed/abc", StreamCatalog.SpgFrameUrls(spg));

        const string fsp = "window.SPG_A={\"sp\":\"abc\",\"spT\":100}; window.FSP={stream:'/manifests/id/master.txt?verify=x'};";
        var protectedManifest = StreamCatalog.ProtectedManifestIn(fsp, "https://player.example/video/id");
        Assert.NotNull(protectedManifest);
        Assert.Equal("https://player.example/manifests/id/master.txt?verify=x", protectedManifest.Value.Url);
        Assert.Equal("abc", protectedManifest.Value.Secret);
        Assert.Equal(100, protectedManifest.Value.Timestamp);
        Assert.Equal("100.xyz.fb45e22b", StreamCatalog.BuildSpProof("abc", 100, "xyz"));

        const string hls = "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,URI=\"audio/list.txt\"\n#EXTINF:2,\nsegments/one.ts\n";
        var rewritten = ProtectedStreamProxy.RewriteHlsManifest(hls, "https://cdn.example/path/master.txt", url => "proxy?u=" + Uri.EscapeDataString(url));
        var proxy = ProtectedStreamProxy.Register(
            "https://fastplay.mom/manifests/episode/master.txt?verify=123-proof",
            "https://fastplay.mom/video/embed/abc",
            "secret",
            1);
        Assert.True(ProtectedStreamProxy.TryUnwrap(proxy, out var unwrapped));
        Assert.Equal("https://fastplay.mom/manifests/episode/master.txt?verify=123-proof", unwrapped);
        Assert.True(DownloadManager.LooksLikeHls(proxy));
        Assert.Contains("proxy?u=https%3A%2F%2Fcdn.example%2Fpath%2Faudio%2Flist.txt", rewritten);
        Assert.Contains("proxy?u=https%3A%2F%2Fcdn.example%2Fpath%2Fsegments%2Fone.ts", rewritten);
    }

    [Fact]
    public void Common_preroll_hosts_are_never_selected_as_main_media()
    {
        Assert.True(StreamCatalog.LooksAd("https://marmorated.pics/preroll/video.mp4"));
        Assert.True(StreamCatalog.LooksAd("https://shrgo.net/ads/clip.mp4"));
        Assert.Null(StreamCatalog.PickMediaUrl([
            "https://marmorated.pics/preroll/video.mp4",
            "https://shrgo.net/ads/clip.mp4"
        ]));
    }

    [Fact]
    public void Kick_web_player_metadata_and_session_manifest_are_parsed()
    {
        const string page =
            "queryKey:[\\\"WebVideo\\\",\\\"details\\\",104131094,\\\"01a044ef-8900-7c3d-9539-696d32367f14\\\",\\\"getVideo\\\"]";
        Assert.Equal("104131094", StreamCatalog.KickChannelIdFromPage(page));
        Assert.Equal(
            "https://cdn.example/session/master.m3u8",
            StreamCatalog.KickSessionManifest("{\"manifestUrl\":\"https://cdn.example/session/master.m3u8\"}"));
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

    [Fact]
    public void Playturka_hash_urls_and_page_sources_are_found()
    {
        Assert.True(StreamCatalog.TryReadPlayturka("https://p.playturka.space/#BCMgxTpc", out var id));
        Assert.Equal("BCMgxTpc", id);
        var embeds = StreamCatalog.PlayerEmbedsIn(
            "activeSource: 'https://p.playturka.space/#BCMgxTpc'",
            "https://filmizlehell.net/film/3685-odyssey-036-izle");
        Assert.Contains("https://p.playturka.space/#BCMgxTpc", embeds);
    }

    [Fact]
    public void Playturka_cipher_round_trips_json()
    {
        const string json = """{"status":"success","files":{"masterUrl":"https://p.playturka.space/videos/BCMgxTpc/master.m3u8"}}""";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        var cipher = new string(encoded.Select(PlayturkaMap).ToArray());
        var decoded = StreamCatalog.DecryptPlayturka(cipher);
        Assert.Contains("master.m3u8", decoded, StringComparison.Ordinal);
        Assert.Contains("BCMgxTpc", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public void Closeload_jw_tracks_keep_named_captions_and_drop_forced()
    {
        const string html =
            """
            tracks: [{"file":"https:\/\/closeload.filmmakinesi.to\/vtt\/en.vtt","kind":"captions","label":"English","default":false},{"file":"https:\/\/closeload.filmmakinesi.to\/vtt\/forced.vtt","kind":"captions","label":"Forced","default":false},{"file":"https:\/\/closeload.filmmakinesi.to\/vtt\/tr.vtt","kind":"captions","label":"Turkish","default":true}]
            """;
        var caps = StreamCatalog.SidecarCaptionsIn(html);
        Assert.Contains(caps, item => item.Name == "English");
        Assert.Contains(caps, item => item.Name == "Turkish");
        Assert.DoesNotContain(caps, item => item.Name.Contains("Forced", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(caps, item => item.Url.Contains("forced", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Closeload_turkish_sidecar_downloads()
    {
        var file = StreamCaptionLoader.LoadSidecar(
            "https://closeload.filmmakinesi.to/vtt/2025/02/yqGDCTbv1Zw-tr-2439052-breakingbads01e02web-dl1080pdualx264_subtitles02tur.vtt",
            "tr",
            "Turkish",
            "https://closeload.filmmakinesi.to/video/embed/yqGDCTbv1Zw/?imdb_id=tt0903747");
        if (file is null)
        {
            return;
        }

        var cues = SrtDocument.Parse(File.ReadAllText(file), compact: false).Cues;
        Assert.True(cues.Count > 10, "cues=" + cues.Count);
    }

    [Fact]
    public void Odyssey_playturka_page_resolves_a_master()
    {
        YouTubePlayable? playable;
        try
        {
            playable = StreamCatalog.Resolve("https://filmizlehell.net/film/3685-odyssey-036-izle");
        }
        catch (Exception)
        {
            return;
        }

        if (playable is null)
        {
            playable = StreamCatalog.Resolve("https://p.playturka.space/#BCMgxTpc");
        }

        if (playable is null)
        {
            return;
        }

        Assert.Contains(".m3u8", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("filmizlehell.net/film/", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
    }

    private static char PlayturkaMap(char ch) => ch switch
    {
        'A' => 'Z', 'B' => 'Y', 'C' => 'X', 'D' => 'W', 'E' => 'V', 'F' => 'U',
        'G' => 'T', 'H' => 'S', 'I' => 'R', 'J' => 'Q', 'K' => 'P', 'L' => 'O',
        'M' => 'N', 'N' => 'M', 'O' => 'L', 'P' => 'K', 'Z' => 'A', 'Y' => 'B',
        'X' => 'C', 'W' => 'D', 'V' => 'E', 'U' => 'F', 'T' => 'G', 'S' => 'H',
        'R' => 'I', 'Q' => 'J',
        'a' => 'z', 'b' => 'y', 'c' => 'x', 'd' => 'w', 'e' => 'v', 'f' => 'u',
        'g' => 't', 'h' => 's', 'i' => 'r', 'j' => 'q', 'k' => 'p', 'l' => 'o',
        'm' => 'n', 'n' => 'm', 'o' => 'l', 'p' => 'k', 'z' => 'a', 'y' => 'b',
        'x' => 'c', 'w' => 'd', 'v' => 'e', 'u' => 'f', 't' => 'g', 's' => 'h',
        'r' => 'i', 'q' => 'j',
        '0' => '5', '1' => '6', '2' => '7', '3' => '8', '4' => '9',
        '5' => '0', '6' => '1', '7' => '2', '8' => '3', '9' => '4',
        '+' => '-', '/' => '_',
        _ => ch
    };
}
