using System.Text.RegularExpressions;
using Grok.Player.Core.Download;
using Grok.Player.Core.Subtitles;

namespace Grok.Player.Core.Media;

public static class HlsCaptions
{
    private static readonly Regex DashSet = new(
        @"<AdaptationSet\b[^>]*>.*?</AdaptationSet>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? TryLoad(string mediaUrl, string? language, string? userAgent, string? cacheId)
    {
        if (string.IsNullOrWhiteSpace(mediaUrl) || MediaLanguage.IsOff(language))
        {
            return null;
        }

        var ext = StreamProbe.Extension(mediaUrl);
        if (ext is ".mp4" or ".mkv" or ".webm" or ".mov" or ".m4v")
        {
            return SidecarNear(mediaUrl);
        }

        if (ext is not (".m3u8" or ".m3u" or ".mpd" or ""))
        {
            return SidecarNear(mediaUrl);
        }

        var master = StreamCatalog.GetText(mediaUrl, userAgent, mediaUrl);
        if (string.IsNullOrWhiteSpace(master))
        {
            return SidecarNear(mediaUrl);
        }

        var uri = SubtitleUriFromManifest(master, mediaUrl, language);
        if (string.IsNullOrWhiteSpace(uri))
        {
            return IsLiveManifest(master) ? null : SidecarNear(mediaUrl);
        }

        var body = ReadDocument(uri, userAgent);
        if (string.IsNullOrWhiteSpace(body) || !StreamCaptionLoader.LooksLikeCaptions(body))
        {
            return null;
        }

        var folder = Path.Combine(Path.GetTempPath(), "GrokPlayer", "captions");
        Directory.CreateDirectory(folder);
        var lang = MediaLanguage.Normalize(language);
        var vtt = Path.Combine(folder, CacheStem(cacheId) + "." + (lang.Length == 0 ? "auto" : lang) + ".vtt");
        return StreamCaptionLoader.WriteSrt(vtt, body);
    }

    public static IReadOnlyList<(string File, string Name, string Language)> LoadAll(
        string mediaUrl,
        string? userAgent,
        string? cacheId)
    {
        var list = new List<(string File, string Name, string Language)>();
        if (string.IsNullOrWhiteSpace(mediaUrl))
        {
            return list;
        }

        var ext = StreamProbe.Extension(mediaUrl);
        if (ext is ".mp4" or ".mkv" or ".webm" or ".mov" or ".m4v")
        {
            return list;
        }

        var master = StreamCatalog.GetText(mediaUrl, userAgent, mediaUrl);
        if (string.IsNullOrWhiteSpace(master) || !HlsPlaylist.IsMaster(master))
        {
            return list;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folder = Path.Combine(Path.GetTempPath(), "GrokPlayer", "captions");
        Directory.CreateDirectory(folder);
        var stem = CacheStem(cacheId ?? mediaUrl);
        foreach (var sub in HlsPlaylist.Subtitles(master, mediaUrl))
        {
            if (sub.Forced)
            {
                continue;
            }

            var lang = MediaLanguage.Normalize(string.IsNullOrWhiteSpace(sub.Language) ? sub.Name : sub.Language);
            if (lang.Length > 0 && !seen.Add(lang))
            {
                continue;
            }

            var body = ReadDocument(sub.Url, userAgent);
            if (string.IsNullOrWhiteSpace(body) || !StreamCaptionLoader.LooksLikeSidecar(body))
            {
                continue;
            }

            var parsed = SrtDocument.Parse(body, compact: false).ForDisplay();
            if (parsed.Cues.Count == 0)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(sub.Name) ? lang : sub.Name.Trim();
            var tag = lang.Length == 0 ? "auto" : lang;
            var vtt = Path.Combine(folder, stem + "." + tag + ".vtt");
            File.WriteAllText(vtt, ToWebVtt(parsed));
            foreach (var extra in new[] { Path.ChangeExtension(vtt, ".srt"), Path.ChangeExtension(vtt, ".ass") })
            {
                if (File.Exists(extra))
                {
                    File.Delete(extra);
                }
            }

            list.Add((vtt, name, lang));
        }

        return list;
    }

    public static string? SubtitleUriFromManifest(string manifest, string mediaUrl, string? language)
    {
        if (string.IsNullOrWhiteSpace(manifest) || string.IsNullOrWhiteSpace(mediaUrl))
        {
            return null;
        }

        if (LooksLikeDash(manifest))
        {
            return StreamProbe.ClassifyManifest(manifest) == StreamKind.Live
                ? null
                : DashSubtitleUri(manifest, mediaUrl, language);
        }

        if (!manifest.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Sliding-window media playlists are live. VOD masters almost never
        // carry EXT-X-ENDLIST, so that tag must not hide subtitle groups.
        if (!HlsPlaylist.IsMaster(manifest) && HlsPlaylist.IsLive(manifest))
        {
            return null;
        }

        return HlsPlaylist.SubtitleUri(manifest, mediaUrl, language);
    }

    public static bool IsLiveManifest(string manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest))
        {
            return false;
        }

        if (LooksLikeDash(manifest))
        {
            return StreamProbe.ClassifyManifest(manifest) == StreamKind.Live;
        }

        return !HlsPlaylist.IsMaster(manifest) && HlsPlaylist.IsLive(manifest);
    }

    private static string ToWebVtt(SrtDocument document)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("WEBVTT\n\n");
        foreach (var cue in document.Cues)
        {
            builder.Append(SrtTime.Format(cue.Start));
            builder.Append(" --> ");
            builder.Append(SrtTime.Format(cue.End));
            builder.Append('\n');
            builder.Append(cue.Text);
            builder.Append("\n\n");
        }

        return builder.ToString();
    }

    private static string CacheStem(string? cacheId)
    {
        if (string.IsNullOrWhiteSpace(cacheId))
        {
            return "hls";
        }

        if (cacheId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && cacheId.Length <= 80)
        {
            return cacheId.Replace("|", ".", StringComparison.Ordinal);
        }

        return "hls-" + Math.Abs(cacheId.GetHashCode(StringComparison.Ordinal))
            .ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string? SidecarUrl(string mediaUrl, string extension)
    {
        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            return null;
        }

        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var path = uri.AbsolutePath;
        var dot = path.LastIndexOf('.');
        var slash = path.LastIndexOf('/');
        var stem = dot > slash ? path[..dot] : path;
        return uri.GetLeftPart(UriPartial.Authority) + stem + ext;
    }

    public static string? DashSubtitleUri(string mpd, string baseUrl, string? language)
    {
        string? fallback = null;
        var want = MediaLanguage.Normalize(language);
        foreach (Match match in DashSet.Matches(mpd))
        {
            var block = match.Value;
            if (!IsDashText(block))
            {
                continue;
            }

            var href = DashBaseUrl(block);
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var resolved = HlsPlaylist.Resolve(baseUrl, href);
            fallback ??= resolved;
            var lang = DashAttribute(block, "lang");
            var label = DashAttribute(block, "label");
            if (want.Length == 0 ||
                MediaLanguage.Matches(language, lang) ||
                MediaLanguage.MatchesName(language, label))
            {
                return resolved;
            }
        }

        return string.IsNullOrWhiteSpace(language) ? fallback : null;
    }

    internal static string? ReadDocument(string url, string? userAgent)
    {
        var text = StreamCatalog.GetText(url, userAgent, url);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (StreamCaptionLoader.LooksLikeCaptions(text) &&
            !text.Contains("#EXTINF", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        if (!text.Contains("#EXTINF", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var (_, segments) = HlsPlaylist.Media(text, url);
        var builder = new System.Text.StringBuilder();
        builder.Append("WEBVTT\n\n");
        foreach (var segment in segments.Take(400))
        {
            var piece = StreamCatalog.GetText(segment.Url, userAgent, url);
            if (string.IsNullOrWhiteSpace(piece))
            {
                continue;
            }

            foreach (var line in piece.Replace("\r", "").Split('\n'))
            {
                if (line.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Kind:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Language:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("X-TIMESTAMP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                builder.Append(line);
                builder.Append('\n');
            }

            builder.Append('\n');
        }

        return builder.Length > 12 ? builder.ToString() : null;
    }

    private static string? SidecarNear(string mediaUrl)
    {
        foreach (var ext in new[] { ".vtt", ".srt" })
        {
            var candidate = SidecarUrl(mediaUrl, ext);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var text = StreamCatalog.GetText(candidate, StreamCatalog.ChromeUa, mediaUrl);
            if (!string.IsNullOrWhiteSpace(text) && StreamCaptionLoader.LooksLikeCaptions(text))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool LooksLikeDash(string text) =>
        text.Contains("<MPD", StringComparison.OrdinalIgnoreCase);

    private static bool IsDashText(string block) =>
        block.Contains("contentType=\"text\"", StringComparison.OrdinalIgnoreCase) ||
        block.Contains("contentType='text'", StringComparison.OrdinalIgnoreCase) ||
        block.Contains("text/vtt", StringComparison.OrdinalIgnoreCase) ||
        block.Contains("wvtt", StringComparison.OrdinalIgnoreCase);

    private static string? DashBaseUrl(string block)
    {
        var start = block.IndexOf("<BaseURL>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += "<BaseURL>".Length;
        var end = block.IndexOf("</BaseURL>", start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? null : block[start..end].Trim();
    }

    private static string DashAttribute(string block, string name)
    {
        var token = name + "=\"";
        var at = block.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            token = name + "='";
            at = block.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return "";
            }
        }

        var start = at + token.Length;
        var quote = token[^1];
        var end = block.IndexOf(quote, start);
        return end < 0 ? "" : block[start..end];
    }
}
