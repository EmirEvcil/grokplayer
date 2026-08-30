using Grok.Player.Core.Media;

namespace Grok.Player.Core.Subtitles;

public static class StreamCaptionLoader
{
    // Do not reuse old flattened/wrong-language caption conversions. Keep those files
    // untouched because the user may have edited them in the subtitle browser.
    public static string CacheDirectory => Path.Combine(Path.GetTempPath(), "GrokPlayer", "captions-v3");

    public static string? LoadSidecar(string url, string? language = null, string? name = null, string? referer = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (File.Exists(url))
        {
            return url;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var local) && local.IsFile && File.Exists(local.LocalPath))
        {
            return local.LocalPath;
        }

        var host = YouTubeCatalog.CaptionReferer(url);
        string? text = null;
        var parsed = new SrtDocument();
        foreach (var tryReferer in new[] { host, referer, null })
        {
            var bytes = YouTubeCatalog.DownloadCaption(url, tryReferer);
            text = DecodeCaptionBytes(bytes);
            parsed = LooksLikeSidecar(text) ? SrtDocument.Parse(text, compact: false) : new SrtDocument();
            if (parsed.Cues.Count > 0)
            {
                break;
            }
        }

        if (parsed.Cues.Count == 0)
        {
            return null;
        }

        var lang = MediaLanguage.Normalize(language);
        if (lang.Length == 0 || MediaLanguage.IsOriginal(lang))
        {
            lang = MediaLanguage.FromName(name);
        }

        if (lang.Length == 0)
        {
            lang = MediaLanguage.Normalize(YouTubeCatalog.CaptionLanguageFromUrl(url));
        }

        if (lang.Length == 0 || MediaLanguage.IsOriginal(lang))
        {
            lang = "auto";
        }

        var folder = CacheDirectory;
        Directory.CreateDirectory(folder);
        var stem = "sidecar-" + Math.Abs(url.GetHashCode(StringComparison.Ordinal))
            .ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        var vtt = Path.Combine(folder, stem + "." + lang + ".vtt");
        WriteSrt(vtt, text!);
        return vtt;
    }

    public static string PlaySidecar(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var vtt = path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, ".vtt");
        return File.Exists(vtt) ? vtt : PlayPath(path);
    }

    private static string DecodeCaptionBytes(byte[]? bytes)
    {
        if (bytes is not { Length: > 15 })
        {
            return "";
        }

        var utf8 = SrtDocument.LoadText(bytes);
        var utf8Cues = LooksLikeSidecar(utf8) ? SrtDocument.Parse(utf8, compact: false).Cues.Count : 0;
        if (utf8Cues > 0 && !utf8.Contains('\uFFFD', StringComparison.Ordinal))
        {
            return utf8;
        }

        try
        {
            var turkish = System.Text.Encoding.GetEncoding(1254).GetString(bytes);
            var turkishCues = LooksLikeSidecar(turkish) ? SrtDocument.Parse(turkish, compact: false).Cues.Count : 0;
            if (turkishCues > utf8Cues)
            {
                return turkish;
            }
        }
        catch (Exception)
        {
        }

        return utf8;
    }

    public static string? Load(string? videoId, string? language, string? captionUrl)
    {
        if (MediaLanguage.IsOff(language))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(language) && string.IsNullOrWhiteSpace(captionUrl))
        {
            return null;
        }

        var folder = CacheDirectory;
        Directory.CreateDirectory(folder);
        var want = EffectiveLanguage(language, captionUrl);
        var tag = CacheTag(want, captionUrl);
        var cached = Existing(videoId, want, captionUrl);
        if (cached is not null && CacheMatches(cached, want))
        {
            return cached;
        }

        var stem = CacheStem(videoId);
        var rawPath = Path.Combine(folder, stem + "." + tag + ".vtt");
        var translating = IsTranslateRequest(want, captionUrl);
        var urls = Urls(videoId, want, captionUrl);
        if (IsYouTubeId(videoId) && want.Length > 0 && !File.Exists(captionUrl))
            urls = urls.Concat(YouTubeCatalog.FreshCaptionUrls(videoId, want, captionUrl));
        foreach (var url in urls.Distinct(StringComparer.Ordinal))
        {
            var bytes = YouTubeCatalog.DownloadCaption(url);
            if (bytes is null || bytes.Length < 15)
            {
                continue;
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes);
            if (YouTubeTimedText.LooksLike(text))
            {
                text = YouTubeTimedText.ToVtt(text, translating ? null : want) ?? "";
            }

            if (!LooksLikeCaptions(text))
            {
                continue;
            }

            var header = YouTubeCatalog.CaptionLanguageHeader(text);
            if (want.Length > 0 && !AcceptsLanguage(url, want, header))
            {
                continue;
            }

            if (translating && IsSameAsSource(videoId, captionUrl, text))
            {
                continue;
            }

            if (translating && want.Length > 0 && string.IsNullOrWhiteSpace(header))
            {
                text = StampLanguage(text, want);
            }

            var parsed = SrtDocument.Parse(text, compact: false);
            if (parsed.Cues.Count == 0) continue;
            return WriteSrt(rawPath, text);
        }

        cached = Existing(videoId, want, captionUrl);
        return cached is not null && CacheMatches(cached, want) ? cached : null;
    }

    public static string EffectiveLanguage(string? language, string? captionUrl)
    {
        if (MediaLanguage.IsOff(language)) return "";
        var requested = MediaLanguage.Normalize(language, keepKind: true);
        if (requested.Length > 0 && !MediaLanguage.IsOriginal(requested)) return requested;
        if (YouTubeCatalog.CaptionUrlIsTranslate(captionUrl))
        {
            var translated = MediaLanguage.Normalize(
                YouTubeCatalog.CaptionLanguageFromUrl(captionUrl),
                keepKind: true);
            return translated.Length > 0
                ? translated
                : MediaLanguage.Normalize(language, keepKind: true);
        }

        var source = MediaLanguage.Normalize(
            YouTubeCatalog.CaptionSourceLanguageFromUrl(captionUrl),
            keepKind: true);
        return source.Length > 0 ? source : MediaLanguage.Normalize(language, keepKind: true);
    }

    public static string? Existing(string? videoId, string? language, string? captionUrl = null)
    {
        var tag = CacheTag(language, captionUrl);
        if (string.IsNullOrWhiteSpace(videoId) || tag.Length == 0 || tag == "auto")
        {
            return null;
        }

        var folder = CacheDirectory;
        var stem = CacheStem(videoId);
        var srt = Path.Combine(folder, stem + "." + tag + ".srt");
        if (File.Exists(srt) && new FileInfo(srt).Length > 8)
        {
            return srt;
        }

        var vtt = Path.Combine(folder, stem + "." + tag + ".vtt");
        return File.Exists(vtt) && new FileInfo(vtt).Length > 8 ? vtt : null;
    }

    internal static string CacheStem(string? videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return "stream";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = videoId.Trim().Select(symbol => invalid.Contains(symbol) ? '-' : symbol).ToArray();
        var stem = new string(chars).Trim('-');
        return stem.Length == 0 ? "stream" : stem;
    }

    internal static string CacheTag(string? language, string? captionUrl)
    {
        var want = MediaLanguage.Normalize(language, keepKind: true).Replace(':', '-');
        if (want.Length == 0)
        {
            want = MediaLanguage.Normalize(YouTubeCatalog.CaptionLanguageFromUrl(captionUrl));
        }

        if (want.Length == 0)
        {
            return "auto";
        }

        var sourceLanguage = MediaLanguage.Normalize(YouTubeCatalog.CaptionSourceLanguageFromUrl(captionUrl), keepKind: true);
        var source = sourceLanguage.Replace(':', '-');
        if (source.Length > 0 && !MediaLanguage.Matches(language, sourceLanguage))
        {
            return source + "." + want;
        }

        return YouTubeCatalog.CaptionUrlIsTranslate(captionUrl) ? want + ".tlang" : want;
    }

    internal static bool CacheMatches(string path, string want)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        if (want.Length == 0)
        {
            return true;
        }

        var document = DocumentPath(path);
        if (!document.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) || !File.Exists(document))
        {
            return true;
        }

        var text = File.ReadAllText(document);
        if (document.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("WEBVTT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var header = YouTubeCatalog.CaptionLanguageHeader(text);
        if (!string.IsNullOrWhiteSpace(header) && !MediaLanguage.Matches(want, header))
        {
            return false;
        }

        var sibling = SourceSibling(document);
        return sibling is null || !SameOpeningCues(SrtDocument.Parse(text, compact: false), SrtDocument.Load(sibling));
    }

    public static string PlayPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        if (path.EndsWith(".ass", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var ass = Path.ChangeExtension(path, ".ass");
        return File.Exists(ass) ? ass : path;
    }

    public static string DocumentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        if (path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            var vtt = Path.ChangeExtension(path, ".vtt");
            return File.Exists(vtt) ? vtt : path;
        }

        if (!path.EndsWith(".ass", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var srt = Path.ChangeExtension(path, ".srt");
        return File.Exists(srt) ? srt : path;
    }

    internal static IEnumerable<string> Urls(string? videoId, string? language, string? captionUrl)
    {
        var translating = IsTranslateRequest(language, captionUrl);
        var want = MediaLanguage.Normalize(language);
        if (!string.IsNullOrWhiteSpace(captionUrl) &&
            (File.Exists(captionUrl) ||
             (Uri.TryCreate(captionUrl, UriKind.Absolute, out var local) && local.IsFile)))
        {
            yield return captionUrl;
        }
        else if (!string.IsNullOrWhiteSpace(captionUrl))
        {
            if (translating)
            {
                yield return YouTubeCatalog.WithTranslate(captionUrl, want);
            }
            else
            {
                yield return YouTubeCatalog.EnsureVtt(captionUrl);
            }
        }

        if (MediaLanguage.IsOff(language) || string.IsNullOrWhiteSpace(language))
        {
            yield break;
        }

        var sourceLang = MediaLanguage.Normalize(YouTubeCatalog.CaptionSourceLanguageFromUrl(captionUrl));
        if (IsYouTubeId(videoId) && want.Length > 0 && !MediaLanguage.IsOriginal(language))
        {
            if (translating)
            {
                if (sourceLang.Length > 0)
                {
                    yield return YouTubeCatalog.CaptionVttUrl(videoId, sourceLang + ":asr") + "&tlang=" + want;
                }
            }
            else
            {
                if (string.Equals(MediaLanguage.Kind(language), "asr", StringComparison.OrdinalIgnoreCase))
                {
                    yield return YouTubeCatalog.CaptionVttUrl(videoId, want + ":asr");
                }

                yield return YouTubeCatalog.CaptionVttUrl(videoId, want);
                yield return YouTubeCatalog.CaptionVttUrl(videoId, want + ":asr");
            }
        }
    }

    internal static bool IsTranslateRequest(string? language, string? captionUrl)
    {
        if (MediaLanguage.IsOriginal(language) || string.IsNullOrWhiteSpace(language))
        {
            return YouTubeCatalog.CaptionUrlIsTranslate(captionUrl);
        }

        if (YouTubeCatalog.CaptionUrlIsTranslate(captionUrl))
        {
            return true;
        }

        var want = MediaLanguage.Normalize(language);
        var source = MediaLanguage.Normalize(YouTubeCatalog.CaptionSourceLanguageFromUrl(captionUrl));
        return want.Length > 0 &&
               source.Length > 0 &&
               !MediaLanguage.Matches(want, source);
    }

    internal static bool AcceptsLanguage(string url, string want, string? header)
    {
        if (url.Contains("tlang=", StringComparison.OrdinalIgnoreCase) &&
            YouTubeCatalog.CaptionUrlMatches(url, want))
        {
            return string.IsNullOrWhiteSpace(header) || MediaLanguage.Matches(want, header);
        }

        if (!string.IsNullOrWhiteSpace(header))
        {
            return MediaLanguage.Matches(want, header);
        }

        return YouTubeCatalog.CaptionUrlMatches(url, want);
    }

    internal static bool IsSameAsSource(string? videoId, string? captionUrl, string text)
    {
        if (string.IsNullOrWhiteSpace(captionUrl) ||
            string.IsNullOrWhiteSpace(text) ||
            !YouTubeCatalog.CaptionUrlIsTranslate(captionUrl))
        {
            return false;
        }

        var sourceUrl = YouTubeCatalog.WithoutTranslate(captionUrl);
        var sourceLang = YouTubeCatalog.CaptionSourceLanguageFromUrl(captionUrl);
        var sourcePath = Existing(videoId, sourceLang, sourceUrl);
        if (sourcePath is null || !File.Exists(DocumentPath(sourcePath)))
        {
            return false;
        }

        return SameOpeningCues(
            SrtDocument.Parse(text, compact: false),
            SrtDocument.Load(DocumentPath(sourcePath)));
    }

    internal static string? SourceSibling(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var last = name.LastIndexOf('.');
        if (last <= 0)
        {
            return null;
        }

        var withoutWant = name[..last];
        if (withoutWant.LastIndexOf('.') <= 0)
        {
            return null;
        }

        foreach (var ext in new[] { ".vtt", ".srt" })
        {
            var candidate = Path.Combine(dir, withoutWant + ext);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static bool SameOpeningCues(SrtDocument left, SrtDocument right)
    {
        var count = Math.Min(3, Math.Min(left.Cues.Count, right.Cues.Count));
        if (count == 0)
        {
            return false;
        }

        var same = 0;
        for (var i = 0; i < count; i++)
        {
            if (string.Equals(
                    left.Cues[i].Text.Trim(),
                    right.Cues[i].Text.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                same++;
            }
        }

        return same == count || same >= 2;
    }

    internal static string StampLanguage(string text, string language)
    {
        var lang = MediaLanguage.Normalize(language);
        if (lang.Length == 0 || string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (!text.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return "WEBVTT\nLanguage: " + lang + text["WEBVTT".Length..];
    }

    internal static bool IsYouTubeId(string? videoId) =>
        !string.IsNullOrWhiteSpace(videoId) &&
        !videoId.Contains('|', StringComparison.Ordinal) &&
        YouTubeCatalog.TryReadVideoId(videoId, out var id) &&
        string.Equals(id, videoId, StringComparison.Ordinal);

    internal static bool LooksLikeCaptions(string text) =>
        text.Contains("WEBVTT", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("-->", StringComparison.Ordinal) ||
        YouTubeTimedText.LooksLike(text);

    internal static bool LooksLikeSidecar(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        !text.Contains("<html", StringComparison.OrdinalIgnoreCase) &&
        LooksLikeCaptions(text);

    internal static string WriteSrt(string vttPath, string text)
    {
        File.WriteAllText(vttPath, text);
        var srt = Path.ChangeExtension(vttPath, ".srt");
        var document = SrtDocument.Parse(text, compact: false).ForDisplay();
        if (document.Cues.Count == 0)
        {
            return vttPath;
        }

        document.Save(srt);
        var ass = Path.ChangeExtension(vttPath, ".ass");
        var prepared = document.ForReadablePlayback();
        if (prepared.HasStyle || prepared.HasKaraoke)
        {
            File.WriteAllText(ass, prepared.ToAss(revealWords: true));
        }
        else if (File.Exists(ass))
        {
            File.Delete(ass);
        }

        return srt;
    }
}
