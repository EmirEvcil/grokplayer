using System.Text.Json;
using System.Text.RegularExpressions;
using Grok.Player.Core.Launch;

namespace Grok.Player.Core.Media;

public sealed class YouTubePlayable
{
    public YouTubePlayable(
        string videoId,
        string mediaUrl,
        string title,
        StreamKind kind,
        string? audioUrl = null,
        string? userAgent = null,
        string? audioLang = null,
        string? subLang = null,
        string? captionUrl = null,
        bool hlsSubtitles = false,
        string? storyboardSpec = null,
        string? referer = null,
        string? formatHint = null,
        string? httpHeaders = null)
    {
        VideoId = videoId;
        MediaUrl = mediaUrl;
        Title = title;
        Kind = kind;
        AudioUrl = audioUrl;
        UserAgent = userAgent;
        AudioLang = audioLang;
        SubLang = subLang;
        CaptionUrl = captionUrl;
        HlsSubtitles = hlsSubtitles;
        StoryboardSpec = storyboardSpec;
        Referer = referer;
        FormatHint = formatHint;
        HttpHeaders = httpHeaders;
    }

    public string VideoId { get; }
    public string MediaUrl { get; }
    public string Title { get; }
    public StreamKind Kind { get; }
    public string? AudioUrl { get; }
    public string? UserAgent { get; }
    public string? AudioLang { get; }
    public string? SubLang { get; }
    public string? CaptionUrl { get; }
    public bool HlsSubtitles { get; }

    public string? StoryboardSpec { get; }

    public string? Referer { get; }

    public string? FormatHint { get; }

    public string? HttpHeaders { get; }

    public YouTubePlayable WithUserAgent(string? userAgent) =>
        Copy(userAgent: userAgent);

    public YouTubePlayable WithLanguages(string? audioLang, string? subLang) =>
        Copy(audioLang: audioLang ?? AudioLang, subLang: subLang ?? SubLang);

    public YouTubePlayable WithCaption(string? captionUrl) =>
        Copy(captionUrl: captionUrl ?? CaptionUrl);

    public YouTubePlayable WithHls(string? audioUrl, bool hlsSubtitles) =>
        Copy(audioUrl: audioUrl ?? AudioUrl, hlsSubtitles: hlsSubtitles);

    public YouTubePlayable WithMedia(string mediaUrl) =>
        Copy(mediaUrl: mediaUrl);

    public YouTubePlayable WithStoryboard(string? spec) =>
        Copy(storyboardSpec: spec ?? StoryboardSpec);

    public YouTubePlayable WithReferer(string? referer) =>
        Copy(referer: referer ?? Referer);

    private YouTubePlayable Copy(
        string? mediaUrl = null,
        string? audioUrl = null,
        string? userAgent = null,
        string? audioLang = null,
        string? subLang = null,
        string? captionUrl = null,
        bool? hlsSubtitles = null,
        string? storyboardSpec = null,
        string? referer = null,
        string? formatHint = null,
        string? httpHeaders = null) =>
        new(
            VideoId,
            mediaUrl ?? MediaUrl,
            Title,
            Kind,
            audioUrl ?? AudioUrl,
            userAgent ?? UserAgent,
            audioLang ?? AudioLang,
            subLang ?? SubLang,
            captionUrl ?? CaptionUrl,
            hlsSubtitles ?? HlsSubtitles,
            storyboardSpec ?? StoryboardSpec,
            referer ?? Referer,
            formatHint ?? FormatHint,
            httpHeaders ?? HttpHeaders);
}

public static class YouTubeCatalog
{
    private static readonly Regex IdPattern = new(
        @"^[A-Za-z0-9_-]{11}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsWatchUrl(string? value)
    {
        return TryReadVideoId(value, out _);
    }

    public static bool TryReadVideoId(string? value, out string videoId)
    {
        videoId = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith("grokplayer:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(text, UriKind.Absolute, out var grok) &&
            !string.IsNullOrWhiteSpace(grok.Query))
        {
            var nested = QueryValue(grok.Query, "url");
            return !string.IsNullOrWhiteSpace(nested) && TryReadVideoId(Uri.UnescapeDataString(nested), out videoId);
        }

        if (IdPattern.IsMatch(text))
        {
            videoId = text;
            return true;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.AbsolutePath.Trim('/');
            if (IdPattern.IsMatch(id))
            {
                videoId = id;
                return true;
            }

            return false;
        }

        if (!host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Contains("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fromQuery = QueryValue(uri.Query, "v");
        if (IdPattern.IsMatch(fromQuery))
        {
            videoId = fromQuery;
            return true;
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            parts[0] is "live" or "embed" or "shorts" or "v" &&
            IdPattern.IsMatch(parts[1]))
        {
            videoId = parts[1];
            return true;
        }

        return false;
    }

    public static YouTubePlayable? ParsePlayerResponse(string json, string? videoId = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("playabilityStatus", out var status) &&
            status.TryGetProperty("status", out var code) &&
            code.GetString() is { } state &&
            state.Equals("ERROR", StringComparison.OrdinalIgnoreCase) &&
            !root.TryGetProperty("streamingData", out _))
        {
            return null;
        }

        var details = root.TryGetProperty("videoDetails", out var video) ? video : default;
        var id = videoId ?? ReadString(details, "videoId") ?? "";
        var title = ReadString(details, "title") ?? (string.IsNullOrWhiteSpace(id) ? "YouTube" : id);
        var live = ReadBool(details, "isLive");

        if (!root.TryGetProperty("streamingData", out var streaming))
        {
            return null;
        }

        var kind = live ? StreamKind.Live : StreamKind.Vod;
        var board = Preview.StoryboardSpec.FromPlayerJson(json);
        if (TryUrl(streaming, "hlsManifestUrl", out var hls))
        {
            return new YouTubePlayable(id, hls, title, kind, storyboardSpec: board);
        }

        if (TryUrl(streaming, "dashManifestUrl", out var dash) && !live)
        {
            return new YouTubePlayable(id, dash, title, StreamKind.Vod, storyboardSpec: board);
        }

        // Live googlevideo DASH/progressive URLs expire immediately and 403
        // without a player n-sig. Only HLS from an Innertube client plays.
        if (live)
        {
            return null;
        }

        if (TryBestFormatUrl(streaming, "formats", out var progressive))
        {
            return new YouTubePlayable(id, progressive, title, StreamKind.Vod, storyboardSpec: board);
        }

        if (TryBestPair(streaming, out var videoUrl, out var audioUrl))
        {
            return new YouTubePlayable(id, videoUrl, title, kind, audioUrl, storyboardSpec: board);
        }

        if (TryBestFormatUrl(streaming, "adaptiveFormats", out var adaptive))
        {
            return new YouTubePlayable(id, adaptive, title, kind, storyboardSpec: board);
        }

        return null;
    }

    public static int? ReadStartSeconds(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var raw = QueryValue(uri.Query, "t");
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = QueryValue(uri.Query, "start");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw.Trim();
        if (int.TryParse(raw.TrimEnd('s', 'S'), out var seconds) && seconds >= 0)
        {
            return seconds;
        }

        var total = 0;
        var matched = false;
        foreach (Match match in Regex.Matches(raw, @"(\d+)([hmsHMS]?)"))
        {
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var value))
            {
                continue;
            }

            matched = true;
            total += match.Groups[2].Value.ToLowerInvariant() switch
            {
                "h" => value * 3600,
                "m" => value * 60,
                _ => value
            };
        }

        return matched ? total : null;
    }

    public static YouTubePlayable BindHlsRenditions(YouTubePlayable playable, int maxHeight = 0)
    {
        if (!playable.MediaUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) &&
            !playable.MediaUrl.Contains("hls_variant", StringComparison.OrdinalIgnoreCase))
        {
            return playable;
        }

        var master = FetchText(playable.MediaUrl, playable.UserAgent ?? ChromeUa);
        if (string.IsNullOrWhiteSpace(master) || !Download.HlsPlaylist.IsMaster(master))
        {
            return playable;
        }

        return BindMaster(playable, master, maxHeight);
    }

    internal static YouTubePlayable BindMaster(YouTubePlayable playable, string master, int maxHeight = 0)
    {
        var wantAudio = !string.IsNullOrWhiteSpace(playable.AudioLang);
        var needDub = wantAudio && !MediaLanguage.IsOriginal(playable.AudioLang);
        var variants = Download.HlsPlaylist.Variants(master, playable.MediaUrl);
        var height = Download.HlsPlaylist.NormalizeHeight(maxHeight);
        var pick = Download.HlsPlaylist.Pick(variants, height, preferVideoOnly: needDub)
                   ?? Download.HlsPlaylist.Pick(variants, height);
        string? audio = null;
        if (pick is null || pick.LooksVideoOnly || needDub)
        {
            audio = (needDub
                    ? Download.HlsPlaylist.AudioUri(master, playable.MediaUrl, playable.AudioLang, pick?.Audio, fallback: false)
                      ?? Download.HlsPlaylist.AudioUri(master, playable.MediaUrl, playable.AudioLang, fallback: false)
                    : null)
                ?? Download.HlsPlaylist.AudioUri(master, playable.MediaUrl, MediaLanguage.Original, pick?.Audio, fallback: false)
                ?? Download.HlsPlaylist.AudioUri(master, playable.MediaUrl, MediaLanguage.Original, fallback: false);
        }

        if (pick is { LooksVideoOnly: false } && !needDub)
        {
            audio = null;
        }

        var bound = playable.WithHls(audio, Download.HlsPlaylist.SubtitleUri(master, playable.MediaUrl, playable.SubLang) is not null);
        if (pick is null || (pick.LooksVideoOnly && string.IsNullOrWhiteSpace(audio)))
        {
            return bound;
        }

        return bound.WithMedia(pick.Url);
    }

    public static bool UsesSeparateAudio(YouTubePlayable playable) =>
        !string.IsNullOrWhiteSpace(playable.AudioUrl) &&
        !playable.MediaUrl.Contains("hls_variant", StringComparison.OrdinalIgnoreCase);

    public static string CaptionVttUrl(string videoId, string language)
    {
        var lang = MediaLanguage.Normalize(language);
        var kind = MediaLanguage.Kind(language);
        var url = "https://www.youtube.com/api/timedtext?v=" + Uri.EscapeDataString(videoId) +
                  "&lang=" + Uri.EscapeDataString(lang) + "&fmt=vtt";
        if (string.Equals(kind, "asr", StringComparison.OrdinalIgnoreCase))
        {
            url += "&kind=asr";
        }

        return url;
    }

    public static string? CaptionLanguageFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?');
        var translated = QueryValue(query, "tlang");
        if (!string.IsNullOrWhiteSpace(translated))
        {
            return MediaLanguage.Normalize(translated);
        }

        return CaptionSourceLanguageFromUrl(url);
    }

    public static string? CaptionSourceLanguageFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?');
        var lang = MediaLanguage.Normalize(QueryValue(query, "lang"));
        if (lang.Length == 0)
        {
            return null;
        }

        var kind = QueryValue(query, "kind");
        return string.Equals(kind, "asr", StringComparison.OrdinalIgnoreCase)
            ? lang + ":asr"
            : lang;
    }

    public static bool CaptionUrlIsTranslate(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.Contains("tlang=", StringComparison.OrdinalIgnoreCase);

    public static bool CaptionUrlMatches(string url, string? language)
    {
        var want = MediaLanguage.Normalize(language);
        if (want.Length == 0 || string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        var query = url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : url;
        var translated = QueryValue(query, "tlang");
        if (!string.IsNullOrWhiteSpace(translated))
        {
            return MediaLanguage.Matches(want, translated);
        }

        var have = QueryValue(query, "lang");
        return string.IsNullOrWhiteSpace(have) || MediaLanguage.Matches(want, have);
    }

    public static string WithTranslate(string url, string language)
    {
        var lang = MediaLanguage.Normalize(language);
        if (lang.Length == 0 || string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (CaptionUrlMatches(url, lang) && url.Contains("tlang=", StringComparison.OrdinalIgnoreCase) == false)
        {
            return url;
        }

        var prefix = url.Contains('?') ? url[..(url.IndexOf('?') + 1)] : url + "?";
        var query = url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : "";
        var parts = new List<string>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!pair.StartsWith("tlang=", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(pair);
            }
        }

        parts.Add("tlang=" + Uri.EscapeDataString(lang));
        return prefix + string.Join('&', parts);
    }

    public static string WithoutTranslate(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !CaptionUrlIsTranslate(url))
        {
            return url;
        }

        var prefix = url.Contains('?') ? url[..(url.IndexOf('?') + 1)] : url;
        var query = url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : "";
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !pair.StartsWith("tlang=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return parts.Count == 0 ? prefix.TrimEnd('?') : prefix + string.Join('&', parts);
    }

    public static string? CaptionLanguageHeader(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Language:", StringComparison.OrdinalIgnoreCase))
            {
                return MediaLanguage.Normalize(line["Language:".Length..]);
            }
        }

        return null;
    }

    public static string EnsureVtt(string url) => WithCaptionFormat(url, "vtt");

    public static string WithCaptionFormat(string url, string format)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var fmt = string.IsNullOrWhiteSpace(format) ? "vtt" : format.Trim();
        if (url.Contains("fmt=", StringComparison.OrdinalIgnoreCase))
        {
            return System.Text.RegularExpressions.Regex.Replace(
                url,
                @"([?&]fmt=)[^&]*",
                "$1" + Uri.EscapeDataString(fmt),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return url + (url.Contains('?') ? "&" : "?") + "fmt=" + Uri.EscapeDataString(fmt);
    }

    public static string? PickCaptionUrl(string? json, string? language)
    {
        if (MediaLanguage.IsOff(language))
        {
            return null;
        }

        return ParseCaptionUrl(json, language) ??
               (MediaLanguage.IsOriginal(language) ? null : ParseCaptionUrl(json, MediaLanguage.Original));
    }

    public static string? ParseCaptionUrl(string? json, string? language)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("captions", out var captions) ||
                !captions.TryGetProperty("playerCaptionsTracklistRenderer", out var renderer) ||
                !renderer.TryGetProperty("captionTracks", out var tracks) ||
                tracks.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var want = MediaLanguage.IsOriginal(language) ? "" : MediaLanguage.Normalize(language);
            if (want.Length == 0)
            {
                want = ParseDefaultCaptionLanguage(renderer);
            }

            string? exact = null;
            string? exactAsr = null;
            string? english = null;
            string? englishAsr = null;
            string? firstManual = null;
            string? firstAsr = null;
            foreach (var track in tracks.EnumerateArray())
            {
                var url = ReadString(track, "baseUrl");
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var code = ReadString(track, "languageCode") ?? "";
                var kind = ReadString(track, "kind") ?? "";
                var asr = kind.Equals("asr", StringComparison.OrdinalIgnoreCase);
                if (asr)
                {
                    firstAsr ??= url;
                    if (MediaLanguage.Matches("en", code))
                    {
                        englishAsr ??= url;
                    }
                }
                else
                {
                    firstManual ??= url;
                    if (MediaLanguage.Matches("en", code))
                    {
                        english ??= url;
                    }
                }

                if (want.Length > 0 && !MediaLanguage.Matches(want, code))
                {
                    continue;
                }

                if (asr)
                {
                    exactAsr ??= url;
                }
                else
                {
                    exact ??= url;
                }
            }

            var wantAsr = string.Equals(MediaLanguage.Kind(language), "asr", StringComparison.OrdinalIgnoreCase);
            var picked = want.Length == 0
                ? english ?? englishAsr ?? firstManual ?? firstAsr
                : wantAsr
                    ? exactAsr ?? exact
                    : exact ?? exactAsr;
            if (!string.IsNullOrWhiteSpace(picked))
            {
                return EnsureVtt(picked);
            }

            var source = english ?? firstManual ?? englishAsr ?? firstAsr;
            return string.IsNullOrWhiteSpace(source) || want.Length == 0
                ? null
                : EnsureVtt(WithTranslate(source, want));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static IReadOnlyList<ExternalCaption> ParseCaptionTracks(string? json)
    {
        var list = new List<ExternalCaption>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return list;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("captions", out var captions) ||
                !captions.TryGetProperty("playerCaptionsTracklistRenderer", out var renderer) ||
                !renderer.TryGetProperty("captionTracks", out var tracks) ||
                tracks.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var track in tracks.EnumerateArray())
            {
                var url = ReadString(track, "baseUrl");
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var code = ReadString(track, "languageCode") ?? "";
                var kind = ReadString(track, "kind") ?? "";
                var asr = kind.Equals("asr", StringComparison.OrdinalIgnoreCase);
                var lang = MediaLanguage.Normalize(code, keepKind: true);
                if (lang.Length == 0)
                {
                    lang = "und";
                }

                if (asr && !string.Equals(MediaLanguage.Kind(lang), "asr", StringComparison.OrdinalIgnoreCase))
                {
                    lang += ":asr";
                }

                var name = CaptionTrackName(track);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = lang;
                }

                list.Add(new ExternalCaption(lang, EnsureVtt(url), name));
            }
        }
        catch (Exception)
        {
        }

        return list;
    }

    public static IReadOnlyList<ExternalCaption> ListCaptions(string? videoIdOrUrl)
    {
        if (!TryReadVideoId(videoIdOrUrl, out var id))
        {
            return [];
        }

        string? visitor = null;
        var page = FetchText("https://www.youtube.com/watch?v=" + Uri.EscapeDataString(id), ChromeUa);
        if (!string.IsNullOrWhiteSpace(page))
        {
            visitor = ExtractVisitorData(page);
            var tracks = ParseCaptionTracks(ExtractAssignedJson(page, "ytInitialPlayerResponse"));
            if (tracks.Count > 0)
            {
                return tracks;
            }
        }

        foreach (var client in PlayerClients(id, visitor, "en"))
        {
            var tracks = ParseCaptionTracks(FetchPlayer(client, visitor));
            if (tracks.Count > 0)
            {
                return tracks;
            }
        }

        return [];
    }

    private static string? CaptionTrackName(JsonElement track)
    {
        if (!track.TryGetProperty("name", out var name))
        {
            return null;
        }

        var simple = ReadString(name, "simpleText");
        if (!string.IsNullOrWhiteSpace(simple))
        {
            return simple;
        }

        if (!name.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var run in runs.EnumerateArray())
        {
            var text = ReadString(run, "text");
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    public static byte[]? DownloadCaption(string url) => DownloadCaption(url, null);

    public static byte[]? DownloadCaption(string url, string? referer)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (File.Exists(url))
        {
            return File.ReadAllBytes(url);
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath))
        {
            return File.ReadAllBytes(uri.LocalPath);
        }

        if (!LooksLikeYouTubeCaptionUrl(url))
        {
            return GetCaptionBytes(url, referer);
        }

        foreach (var candidate in CaptionDownloadUrls(url))
        {
            var bytes = GetCaptionBytes(candidate);
            if (bytes is { Length: > 15 })
            {
                return bytes;
            }
        }

        return null;
    }

    internal static bool LooksLikeYouTubeCaptionUrl(string url) =>
        url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("timedtext", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<string> CaptionDownloadUrls(string url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            yield return url;
            yield break;
        }

        yield return WithCaptionFormat(url, "srv3");
        yield return WithCaptionFormat(url, "json3");
        yield return EnsureVtt(url);
    }

    private static byte[]? GetCaptionBytes(string url, string? refererOverride = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            var referer = string.IsNullOrWhiteSpace(refererOverride) ? CaptionReferer(url) : refererOverride.Trim();
            var origin = StreamCatalog.PageOrigin(referer) ?? referer.TrimEnd('/');
            request.Headers.TryAddWithoutValidation("Referer", referer);
            request.Headers.TryAddWithoutValidation("Origin", origin.TrimEnd('/'));
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = Http.Send(request, cts.Token);
            if ((int)response.StatusCode == 429)
            {
                Thread.Sleep(500);
                using var retry = new HttpRequestMessage(HttpMethod.Get, url);
                retry.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
                retry.Headers.TryAddWithoutValidation("Referer", referer);
                retry.Headers.TryAddWithoutValidation("Origin", referer.TrimEnd('/'));
                retry.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                using var retryCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var again = Http.Send(retry, retryCts.Token);
                return again.IsSuccessStatusCode
                    ? again.Content.ReadAsByteArrayAsync(retryCts.Token).GetAwaiter().GetResult()
                    : null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return response.Content.ReadAsByteArrayAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string CaptionReferer(string url)
    {
        if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase))
        {
            return "https://www.youtube.com/";
        }

        return StreamCatalog.PageOrigin(url) ?? "https://www.youtube.com/";
    }

    public static string Diagnose(string urlOrId)
    {
        if (!TryReadVideoId(urlOrId, out var id))
        {
            return "bad-id";
        }

        var lines = new List<string> { "id=" + id };
        WarmHttp();
        var page = FetchText("https://www.youtube.com/watch?v=" + id + "&hl=en&bpctr=9999999999&has_verified=1", ChromeUa);
        var visitor = ExtractVisitorData(page);
        var pageJson = ExtractAssignedJson(page, "ytInitialPlayerResponse");
        lines.Add(
            "page bytes=" + (page?.Length ?? 0) +
            " visitor=" + (string.IsNullOrWhiteSpace(visitor) ? "no" : "yes") +
            " playerJson=" + (pageJson?.Length ?? 0) +
            " status=" + PlayabilityOf(pageJson) +
            " parsed=" + (pageJson is null ? "n/a" : ParsePlayerResponse(pageJson, id) is null ? "null" : "ok"));
        foreach (var client in PlayerClients(id, visitor, "en"))
        {
            var json = FetchPlayer(client, visitor);
            lines.Add(
                client.Name +
                " bytes=" + (json?.Length ?? 0) +
                " status=" + PlayabilityOf(json) +
                " parsed=" + (json is null ? "n/a" : ParsePlayerResponse(json, id) is null ? "null" : "ok"));
        }

        return string.Join(" | ", lines);
    }

    private static string PlayabilityOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{'))
        {
            return "empty";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var status = document.RootElement.TryGetProperty("playabilityStatus", out var el)
                ? ReadString(el, "status") ?? "?"
                : "no-status";
            var reason = document.RootElement.TryGetProperty("playabilityStatus", out var play)
                ? ReadString(play, "reason")
                : null;
            var hls = document.RootElement.TryGetProperty("streamingData", out var streaming) &&
                      streaming.TryGetProperty("hlsManifestUrl", out _);
            var dash = document.RootElement.TryGetProperty("streamingData", out streaming) &&
                       streaming.TryGetProperty("dashManifestUrl", out _);
            var formats = document.RootElement.TryGetProperty("streamingData", out streaming) &&
                          streaming.TryGetProperty("formats", out var list)
                ? list.GetArrayLength()
                : 0;
            return status +
                   (string.IsNullOrWhiteSpace(reason) ? "" : "/" + reason) +
                   " hls=" + hls +
                   " dash=" + dash +
                   " formats=" + formats;
        }
        catch (Exception)
        {
            return "bad-json";
        }
    }

    public static YouTubePlayable? Resolve(string urlOrId, Func<string, string?>? fetchPlayer = null) =>
        Resolve(urlOrId, fetchPlayer, null, null);

    private static readonly object ResolveGate = new();

    public static YouTubePlayable? Resolve(string urlOrId, Func<string, string?>? fetchPlayer, string? audioLang, string? subLang)
    {
        if (!TryReadVideoId(urlOrId, out var id))
        {
            return null;
        }

        lock (ResolveGate)
        {
            WarmHttp();
            var first = ResolveUnlocked(id, fetchPlayer, audioLang, subLang);
            if (first is not null)
            {
                return first;
            }

            Thread.Sleep(400);
            var second = ResolveUnlocked(id, fetchPlayer, audioLang, subLang);
            if (second is not null)
            {
                return second;
            }

            Thread.Sleep(800);
            return ResolveUnlocked(id, fetchPlayer, audioLang, subLang);
        }
    }

    private static YouTubePlayable? ResolveUnlocked(string id, Func<string, string?>? fetchPlayer, string? audioLang, string? subLang)
    {

        audioLang = MediaLanguage.Normalize(audioLang);
        subLang = string.IsNullOrWhiteSpace(subLang) ? subLang : MediaLanguage.Normalize(subLang, keepKind: true);
        if (fetchPlayer is not null)
        {
            var injected = fetchPlayer(id);
            var parsed = injected is null ? null : ParsePlayerResponse(injected, id);
            return parsed?.WithLanguages(audioLang, subLang).WithCaption(PickCaptionUrl(injected, subLang));
        }

        var hl = string.IsNullOrWhiteSpace(audioLang) ? "en" : audioLang;
        string? visitor = null;
        string? pageStoryboard = null;
        var page = FetchText("https://www.youtube.com/watch?v=" + id + "&hl=" + hl + "&bpctr=9999999999&has_verified=1", ChromeUa);
        if (!string.IsNullOrWhiteSpace(page))
        {
            visitor = ExtractVisitorData(page);
            var pageJson = ExtractAssignedJson(page, "ytInitialPlayerResponse");
            pageStoryboard = Preview.StoryboardSpec.FromPlayerJson(pageJson);
            var fromPage = pageJson is null ? null : ParsePlayerResponse(pageJson, id);
            if (fromPage is not null)
            {
                return fromPage.WithUserAgent(ChromeUa)
                    .WithLanguages(audioLang, subLang)
                    .WithCaption(PickCaptionUrl(pageJson, subLang));
            }
        }

        foreach (var client in PlayerClients(id, visitor, hl))
        {
            var json = FetchPlayer(client, visitor);
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            visitor ??= ExtractVisitorData(json);
            var playable = ParsePlayerResponse(json, id);
            if (playable is not null)
            {
                return playable.WithUserAgent(client.UserAgent)
                    .WithStoryboard(BetterStoryboard(pageStoryboard, playable.StoryboardSpec))
                    .WithLanguages(audioLang, subLang)
                    .WithCaption(PickCaptionUrl(json, subLang));
            }
        }

        return null;
    }

    internal static string? BetterStoryboard(string? pageSpec, string? clientSpec) =>
        (Preview.StoryboardSpec.Parse(pageSpec)?.BestLevel?.Width ?? 0) >
        (Preview.StoryboardSpec.Parse(clientSpec)?.BestLevel?.Width ?? 0) ? pageSpec : clientSpec;

    // Refresh expired/page-bound timedtext URLs from actual track metadata.
    // Never guess that a video has English/Turkish captions or discard the target language.
    public static IEnumerable<string> FreshCaptionUrls(string videoId, string language, string? sourceHint)
    {
        string? visitor = null;
        var source = CaptionSourceLanguageFromUrl(sourceHint);
        var translated = CaptionUrlIsTranslate(sourceHint);
        string? Pick(string? json)
        {
            var picked = ParseCaptionUrl(json, translated && !string.IsNullOrWhiteSpace(source) ? source : language);
            return picked is null ? null : translated ? WithTranslate(picked, language) : picked;
        }
        var page = FetchText("https://www.youtube.com/watch?v=" + Uri.EscapeDataString(videoId), ChromeUa);
        if (!string.IsNullOrWhiteSpace(page))
        {
            visitor = ExtractVisitorData(page);
            var picked = Pick(ExtractAssignedJson(page, "ytInitialPlayerResponse"));
            if (picked is not null) yield return picked;
        }
        foreach (var client in PlayerClients(videoId, visitor, "en"))
        {
            var json = FetchPlayer(client, visitor);
            var picked = Pick(json);
            if (picked is not null) yield return picked;
        }
    }

    internal static string? ExtractAssignedJson(string? html, string name)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var marker in new[] { name + " = ", name + "=", "var " + name + " = ", "var " + name + "=" })
        {
            var at = html.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var brace = html.IndexOf('{', at + marker.Length);
            if (brace >= 0)
            {
                return SliceJsonObject(html, brace);
            }
        }

        return null;
    }

    internal static string? ExtractVisitorData(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var key in new[] { "\"VISITOR_DATA\":\"", "\"visitorData\":\"" })
        {
            var at = text.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var start = at + key.Length;
            var end = text.IndexOf('"', start);
            if (end > start)
            {
                return text[start..end];
            }
        }

        return null;
    }

    internal static string? SliceJsonObject(string text, int brace)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = brace; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (ch == '\\')
                {
                    escape = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[brace..(i + 1)];
                }
            }
        }

        return null;
    }

    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var cookies = new System.Net.CookieContainer();
        cookies.Add(new Uri("https://www.youtube.com"), new System.Net.Cookie("SOCS", "CAI"));
        cookies.Add(new Uri("https://www.youtube.com"), new System.Net.Cookie("CONSENT", "YES+"));
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            CookieContainer = cookies,
            UseCookies = true
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static int _warmed;

    private static void WarmHttp()
    {
        if (Interlocked.Exchange(ref _warmed, 1) != 0)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/");
            request.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            using var response = Http.Send(request);
            response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }
    }

    private static IEnumerable<InnertubeClient> PlayerClients(string videoId, string? visitor, string hl = "en")
    {
        if (string.IsNullOrWhiteSpace(hl))
        {
            hl = "en";
        }

        var visit = string.IsNullOrWhiteSpace(visitor)
            ? ""
            : ",\"visitorData\":\"" + visitor + "\"";
        yield return new InnertubeClient(
            "VISIONOS",
            101,
            "1.02",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15",
            "{\"context\":{\"client\":{\"clientName\":\"VISIONOS\",\"clientVersion\":\"1.02\",\"deviceMake\":\"Apple\",\"deviceModel\":\"RealityDevice17,1\",\"osName\":\"visionOS\",\"osVersion\":\"26.5.23O471\",\"hl\":\"" + hl + "\",\"gl\":\"US\"" + visit + "}},\"videoId\":\"" + videoId + "\",\"contentCheckOk\":true,\"racyCheckOk\":true}");
        yield return new InnertubeClient(
            "ANDROID_VR",
            28,
            "1.65.10",
            "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip",
            "{\"context\":{\"client\":{\"clientName\":\"ANDROID_VR\",\"clientVersion\":\"1.65.10\",\"deviceMake\":\"Oculus\",\"deviceModel\":\"Quest 3\",\"androidSdkVersion\":32,\"osName\":\"Android\",\"osVersion\":\"12L\",\"hl\":\"" + hl + "\",\"gl\":\"US\"" + visit + "}},\"videoId\":\"" + videoId + "\",\"contentCheckOk\":true,\"racyCheckOk\":true}");
        yield return new InnertubeClient(
            "IOS",
            5,
            "21.26.4",
            "com.google.ios.youtube/21.26.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)",
            "{\"context\":{\"client\":{\"clientName\":\"IOS\",\"clientVersion\":\"21.26.4\",\"deviceMake\":\"Apple\",\"deviceModel\":\"iPhone16,2\",\"osName\":\"iPhone\",\"osVersion\":\"18.3.2.22D82\",\"hl\":\"" + hl + "\",\"gl\":\"US\"" + visit + "}},\"videoId\":\"" + videoId + "\",\"contentCheckOk\":true,\"racyCheckOk\":true}");
        yield return new InnertubeClient(
            "ANDROID",
            3,
            "20.10.38",
            "com.google.android.youtube/20.10.38 (Linux; U; Android 14) gzip",
            "{\"context\":{\"client\":{\"clientName\":\"ANDROID\",\"clientVersion\":\"20.10.38\",\"androidSdkVersion\":34,\"osName\":\"Android\",\"osVersion\":\"14\",\"hl\":\"" + hl + "\",\"gl\":\"US\"" + visit + "}},\"videoId\":\"" + videoId + "\",\"contentCheckOk\":true,\"racyCheckOk\":true}");
        yield return new InnertubeClient(
            "MWEB",
            2,
            "2.20260817.05.00",
            "Mozilla/5.0 (iPad; CPU OS 16_7_10 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1,gzip(gfe)",
            "{\"context\":{\"client\":{\"clientName\":\"MWEB\",\"clientVersion\":\"2.20260817.05.00\",\"hl\":\"" + hl + "\",\"gl\":\"US\"" + visit + "}},\"videoId\":\"" + videoId + "\",\"contentCheckOk\":true,\"racyCheckOk\":true}");
        yield return new InnertubeClient(
            "TVHTML5",
            7,
            "7.20260707.07.00",
            "Mozilla/5.0 (ChromiumStylePlatform) Cobalt/25.lts.30.1034943-gold (unlike Gecko), Unknown_TV_Unknown_0/Unknown (Unknown, Unknown)",
            "{\"context\":{\"client\":{\"clientName\":\"TVHTML5\",\"clientVersion\":\"7.20260707.07.00\",\"hl\":\"" + hl + "\",\"gl\":\"US\"" + visit + "}},\"videoId\":\"" + videoId + "\",\"contentCheckOk\":true,\"racyCheckOk\":true}");
        yield return new InnertubeClient(
            "WEB",
            1,
            "2.20260817.01.00",
            ChromeUa,
            "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20260817.01.00\",\"hl\":\"" + hl + "\",\"gl\":\"US\"" + visit + "}},\"videoId\":\"" + videoId + "\",\"playbackContext\":{\"contentPlaybackContext\":{\"html5Preference\":\"HTML5_PREF_WANTS\"}},\"contentCheckOk\":true,\"racyCheckOk\":true}");
    }

    private static string? FetchPlayer(InnertubeClient client, string? visitor)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://www.youtube.com/youtubei/v1/player?prettyPrint=false");
            request.Headers.TryAddWithoutValidation("User-Agent", client.UserAgent);
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", client.ClientName.ToString());
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", client.ClientVersion);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            if (!string.IsNullOrWhiteSpace(visitor))
            {
                request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);
            }

            request.Content = new StringContent(client.Body, System.Text.Encoding.UTF8, "application/json");
            using var response = Http.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FetchText(string url, string userAgent)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("Accept-Language", "tr-TR,tr;q=0.9,en-US,en;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var response = Http.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private readonly record struct InnertubeClient(
        string Name,
        int ClientName,
        string ClientVersion,
        string UserAgent,
        string Body);

    private static bool TryBestPair(JsonElement streaming, out string videoUrl, out string? audioUrl)
    {
        videoUrl = "";
        audioUrl = null;
        if (!streaming.TryGetProperty("adaptiveFormats", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string? video = null;
        var videoScore = -1;
        string? audio = null;
        var audioScore = -1;
        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var href) || href.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = href.GetString();
            if (string.IsNullOrWhiteSpace(value) || LooksLikeDrm(item))
            {
                continue;
            }

            var mime = item.TryGetProperty("mimeType", out var mimeEl) ? mimeEl.GetString() ?? "" : "";
            var width = item.TryGetProperty("width", out var w) && w.TryGetInt32(out var px) ? px : 0;
            if (width > 0 && mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                var score = width >= 1920 ? 4000 + width : width >= 1280 ? 3000 + width : width;
                if (mime.Contains("avc1", StringComparison.OrdinalIgnoreCase))
                {
                    score += 500;
                }

                if (score > videoScore)
                {
                    videoScore = score;
                    video = value;
                }

                continue;
            }

            if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                mime.Contains("mp4a", StringComparison.OrdinalIgnoreCase))
            {
                var score = mime.Contains("mp4a", StringComparison.OrdinalIgnoreCase) ? 200 : 100;
                if (item.TryGetProperty("bitrate", out var br) && br.TryGetInt32(out var bps))
                {
                    score += Math.Min(bps / 1000, 200);
                }

                if (score > audioScore)
                {
                    audioScore = score;
                    audio = value;
                }
            }
        }

        if (video is null || videoScore < 1280)
        {
            return false;
        }

        videoUrl = video;
        audioUrl = audio;
        return true;
    }

    private static bool TryBestFormatUrl(JsonElement streaming, string name, out string url)
    {
        url = "";
        if (!streaming.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string? best = null;
        var bestScore = -1;
        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var href) || href.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = href.GetString();
            if (string.IsNullOrWhiteSpace(value) || LooksLikeDrm(item))
            {
                continue;
            }

            var mime = item.TryGetProperty("mimeType", out var mimeEl) ? mimeEl.GetString() ?? "" : "";
            if (mime.Contains("webm", StringComparison.OrdinalIgnoreCase) && best is not null)
            {
                continue;
            }

            var width = item.TryGetProperty("width", out var w) && w.TryGetInt32(out var px) ? px : 0;
            var score = width + (mime.Contains("avc1", StringComparison.OrdinalIgnoreCase) ? 100 : 0);
            if (score >= bestScore)
            {
                bestScore = score;
                best = value;
            }
        }

        if (best is null)
        {
            return false;
        }

        url = best;
        return true;
    }

    private static bool LooksLikeDrm(JsonElement item) =>
        item.TryGetProperty("drmFamilies", out _) ||
        item.TryGetProperty("signatureCipher", out _) && !item.TryGetProperty("url", out _);

    private static bool TryUrl(JsonElement obj, string name, out string url)
    {
        url = "";
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        url = text;
        return true;
    }

    internal static string ParseDefaultCaptionLanguage(JsonElement renderer)
    {
        if (renderer.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        var index = -1;
        if (renderer.TryGetProperty("audioTracks", out var audioTracks) &&
            audioTracks.ValueKind == JsonValueKind.Array &&
            audioTracks.GetArrayLength() > 0)
        {
            var audioIndex = 0;
            if (renderer.TryGetProperty("defaultAudioTrackIndex", out var defaultAudio) &&
                defaultAudio.TryGetInt32(out var parsedAudio))
            {
                audioIndex = parsedAudio;
            }

            if (audioIndex < 0 || audioIndex >= audioTracks.GetArrayLength())
            {
                audioIndex = 0;
            }

            var audio = audioTracks[audioIndex];
            if (audio.TryGetProperty("defaultCaptionTrackIndex", out var captionIndex) &&
                captionIndex.TryGetInt32(out var parsedCaption))
            {
                index = parsedCaption;
            }
            else if (audio.TryGetProperty("captionTrackIndices", out var list) &&
                     list.ValueKind == JsonValueKind.Array &&
                     list.GetArrayLength() > 0 &&
                     list[0].TryGetInt32(out var first))
            {
                index = first;
            }
        }

        if (index >= 0 &&
            renderer.TryGetProperty("captionTracks", out var captions) &&
            captions.ValueKind == JsonValueKind.Array &&
            index < captions.GetArrayLength())
        {
            return MediaLanguage.Normalize(ReadString(captions[index], "languageCode"));
        }

        return "";
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string QueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts[0].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            }
        }

        return "";
    }
}
