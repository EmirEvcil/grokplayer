using Grok.Player.Core.Media;

namespace Grok.Player.Core.Subtitles;

public static class StreamCaptionLoader
{
    public static string? Load(string? videoId, string? language, string? captionUrl)
    {
        if (MediaLanguage.IsOff(language) || string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var folder = Path.Combine(Path.GetTempPath(), "GrokPlayer", "captions");
        Directory.CreateDirectory(folder);
        var tag = MediaLanguage.Normalize(language);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var stem = string.IsNullOrWhiteSpace(videoId) ? "stream" : videoId;
        var rawPath = Path.Combine(folder, stem + "." + tag + ".vtt");
        var want = MediaLanguage.Normalize(language);
        foreach (var url in Urls(videoId, language, captionUrl))
        {
            var bytes = YouTubeCatalog.DownloadCaption(url);
            if (bytes is null || bytes.Length < 15)
            {
                continue;
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes);
            if (!LooksLikeCaptions(text))
            {
                continue;
            }

            var header = YouTubeCatalog.CaptionLanguageHeader(text);
            if (want.Length > 0 && !AcceptsLanguage(url, want, header))
            {
                continue;
            }

            return WriteSrt(rawPath, text);
        }

        return Existing(videoId, language);
    }

    public static string? Existing(string? videoId, string? language)
    {
        var tag = MediaLanguage.Normalize(language);
        if (string.IsNullOrWhiteSpace(videoId) || tag.Length == 0)
        {
            return null;
        }

        var srt = Path.Combine(Path.GetTempPath(), "GrokPlayer", "captions", videoId + "." + tag + ".srt");
        return File.Exists(srt) && new FileInfo(srt).Length > 8 ? srt : null;
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

        if (!path.EndsWith(".ass", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var srt = Path.ChangeExtension(path, ".srt");
        return File.Exists(srt) ? srt : path;
    }

    internal static IEnumerable<string> Urls(string? videoId, string? language, string? captionUrl)
    {
        if (MediaLanguage.IsOff(language) || string.IsNullOrWhiteSpace(language))
        {
            yield break;
        }

        var want = MediaLanguage.Normalize(language);
        if (!string.IsNullOrWhiteSpace(captionUrl) &&
            (want.Length == 0 ||
             MediaLanguage.IsOriginal(language) ||
             YouTubeCatalog.CaptionUrlMatches(captionUrl, want)))
        {
            yield return captionUrl;
        }

        if (!string.IsNullOrWhiteSpace(captionUrl) &&
            want.Length > 0 &&
            !MediaLanguage.IsOriginal(language) &&
            !YouTubeCatalog.CaptionUrlMatches(captionUrl, want))
        {
            yield return YouTubeCatalog.WithTranslate(captionUrl, want);
        }

        if (!string.IsNullOrWhiteSpace(videoId) && want.Length > 0 && !MediaLanguage.IsOriginal(language))
        {
            yield return YouTubeCatalog.CaptionVttUrl(videoId, want);
            yield return YouTubeCatalog.CaptionVttUrl(videoId, want + ":asr");
            if (!MediaLanguage.Matches(want, "en"))
            {
                yield return YouTubeCatalog.CaptionVttUrl(videoId, "en") + "&tlang=" + want;
                yield return YouTubeCatalog.CaptionVttUrl(videoId, "en:asr") + "&tlang=" + want;
            }
        }

        if (!string.IsNullOrWhiteSpace(captionUrl) &&
            want.Length == 0)
        {
            yield return captionUrl;
        }
    }

    internal static bool AcceptsLanguage(string url, string want, string? header)
    {
        if (url.Contains("tlang=", StringComparison.OrdinalIgnoreCase) &&
            YouTubeCatalog.CaptionUrlMatches(url, want))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(header))
        {
            return MediaLanguage.Matches(want, header);
        }

        return YouTubeCatalog.CaptionUrlMatches(url, want);
    }

    internal static bool LooksLikeCaptions(string text) =>
        text.Contains("WEBVTT", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("-->", StringComparison.Ordinal);

    internal static string WriteSrt(string vttPath, string text)
    {
        File.WriteAllText(vttPath, text);
        var srt = Path.ChangeExtension(vttPath, ".srt");
        var document = SrtDocument.Parse(text);
        if (document.Cues.Count == 0)
        {
            return vttPath;
        }

        document.Save(srt);
        var ass = Path.ChangeExtension(vttPath, ".ass");
        if (document.HasStyle)
        {
            File.WriteAllText(ass, document.ToAss());
        }
        else if (File.Exists(ass))
        {
            File.Delete(ass);
        }

        return srt;
    }
}
