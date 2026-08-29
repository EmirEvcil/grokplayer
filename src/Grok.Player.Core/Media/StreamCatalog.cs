using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grok.Player.Core.Download;
using Grok.Player.Core.Subtitles;

namespace Grok.Player.Core.Media;

public static class StreamCatalog
{
    public const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string TwitchClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    { Timeout = TimeSpan.FromSeconds(10) };

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

        if (IsDirectMedia(url))
        {
            return ResolveDirect(url, audioLang, subLang);
        }

        return ResolvePage(url, audioLang, subLang);
    }

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

        var fromPage = KickFromPage(pageUrl, id, audioLang, subLang);
        return fromPage;
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

    internal static YouTubePlayable ResolveDirect(string url, string? audioLang, string? subLang)
    {
        var ext = StreamProbe.Extension(url);
        var kind = ext is ".m3u8" or ".m3u" or ".mpd" ? StreamKind.Unknown : StreamKind.Vod;
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
            var embedId = RumbleEmbedId(pageUrl);
            if (!string.IsNullOrWhiteSpace(embedId) &&
                !embedId.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                json = GetJson(
                    "https://rumble.com/embedJS/u3/?request=video&ver=2&v=" + Uri.EscapeDataString(embedId),
                    pageUrl);
            }

            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{'))
            {
                return null;
            }

            id = string.IsNullOrWhiteSpace(embedId) ? id : embedId;
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
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var html = ReadString(document.RootElement, "html");
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
        catch (Exception)
        {
            return null;
        }
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
                referer: "https://www.dailymotion.com/");
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

        var html = GetText(url, ChromeUa, referer ?? url);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var media = PickMediaUrl(MediaUrlsIn(html));
        var strong = !string.IsNullOrWhiteSpace(media) && MediaScore(media) >= 3000;
        if (!strong && depth < 2)
        {
            foreach (var embed in PlayerEmbedsIn(html, url))
            {
                var nested = ResolvePage(embed, audioLang, subLang, depth + 1, url);
                if (nested is not null)
                {
                    return nested;
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
            referer: url);
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
                LooksImagePlaylistUrl(clean) ||
                list.Contains(clean, StringComparer.Ordinal))
            {
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
            if (!Regex.IsMatch(mark, @"embed|player|video|rapidrame|/e/|watch", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
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

        return list;
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
                     @"dc_[A-Za-z0-9]+\(\s*\[((?:""[^""]*""\s*,?\s*)+)\]\s*\)",
                     RegexOptions.CultureInvariant))
        {
            var parts = new List<string>();
            foreach (Match part in Regex.Matches(call.Groups[1].Value, @"""([^""]*)"""))
            {
                parts.Add(part.Groups[1].Value);
            }

            var decoded = DecodePackedPlayerUrl(parts);
            if (!string.IsNullOrWhiteSpace(decoded) &&
                decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !list.Contains(decoded, StringComparer.Ordinal))
            {
                list.Add(decoded);
            }
        }

        return list;
    }

    internal static string? DecodePackedPlayerUrl(IReadOnlyList<string> parts)
    {
        if (parts is null || parts.Count == 0)
        {
            return null;
        }

        try
        {
            var joined = string.Concat(parts);
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

        return score <= 0 ? null : best;
    }

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
            @"doubleclick|googlesyndication|imasdk|adsystem|/ads?/|preroll|vast|spotx|pubads|adnxs|advert|promo|/rekla/|reklam|xpartner|dmxleo",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static YouTubePlayable AttachVodCaptions(YouTubePlayable playable, string? language)
    {
        if (playable.Kind == StreamKind.Live ||
            !string.IsNullOrWhiteSpace(playable.CaptionUrl) ||
            MediaLanguage.IsOff(language))
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
            request.Headers.TryAddWithoutValidation("Origin", referer.TrimEnd('/'));
            request.Headers.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
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

    internal static string? GetText(string url, string? userAgent, string? referer)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent ?? ChromeUa);
            if (!string.IsNullOrWhiteSpace(referer))
            {
                request.Headers.TryAddWithoutValidation("Referer", referer);
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

    private static string? PostJson(string url, string body, string referer, string? clientId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            request.Headers.TryAddWithoutValidation("Referer", referer);
            request.Headers.TryAddWithoutValidation("Origin", referer.TrimEnd('/'));
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                request.Headers.TryAddWithoutValidation("Client-Id", clientId);
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
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
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
