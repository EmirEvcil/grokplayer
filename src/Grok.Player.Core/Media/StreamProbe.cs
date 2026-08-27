namespace Grok.Player.Core.Media;

public enum StreamKind
{
    Unknown,
    Vod,
    Live
}

public static class StreamProbe
{
    public static string FormatLabel(string url)
    {
        if (YouTubeCatalog.IsWatchUrl(url) ||
            url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            var youtubeExt = Extension(url);
            return youtubeExt is ".m3u8" or ".m3u" || url.Contains("hls_variant", StringComparison.OrdinalIgnoreCase)
                ? "hls"
                : youtubeExt is ".mpd" || url.Contains("/dash/", StringComparison.OrdinalIgnoreCase)
                    ? "dash"
                    : "youtube";
        }

        var ext = Extension(url);
        return ext switch
        {
            ".mpd" => "dash",
            ".m3u8" or ".m3u" => "hls",
            ".ts" or ".m2ts" => "ts",
            _ => "url"
        };
    }

    public static string Extension(string url)
    {
        var path = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
        }

        var q = path.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            path = path[..q];
        }

        return Path.GetExtension(path);
    }

    public static StreamKind ClassifyUrl(string url)
    {
        var ext = Extension(url);
        if (ext is ".mp4" or ".mkv" or ".webm" or ".mov" or ".m4v" or ".avi" or ".wmv")
        {
            return StreamKind.Vod;
        }

        return StreamKind.Unknown;
    }

    public static StreamKind ClassifyManifest(string text, string? contentType = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return StreamKind.Unknown;
        }

        var body = text.TrimStart();
        if (body.Contains("<MPD", StringComparison.OrdinalIgnoreCase) ||
            (contentType?.Contains("dash", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            if (ContainsIgnoreCase(body, "type=\"dynamic\"") || ContainsIgnoreCase(body, "type='dynamic'"))
            {
                return StreamKind.Live;
            }

            if (ContainsIgnoreCase(body, "type=\"static\"") || ContainsIgnoreCase(body, "type='static'"))
            {
                return StreamKind.Vod;
            }

            return StreamKind.Vod;
        }

        if (body.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ||
            (contentType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            if (ContainsIgnoreCase(body, "#EXT-X-ENDLIST") ||
                ContainsIgnoreCase(body, "PLAYLIST-TYPE:VOD"))
            {
                return StreamKind.Vod;
            }

            if (ContainsIgnoreCase(body, "#EXT-X-STREAM-INF"))
            {
                return StreamKind.Unknown;
            }

            if (ContainsIgnoreCase(body, "PLAYLIST-TYPE:EVENT") ||
                ContainsIgnoreCase(body, "#EXT-X-MEDIA-SEQUENCE") ||
                ContainsIgnoreCase(body, "#EXT-X-TARGETDURATION"))
            {
                return StreamKind.Live;
            }

            return StreamKind.Unknown;
        }

        return StreamKind.Unknown;
    }

    public static StreamKind ClassifyPlayback(double? durationSeconds, bool? seekable, string? format)
    {
        var hlsOrDash = format is not null &&
                        (format.Contains("hls", StringComparison.OrdinalIgnoreCase) ||
                         format.Contains("dash", StringComparison.OrdinalIgnoreCase) ||
                         format.Contains("mpegts", StringComparison.OrdinalIgnoreCase));

        if (hlsOrDash)
        {
            if (seekable == false || durationSeconds is null or <= 0.5)
            {
                return StreamKind.Live;
            }

            // Live DVR reports a duration and is seekable. The manifest decides.
            return StreamKind.Unknown;
        }

        if (durationSeconds is null or <= 0.5)
        {
            return StreamKind.Unknown;
        }

        return StreamKind.Vod;
    }

    public static StreamKind Combine(StreamKind manifest, StreamKind playback)
    {
        if (manifest == StreamKind.Vod)
        {
            return StreamKind.Vod;
        }

        if (manifest == StreamKind.Live)
        {
            return StreamKind.Live;
        }

        if (playback != StreamKind.Unknown)
        {
            return playback;
        }

        return manifest;
    }

    public static string? FirstVariantUri(string master, string baseUrl)
    {
        foreach (var raw in master.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (Uri.TryCreate(line, UriKind.Absolute, out var absolute))
            {
                return absolute.ToString();
            }

            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) &&
                Uri.TryCreate(root, line, out var resolved))
            {
                return resolved.ToString();
            }
        }

        return null;
    }

    public static bool LooksLikeDrm(string manifest) =>
        ContainsIgnoreCase(manifest, "ContentProtection") ||
        ContainsIgnoreCase(manifest, "com.widevine") ||
        ContainsIgnoreCase(manifest, "playready") ||
        ContainsIgnoreCase(manifest, "EXT-X-KEY:METHOD=SAMPLE-AES") ||
        ContainsIgnoreCase(manifest, "EXT-X-SESSION-KEY");

    private static bool ContainsIgnoreCase(string text, string value) =>
        text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
}
