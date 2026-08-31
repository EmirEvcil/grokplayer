using Grok.Player.Core.Launch;
using Grok.Player.Core.Media;
using Grok.Player.Core.Subtitles;

namespace Grok.Player.Core.Tests;

public sealed class YouTubeCatalogTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9wgBcQ", "dQw4w9wgBcQ")]
    [InlineData("https://youtu.be/dQw4w9wgBcQ", "dQw4w9wgBcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9wgBcQ", "dQw4w9wgBcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9wgBcQ", "dQw4w9wgBcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9wgBcQ", "dQw4w9wgBcQ")]
    [InlineData("dQw4w9wgBcQ", "dQw4w9wgBcQ")]
    public void Reads_video_ids(string url, string id)
    {
        Assert.True(YouTubeCatalog.TryReadVideoId(url, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void Rejects_non_youtube()
    {
        Assert.False(YouTubeCatalog.IsWatchUrl("https://example.com/watch?v=dQw4w9wgBcQ"));
        Assert.False(YouTubeCatalog.IsWatchUrl("https://storage.googleapis.com/a.m3u8"));
    }

    [Fact]
    public void Parses_live_hls_from_player_json()
    {
        var json =
            """
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"abcdefghijk","title":"News","isLive":true},"streamingData":{"hlsManifestUrl":"https://manifest.googlevideo.com/api/manifest/hls_variant/live.m3u8"}}
            """;
        var playable = YouTubeCatalog.ParsePlayerResponse(json);
        Assert.NotNull(playable);
        Assert.Equal(StreamKind.Live, playable!.Kind);
        Assert.Contains("m3u8", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("News", playable.Title);
    }

    [Fact]
    public void Live_adaptive_only_json_is_not_playable()
    {
        var json =
            """
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"abcdefghijk","title":"News","isLive":true},"streamingData":{"adaptiveFormats":[{"url":"https://googlevideo.com/videoplayback?id=137","width":1920,"mimeType":"video/mp4"}]}}
            """;
        Assert.Null(YouTubeCatalog.ParsePlayerResponse(json));
    }

    [Fact]
    public void Live_dash_only_json_is_not_playable()
    {
        var json =
            """
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"abcdefghijk","title":"News","isLive":true},"streamingData":{"dashManifestUrl":"https://manifest.googlevideo.com/api/manifest/dash/live.mpd"}}
            """;
        Assert.Null(YouTubeCatalog.ParsePlayerResponse(json));
    }

    [Fact]
    public void Parses_vod_progressive_url()
    {
        var json =
            """
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"abcdefghijk","title":"Clip"},"streamingData":{"formats":[{"url":"https://googlevideo.com/videoplayback?id=1","width":1280,"mimeType":"video/mp4; codecs=\"avc1.4d401f,mp4a.40.2\""}]}}
            """;
        var playable = YouTubeCatalog.ParsePlayerResponse(json);
        Assert.NotNull(playable);
        Assert.Equal(StreamKind.Vod, playable!.Kind);
        Assert.StartsWith("https://googlevideo.com/videoplayback", playable.MediaUrl);
    }

    [Fact]
    public void Prefers_1080p_video_plus_aac_audio()
    {
        var json =
            """
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"abcdefghijk","title":"Doc"},"streamingData":{"adaptiveFormats":[{"url":"https://g/v?itag=137","width":1920,"mimeType":"video/mp4; codecs=\"avc1.640028\""},{"url":"https://g/v?itag=136","width":1280,"mimeType":"video/mp4; codecs=\"avc1.4d401f\""},{"url":"https://g/a?itag=140","mimeType":"audio/mp4; codecs=\"mp4a.40.2\"","bitrate":128000}]}}
            """;
        var playable = YouTubeCatalog.ParsePlayerResponse(json);
        Assert.NotNull(playable);
        Assert.Contains("itag=137", playable!.MediaUrl);
        Assert.Contains("itag=140", playable.AudioUrl);
    }

    [Fact]
    public void Unplayable_json_is_null()
    {
        Assert.Null(YouTubeCatalog.ParsePlayerResponse("""{"playabilityStatus":{"status":"LOGIN_REQUIRED"}}"""));
        Assert.Null(YouTubeCatalog.ParsePlayerResponse("""{"playabilityStatus":{"status":"ERROR","reason":"This video is unavailable"}}"""));
    }

    [Fact]
    public void Reads_player_json_from_watch_html()
    {
        var html =
            """
            <html><script>var meta = {};</script>
            <script>var ytInitialPlayerResponse = {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"abcdefghijk","title":"Clip"},"streamingData":{"formats":[{"url":"https://googlevideo.com/videoplayback?id=18","width":1280,"mimeType":"video/mp4"}]}};</script>
            <script>ytcfg.set({"VISITOR_DATA":"CgtVisitor123"});</script></html>
            """;
        var json = YouTubeCatalog.ExtractAssignedJson(html, "ytInitialPlayerResponse");
        Assert.NotNull(json);
        var playable = YouTubeCatalog.ParsePlayerResponse(json);
        Assert.NotNull(playable);
        Assert.Equal("Clip", playable!.Title);
        Assert.Equal("CgtVisitor123", YouTubeCatalog.ExtractVisitorData(html));
    }

    [Fact]
    public void Ended_live_content_is_still_vod()
    {
        var json =
            """
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"abcdefghijk","title":"Encore","isLiveContent":true},"streamingData":{"hlsManifestUrl":"https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8"}}
            """;
        var playable = YouTubeCatalog.ParsePlayerResponse(json);
        Assert.NotNull(playable);
        Assert.Equal(StreamKind.Vod, playable!.Kind);
    }

    [Fact]
    public void Resolve_uses_injected_player_payload()
    {
        var json =
            """
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"V3kIwZBou_o","title":"Doc"},"streamingData":{"formats":[{"url":"https://googlevideo.com/videoplayback?id=18","width":640,"mimeType":"video/mp4; codecs=\"avc1.42001E, mp4a.40.2\""}]}}
            """;
        var playable = YouTubeCatalog.Resolve("https://www.youtube.com/watch?v=V3kIwZBou_o", _ => json);
        Assert.NotNull(playable);
        Assert.Equal("https://googlevideo.com/videoplayback?id=18", playable!.MediaUrl);
        Assert.Equal(StreamKind.Vod, playable.Kind);
    }

    [Fact]
    public void Vod_with_hls_and_progressive_prefers_hls()
    {
        var json =
            """
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"abcdefghijk","title":"Clip"},"streamingData":{"hlsManifestUrl":"https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8","formats":[{"url":"https://googlevideo.com/videoplayback?id=18","width":1280,"mimeType":"video/mp4; codecs=\"avc1.4d401f,mp4a.40.2\""}]}}
            """;
        var playable = YouTubeCatalog.ParsePlayerResponse(json);
        Assert.NotNull(playable);
        Assert.Equal(StreamKind.Vod, playable!.Kind);
        Assert.Contains("hls_variant", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Vod_hls_only_is_not_classified_live()
    {
        var json =
            """
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"abcdefghijk","title":"Clip"},"streamingData":{"hlsManifestUrl":"https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8"}}
            """;
        var playable = YouTubeCatalog.ParsePlayerResponse(json);
        Assert.NotNull(playable);
        Assert.Equal(StreamKind.Vod, playable!.Kind);
        Assert.Contains("m3u8", playable.MediaUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Web_sabr_payload_without_urls_is_not_playable()
    {
        var json =
            """
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"V3kIwZBou_o","title":"Doc"},"streamingData":{"formats":[{}],"adaptiveFormats":[{},{}]}}
            """;
        Assert.Null(YouTubeCatalog.ParsePlayerResponse(json));
    }

    [Fact]
    public void Extension_protocol_opens_the_watch_url()
    {
        var raw =
            "grokplayer://open?url=" +
            Uri.EscapeDataString("https://www.youtube.com/watch?v=WFSdNlLtu7I") +
            "&title=" + Uri.EscapeDataString("İbrahim Tatlıses") +
            "&kind=vod&play=1";
        Assert.True(ExternalOpen.TryParse(raw, out var open));
        Assert.Equal("https://www.youtube.com/watch?v=WFSdNlLtu7I", open.Url);
        Assert.Equal("İbrahim Tatlıses", open.Title);
        Assert.Equal(StreamKind.Vod, open.Kind);
        Assert.True(open.Play);
        var dubbed =
            "grokplayer://open?url=" +
            Uri.EscapeDataString("https://www.youtube.com/watch?v=WFSdNlLtu7I") +
            "&audio=tr&sub=tr&kind=vod&play=1";
        Assert.True(ExternalOpen.TryParse(dubbed, out var lang));
        Assert.Equal("tr", lang.AudioLang);
        Assert.Equal("tr", lang.SubLang);
        var dotted =
            "grokplayer://open?url=" +
            Uri.EscapeDataString("https://www.youtube.com/watch?v=WFSdNlLtu7I") +
            "&audio=tr.3&sub=tr:asr&kind=vod&play=1";
        Assert.True(ExternalOpen.TryParse(dotted, out var dottedLang));
        Assert.Equal("tr", dottedLang.AudioLang);
        Assert.Equal("tr:asr", dottedLang.SubLang);
        var asr =
            "grokplayer://open?url=" +
            Uri.EscapeDataString("https://www.youtube.com/watch?v=WFSdNlLtu7I") +
            "&sub=en:asr&caption=" +
            Uri.EscapeDataString("https://www.youtube.com/api/timedtext?v=WFSdNlLtu7I&lang=en&kind=asr");
        Assert.True(ExternalOpen.TryParse(asr, out var asrOpen));
        Assert.Equal("en:asr", asrOpen.SubLang);
        Assert.Contains("kind=asr", asrOpen.CaptionUrl, StringComparison.Ordinal);
        Assert.Single(asrOpen.Captions);
        Assert.Equal(asrOpen.CaptionUrl, asrOpen.Captions[0].Url);
    }

    [Fact]
    public void Extension_protocol_keeps_youtube_caption_and_adds_cap_list()
    {
        var english = "https://cdn.example/en.vtt";
        var turkish = "https://cdn.example/tr.vtt";
        var link = ExternalOpen.ToProtocol(
            "https://cdn.example/master.m3u8",
            "Episode",
            StreamKind.Vod,
            captions:
            [
                new ExternalCaption("en", english, "English"),
                new ExternalCaption("tr", turkish, "Turkish")
            ]);
        Assert.Contains("cap=", link, StringComparison.Ordinal);
        Assert.True(ExternalOpen.TryParse(link, out var open));
        Assert.Equal("https://cdn.example/master.m3u8", open.Url);
        Assert.Equal(2, open.Captions.Count);
        Assert.Equal("en", open.Captions[0].Language);
        Assert.Equal(english, open.Captions[0].Url);
        Assert.Equal("English", open.Captions[0].Name);
        Assert.Equal("tr", open.Captions[1].Language);
        Assert.Equal(turkish, open.Captions[1].Url);
        Assert.True(ExternalOpen.TryParse(
            ExternalOpen.ToProtocol(
                "https://www.youtube.com/watch?v=dQw4w9wgBcQ",
                "Song",
                StreamKind.Vod,
                "tr",
                "tr",
                captionUrl: "https://www.youtube.com/api/timedtext?v=dQw4w9wgBcQ&lang=tr"),
            out var youtube));
        Assert.Equal("tr", youtube.AudioLang);
        Assert.Equal("tr", youtube.SubLang);
        Assert.Contains("timedtext", youtube.CaptionUrl, StringComparison.Ordinal);
        Assert.Single(youtube.Captions);
        Assert.False(YouTubeCatalog.LooksLikeYouTubeCaptionUrl("https://i.knitwears.pics/cdn/down/abc/Subtitle/subtitle_eng.vtt"));
        Assert.True(YouTubeCatalog.LooksLikeYouTubeCaptionUrl("https://www.youtube.com/api/timedtext?v=x&lang=en"));
    }

    [Fact]
    public void Sidecar_loader_keeps_unlabeled_vtt_files()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(path, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHi\n");
        try
        {
            Assert.Equal(path, StreamCaptionLoader.LoadSidecar(path, "und", "English"));
            Assert.Equal(path, StreamCaptionLoader.LoadSidecar(path, "", ""));
            Assert.Equal("dailymotion-xap6qz2", StreamCaptionLoader.CacheStem("dailymotion|xap6qz2"));
            Assert.DoesNotContain("|", StreamCaptionLoader.CacheStem("sidecar|68a43e2f"), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Caption_url_normalizes_dub_codes_and_asr()
    {
        var url = YouTubeCatalog.CaptionVttUrl("dQw4w9wgBcQ", "tr.3");
        Assert.Contains("lang=tr", url, StringComparison.Ordinal);
        Assert.DoesNotContain("tr.3", url, StringComparison.Ordinal);
        Assert.Contains("fmt=vtt", url, StringComparison.Ordinal);
        Assert.Contains("kind=asr", YouTubeCatalog.CaptionVttUrl("dQw4w9wgBcQ", "tr:asr"), StringComparison.Ordinal);
        var asrUrls = StreamCaptionLoader.Urls("EzWLUda58k4", "tr:asr", null).ToList();
        Assert.Contains(asrUrls, item => item.Contains("kind=asr", StringComparison.Ordinal) &&
                                         item.Contains("lang=tr", StringComparison.Ordinal));
        Assert.Equal(asrUrls[0], YouTubeCatalog.CaptionVttUrl("EzWLUda58k4", "tr:asr"));
    }

    [Fact]
    public void Parse_caption_url_picks_matching_track()
    {
        var json =
            """
            {"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[
              {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=en","languageCode":"en","kind":"asr"},
              {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=tr","languageCode":"tr"}
            ]}}}
            """;
        var chinese =
            """
            {"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[
              {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=zh-Hans","languageCode":"zh-Hans"},
              {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=zh-Hant","languageCode":"zh-Hant"},
              {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=bn","languageCode":"bn"},
              {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=ru","languageCode":"ru"}
            ]}}}
            """;
        Assert.Contains("lang=zh-Hans", YouTubeCatalog.PickCaptionUrl(chinese, "zh-Hans"), StringComparison.Ordinal);
        Assert.Contains("lang=zh-Hant", YouTubeCatalog.PickCaptionUrl(chinese, "zh-Hant"), StringComparison.Ordinal);
        Assert.Contains("lang=bn", YouTubeCatalog.PickCaptionUrl(chinese, "bn"), StringComparison.Ordinal);
        Assert.Contains("lang=ru", YouTubeCatalog.PickCaptionUrl(chinese, "ru"), StringComparison.Ordinal);
        Assert.Contains("lang=zh-Hans", YouTubeCatalog.CaptionVttUrl("abcdefghijk", "zh-Hans"), StringComparison.Ordinal);
        var url = YouTubeCatalog.ParseCaptionUrl(json, "tr.3");
        Assert.NotNull(url);
        Assert.Contains("lang=tr", url, StringComparison.Ordinal);
        Assert.Contains("fmt=vtt", url, StringComparison.Ordinal);
        var any = YouTubeCatalog.ParseCaptionUrl(json, null);
        Assert.NotNull(any);
        Assert.Contains("lang=en", any, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lang=en", YouTubeCatalog.PickCaptionUrl(json, null), StringComparison.OrdinalIgnoreCase);
        Assert.Null(YouTubeCatalog.PickCaptionUrl(json, "off"));
        var missing = YouTubeCatalog.ParseCaptionUrl(json, "de");
        Assert.NotNull(missing);
        Assert.Contains("lang=tr", missing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tlang=de", missing, StringComparison.OrdinalIgnoreCase);
        var asrOnly =
            """
            {"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[
              {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=tr&kind=asr","languageCode":"tr","kind":"asr"}
            ]}}}
            """;
        var translated = YouTubeCatalog.ParseCaptionUrl(asrOnly, "de");
        Assert.NotNull(translated);
        Assert.Contains("lang=tr", translated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tlang=de", translated, StringComparison.OrdinalIgnoreCase);
        Assert.True(YouTubeCatalog.CaptionUrlMatches("https://www.youtube.com/api/timedtext?v=x&lang=en&fmt=vtt", "en"));
        Assert.True(YouTubeCatalog.CaptionUrlMatches("https://www.youtube.com/api/timedtext?v=x&lang=en&tlang=tr&fmt=vtt", "tr"));
        Assert.False(YouTubeCatalog.CaptionUrlMatches("https://www.youtube.com/api/timedtext?v=x&lang=ar&fmt=vtt", "en"));
        Assert.True(MediaLanguage.Matches("en", "eng"));
        Assert.True(MediaLanguage.Matches("tr", "tur"));
        Assert.True(MediaLanguage.Matches("de", "ger"));
        Assert.True(MediaLanguage.Matches("en", "English"));
        Assert.True(MediaLanguage.Matches("en", YouTubeCatalog.CaptionLanguageHeader("WEBVTT\nLanguage: English\n")));
    }

    [Fact]
    public void Resolve_keeps_requested_languages()
    {
        var json =
            """
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"V3kIwZBou_o","title":"Doc"},"streamingData":{"hlsManifestUrl":"https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8"},"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[{"baseUrl":"https://www.youtube.com/api/timedtext?v=V3kIwZBou_o&lang=tr","languageCode":"tr"}]}}}
            """;
        var playable = YouTubeCatalog.Resolve("https://www.youtube.com/watch?v=V3kIwZBou_o", _ => json, "tr.3", "tr");
        Assert.NotNull(playable);
        Assert.Equal("tr", playable!.AudioLang);
        Assert.Equal("tr", playable.SubLang);
        Assert.Contains("lang=tr", playable.CaptionUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tr.3", "tr")]
    [InlineData("tr-TR", "tr")]
    [InlineData("en-US", "en")]
    [InlineData("tr:asr", "tr")]
    [InlineData("a.tr", "tr")]
    [InlineData("bn.3", "bn")]
    [InlineData("ru.3", "ru")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("zh-Hans.3", "zh-Hans")]
    [InlineData("pt-BR", "pt")]
    public void Language_normalize_strips_youtube_suffixes(string raw, string expected)
    {
        Assert.Equal(expected, MediaLanguage.Normalize(raw));
        Assert.True(MediaLanguage.Matches(raw, expected));
    }

    [Fact]
    public void Language_keeps_chinese_scripts_apart()
    {
        Assert.False(MediaLanguage.Matches("zh-Hans", "zh-Hant"));
        Assert.True(MediaLanguage.Matches("zh-Hans", "zh"));
        Assert.True(MediaLanguage.MatchesName("bn", "Bangla"));
        Assert.True(MediaLanguage.MatchesName("ru", "Russian"));
        Assert.True(MediaLanguage.MatchesName("zh-Hans", "Chinese (Simplified)"));
        Assert.False(MediaLanguage.MatchesName("zh-Hans", "Chinese (Traditional)"));
    }

    [Fact]
    public void Protocol_keeps_original_and_off()
    {
        Assert.True(ExternalOpen.TryParse(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=dQw4w9wgBcQ") +
            "&audio=original&sub=off",
            out var open));
        Assert.Equal("original", open.AudioLang);
        Assert.Equal("off", open.SubLang);
        Assert.True(MediaLanguage.IsOriginal("original"));
        Assert.True(MediaLanguage.IsPlausible("original"));
        Assert.True(MediaLanguage.IsOff("off"));
        Assert.True(ExternalOpen.TryParse(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=dQw4w9wgBcQ") +
            "&height=1072",
            out var sized));
        Assert.Equal(1080, sized.Height);
        Assert.True(ExternalOpen.TryParse(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=Qtl8lJwbd4g") +
            "&audio=zh-Hans&sub=bn",
            out var all));
        Assert.Equal("zh-Hans", all.AudioLang);
        Assert.Equal("bn", all.SubLang);
    }

    [Theory]
    [InlineData("Altyazılar", "")]
    [InlineData("alt", "")]
    [InlineData("Ses parçası", "")]
    [InlineData("Türkçe", "tr")]
    [InlineData("Turkish", "tr")]
    [InlineData("English", "en")]
    public void Language_normalize_rejects_menu_labels(string raw, string expected)
    {
        Assert.Equal(expected, MediaLanguage.Normalize(raw));
        Assert.Equal(expected.Length > 0, MediaLanguage.IsPlausible(MediaLanguage.Normalize(raw)));
    }

    [Fact]
    public void Protocol_roundtrip_keeps_url_and_kind()
    {
        var link = ExternalOpen.ToProtocol("https://www.youtube.com/watch?v=dQw4w9wgBcQ", "Song", StreamKind.Live);
        Assert.True(ExternalOpen.TryParse(link, out var open));
        Assert.Contains("youtube.com/watch", open.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Song", open.Title);
        Assert.Equal(StreamKind.Live, open.Kind);
        Assert.True(YouTubeCatalog.IsWatchUrl(open.Url));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=Qtl8lJwbd4g&t=159s", 159)]
    [InlineData("https://www.youtube.com/watch?v=Qtl8lJwbd4g&t=2m39s", 159)]
    [InlineData("https://www.youtube.com/watch?v=Qtl8lJwbd4g&start=40", 40)]
    public void Reads_watch_start_time(string url, int seconds)
    {
        Assert.Equal(seconds, YouTubeCatalog.ReadStartSeconds(url));
    }

    [Fact]
    public void Parse_caption_url_uses_default_audio_caption()
    {
        var json =
            """
            {"captions":{"playerCaptionsTracklistRenderer":{
              "defaultAudioTrackIndex":0,
              "audioTracks":[{"defaultCaptionTrackIndex":1,"captionTrackIndices":[0,1]}],
              "captionTracks":[
                {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=en","languageCode":"en"},
                {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=tr","languageCode":"tr"}
              ]
            }}}
            """;
        var url = YouTubeCatalog.ParseCaptionUrl(json, null);
        Assert.NotNull(url);
        Assert.Contains("lang=tr", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Caption_loader_keeps_requested_language()
    {
        var english = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(english, "WEBVTT\nLanguage: English\n\n00:00:00.000 --> 00:00:01.000\nHi\n");
        var urls = StreamCaptionLoader.Urls("dQw4w9wgBcQ", "tr", "https://www.youtube.com/api/timedtext?v=x&lang=en&fmt=vtt").ToList();
        Assert.Contains(urls, item => item.Contains("tlang=tr", StringComparison.Ordinal));
        Assert.Contains("tlang=tr", urls[0], StringComparison.Ordinal);
        var loaded = StreamCaptionLoader.Load("nope", "tr", english);
        Assert.Null(loaded);
        File.Delete(english);
    }

    [Fact]
    public void Caption_loader_uses_signed_url_without_language()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nAuto line\n");
        var path = StreamCaptionLoader.Load("signedvidxx1", null, vtt);
        Assert.NotNull(path);
        Assert.Contains("Auto line", File.ReadAllText(StreamCaptionLoader.DocumentPath(path)), StringComparison.Ordinal);
        File.Delete(vtt);
    }

    [Fact]
    public void Parse_caption_tracks_keeps_official_and_asr_apart()
    {
        var tracks = YouTubeCatalog.ParseCaptionTracks(
            """
            {"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[
              {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=en","languageCode":"en","name":{"simpleText":"English"}},
              {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=en&kind=asr","languageCode":"en","kind":"asr","name":{"simpleText":"English (auto-generated)"}},
              {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=tr","languageCode":"tr","name":{"simpleText":"Turkish"}}
            ]}}}
            """);
        Assert.Equal(3, tracks.Count);
        Assert.Contains(tracks, item => item.Language == "en" && item.Name == "English");
        Assert.Contains(tracks, item => item.Language == "en:asr" && item.Url.Contains("kind=asr", StringComparison.Ordinal));
        Assert.Contains(tracks, item => item.Language == "tr" && item.Url.Contains("fmt=vtt", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_caption_url_prefers_asr_when_requested()
    {
        var json =
            """
            {"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[
              {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=en","languageCode":"en"},
              {"baseUrl":"https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=en&kind=asr","languageCode":"en","kind":"asr"}
            ]}}}
            """;
        var asr = YouTubeCatalog.ParseCaptionUrl(json, "en:asr");
        Assert.NotNull(asr);
        Assert.Contains("kind=asr", asr, StringComparison.Ordinal);
        var manual = YouTubeCatalog.ParseCaptionUrl(json, "en");
        Assert.NotNull(manual);
        Assert.DoesNotContain("kind=asr", manual, StringComparison.Ordinal);
        Assert.Equal("en:asr", YouTubeCatalog.CaptionLanguageFromUrl(
            "https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=en&kind=asr&fmt=vtt"));
        Assert.Equal("en", YouTubeCatalog.CaptionLanguageFromUrl(
            "https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=tr&kind=asr&tlang=en"));
        Assert.Equal("tr:asr", YouTubeCatalog.CaptionSourceLanguageFromUrl(
            "https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=tr&kind=asr&tlang=en"));
        Assert.True(YouTubeCatalog.CaptionUrlIsTranslate(
            "https://www.youtube.com/api/timedtext?v=abcdefghijk&lang=tr&kind=asr&tlang=en"));
    }

    [Fact]
    public void Caption_loader_does_not_fall_back_to_native_target_for_auto_translate()
    {
        var caption =
            "https://www.youtube.com/api/timedtext?v=fFxbSyTAmBs&lang=tr&kind=asr&tlang=en&fmt=vtt";
        var urls = StreamCaptionLoader.Urls("fFxbSyTAmBs", "en", caption).ToList();
        Assert.True(StreamCaptionLoader.IsTranslateRequest("en", caption));
        Assert.Contains(urls, item => item.Contains("tlang=en", StringComparison.Ordinal) &&
                                      item.Contains("lang=tr", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, item =>
            item.Contains("lang=en", StringComparison.OrdinalIgnoreCase) &&
            !item.Contains("tlang=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("tr-asr.en", StreamCaptionLoader.CacheTag("en", caption));
        Assert.Contains(
            YouTubeCatalog.CaptionDownloadUrls(caption),
            item => item.Contains("fmt=srv3", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("en-asr", StreamCaptionLoader.CacheTag("en:asr",
            "https://www.youtube.com/api/timedtext?v=fFxbSyTAmBs&lang=en&kind=asr&fmt=vtt"));
        var official = "https://www.youtube.com/api/timedtext?v=Qtl8lJwbd4g&lang=en&fmt=vtt";
        var officialFmts = YouTubeCatalog.CaptionDownloadUrls(official).ToList();
        Assert.Contains(officialFmts, item => item.Contains("fmt=srv3", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(officialFmts, item => item.Contains("fmt=vtt", StringComparison.OrdinalIgnoreCase));
        Assert.True(MediaLanguage.Matches("de", "de-DE"));
        Assert.Equal("de", MediaLanguage.Normalize("de-DE"));
        var tlangFmts = YouTubeCatalog.CaptionDownloadUrls(caption).ToList();
        Assert.StartsWith(YouTubeCatalog.WithCaptionFormat(caption, "srv3"), tlangFmts[0]);
        Assert.Contains(tlangFmts, item => item.Contains("fmt=srv3", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tlangFmts, item => item.Contains("fmt=json3", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(YouTubeCatalog.EnsureVtt(caption), tlangFmts[^1]);
        Assert.Equal(
            "de",
            StreamCaptionLoader.EffectiveLanguage(
                "de",
                "https://www.youtube.com/api/timedtext?v=Qtl8lJwbd4g&lang=en&fmt=vtt"));
        Assert.Equal(
            "tr:asr",
            StreamCaptionLoader.EffectiveLanguage(
                "tr:asr",
                "https://www.youtube.com/api/timedtext?v=EzWLUda58k4&lang=tr&kind=asr&tlang=de&fmt=vtt"));
        Assert.Equal(
            "de",
            StreamCaptionLoader.EffectiveLanguage(
                "de",
                "https://www.youtube.com/api/timedtext?v=EzWLUda58k4&lang=tr&kind=asr&fmt=vtt"));
        var leftover = StreamCaptionLoader.Urls(
            "Qtl8lJwbd4g",
            "de",
            "https://www.youtube.com/api/timedtext?v=Qtl8lJwbd4g&lang=en&tlang=de&fmt=vtt").ToList();
        Assert.Contains(leftover, item => item.Contains("tlang=de", StringComparison.Ordinal));
        Assert.DoesNotContain(leftover, item =>
            item.Contains("lang=en", StringComparison.Ordinal) &&
            !item.Contains("tlang=", StringComparison.Ordinal));
        Assert.Equal(
            "https://www.youtube.com/api/timedtext?v=Qtl8lJwbd4g&lang=en&fmt=vtt",
            YouTubeCatalog.WithoutTranslate(
                "https://www.youtube.com/api/timedtext?v=Qtl8lJwbd4g&lang=en&tlang=de&fmt=vtt"));
    }

    [Fact]
    public void Caption_loader_does_not_reuse_native_cache_for_a_translation()
    {
        var folder = StreamCaptionLoader.CacheDirectory;
        Directory.CreateDirectory(folder);
        var id = "xlatcachexx1";
        var native = Path.Combine(folder, id + ".en.srt");
        File.WriteAllText(native, "1\n00:00:00,000 --> 00:00:01,000\nNative auto\n");
        var caption =
            "https://www.youtube.com/api/timedtext?v=" + id + "&lang=tr&kind=asr&tlang=en";
        Assert.Null(StreamCaptionLoader.Existing(id, "en", caption));
        Assert.NotNull(StreamCaptionLoader.Existing(id, "en"));
        File.Delete(native);
    }

    [Fact]
    public void Open_drop_queue_roundtrips()
    {
        var payload = "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=dQw4w9wgBcQ") + "&sub=tr";
        Assert.True(InstanceIpc.TryEnqueueDrop(payload));
        var drained = InstanceIpc.DrainDrops();
        Assert.Contains(payload, drained);
        Assert.Empty(InstanceIpc.DrainDrops());
    }

    [Fact]
    public void Stream_launch_flag_reads_the_url()
    {
        var parsed = InstanceLaunchArgs.Parse(["--stream", "https://www.youtube.com/watch?v=dQw4w9wgBcQ"]);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9wgBcQ", parsed.Path);
    }
}
