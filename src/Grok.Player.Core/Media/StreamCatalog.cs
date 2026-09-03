using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grok.Player.Core.Download;
using Grok.Player.Core.Launch;
using Grok.Player.Core.Subtitles;

namespace Grok.Player.Core.Media;

public static class StreamCatalog
{
    public const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string TwitchClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
    }

    public static bool LooksResolvable(string? url) =>
        !YouTubeCatalog.IsWatchUrl(url) &&
        UrlSanitizer.IsUrl(url) &&
        !IsDirectMedia(url) &&
        (TryReadKick(url, out _, out _) ||
         TryReadTwitch(url, out _, out _) ||
         TryReadRumble(url, out _) ||
         TryReadTikTok(url, out _) ||
         TryReadDailymotion(url, out _) ||
         TryReadInstagram(url, out _) ||
         LooksLikeHtmlPage(url));

    public static string ContentKey(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? ""
            : id.Contains('|', StringComparison.Ordinal) ? id : "youtube|" + id;

    public static YouTubePlayable? Resolve(string url, string? audioLang = null, string? subLang = null)
    {
        if (YouTubeCatalog.IsWatchUrl(url))
        {
            return YouTubeCatalog.Resolve(url, null, audioLang, subLang);
        }

        if (TryReadKick(url, out var kickKind, out var kickId))
        {
            return ResolveKick(kickKind, kickId, url, audioLang, subLang);
        }

        if (TryReadTwitch(url, out var twitchKind, out var twitchId))
        {
            return ResolveTwitch(twitchKind, twitchId, audioLang, subLang);
        }

        if (TryReadRumble(url, out var rumbleId))
        {
            return ResolveRumble(rumbleId, url, audioLang, subLang);
        }

        if (TryReadTikTok(url, out var tiktokId))
        {
            return ResolveTikTok(tiktokId, url, audioLang, subLang);
        }

        if (TryReadDailymotion(url, out var dailyId))
        {
            return ResolveDailymotion(dailyId, url, audioLang, subLang);
        }

        if (TryReadInstagram(url, out var instagramId))
        {
            return ResolveInstagram(instagramId, url, audioLang, subLang);
        }

        if (TryReadPlayturka(url, out var playturkaId))
        {
            return ResolvePlayturka(playturkaId, url, audioLang, subLang);
        }

        if (IsDirectMedia(url))
        {
            return ResolveDirect(url, audioLang, subLang);
        }

        return ResolvePage(url, audioLang, subLang);
    }

    public static bool LooksKickLivePlayback(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.Contains("live-video.net", StringComparison.OrdinalIgnoreCase) &&
        url.Contains("channel.", StringComparison.OrdinalIgnoreCase);

    public static bool TryReadKick(string? url, out string kind, out string id)
    {
        kind = "";
        id = "";
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("kick.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            kind = "video";
            id = parts[1];
            return id.Length > 0;
        }

        if (parts.Length >= 3 && parts[1].Equals("videos", StringComparison.OrdinalIgnoreCase))
        {
            kind = "video";
            id = parts[2];
            return id.Length > 0;
        }

        if (parts.Length >= 3 && parts[1].Equals("clips", StringComparison.OrdinalIgnoreCase))
        {
            kind = "clip";
            id = parts[2];
            return id.Length > 0;
        }

        if (parts.Length >= 1 &&
            !parts[0].Equals("video", StringComparison.OrdinalIgnoreCase) &&
            !parts[0].Equals("browse", StringComparison.OrdinalIgnoreCase))
        {
            kind = "live";
            id = parts[0];
            return id.Length > 0;
        }

        return false;
    }

    public static bool TryReadTwitch(string? url, out string kind, out string id)
    {
        kind = "";
        id = "";
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("videos", StringComparison.OrdinalIgnoreCase))
        {
            kind = "vod";
            id = parts[1];
            return id.Length > 0;
        }

        if (parts.Length >= 3 && parts[1].Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            kind = "vod";
            id = parts[2];
            return id.Length > 0;
        }

        if (parts.Length >= 1)
        {
            kind = "live";
            id = parts[0];
            return id.Length > 0 && !parts[0].Equals("directory", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static bool TryReadRumble(string? url, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("rumble.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("embed", StringComparison.OrdinalIgnoreCase))
        {
            id = parts[1];
            return id.Length > 0;
        }

        if (parts.Length >= 1)
        {
            var slug = parts[0];
            var dash = slug.IndexOf('-');
            id = dash > 0 ? slug[..dash] : slug;
            return id.Length > 1 && id[0] is 'v' or 'V';
        }

        return false;
    }

    public static bool TryReadTikTok(string? url, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("video", StringComparison.OrdinalIgnoreCase) &&
                parts[i + 1].Length >= 8 &&
                parts[i + 1].All(char.IsDigit))
            {
                id = parts[i + 1];
                return true;
            }
        }

        return false;
    }

    public static bool TryReadDailymotion(string? url, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !(uri.Host.Contains("dailymotion.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Contains("dai.ly", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            id = parts[1];
            var cut = id.IndexOf('_');
            if (cut > 0)
            {
                id = id[..cut];
            }

            return id.Length > 0;
        }

        if (parts.Length == 1 && uri.Host.Contains("dai.ly", StringComparison.OrdinalIgnoreCase))
        {
            id = parts[0];
            return id.Length > 0;
        }

        return false;
    }

    public static bool TryReadInstagram(string? url, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            (parts[0].Equals("reel", StringComparison.OrdinalIgnoreCase) ||
             parts[0].Equals("reels", StringComparison.OrdinalIgnoreCase) ||
             parts[0].Equals("p", StringComparison.OrdinalIgnoreCase) ||
             parts[0].Equals("tv", StringComparison.OrdinalIgnoreCase)))
        {
            id = parts[1];
            return id.Length > 0;
        }

        return false;
    }

    internal static YouTubePlayable? ResolveInstagram(string id, string pageUrl, string? audioLang, string? subLang)
    {
        var html = GetText(pageUrl, ChromeUa, "https://www.instagram.com/");
        var media = PickMediaUrl(MediaUrlsIn(html));
        if (string.IsNullOrWhiteSpace(media))
        {
            return ResolvePage(pageUrl, audioLang, subLang);
        }

        return new YouTubePlayable(
            "instagram|" + id,
            media,
            HtmlTitle(html) ?? id,
            StreamKind.Vod,
            userAgent: ChromeUa,
            audioLang: audioLang,
            subLang: subLang,
            referer: "https://www.instagram.com/");
    }

    public static bool IsDirectMedia(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !UrlSanitizer.IsUrl(url))
        {
            return false;
        }

        var ext = StreamProbe.Extension(url);
        if (ext is ".m3u8" or ".m3u" or ".mpd" or ".mp4" or ".mkv" or ".webm" or ".mov" or ".m4v" ||
            IsMediaCdn(url))
        {
            return true;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.AbsolutePath.EndsWith("master.txt", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.EndsWith("playlist.txt", StringComparison.OrdinalIgnoreCase));
    }

    public static bool RequiresPlayerPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return Regex.IsMatch(
                   uri.AbsolutePath,
                   @"/manifests/[^/]+/master\.(?:txt|m3u8)$",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
               (uri.Query.Contains("verify=", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Contains("fastplay.", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsMediaCdn(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        var text = uri.AbsoluteUri;
        return host.Contains("tiktokcdn", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("byteoversea", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("googlevideo", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("live-video.net", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("stream.kick.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("ttvnw.net", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("rumble.cloud", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("dmcdn.net", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("playmix.uno", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("collaborate.pics", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("hls-vod", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("mime_type=video", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("master.txt", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("playlist.txt", StringComparison.OrdinalIgnoreCase) ||
               ((host.Contains("cdninstagram", StringComparison.OrdinalIgnoreCase) ||
                 host.Contains("fbcdn.net", StringComparison.OrdinalIgnoreCase) ||
                 host.Contains("scontent", StringComparison.OrdinalIgnoreCase)) &&
                (text.Contains("/t16", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("/t2/", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("/t3/", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("/o1/v/", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains(".mp4", StringComparison.OrdinalIgnoreCase)));
    }

    public static bool LooksLikeHtmlPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !UrlSanitizer.IsUrl(url) || IsDirectMedia(url))
        {
            return false;
        }

        var ext = StreamProbe.Extension(url);
        return ext is "" or ".html" or ".htm" or ".php" or ".aspx";
    }

    internal static YouTubePlayable? ResolveKick(string kind, string id, string pageUrl, string? audioLang, string? subLang)
    {
        if (kind == "video")
        {
            return ResolveKickVideo(id, pageUrl, audioLang, subLang);
        }

        var json = kind == "clip"
            ? GetJson("https://kick.com/api/v2/clips/" + Uri.EscapeDataString(id), "https://kick.com/")
            : GetJson("https://kick.com/api/v2/channels/" + Uri.EscapeDataString(id), "https://kick.com/");
        return ParseKickJson(json, kind, id, audioLang, subLang);
    }

    internal static YouTubePlayable? ResolveKickVideo(string id, string pageUrl, string? audioLang, string? subLang)
    {
        var fromWebPlayer = ResolveKickWebVideo(id, pageUrl, audioLang, subLang);
        if (fromWebPlayer is not null)
        {
            return fromWebPlayer;
        }

        if (LooksUuid(id))
        {
            var byUuid = ParseKickJson(
                GetJson("https://kick.com/api/v1/video/" + Uri.EscapeDataString(id), "https://kick.com/"),
                "video",
                id,
                audioLang,
                subLang);
            if (byUuid is not null)
            {
                return byUuid;
            }
        }

        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[1].Equals("videos", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var list = GetJson("https://kick.com/api/v2/channels/" + Uri.EscapeDataString(parts[0]) + "/videos", "https://kick.com/");
        if (string.IsNullOrWhiteSpace(list))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(list);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var slug = ReadString(item, "slug");
                var uuid = item.TryGetProperty("video", out var video) ? ReadString(video, "uuid") : null;
                if (!id.Equals(slug, StringComparison.OrdinalIgnoreCase) &&
                    !id.Equals(uuid, StringComparison.OrdinalIgnoreCase) &&
                    !id.Equals(ReadNumber(item, "id"), StringComparison.Ordinal))
                {
                    continue;
                }

                var fromList = ParseKickJson(item.GetRawText(), "video", uuid ?? id, audioLang, subLang);
                if (fromList is not null)
                {
                    return fromList;
                }

                if (!string.IsNullOrWhiteSpace(uuid))
                {
                    return ParseKickJson(
                        GetJson("https://kick.com/api/v1/video/" + Uri.EscapeDataString(uuid), "https://kick.com/"),
                        "video",
                        uuid,
                        audioLang,
                        subLang);
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        // A VOD page on a live channel still embeds the live thumbnail. Do
        // not rebuild that live HLS and open it as this video.
        return null;
    }

    private static YouTubePlayable? ResolveKickWebVideo(string id, string pageUrl, string? audioLang, string? subLang)
    {
        var html = GetText(pageUrl, ChromeUa, "https://kick.com/");
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var channelId = KickChannelIdFromPage(html);
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return null;
        }

        var details = GetJson(
            "https://web.kick.com/api/v1/channels/" + channelId + "/videos/" + Uri.EscapeDataString(id),
            pageUrl);
        var title = KickWebVideoTitle(details) ?? KickTitleFromPage(html) ?? id;
        var playback = PostKickPlayback(id, pageUrl);
        if (string.IsNullOrWhiteSpace(playback))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(playback);
            var media = ReadNested(document.RootElement, "playback_url", "vod");
            var session = ReadNested(document.RootElement, "playback_url", "vod_session");
            if (!string.IsNullOrWhiteSpace(session))
            {
                var sessionJson = GetJson(session, pageUrl);
                var sessionMedia = KickSessionManifest(sessionJson);
                if (!string.IsNullOrWhiteSpace(sessionMedia))
                {
                    media = sessionMedia;
                }
            }
            if (string.IsNullOrWhiteSpace(media) || LooksAd(media))
            {
                return null;
            }

            var playable = new YouTubePlayable(
                "kick|" + id,
                media,
                title,
                StreamKind.Vod,
                userAgent: ChromeUa,
                audioLang: audioLang,
                subLang: subLang,
                referer: "https://kick.com/");
            return AttachVodCaptions(playable, subLang);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? KickSessionManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadString(document.RootElement, "manifestUrl") ??
                   ReadString(document.RootElement, "manifest_url");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? KickChannelIdFromPage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        foreach (var pattern in new[]
                 {
                     @"WebVideo[^0-9]{1,100}(\d+)",
                     @"initialValue[^0-9]{0,80}channelId[^0-9]{0,20}(\d+)",
                     @"channel\\?""\s*:\s*\{[^{}]{0,400}?id\\?""\s*:\s*(\d+)"
                 })
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static string? KickWebVideoTitle(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("data", out var data)
                ? ReadString(data, "title")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? PostKickPlayback(string id, string pageUrl)
    {
        var path = Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)
            ? page.AbsolutePath.TrimStart('/')
            : "videos/" + id;
        var body = JsonSerializer.Serialize(new
        {
            video_player = new
            {
                player = new
                {
                    player_name = "web",
                    player_version = "grokplayer",
                    player_software = "IVS Player",
                    player_software_version = "1"
                },
                mux_sdk = new { sdk_available = false },
                pal_sdk = new { sdk_available = false, nonce = "" },
                datazoom_sdk = new { sdk_available = false, datazoom_sdk_version = "", om_sdk_version = "" },
                google_ads_sdk = new { sdk_available = false }
            },
            video_session = new
            {
                page_type = "video",
                player_remote_played = false,
                enable_sampling = false,
                url_path = path,
                autoplay_behaviour = "auto",
                play_muted = false,
                viewer_connection_type = ""
            },
            user_session = new
            {
                session_id = "",
                player_device_id = "unknown",
                browser_lang = "en-US",
                non_personalised_ads = true,
                ad_targeting = ""
            }
        });
        return PostJson(
            "https://web.kick.com/api/v1/stream/" + Uri.EscapeDataString(id) + "/playback",
            body,
            pageUrl,
            null,
            webPlatform: true);
    }

    internal static YouTubePlayable? KickFromPage(string pageUrl, string id, string? audioLang, string? subLang)
    {
        var html = GetText(pageUrl, ChromeUa, "https://kick.com/");
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var hls = KickHlsFromPage(html);
        if (string.IsNullOrWhiteSpace(hls))
        {
            return null;
        }

        var title = KickTitleFromPage(html) ?? id;
        var playable = new YouTubePlayable(
            "kick|" + id,
            hls,
            title,
            StreamKind.Vod,
            userAgent: ChromeUa,
            audioLang: audioLang,
            subLang: subLang,
            referer: "https://kick.com/");
        return AttachVodCaptions(playable, subLang);
    }

    public static string? KickHlsFromPage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var thumb = Regex.Match(
            html,
            @"video_thumbnails/([A-Za-z0-9_-]+)/([A-Za-z0-9_-]+)/",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var start = Regex.Match(
            html,
            @"start_time\\?""?\s*:\s*\\?""(\d{4}-\d{2}-\d{2}T\d{2}:\d{2})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!thumb.Success || !start.Success)
        {
            return null;
        }

        if (!DateTime.TryParse(
                start.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var at))
        {
            return null;
        }

        return "https://stream.kick.com/3c81249a5ce0/ivs/v1/196233775518/" +
               thumb.Groups[1].Value + "/" +
               at.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
               at.Month.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
               at.Day.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
               at.Hour.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
               at.Minute.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
               thumb.Groups[2].Value +
               "/media/hls/master.m3u8";
    }

    internal static string? KickTitleFromPage(string html)
    {
        var title = Regex.Match(
            html,
            @"session_title\\?""?\s*:\s*\\?""([^""\\]+)",
            RegexOptions.IgnoreCase);
        if (title.Success)
        {
            return System.Net.WebUtility.HtmlDecode(title.Groups[1].Value);
        }

        return HtmlTitle(html);
    }

    private static YouTubePlayable? ParseKickJson(string? json, string kind, string id, string? audioLang, string? subLang)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var title = ReadString(root, "session_title") ??
                        ReadNested(root, "livestream", "session_title") ??
                        ReadString(root, "title") ??
                        id;
            var playback = ReadString(root, "playback_url") ??
                           ReadNested(root, "livestream", "playback_url") ??
                           ReadNested(root, "source", "url") ??
                           ReadString(root, "source");
            var live = ReadBool(root, "is_live") ||
                       (root.TryGetProperty("livestream", out var liveEl) &&
                        liveEl.ValueKind == JsonValueKind.Object &&
                        ReadBool(liveEl, "is_live"));
            if (string.IsNullOrWhiteSpace(playback))
            {
                return null;
            }

            var streamKind = live || kind == "live" ? StreamKind.Live : StreamKind.Vod;
            if (kind == "live" && !live)
            {
                return null;
            }

            var playable = new YouTubePlayable(
                "kick|" + id,
                playback,
                title,
                streamKind,
                userAgent: ChromeUa,
                audioLang: audioLang,
                subLang: subLang,
                referer: "https://kick.com/");
            if (streamKind != StreamKind.Live)
            {
                var hinted = ReadString(root, "subtitle") ??
                             ReadString(root, "subtitles") ??
                             ReadNested(root, "subtitles", "url") ??
                             ReadNested(root, "subtitle", "url");
                if (!string.IsNullOrWhiteSpace(hinted))
                {
                    playable = playable.WithCaption(hinted);
                }
            }

            return AttachVodCaptions(playable, subLang);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static YouTubePlayable? ResolveTwitch(string kind, string id, string? audioLang, string? subLang)
    {
        var isLive = kind == "live";
        var token = TwitchAccessToken(isLive, id);
        if (token is null)
        {
            return null;
        }

        var path = isLive
            ? "https://usher.ttvnw.net/api/channel/hls/" + Uri.EscapeDataString(id) + ".m3u8"
            : "https://usher.ttvnw.net/vod/" + Uri.EscapeDataString(id) + ".m3u8";
        var media = path + "?client_id=" + TwitchClientId +
                    "&token=" + Uri.EscapeDataString(token.Value.Token) +
                    "&sig=" + Uri.EscapeDataString(token.Value.Signature) +
                    "&allow_source=true&allow_audio_only=true&fast_bread=true";
        var playable = new YouTubePlayable(
            isLive ? "twitch|" + id : "twitch|v" + id,
            media,
            id,
            isLive ? StreamKind.Live : StreamKind.Vod,
            userAgent: ChromeUa,
            audioLang: audioLang,
            subLang: subLang,
            referer: "https://www.twitch.tv/");
        if (!isLive)
        {
            var hinted = TwitchCaptionUrl(id, subLang);
            if (!string.IsNullOrWhiteSpace(hinted))
            {
                playable = playable.WithCaption(hinted);
            }
        }

        return AttachVodCaptions(playable, subLang);
    }

    private static StreamKind ClassifyDirectPlaylist(string url)
    {
        var fromUrl = StreamProbe.ClassifyUrl(url);
        if (fromUrl != StreamKind.Unknown)
        {
            return fromUrl;
        }

        var body = GetText(url, ChromeUa, PageOrigin(url)) ?? "";
        var fromManifest = StreamProbe.ClassifyManifest(body);
        if (fromManifest != StreamKind.Unknown)
        {
            return fromManifest;
        }

        if (HlsPlaylist.IsMaster(body))
        {
            var variant = StreamProbe.FirstVariantUri(body, url);
            if (!string.IsNullOrWhiteSpace(variant))
            {
                var inner = StreamProbe.ClassifyManifest(GetText(variant, ChromeUa, PageOrigin(url)) ?? "");
                if (inner != StreamKind.Unknown)
                {
                    return inner;
                }
            }
        }

        return StreamKind.Vod;
    }

    internal static YouTubePlayable? ResolveDirect(string url, string? audioLang, string? subLang)
    {
        if (LooksImagePlaylistUrl(url))
        {
            var sibling = SiblingPlaylistUrl(url);
            return string.IsNullOrWhiteSpace(sibling) || sibling == url
                ? null
                : ResolveDirect(sibling, audioLang, subLang);
        }

        var ext = StreamProbe.Extension(url);
        var kind = ext is ".m3u8" or ".m3u" or ".mpd" or ".txt"
            ? ClassifyDirectPlaylist(url)
            : StreamKind.Vod;
        if (ext is ".m3u8" or ".m3u" or ".mpd" or ".txt")
        {
            var body = GetText(url, ChromeUa, PageOrigin(url)) ?? "";
            if (string.IsNullOrWhiteSpace(body) ||
                body.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                IsImagePlaylist(body))
            {
                return null;
            }
        }
        var playable = new YouTubePlayable(
            "direct|" + Math.Abs(url.GetHashCode(StringComparison.Ordinal)).ToString("x", System.Globalization.CultureInfo.InvariantCulture),
            url,
            UrlSanitizer.DisplayName(url),
            kind,
            userAgent: ChromeUa,
            audioLang: audioLang,
            subLang: subLang,
            referer: PageOrigin(url));
        return AttachVodCaptions(playable, subLang) ?? playable;
    }

    internal static YouTubePlayable? ResolveRumble(string id, string pageUrl, string? audioLang, string? subLang)
    {
        var embedId = RumbleEmbedId(pageUrl);
        if (string.IsNullOrWhiteSpace(embedId))
        {
            embedId = RumbleEmbedIdFromHtml(GetText(pageUrl, ChromeUa, "https://rumble.com/"));
        }

        if (!string.IsNullOrWhiteSpace(embedId))
        {
            id = embedId;
        }

        var json = GetJson(
            "https://rumble.com/embedJS/u3/?request=video&ver=2&v=" + Uri.EscapeDataString(id),
            pageUrl);
        if (string.IsNullOrWhiteSpace(json))
        {
            json = GetText(
                "https://rumble.com/embedJS/u3/?request=video&ver=2&v=" + Uri.EscapeDataString(id),
                ChromeUa,
                pageUrl);
        }

        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{'))
        {
            json = CurlText(
                "https://rumble.com/embedJS/u3/?request=video&ver=2&v=" + Uri.EscapeDataString(id),
                pageUrl);
        }

        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{'))
        {
            return RumbleFromHls(id, pageUrl, audioLang, subLang);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var title = ReadString(root, "title") ?? id;
            var live = root.TryGetProperty("live", out var liveEl) &&
                       liveEl.ValueKind == JsonValueKind.Number &&
                       liveEl.GetInt32() == 2;
            var media = RumbleBestUrl(root);
            if (string.IsNullOrWhiteSpace(media))
            {
                return null;
            }

            var playable = new YouTubePlayable(
                "rumble|" + id,
                media,
                title,
                live ? StreamKind.Live : StreamKind.Vod,
                userAgent: ChromeUa,
                audioLang: audioLang,
                subLang: subLang,
                referer: "https://rumble.com/");
            if (!live)
            {
                var caption = RumbleCaptionUrl(root, subLang);
                if (!string.IsNullOrWhiteSpace(caption))
                {
                    playable = playable.WithCaption(caption);
                }
            }

            return AttachVodCaptions(playable, subLang);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? RumbleEmbedId(string? pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl) || !UrlSanitizer.IsUrl(pageUrl))
        {
            return null;
        }

        var json = GetJson(
            "https://rumble.com/api/Media/oembed.json?url=" + Uri.EscapeDataString(pageUrl),
            "https://rumble.com/");
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{'))
        {
            json = CurlText(
                "https://rumble.com/api/Media/oembed.json?url=" + Uri.EscapeDataString(pageUrl),
                "https://rumble.com/");
        }

        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return RumbleEmbedIdFromHtml(ReadString(document.RootElement, "html"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? RumbleEmbedIdFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = Regex.Match(
            html,
            @"rumble\.com/embed/([^/?""']+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var embed = match.Groups[1].Value.Trim('/');
        var dot = embed.LastIndexOf('.');
        return dot >= 0 ? embed[(dot + 1)..] : embed;
    }

    private static YouTubePlayable? RumbleFromHls(string id, string pageUrl, string? audioLang, string? subLang)
    {
        foreach (var media in RumbleHlsCandidates(id))
        {
            if (!IsFetchableMedia(media, "https://rumble.com/"))
            {
                continue;
            }

            return new YouTubePlayable(
                "rumble|" + id,
                media,
                id,
                StreamKind.Vod,
                userAgent: ChromeUa,
                audioLang: audioLang,
                subLang: subLang,
                referer: "https://rumble.com/");
        }

        var html = GetText(pageUrl, ChromeUa, "https://rumble.com/");
        var picked = PickMediaUrl(MediaUrlsIn(html));
        if (string.IsNullOrWhiteSpace(picked) || !IsFetchableMedia(picked, pageUrl))
        {
            return null;
        }

        return new YouTubePlayable(
            "rumble|" + id,
            picked,
            HtmlTitle(html) ?? id,
            StreamKind.Vod,
            userAgent: ChromeUa,
            audioLang: audioLang,
            subLang: subLang,
            referer: "https://rumble.com/");
    }

    public static IReadOnlyList<string> RumbleHlsCandidates(string id)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            return list;
        }

        list.Add("https://rumble.com/hls-vod/" + Uri.EscapeDataString(id) + "/playlist.m3u8");
        if (id.Length > 1 && (id[0] is 'v' or 'V'))
        {
            list.Add("https://rumble.com/hls-vod/" + Uri.EscapeDataString(id[1..]) + "/playlist.m3u8");
        }
        else
        {
            list.Add("https://rumble.com/hls-vod/v" + Uri.EscapeDataString(id) + "/playlist.m3u8");
        }

        return list;
    }

    internal static YouTubePlayable? ResolveTikTok(string id, string pageUrl, string? audioLang, string? subLang)
    {
        var html = GetText(pageUrl, ChromeUa, "https://www.tiktok.com/");
        var media = TikTokPlayUrl(html, id);
        if (string.IsNullOrWhiteSpace(media))
        {
            return null;
        }

        var title = HtmlTitle(html) ?? id;
        return new YouTubePlayable(
            "tiktok|" + id,
            media,
            title,
            StreamKind.Vod,
            userAgent: ChromeUa,
            audioLang: audioLang,
            subLang: subLang,
            referer: "https://www.tiktok.com/");
    }

    internal static YouTubePlayable? ResolveDailymotion(string id, string pageUrl, string? audioLang, string? subLang)
    {
        var json = GetJson(
            "https://www.dailymotion.com/player/metadata/video/" + Uri.EscapeDataString(id),
            "https://www.dailymotion.com/");
        if (string.IsNullOrWhiteSpace(json))
        {
            return ResolvePage(pageUrl, audioLang, subLang);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var title = ReadString(root, "title") ?? id;
            var media = DailyQualityUrl(root);
            if (string.IsNullOrWhiteSpace(media))
            {
                return ResolvePage(pageUrl, audioLang, subLang);
            }

            var playable = new YouTubePlayable(
                "dailymotion|" + id,
                media,
                title,
                StreamKind.Vod,
                userAgent: ChromeUa,
                audioLang: audioLang,
                subLang: subLang,
                referer: pageUrl);
            var captions = DailyCaptionTracks(root);
            var caption = DailyCaptionUrl(root, subLang) ?? captions.FirstOrDefault()?.Url;
            if (!string.IsNullOrWhiteSpace(caption))
            {
                playable = playable.WithCaption(caption);
            }

            return AttachVodCaptions(playable, subLang);
        }
        catch (Exception)
        {
            return ResolvePage(pageUrl, audioLang, subLang);
        }
    }

    internal static YouTubePlayable? ResolvePage(string url, string? audioLang, string? subLang, int depth = 0, string? referer = null)
    {
        if (!UrlSanitizer.IsUrl(url) || IsDirectMedia(url))
        {
            return null;
        }

        var html = FetchHtml(url, referer ?? url);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var previewSpec = PreviewStoryboardIn(html, url);

        if (LooksMediaManifest(html))
        {
            return new YouTubePlayable(
                "manifest|" + Math.Abs(url.GetHashCode(StringComparison.Ordinal)).ToString("x", System.Globalization.CultureInfo.InvariantCulture),
                url,
                UrlSanitizer.DisplayName(url),
                StreamKind.Vod,
                userAgent: ChromeUa,
                audioLang: audioLang,
                subLang: subLang,
                storyboardSpec: previewSpec,
                referer: referer ?? url,
                formatHint: html.AsSpan().TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ? "hls" : "dash");
        }

        var protectedManifest = ProtectedManifestIn(html, url);
        if (protectedManifest is not null)
        {
            return new YouTubePlayable(
                "protected-manifest|" + Math.Abs(url.GetHashCode(StringComparison.Ordinal)).ToString("x", System.Globalization.CultureInfo.InvariantCulture),
                ProtectedStreamProxy.Register(
                    protectedManifest.Value.Url,
                    url,
                    protectedManifest.Value.Secret,
                    protectedManifest.Value.Timestamp),
                HtmlTitle(html) ?? UrlSanitizer.DisplayName(url),
                StreamKind.Vod,
                userAgent: ChromeUa,
                audioLang: audioLang,
                subLang: subLang,
                storyboardSpec: previewSpec,
                referer: url,
                formatHint: "hls");
        }

        var documents = PlayerDocuments(html);
        string? media = null;
        foreach (var document in documents)
        {
            media = PickFetchableMedia(document, url, referer ?? url);
            if (!string.IsNullOrWhiteSpace(media))
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(media) && depth < 4)
        {
            foreach (var document in documents)
            {
                var embeds = PlayerEmbedsIn(document, url)
                    .Concat(AjaxPlayerEmbeds(document, url));
                foreach (var embed in embeds.Distinct(StringComparer.Ordinal))
                {
                    if (TryReadPlayturka(embed, out var playturkaId))
                    {
                        var playturka = ResolvePlayturka(playturkaId, embed, audioLang, subLang);
                        if (playturka is not null)
                        {
                            return playturka;
                        }
                    }

                    var nested = ResolvePage(embed, audioLang, subLang, depth + 1, url);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(media))
        {
            return null;
        }

        return new YouTubePlayable(
            "page|" + Math.Abs(url.GetHashCode(StringComparison.Ordinal)).ToString("x", System.Globalization.CultureInfo.InvariantCulture),
            media,
            HtmlTitle(html) ?? UrlSanitizer.DisplayName(url),
            StreamKind.Vod,
            userAgent: ChromeUa,
            audioLang: audioLang,
            subLang: subLang,
            storyboardSpec: previewSpec,
            referer: url);
    }

    internal static string? PreviewStoryboardIn(string? html, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(html) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page))
        {
            return null;
        }

        var patterns = new[]
        {
            @"""(?:file|src|url)""\s*:\s*""(?<url>[^""]+\.vtt[^""]*)""[^\}\]]{0,240}?""(?:kind|type)""\s*:\s*""(?:thumbnails?|storyboard|preview|sprite|timeline)""",
            @"""(?:kind|type)""\s*:\s*""(?:thumbnails?|storyboard|preview|sprite|timeline)""[^\}\]]{0,240}?""(?:file|src|url)""\s*:\s*""(?<url>[^""]+\.vtt[^""]*)""",
            @"(?<url>(?:https?:)?//[^\s""'<>]+(?:thumbnail|storyboard|thumb|seeker|filmstrip|sprite|preview|timeline)[^\s""'<>]*\.vtt[^\s""'<>]*)"
        };
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var raw = System.Net.WebUtility.HtmlDecode(match.Groups["url"].Value)
                .Replace("\\/", "/", StringComparison.Ordinal);
            if (raw.StartsWith("//", StringComparison.Ordinal)) raw = page.Scheme + ":" + raw;
            if (Uri.TryCreate(page, raw, out var resolved) && resolved.Scheme is "http" or "https")
            {
                return "webvtt:" + resolved.AbsoluteUri;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> PlayerDocuments(string html)
    {
        var documents = new List<string> { html };
        for (var cursor = 0; cursor < documents.Count && cursor < 12; cursor++)
        {
            foreach (var unpacked in UnpackDeanEdwards(documents[cursor]))
            {
                if (!documents.Contains(unpacked, StringComparer.Ordinal))
                {
                    documents.Add(unpacked);
                }
            }

            foreach (var decrypted in DecryptCryptoJsDocuments(documents[cursor]))
            {
                if (!documents.Contains(decrypted, StringComparer.Ordinal))
                {
                    documents.Add(decrypted);
                }
            }
        }

        return documents;
    }

    internal static IReadOnlyList<string> DecryptCryptoJsDocuments(string? html)
    {
        var documents = new List<string>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return documents;
        }

        foreach (Match match in Regex.Matches(
                     html,
                     @"CryptoJS\.AES\.decrypt\(\s*(?:""(?<cipher>[^""]+)""|'(?<cipher>[^']+)')\s*,\s*(?:""(?<pass>[^""]*)""|'(?<pass>[^']*)')",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            try
            {
                var encrypted = Convert.FromBase64String(JsStringUnescape(match.Groups["cipher"].Value));
                if (encrypted.Length <= 16 || !encrypted.AsSpan(0, 8).SequenceEqual("Salted__"u8))
                {
                    continue;
                }

                var salt = encrypted.AsSpan(8, 8).ToArray();
                var password = Encoding.UTF8.GetBytes(JsStringUnescape(match.Groups["pass"].Value));
                var material = new List<byte>(48);
                byte[] previous = [];
                while (material.Count < 48)
                {
                    var input = new byte[previous.Length + password.Length + salt.Length];
                    Buffer.BlockCopy(previous, 0, input, 0, previous.Length);
                    Buffer.BlockCopy(password, 0, input, previous.Length, password.Length);
                    Buffer.BlockCopy(salt, 0, input, previous.Length + password.Length, salt.Length);
                    previous = MD5.HashData(input);
                    material.AddRange(previous);
                }

                using var aes = Aes.Create();
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = material.Take(32).ToArray();
                aes.IV = material.Skip(32).Take(16).ToArray();
                using var decryptor = aes.CreateDecryptor();
                var plain = decryptor.TransformFinalBlock(encrypted, 16, encrypted.Length - 16);
                var document = Encoding.UTF8.GetString(plain);
                if (!string.IsNullOrWhiteSpace(document))
                {
                    documents.Add(document);
                }
            }
            catch (Exception error) when (error is FormatException or CryptographicException)
            {
            }
        }

        return documents;
    }

    private static string? FirePlayerMedia(string html, string pageUrl, string outerReferer)
    {
        var texts = new List<string> { html };
        texts.AddRange(UnpackDeanEdwards(html));
        string? id = null;
        foreach (var text in texts)
        {
            var match = Regex.Match(
                text,
                @"\bFirePlayer\(\s*[""']([A-Za-z0-9_-]{16,})[""']",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                id = match.Groups[1].Value;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(id) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page))
        {
            return null;
        }

        var endpoint = new Uri(
            page,
            "/player/index.php?data=" + Uri.EscapeDataString(id) + "&do=getVideo").AbsoluteUri;
        var json = PostForm(endpoint, new Dictionary<string, string>
        {
            ["hash"] = id,
            ["r"] = outerReferer
        }, pageUrl);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var secured = ReadString(root, "securedLink") ?? ReadString(root, "secured_link");
            var source = ReadString(root, "videoSource") ?? ReadString(root, "video_source");
            return PickMediaUrl(new[] { secured, source }.Where(value => !string.IsNullOrWhiteSpace(value))!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? PickFetchableMedia(string html, string pageUrl, string referer)
    {
        foreach (var candidate in MediaUrlsIn(html)
                     .OrderByDescending(MediaScore)
                     .ThenBy(item => LooksDecoyManifest(item) ? 1 : 0))
        {
            if (LooksDecoyManifest(candidate) || MediaScore(candidate) < 0)
            {
                continue;
            }

            if (LooksPackedHls(candidate) || IsFetchableMedia(candidate, pageUrl))
            {
                return candidate;
            }
        }

        return FirePlayerMedia(html, pageUrl, referer);
    }

    private static bool IsFetchableMedia(string? media, string referer)
    {
        if (string.IsNullOrWhiteSpace(media) || LooksImagePlaylistUrl(media) || LooksAd(media) || LooksDecoyManifest(media))
        {
            return false;
        }

        if (Uri.TryCreate(media, UriKind.Absolute, out var mediaUri) &&
            Uri.TryCreate(referer, UriKind.Absolute, out var pageUri) &&
            string.Equals(
                mediaUri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                pageUri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ext = StreamProbe.Extension(media);
        if (ext is not (".m3u8" or ".m3u" or ".mpd" or ".txt"))
        {
            return true;
        }

        var body = GetText(media, ChromeUa, referer);
        if (string.IsNullOrWhiteSpace(body) ||
            body.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
            IsImagePlaylist(body))
        {
            return false;
        }

        var trimmed = body.AsSpan().TrimStart();
        if (ext is ".mpd")
        {
            return body.Contains("<MPD", StringComparison.OrdinalIgnoreCase);
        }

        return trimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> MediaUrlsIn(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return list;
        }

        void Add(string? href)
        {
            if (string.IsNullOrWhiteSpace(href))
            {
                return;
            }

            var clean = href.Replace("\\/", "/", StringComparison.Ordinal)
                .Replace("\\u002F", "/", StringComparison.Ordinal)
                .Replace("\\u002f", "/", StringComparison.Ordinal);
            if (!clean.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                list.Contains(clean, StringComparer.Ordinal))
            {
                return;
            }

            if (LooksImagePlaylistUrl(clean))
            {
                Add(SiblingPlaylistUrl(clean));
                return;
            }

            list.Add(clean);
        }

        foreach (Match match in Regex.Matches(
                     text,
                     @"https?:\\?/\\?/[^""'\s<>]+?\.(?:m3u8|m3u|mpd|mp4|mkv|webm|mov)(?:/(?:master|playlist)\.txt)?(?:\?[^""'\s<>]*)?",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            Add(match.Value);
        }

        foreach (Match match in Regex.Matches(
                     text,
                     @"https?:\\?/\\?/[^""'\s<>]+?/(?:master|playlist)\.txt(?:\?[^""'\s<>]*)?",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            Add(match.Value);
        }

        foreach (Match match in Regex.Matches(
                     text,
                     @"""contentUrl""\s*:\s*""([^""]+)""",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            Add(Regex.Unescape(match.Groups[1].Value));
        }

        foreach (Match match in Regex.Matches(
                     text,
                     @"property\s*=\s*""og:video(?::(?:url|secure_url))?""\s+content\s*=\s*""([^""]+)""",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            Add(match.Groups[1].Value);
        }

        foreach (var packed in PackedPlayerUrls(text))
        {
            Add(packed);
        }

        list.Sort(static (left, right) => right.Length.CompareTo(left.Length));
        var compact = new List<string>();
        foreach (var url in list)
        {
            if (compact.Exists(kept => kept.StartsWith(url, StringComparison.Ordinal)))
            {
                continue;
            }

            compact.Add(url);
        }

        return compact;
    }

    public static IReadOnlyList<ExternalCaption> SidecarCaptionsFromPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !UrlSanitizer.IsUrl(url) || IsDirectMedia(url))
        {
            return [];
        }

        var pending = new Queue<(string Page, string Referer, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue((url, url, 0));
        while (pending.Count > 0 && visited.Count < 18)
        {
            var (page, referer, depth) = pending.Dequeue();
            if (!visited.Add(page)) continue;
            try
            {
                if (TryReadPlayturka(page, out var playturkaId))
                {
                    var fromPlayer = PlayturkaCaptions(playturkaId);
                    if (fromPlayer.Count > 0)
                    {
                        return fromPlayer;
                    }
                }

                string? html = null;
                foreach (var tryReferer in new[] { referer, url, PageOrigin(page), page }.Distinct(StringComparer.Ordinal))
                {
                    html = FetchHtml(page, tryReferer);
                    if (!string.IsNullOrWhiteSpace(html)) break;
                }

                var found = SidecarCaptionsIn(html);
                if (found.Count > 0) return found;
                if (depth >= 3 || string.IsNullOrWhiteSpace(html)) continue;
                foreach (var embed in PlayerEmbedsIn(html, page)
                             .Concat(AjaxPlayerEmbeds(html, page))
                             .Distinct(StringComparer.Ordinal)
                             .Take(8))
                {
                    if (!visited.Contains(embed)) pending.Enqueue((embed, page, depth + 1));
                }
            }
            catch (Exception)
            {
            }
        }

        return [];
    }

    public static IReadOnlyList<ExternalCaption> SidecarCaptionsIn(string? html)
    {
        var list = new List<ExternalCaption>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return list;
        }

        foreach (Match match in Regex.Matches(
                     html,
                     @"\[([^\]]+)\]\s*(https?://[^\s,""'\]<>]+)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            AddSidecar(list, match.Groups[1].Value, match.Groups[2].Value.TrimEnd('.', ';'));
        }

        foreach (Match match in Regex.Matches(
                     html,
                     @"""file""\s*:\s*""(https?:[^""]+\.(?:vtt|srt|ass|ssa|ttml)[^""]*)""[^\]\}]{0,240}?""label""\s*:\s*""([^""]+)""",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            AddSidecar(list, match.Groups[2].Value, match.Groups[1].Value);
        }

        foreach (Match match in Regex.Matches(
                     html,
                     @"""label""\s*:\s*""([^""]+)""[^\]\}]{0,240}?""file""\s*:\s*""(https?:[^""]+\.(?:vtt|srt|ass|ssa|ttml)[^""]*)""",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            AddSidecar(list, match.Groups[1].Value, match.Groups[2].Value);
        }

        foreach (Match match in Regex.Matches(
                     html,
                     @"""?file""?\s*:\s*""(https?:[^""]+\.(?:vtt|srt|ass|ssa|ttml)[^""]*)""",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            AddSidecar(list, "", match.Groups[1].Value);
        }

        return list;
    }

    private static void AddSidecar(List<ExternalCaption> list, string name, string url)
    {
        url = url.Replace("\\/", "/", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(url) ||
            !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(url, @"chapter|storyboard|thumb|seeker|filmstrip|sprite|preview|timeline", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return;
        }

        if (!Regex.IsMatch(url, @"\.(vtt|srt|ass|ssa|ttml|dfxp)(?:$|\?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
            !Regex.IsMatch(url, @"subtitle|caption|timedtext|/(?:subs?|subtitles?)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
            string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (Regex.IsMatch(name + " " + url, @"forced|zorunlu", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return;
        }

        var language = MediaLanguage.Normalize(name);
        if (language.Length == 0 || MediaLanguage.IsOriginal(language))
        {
            language = MediaLanguage.FromName(name);
        }

        if (list.Any(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        list.Add(new ExternalCaption(
            language,
            url,
            string.IsNullOrWhiteSpace(name) ? (language.Length == 0 ? "Subtitle" : language) : name.Trim()));
    }

    internal static IReadOnlyList<string> PlayerEmbedsIn(string? html, string pageUrl)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(html) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page))
        {
            return list;
        }

        foreach (Match tag in Regex.Matches(html, @"<iframe\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var src = Regex.Match(
                tag.Value,
                @"(?:src|data-src)\s*=\s*[""']([^""']+)[""']",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!src.Success)
            {
                continue;
            }

            var href = src.Groups[1].Value.Trim();
            if (href.StartsWith("//", StringComparison.Ordinal))
            {
                href = page.Scheme + ":" + href;
            }
            else if (href.StartsWith('/'))
            {
                href = page.GetLeftPart(UriPartial.Authority) + href;
            }

            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) || LooksAd(uri.AbsoluteUri))
            {
                continue;
            }

            var mark = uri.AbsoluteUri + " " + tag.Value;
            if (!Regex.IsMatch(mark, @"embed|player|video|vod|rapidvid|rapidrame|/e/|watch|\bclose\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            if (uri.Host.Contains("googletagmanager", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Contains("doubleclick", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!list.Contains(uri.AbsoluteUri, StringComparer.Ordinal))
            {
                list.Add(uri.AbsoluteUri);
            }
        }

        foreach (Match hash in Regex.Matches(
                     html,
                     @"https?://p\.playturka\.space/#[A-Za-z0-9]+",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (!list.Contains(hash.Value, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(hash.Value);
            }
        }

        foreach (Match attribute in Regex.Matches(
                     html,
                     @"data-(?:cfg|config|player)\s*=\s*[""']([^""']+)[""']",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var encoded = System.Net.WebUtility.HtmlDecode(attribute.Groups[1].Value).Trim();
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(FromBase64(encoded));
                foreach (Match url in Regex.Matches(decoded, @"https?://[^""'\s<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    var href = url.Value.Replace("\\/", "/", StringComparison.Ordinal);
                    if (!LooksAd(href) &&
                        !LooksImagePlaylistUrl(href) &&
                        !IsDirectMedia(href) &&
                        Uri.TryCreate(href, UriKind.Absolute, out _) &&
                        !list.Contains(href, StringComparer.Ordinal))
                    {
                        list.Add(href);
                    }
                }
            }
            catch (FormatException)
            {
            }
        }

        foreach (Match source in Regex.Matches(
                     html,
                     @"\b(?:file|source|src)\s*:\s*[""'](https?://[^""']+)[""']",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var href = System.Net.WebUtility.HtmlDecode(source.Groups[1].Value)
                .Replace("\\/", "/", StringComparison.Ordinal);
            if (!LooksAd(href) &&
                !LooksImagePlaylistUrl(href) &&
                !IsDirectMedia(href) &&
                Uri.TryCreate(href, UriKind.Absolute, out _) &&
                !list.Contains(href, StringComparer.Ordinal))
            {
                list.Add(href);
            }
        }

        foreach (var frame in SpgFrameUrls(html))
        {
            if (!LooksAd(frame) && !list.Contains(frame, StringComparer.Ordinal))
            {
                list.Add(frame);
            }
        }

        return list;
    }

    internal static IReadOnlyList<string> SpgFrameUrls(string? html)
    {
        var urls = new List<string>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return urls;
        }

        foreach (Match match in Regex.Matches(
                     html,
                     @"\bSPG\.cerceve\(\s*[""'][^""']+[""']\s*,\s*[""'](?<data>[^""']+)[""']\s*,\s*[""'](?<key>[^""']+)[""']",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            try
            {
                var encrypted = FromBase64(JsStringUnescape(match.Groups["data"].Value));
                var key = FromBase64(JsStringUnescape(match.Groups["key"].Value));
                if (key.Length == 0)
                {
                    continue;
                }

                var plain = new byte[encrypted.Length];
                for (var i = 0; i < encrypted.Length; i++)
                {
                    plain[i] = (byte)(encrypted[i] ^ key[i % key.Length]);
                }

                var frame = Encoding.UTF8.GetString(plain).Split('|')[0];
                if (Uri.TryCreate(frame, UriKind.Absolute, out var uri) &&
                    uri.Scheme is "http" or "https" &&
                    !urls.Contains(uri.AbsoluteUri, StringComparer.Ordinal))
                {
                    urls.Add(uri.AbsoluteUri);
                }
            }
            catch (FormatException)
            {
            }
        }

        return urls;
    }

    internal static (string Url, string Secret, long Timestamp)? ProtectedManifestIn(string? html, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(html) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page))
        {
            return null;
        }

        var guard = Regex.Match(
            html,
            @"\bwindow\.SPG_A\s*=\s*\{(?<body>[^}]+)\}",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var stream = Regex.Match(
            html,
            @"\bstream\s*:\s*[""']([^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!guard.Success || !stream.Success)
        {
            return null;
        }

        static string GuardField(string body, string name)
        {
            var value = Regex.Match(
                body,
                @"[""']?" + Regex.Escape(name) + @"[""']?\s*:\s*[""']([^""']*)[""']",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return value.Success ? JsStringUnescape(value.Groups[1].Value) : "";
        }

        var body = guard.Groups["body"].Value;
        var secret = GuardField(body, "sp");
        var time = Regex.Match(
            body,
            @"[""']?spT[""']?\s*:\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups[1].Value;
        if (string.IsNullOrWhiteSpace(secret) || !long.TryParse(time, out var timestamp))
        {
            return null;
        }

        var href = JsStringUnescape(stream.Groups[1].Value);
        if (!Uri.TryCreate(href, UriKind.Absolute, out var manifest))
        {
            manifest = new Uri(page, href);
        }

        return (manifest.AbsoluteUri, secret, timestamp);
    }

    internal static string BuildSpProof(string secret, long timestamp, string random)
    {
        var input = secret + "|" + timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + random;
        var hash = 2166136261u;
        foreach (var symbol in input)
        {
            hash ^= symbol;
            hash = unchecked(hash * 16777619u);
        }

        return timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + random + "." + hash.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static (string Endpoint, string Nonce, string PostId, IReadOnlyList<string> Players)? WordPressAjaxPlayerFields(
        string? html,
        string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(html) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page))
        {
            return null;
        }

        var ajax = Regex.Match(
            html,
            @"\bvideoAjax\s*=\s*\{(?<body>[^}]+)\}",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!ajax.Success ||
            !Regex.IsMatch(html, @"\baction\s*:\s*[""']get_video_url[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return null;
        }

        static string Field(string body, string name)
        {
            var match = Regex.Match(
                body,
                @"\b" + Regex.Escape(name) + @"\s*:\s*[""']([^""']+)[""']",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? JsStringUnescape(match.Groups[1].Value) : "";
        }

        var endpoint = Field(ajax.Groups["body"].Value, "ajaxurl");
        var nonce = Field(ajax.Groups["body"].Value, "nonce");
        var post = Regex.Match(
            html,
            @"\bdata-post-id\s*=\s*[""']([^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups[1].Value;
        var players = Regex.Matches(
                html,
                @"\bdata-player-name\s*=\s*[""']([^""']+)[""']",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(nonce) ||
            string.IsNullOrWhiteSpace(post) || players.Length == 0)
        {
            return null;
        }

        if (endpoint.StartsWith('/'))
        {
            endpoint = page.GetLeftPart(UriPartial.Authority) + endpoint;
        }

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            ? (endpointUri.AbsoluteUri, nonce, post, players)
            : null;
    }

    internal static IReadOnlyList<string> AjaxPlayerEmbeds(string html, string pageUrl)
    {
        var fields = WordPressAjaxPlayerFields(html, pageUrl);
        if (fields is null)
        {
            return [];
        }

        var embeds = new List<string>();
        foreach (var player in fields.Value.Players.Take(8))
        {
            var json = PostForm(fields.Value.Endpoint, new Dictionary<string, string>
            {
                ["action"] = "get_video_url",
                ["nonce"] = fields.Value.Nonce,
                ["post_id"] = fields.Value.PostId,
                ["player_name"] = player,
                ["part_key"] = ""
            }, pageUrl);
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("url", out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    Uri.TryCreate(value.GetString(), UriKind.Absolute, out var embed) &&
                    !LooksAd(embed.AbsoluteUri))
                {
                    embeds.Add(embed.AbsoluteUri);
                }
            }
            catch (JsonException)
            {
            }
        }

        return embeds;
    }

    internal static bool LooksMediaManifest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.AsSpan().TrimStart();
        return trimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ||
               (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("<MPD", StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<string> PackedPlayerUrls(string? html)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return list;
        }

        foreach (Match call in Regex.Matches(
                     html,
                     @"dc_([A-Za-z0-9]+)\(\s*\[((?:""[^""]*""\s*,?\s*)+)\]\s*\)",
                     RegexOptions.CultureInvariant))
        {
            var parts = new List<string>();
            foreach (Match part in Regex.Matches(call.Groups[2].Value, @"""([^""]*)"""))
            {
                parts.Add(UnescapePackedPart(part.Groups[1].Value));
            }

            var body = PackedDecoderBody(html, "dc_" + call.Groups[1].Value);
            var decoded = DecodePackedWithBody(parts, body) ?? DecodePackedPlayerUrl(parts);
            if (!string.IsNullOrWhiteSpace(decoded) &&
                decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !list.Contains(decoded, StringComparer.Ordinal))
            {
                list.Add(decoded);
            }
        }

        foreach (Match call in Regex.Matches(
                     html,
                     @"\bav\(\s*[""']([^""']+)[""']\s*\)",
                     RegexOptions.CultureInvariant))
        {
            var decoded = DecodeRapidPlayerUrl(call.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(decoded) &&
                decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !list.Contains(decoded, StringComparer.Ordinal))
            {
                list.Add(decoded);
            }
        }

        foreach (var unpacked in UnpackDeanEdwards(html))
        {
            foreach (var url in MediaUrlsIn(unpacked))
            {
                if (!list.Contains(url, StringComparer.Ordinal))
                {
                    list.Add(url);
                }
            }
        }

        return list;
    }

    internal static string? DecodeRapidPlayerUrl(string encoded)
    {
        try
        {
            var bytes = FromBase64(ReverseAscii(encoded));
            var inner = new byte[bytes.Length];
            ReadOnlySpan<byte> shift = [1, 3, 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                inner[i] = (byte)(bytes[i] - shift[i % shift.Length]);
            }

            return System.Text.Encoding.UTF8.GetString(FromBase64(Latin1(inner)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<string> UnpackDeanEdwards(string? html)
    {
        var output = new List<string>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return output;
        }

        foreach (Match match in Regex.Matches(
                     html,
                     @"eval\(function\(p,a,c,k,e,d\).*?\}\(\s*'((?:\\.|[^'])*)'\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*'((?:\\.|[^'])*)'\.split\('\|'\)",
                     RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            if (!int.TryParse(match.Groups[2].Value, out var radix) ||
                !int.TryParse(match.Groups[3].Value, out var count) ||
                radix is < 2 or > 62)
            {
                continue;
            }

            var payload = JsStringUnescape(match.Groups[1].Value);
            var symbols = JsStringUnescape(match.Groups[4].Value).Split('|');
            for (var i = Math.Min(count, symbols.Length) - 1; i >= 0; i--)
            {
                if (symbols[i].Length == 0)
                {
                    continue;
                }

                payload = Regex.Replace(
                    payload,
                    @"\b" + Regex.Escape(ToRadix(i, radix)) + @"\b",
                    _ => symbols[i],
                    RegexOptions.CultureInvariant);
            }

            output.Add(payload);
        }

        return output;
    }

    private static string ToRadix(int value, int radix)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        if (value == 0)
        {
            return "0";
        }

        var result = "";
        while (value > 0)
        {
            result = digits[value % radix] + result;
            value /= radix;
        }

        return result;
    }

    private static string JsStringUnescape(string value)
    {
        return Regex.Replace(value, @"\\(?:u([0-9a-fA-F]{4})|x([0-9a-fA-F]{2})|(.))", match =>
        {
            if (match.Groups[1].Success)
            {
                return ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString();
            }

            if (match.Groups[2].Success)
            {
                return ((char)Convert.ToInt32(match.Groups[2].Value, 16)).ToString();
            }

            return match.Groups[3].Value switch
            {
                "n" => "\n",
                "r" => "\r",
                "t" => "\t",
                "b" => "\b",
                "f" => "\f",
                var escaped => escaped
            };
        }, RegexOptions.CultureInvariant);
    }

    internal static string? DecodePackedPlayerUrl(IReadOnlyList<string> parts)
    {
        if (parts is null || parts.Count == 0)
        {
            return null;
        }

        var joined = string.Concat(parts.Select(UnescapePackedPart));
        foreach (var candidate in new[] { DecodePackedCurrent(joined), DecodePackedLegacy(joined) })
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string UnescapePackedPart(string part) =>
        part.Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
            .Replace("\\u002f", "/", StringComparison.Ordinal);

    private static string? PackedDecoderBody(string html, string name)
    {
        var header = Regex.Match(
            html,
            @"(?:function\s+)?" + Regex.Escape(name) + @"\s*\(\s*value_parts\s*\)\s*\{",
            RegexOptions.CultureInvariant);
        if (!header.Success)
        {
            return null;
        }

        var close = html.IndexOf("return unmix;", header.Index, StringComparison.Ordinal);
        if (close < 0)
        {
            return null;
        }

        return html[header.Index..close];
    }

    internal static string? DecodePackedWithBody(IReadOnlyList<string> parts, string? body)
    {
        if (parts is null || parts.Count == 0 || string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var result = string.Concat(parts.Select(UnescapePackedPart));
            var steps = new List<(int Index, int End, string Kind, int Arg)>();
            foreach (Match item in Regex.Matches(body, @"atob\s*\(\s*(?:result|value)[^;]*\)", RegexOptions.CultureInvariant))
            {
                steps.Add((item.Index, item.Index + item.Length, "atob", 0));
            }

            foreach (Match item in Regex.Matches(body, @"split\(\s*['""]['""]\s*\)\.reverse", RegexOptions.CultureInvariant))
            {
                steps.Add((item.Index, item.Index + item.Length, "reverse", 0));
            }

            foreach (Match item in Regex.Matches(body, @"base\s*\+\s*(\d+)", RegexOptions.CultureInvariant))
            {
                if (int.TryParse(item.Groups[1].Value, out var shift))
                {
                    steps.Add((item.Index, item.Index + item.Length, "rot", shift));
                }
            }

            steps.Sort((left, right) =>
            {
                if (left.Index < right.Index && right.Index < left.End)
                {
                    return 1;
                }

                if (right.Index < left.Index && left.Index < right.End)
                {
                    return -1;
                }

                return left.Index.CompareTo(right.Index);
            });
            foreach (var step in steps)
            {
                result = step.Kind switch
                {
                    "reverse" => ReverseAscii(result),
                    "atob" => Latin1(FromBase64(result)),
                    "rot" => RotLetters(result, step.Arg),
                    _ => result
                };
            }

            var accMatch = Regex.Match(body, @"var\s+acc\s*=\s*(\d+)", RegexOptions.CultureInvariant);
            var addMatch = Regex.Match(body, @"acc\s*=\s*\(acc\s*\+\s*(\d+)\)", RegexOptions.CultureInvariant);
            if (!accMatch.Success || !addMatch.Success ||
                !int.TryParse(accMatch.Groups[1].Value, out var acc) ||
                !int.TryParse(addMatch.Groups[1].Value, out var add))
            {
                return null;
            }

            var bytes = System.Text.Encoding.Latin1.GetBytes(result);
            var plain = new byte[bytes.Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                var value = bytes[i];
                acc = (acc + add) % 256;
                plain[i] = (byte)(value ^ acc);
                acc = (acc + value) % 256;
            }

            var decoded = System.Text.Encoding.UTF8.GetString(plain);
            return decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? decoded : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? DecodePackedCurrent(string joined)
    {
        try
        {
            var result = Latin1(FromBase64(joined));
            result = RotLetters(result, 16);
            result = Latin1(FromBase64(result));
            result = Latin1(FromBase64(result));
            result = Latin1(FromBase64(result));
            result = Latin1(FromBase64(result));
            var bytes = System.Text.Encoding.Latin1.GetBytes(result);
            var acc = 105;
            var plain = new byte[bytes.Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                var value = bytes[i];
                acc = (acc + 11) % 256;
                plain[i] = (byte)(value ^ acc);
                acc = (acc + value) % 256;
            }

            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? DecodePackedLegacy(string joined)
    {
        try
        {
            var rotated = RotLetters(ReverseAscii(joined), 15);
            var once = Latin1(FromBase64(rotated));
            var twice = Latin1(FromBase64(once));
            var bytes = FromBase64(ReverseAscii(twice));
            var acc = 14;
            var plain = new byte[bytes.Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                var value = bytes[i];
                acc = (acc + 16) % 256;
                plain[i] = (byte)(value ^ acc);
                acc = (acc + value) % 256;
            }

            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string ReverseAscii(string text)
    {
        var chars = text.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private static string RotLetters(string text, int shift)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var symbol = chars[i];
            if (symbol is >= 'A' and <= 'Z')
            {
                chars[i] = (char)('A' + (symbol - 'A' + shift) % 26);
            }
            else if (symbol is >= 'a' and <= 'z')
            {
                chars[i] = (char)('a' + (symbol - 'a' + shift) % 26);
            }
        }

        return new string(chars);
    }

    private static byte[] FromBase64(string text)
    {
        var padded = text.Trim();
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        };
        return Convert.FromBase64String(padded);
    }

    private static string Latin1(byte[] bytes) => System.Text.Encoding.Latin1.GetString(bytes);

    public static string? PickMediaUrl(IEnumerable<string>? urls)
    {
        string? best = null;
        var score = int.MinValue;
        foreach (var url in urls ?? [])
        {
            var next = MediaScore(url);
            if (next > score)
            {
                score = next;
                best = url;
            }
        }

        return score < 0 ? null : best;
    }

    public static string? SiblingPlaylistUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Regex.IsMatch(url, @"/txt/master\.txt", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return null;
        }

        return Regex.Replace(url, @"/txt/master\.txt", "/master.txt", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static bool LooksDecoyManifest(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.Contains("playmix.uno", StringComparison.OrdinalIgnoreCase);

    internal static bool LooksPackedHls(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        !LooksDecoyManifest(url) &&
        !LooksImagePlaylistUrl(url) &&
        url.Contains("/hls/", StringComparison.OrdinalIgnoreCase) &&
        url.Contains("master.txt", StringComparison.OrdinalIgnoreCase);

    public static bool LooksImagePlaylistUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        (Regex.IsMatch(url, @"/image\d+\.(jpg|jpeg|png|webp)|/txt/master\.txt", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
         (Regex.IsMatch(url, @"\.(jpg|jpeg|png|webp|gif|heic)(?:$|\?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
          !Regex.IsMatch(url, @"\.(mp4|m3u8|webm|mov)(?:$|\?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) ||
         Regex.IsMatch(url, @"(?:scontent|cdninstagram|fbcdn\.net).*(?:/t51\.|/t53\.|/p\d+x\d+/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    public static bool IsImagePlaylist(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        Regex.IsMatch(text, @"image\d+\.(jpg|jpeg|png|webp)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int MediaScore(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            LooksImagePlaylistUrl(url))
        {
            return -10000;
        }

        var text = url.ToLowerInvariant();
        var score = 0;
        if (text.Contains(".m3u8", StringComparison.Ordinal) ||
            text.Contains(".m3u?", StringComparison.Ordinal) ||
            text.Contains("master.txt", StringComparison.Ordinal) ||
            text.Contains("playlist.txt", StringComparison.Ordinal) ||
            text.Contains("/hls/", StringComparison.Ordinal))
        {
            score += 4000;
        }
        else if (text.Contains(".mpd", StringComparison.Ordinal))
        {
            score += 3000;
        }
        else if (text.Contains(".mp4", StringComparison.Ordinal))
        {
            score += 2000;
        }
        else if (text.Contains(".mkv", StringComparison.Ordinal) ||
                 text.Contains(".webm", StringComparison.Ordinal) ||
                 text.Contains(".mov", StringComparison.Ordinal))
        {
            score += 1500;
        }

        if (LooksDecoyManifest(text))
        {
            score -= 5000;
        }

        if (LooksAd(text) ||
            text.Contains("timeline", StringComparison.Ordinal) ||
            text.Contains("preview", StringComparison.Ordinal) ||
            text.Contains("thumb", StringComparison.Ordinal) ||
            text.Contains("storyboard", StringComparison.Ordinal) ||
            text.Contains("sprite", StringComparison.Ordinal) ||
            text.Contains("/assets/", StringComparison.Ordinal) ||
            text.Contains("/dist/", StringComparison.Ordinal) ||
            text.Contains("site.webm", StringComparison.Ordinal))
        {
            score -= 5000;
        }

        if (text.Contains("bytestart", StringComparison.Ordinal) ||
            text.Contains("byteend", StringComparison.Ordinal) ||
            text.Contains("dashinit", StringComparison.Ordinal) ||
            text.Contains("frag_", StringComparison.Ordinal) ||
            text.Contains("fragment", StringComparison.Ordinal) ||
            text.Contains("init.mp4", StringComparison.Ordinal))
        {
            score -= 4000;
        }

        if (text.Contains("1080", StringComparison.Ordinal) || text.Contains("1920", StringComparison.Ordinal))
        {
            score += 1080;
        }
        else if (text.Contains("720", StringComparison.Ordinal) || text.Contains("1280", StringComparison.Ordinal))
        {
            score += 720;
        }
        else if (text.Contains("480", StringComparison.Ordinal))
        {
            score += 480;
        }
        else if (text.Contains("360", StringComparison.Ordinal))
        {
            score += 360;
        }

        return score;
    }

    public static bool LooksAd(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Regex.IsMatch(
            url,
            @"doubleclick|googlesyndication|imasdk|adsystem|/ads?/|preroll|vast|spotx|pubads|adnxs|advert|promo|/rekla/|reklam|xpartner|dmxleo|marmorated\.pics|shrgo\.net|clips\.kick|/clips?/|bumper|site\.webm",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static YouTubePlayable AttachVodCaptions(YouTubePlayable playable, string? language)
    {
        if (playable.Kind == StreamKind.Live ||
            !string.IsNullOrWhiteSpace(playable.CaptionUrl) ||
            MediaLanguage.IsOff(language) ||
            string.IsNullOrWhiteSpace(MediaLanguage.Normalize(language)))
        {
            return playable;
        }

        var caption = HlsCaptions.TryLoad(playable.MediaUrl, language, playable.UserAgent, playable.VideoId);
        return string.IsNullOrWhiteSpace(caption) ? playable : playable.WithCaption(caption);
    }

    private static string? TwitchCaptionUrl(string id, string? language)
    {
        const string query =
            "query($id: ID!) { video(id: $id) { captions { languageCode localizedName url } } }";
        var body = "{\"query\":" + JsonSerializer.Serialize(query) +
                   ",\"variables\":{\"id\":" + JsonSerializer.Serialize(id) + "}}";
        var json = PostJson("https://gql.twitch.tv/gql", body, "https://www.twitch.tv/", TwitchClientId);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("video", out var video) ||
                video.ValueKind != JsonValueKind.Object ||
                !video.TryGetProperty("captions", out var captions) ||
                captions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? fallback = null;
            var want = MediaLanguage.Normalize(language);
            foreach (var item in captions.EnumerateArray())
            {
                var url = ReadString(item, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                fallback ??= url;
                var code = ReadString(item, "languageCode") ?? ReadString(item, "localizedName");
                if (want.Length == 0 || MediaLanguage.Matches(language, code))
                {
                    return url;
                }
            }

            return string.IsNullOrWhiteSpace(language) ? fallback : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (string Token, string Signature)? TwitchAccessToken(bool isLive, string id)
    {
        const string query =
            "query PlaybackAccessToken_Template($login: String!, $isLive: Boolean!, $vodID: ID!, $isVod: Boolean!, $playerType: String!) { streamPlaybackAccessToken(channelName: $login, params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: $playerType}) @include(if: $isLive) { value signature } videoPlaybackAccessToken(id: $vodID, params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: $playerType}) @include(if: $isVod) { value signature } }";
        var body =
            "{\"operationName\":\"PlaybackAccessToken_Template\",\"query\":" +
            JsonSerializer.Serialize(query) +
            ",\"variables\":{\"isLive\":" + (isLive ? "true" : "false") +
            ",\"login\":" + JsonSerializer.Serialize(isLive ? id : "") +
            ",\"isVod\":" + (isLive ? "false" : "true") +
            ",\"vodID\":" + JsonSerializer.Serialize(isLive ? "" : id) +
            ",\"playerType\":\"embed\"}}";
        var json = PostJson("https://gql.twitch.tv/gql", body, "https://www.twitch.tv/", TwitchClientId);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data))
            {
                return null;
            }

            var node = isLive
                ? data.TryGetProperty("streamPlaybackAccessToken", out var live) ? live : default
                : data.TryGetProperty("videoPlaybackAccessToken", out var vod) ? vod : default;
            if (node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var token = node.TryGetProperty("value", out var value) ? value.GetString() : null;
            var signature = node.TryGetProperty("signature", out var sig) ? sig.GetString() : null;
            return string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(signature)
                ? null
                : (token, signature);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? GetJson(string url, string referer)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            request.Headers.TryAddWithoutValidation("Referer", referer);
            var origin = PageOrigin(referer)?.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(origin))
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }
            request.Headers.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
            request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            using var response = Http.Send(request);
            return response.IsSuccessStatusCode
                ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? FetchHtml(string url, string? referer) =>
        GetText(url, ChromeUa, referer ?? url) ?? CurlText(url, referer ?? url);

    internal static string? GetText(string url, string? userAgent, string? referer)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent ?? ChromeUa);
            if (!string.IsNullOrWhiteSpace(referer))
            {
                request.Headers.TryAddWithoutValidation("Referer", referer);
                var origin = PageOrigin(referer)?.TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(origin))
                {
                    request.Headers.TryAddWithoutValidation("Origin", origin);
                }
            }

            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            using var response = Http.Send(request);
            return response.IsSuccessStatusCode
                ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? CurlText(string url, string? referer)
    {
        var bytes = CurlBytes(url, referer);
        return bytes is { Length: > 0 } ? Encoding.UTF8.GetString(bytes) : null;
    }

    internal static byte[]? CurlBytes(string url, string? referer, string? userAgent = null, long? rangeStart = null, int? rangeLength = null)
    {
        var dest = Path.Combine(Path.GetTempPath(), "grok-curl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var args = "-sS -L --max-time 20 -A \"" + (userAgent ?? ChromeUa).Replace("\"", "", StringComparison.Ordinal) + "\"";
            if (!string.IsNullOrWhiteSpace(referer))
            {
                var page = referer.Replace("\"", "", StringComparison.Ordinal);
                args += " -H \"Referer: " + page + "\"";
                var origin = PageOrigin(page)?.TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(origin))
                {
                    args += " -H \"Origin: " + origin + "\"";
                }
            }

            args += " -H \"Accept: */*\"";
            if (rangeStart is { } start && rangeLength is { } length)
            {
                args += " -r " + start + "-" + (start + length - 1);
            }

            args += " -o \"" + dest + "\" \"" + url + "\"";
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "curl.exe",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            if (!process.WaitForExit(22000))
            {
                try { process.Kill(true); } catch (Exception) { }
                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(dest))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(dest);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch (Exception) { }
        }
    }

    private static string? PostJson(string url, string body, string referer, string? clientId, bool webPlatform = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            request.Headers.TryAddWithoutValidation("Referer", referer);
            var origin = PageOrigin(referer)?.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(origin))
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                request.Headers.TryAddWithoutValidation("Client-Id", clientId);
            }
            if (webPlatform)
            {
                request.Headers.TryAddWithoutValidation("x-app-platform", "web");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
            }

            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = Http.Send(request);
            return response.IsSuccessStatusCode
                ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? PostForm(string url, IReadOnlyDictionary<string, string> values, string referer)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            request.Headers.TryAddWithoutValidation("Referer", referer);
            request.Headers.TryAddWithoutValidation("Origin", PageOrigin(referer)?.TrimEnd('/'));
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Content = new FormUrlEncodedContent(values);
            using var response = Http.Send(request);
            return response.IsSuccessStatusCode
                ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? RumbleBestUrl(JsonElement root)
    {
        string? best = null;
        var score = int.MinValue;
        void Consider(string? url, int extra)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var next = MediaScore(url) + extra;
            if (next > 0 && next > score)
            {
                score = next;
                best = url;
            }
        }

        if (root.TryGetProperty("u", out var u) && u.ValueKind == JsonValueKind.Object &&
            u.TryGetProperty("hls", out var uhls) && uhls.ValueKind == JsonValueKind.Object)
        {
            Consider(ReadString(uhls, "url"), 500);
        }

        if (!root.TryGetProperty("ua", out var ua) || ua.ValueKind != JsonValueKind.Object)
        {
            return best;
        }

        foreach (var type in ua.EnumerateObject())
        {
            if (type.NameEquals("timeline") || type.NameEquals("tar"))
            {
                continue;
            }

            if (type.NameEquals("hls") && type.Value.ValueKind == JsonValueKind.Object)
            {
                if (type.Value.TryGetProperty("auto", out var auto))
                {
                    Consider(ReadString(auto, "url"), 800);
                }

                foreach (var item in type.Value.EnumerateObject())
                {
                    Consider(ReadString(item.Value, "url"), 600);
                }

                continue;
            }

            if (type.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var item in type.Value.EnumerateObject())
            {
                var extra = int.TryParse(item.Name, out var height) ? height : 0;
                Consider(ReadString(item.Value, "url"), extra);
            }
        }

        return best;
    }

    private static string? RumbleCaptionUrl(JsonElement root, string? language)
    {
        if (!root.TryGetProperty("cc", out var cc) || cc.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? fallback = null;
        var want = MediaLanguage.Normalize(language);
        foreach (var item in cc.EnumerateArray())
        {
            var url = ReadString(item, "url") ?? ReadString(item, "path") ?? ReadString(item, "file");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            fallback ??= url;
            var code = ReadString(item, "language") ?? ReadString(item, "lang");
            if (want.Length == 0 || MediaLanguage.Matches(language, code))
            {
                return url;
            }
        }

        return string.IsNullOrWhiteSpace(language) ? fallback : null;
    }

    private static string? TikTokPlayUrl(string? html, string id)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        foreach (var marker in new[] { "\"playAddr\"", "\"downloadAddr\"", "\"play_addr\"", "playAddr" })
        {
            var at = html.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var slice = html[at..Math.Min(html.Length, at + 2500)];
            var match = Regex.Match(slice, @"https?:\\?/\\?/[^""\\]+", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Value.Replace("\\u002F", "/", StringComparison.Ordinal)
                    .Replace("\\/", "/", StringComparison.Ordinal);
            }
        }

        var fromPage = PickMediaUrl(MediaUrlsIn(html));
        return string.IsNullOrWhiteSpace(fromPage) ? null : fromPage;
    }

    public static bool TryReadPlayturka(string? url, out string id)
    {
        id = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("playturka", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hash = uri.Fragment.TrimStart('#');
        if (hash.Length >= 4 && hash.All(char.IsLetterOrDigit))
        {
            id = hash;
            return true;
        }

        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts[0].Equals("id", StringComparison.OrdinalIgnoreCase) &&
                parts.Length > 1 &&
                parts[1].Length >= 4)
            {
                id = Uri.UnescapeDataString(parts[1]);
                return true;
            }
        }

        return false;
    }

    internal static YouTubePlayable? ResolvePlayturka(string id, string pageUrl, string? audioLang, string? subLang)
    {
        var payload = PlayturkaPayload(id);
        if (payload is null)
        {
            return null;
        }

        var media = payload.Value.Media;
        if (string.IsNullOrWhiteSpace(media))
        {
            return null;
        }

        var referer = "https://p.playturka.space/#" + id;
        var playable = new YouTubePlayable(
            "playturka|" + id,
            media,
            UrlSanitizer.DisplayName(pageUrl),
            StreamKind.Vod,
            userAgent: ChromeUa,
            audioLang: audioLang,
            subLang: subLang,
            referer: referer,
            formatHint: "hls");
        var caption = PickPlayturkaCaption(payload.Value.Captions, subLang);
        return string.IsNullOrWhiteSpace(caption) ? playable : playable.WithCaption(caption);
    }

    public static IReadOnlyList<ExternalCaption> PlayturkaCaptions(string? id)
    {
        var payload = PlayturkaPayload(id);
        return payload?.Captions ?? [];
    }

    private static (string Media, IReadOnlyList<ExternalCaption> Captions)? PlayturkaPayload(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var href = "https://p.playturka.space/api/video-url?id=" + Uri.EscapeDataString(id);
        var raw = FetchHtml(href, "https://p.playturka.space/#" + id);
        var json = DecryptPlayturka(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String &&
                !status.GetString()!.Equals("success", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var media = ReadString(files, "masterUrl") ?? ReadString(files, "videoUrl");
            var captions = new List<ExternalCaption>();
            if (files.TryGetProperty("subtitles", out var subs) && subs.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in subs.EnumerateObject())
                {
                    var url = item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() : null;
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    captions.Add(new ExternalCaption(
                        MediaLanguage.FromName(item.Name),
                        url,
                        item.Name));
                }
            }

            return string.IsNullOrWhiteSpace(media) ? null : (media, captions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? PickPlayturkaCaption(IReadOnlyList<ExternalCaption> captions, string? language)
    {
        if (captions.Count == 0)
        {
            return null;
        }

        var want = MediaLanguage.Normalize(language);
        foreach (var cap in captions)
        {
            if (want.Length > 0 &&
                (MediaLanguage.Matches(want, cap.Language) || MediaLanguage.MatchesName(want, cap.Name)))
            {
                return cap.Url;
            }
        }

        foreach (var cap in captions)
        {
            if (MediaLanguage.Matches(cap.Language, "tr") || MediaLanguage.MatchesName("tr", cap.Name))
            {
                return cap.Url;
            }
        }

        return captions[0].Url;
    }

    internal static string? DecryptPlayturka(string? cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher) || cipher.Contains('<', StringComparison.Ordinal))
        {
            return null;
        }

        var mapped = new StringBuilder(cipher.Length);
        foreach (var ch in cipher.Trim())
        {
            mapped.Append(PlayturkaUnmap(ch));
        }

        var text = mapped.ToString();
        var pad = text.Length % 4;
        if (pad > 0)
        {
            text += new string('=', 4 - pad);
        }

        try
        {
            var bytes = Convert.FromBase64String(text);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static char PlayturkaUnmap(char ch) => ch switch
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
        '-' => '+', '_' => '/',
        _ => ch
    };

    internal static IReadOnlyList<ExternalCaption> DailyCaptionTracks(JsonElement root)
    {
        var list = new List<ExternalCaption>();
        if (!root.TryGetProperty("subtitles", out var subs) ||
            subs.ValueKind != JsonValueKind.Object ||
            !subs.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        foreach (var item in data.EnumerateObject())
        {
            var url = DailySubtitleFile(item.Value);
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var label = item.Value.TryGetProperty("label", out var labelEl) ? labelEl.GetString() : null;
            var language = MediaLanguage.Normalize(item.Name, keepKind: true);
            if (language.Length == 0 || MediaLanguage.IsOriginal(language))
            {
                language = MediaLanguage.FromName(label);
            }

            list.Add(new ExternalCaption(
                language,
                url,
                string.IsNullOrWhiteSpace(label) ? (language.Length == 0 ? "Subtitle" : language) : label));
        }

        return list;
    }

    public static IReadOnlyList<ExternalCaption> DailyCaptions(string? videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return [];
        }

        var json = GetJson(
            "https://www.dailymotion.com/player/metadata/video/" + Uri.EscapeDataString(videoId),
            "https://www.dailymotion.com/");
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return DailyCaptionTracks(document.RootElement);
        }
        catch (Exception)
        {
            return [];
        }
    }

    internal static string? DailyCaptionUrl(JsonElement root, string? language)
    {
        var tracks = DailyCaptionTracks(root);
        if (tracks.Count == 0)
        {
            return null;
        }

        var want = MediaLanguage.Normalize(language);
        foreach (var track in tracks)
        {
            if (want.Length == 0 ||
                MediaLanguage.Matches(language, track.Language) ||
                MediaLanguage.MatchesName(language, track.Name))
            {
                return track.Url;
            }
        }

        return string.IsNullOrWhiteSpace(language) ? tracks[0].Url : null;
    }

    private static string? DailySubtitleFile(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty("urls", out var urls) ||
            urls.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in urls.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var href = item.GetString();
            if (string.IsNullOrWhiteSpace(href) ||
                Regex.IsMatch(href, @"chapter|storyboard|thumb|seeker|filmstrip|sprite|preview|timeline", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            return href;
        }

        return null;
    }

    private static string? DailyQualityUrl(JsonElement root)
    {
        if (!root.TryGetProperty("qualities", out var qualities) || qualities.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (qualities.TryGetProperty("auto", out var auto))
        {
            var autoUrl = DailyFirstUrl(auto);
            if (!string.IsNullOrWhiteSpace(autoUrl))
            {
                return autoUrl;
            }
        }

        string? best = null;
        var score = -1;
        foreach (var item in qualities.EnumerateObject())
        {
            var url = DailyFirstUrl(item.Value);
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var next = MediaScore(url);
            if (int.TryParse(item.Name, out var height))
            {
                next += height;
            }

            if (next > score)
            {
                score = next;
                best = url;
            }
        }

        return best;
    }

    private static string? DailyFirstUrl(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            return ReadString(node, "url") ??
                   (node.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.Array && url.GetArrayLength() > 0
                       ? url[0].GetString()
                       : null);
        }

        if (node.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in node.EnumerateArray())
        {
            var url = ReadString(item, "url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }

    private static string? HtmlTitle(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var title = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
        return LooksLikeErrorTitle(title) ? null : title;
    }

    public static string UsableTitle(string? title, string? sourceUrl)
    {
        if (!LooksLikeErrorTitle(title))
        {
            return title!.Trim();
        }

        var fromUrl = UrlSanitizer.DisplayName(sourceUrl ?? "");
        return string.IsNullOrWhiteSpace(fromUrl) ? "download" : fromUrl;
    }

    internal static bool LooksLikeErrorTitle(string? title)
    {
        var text = (title ?? "").Trim();
        if (text.Length == 0)
        {
            return true;
        }

        return Regex.IsMatch(text, @"^\d{3}(\b|$)", RegexOptions.CultureInvariant) ||
               text.Equals("Not Found", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Forbidden", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Error", StringComparison.OrdinalIgnoreCase);
    }

    public static string? PageOrigin(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority) + "/"
            : null;

    public static string SiteReferer(string? mediaUrl, string? pageUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(pageUrl) && UrlSanitizer.IsUrl(pageUrl) && !IsDirectMedia(pageUrl))
        {
            return pageUrl;
        }

        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var uri))
        {
            return pageUrl ?? "";
        }

        var host = uri.Host;
        if (host.Contains("kick.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("live-video.net", StringComparison.OrdinalIgnoreCase))
        {
            return "https://kick.com/";
        }

        if (host.Contains("twitch", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("ttvnw.net", StringComparison.OrdinalIgnoreCase))
        {
            return "https://www.twitch.tv/";
        }

        if (host.Contains("rumble", StringComparison.OrdinalIgnoreCase))
        {
            return "https://rumble.com/";
        }

        if (host.Contains("tiktok", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("byteoversea", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("ibyteimg", StringComparison.OrdinalIgnoreCase))
        {
            return "https://www.tiktok.com/";
        }

        if (host.Contains("dailymotion", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("dmcdn", StringComparison.OrdinalIgnoreCase))
        {
            return "https://www.dailymotion.com/";
        }

        if (host.Contains("instagram", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("cdninstagram", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("fbcdn.net", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("scontent", StringComparison.OrdinalIgnoreCase))
        {
            return "https://www.instagram.com/";
        }

        return uri.GetLeftPart(UriPartial.Authority) + "/";
    }

    private static bool LooksUuid(string value)
    {
        return Guid.TryParse(value, out _);
    }

    private static bool ReadBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string ReadNumber(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.ToString()
            : "";

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadNested(JsonElement element, string parent, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(parent, out var child)
            ? ReadString(child, name)
            : null;
}
