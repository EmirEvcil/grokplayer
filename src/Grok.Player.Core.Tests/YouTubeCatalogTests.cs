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
    }

    [Fact]
    public void Caption_url_normalizes_dub_codes_and_asr()
    {
        var url = YouTubeCatalog.CaptionVttUrl("dQw4w9wgBcQ", "tr.3");
        Assert.Contains("lang=tr", url, StringComparison.Ordinal);
        Assert.DoesNotContain("tr.3", url, StringComparison.Ordinal);
        Assert.Contains("fmt=vtt", url, StringComparison.Ordinal);
        Assert.Contains("kind=asr", YouTubeCatalog.CaptionVttUrl("dQw4w9wgBcQ", "tr:asr"), StringComparison.Ordinal);
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
        Assert.Null(YouTubeCatalog.PickCaptionUrl(json, null));
        Assert.Null(YouTubeCatalog.PickCaptionUrl(json, "off"));
        var missing = YouTubeCatalog.ParseCaptionUrl(json, "de");
        Assert.NotNull(missing);
        Assert.Contains("lang=en", missing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tlang=de", missing, StringComparison.OrdinalIgnoreCase);
        Assert.True(YouTubeCatalog.CaptionUrlMatches("https://www.youtube.com/api/timedtext?v=x&lang=en&fmt=vtt", "en"));
        Assert.True(YouTubeCatalog.CaptionUrlMatches("https://www.youtube.com/api/timedtext?v=x&lang=en&tlang=tr&fmt=vtt", "tr"));
        Assert.False(YouTubeCatalog.CaptionUrlMatches("https://www.youtube.com/api/timedtext?v=x&lang=ar&fmt=vtt", "en"));
        Assert.True(MediaLanguage.Matches("en", "eng"));
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
        Assert.DoesNotContain(urls, item => item.Equals("https://www.youtube.com/api/timedtext?v=x&lang=en&fmt=vtt", StringComparison.Ordinal));
        var loaded = StreamCaptionLoader.Load("nope", "tr", english);
        Assert.Null(loaded);
        File.Delete(english);
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
