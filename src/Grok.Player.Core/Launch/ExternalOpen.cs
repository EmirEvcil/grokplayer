using Grok.Player.Core.Media;

namespace Grok.Player.Core.Launch;

public sealed class ExternalOpen
{
    public ExternalOpen(string url, string? title = null, StreamKind kind = StreamKind.Unknown, bool play = true, string? audioLang = null, string? subLang = null, int height = 0, string? captionUrl = null, string? referer = null, double? durationSeconds = null, string? soundtrack = null)
    {
        Url = url;
        Title = title;
        Kind = kind;
        Play = play;
        AudioLang = audioLang;
        SubLang = subLang;
        Height = height;
        CaptionUrl = captionUrl;
        Referer = referer;
        DurationSeconds = durationSeconds is > 0 and < 604800 ? durationSeconds : null;
        Soundtrack = string.IsNullOrWhiteSpace(soundtrack) ? null : soundtrack.Trim();
    }

    public string Url { get; }
    public string? Title { get; }
    public StreamKind Kind { get; }
    public bool Play { get; }
    public string? AudioLang { get; }
    public string? SubLang { get; }

    public int Height { get; }

    public string? CaptionUrl { get; }

    public string? Referer { get; }

    public double? DurationSeconds { get; }

    public string? Soundtrack { get; }

    public static bool TryParse(string? raw, out ExternalOpen open)
    {
        open = new ExternalOpen("");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim().Trim('"');
        if (text.StartsWith("grokplayer:", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseProtocol(text, out open);
        }

        if (UrlSanitizer.IsUrl(text) || YouTubeCatalog.IsWatchUrl(text))
        {
            open = new ExternalOpen(text, kind: YouTubeCatalog.IsWatchUrl(text) ? StreamKind.Unknown : StreamKind.Unknown);
            return true;
        }

        return false;
    }

    public static string ToProtocol(string url, string? title = null, StreamKind kind = StreamKind.Unknown, string? audioLang = null, string? subLang = null, int height = 0, string? captionUrl = null, string? referer = null, double? durationSeconds = null, string? soundtrack = null)
    {
        var query = "url=" + Uri.EscapeDataString(url);
        if (!string.IsNullOrWhiteSpace(title))
        {
            query += "&title=" + Uri.EscapeDataString(title);
        }

        if (kind != StreamKind.Unknown)
        {
            query += "&kind=" + kind.ToString().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(audioLang))
        {
            query += "&audio=" + Uri.EscapeDataString(audioLang);
        }

        if (!string.IsNullOrWhiteSpace(subLang))
        {
            query += "&sub=" + Uri.EscapeDataString(subLang);
        }

        if (height > 0)
        {
            query += "&height=" + height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(captionUrl))
        {
            query += "&caption=" + Uri.EscapeDataString(captionUrl);
        }

        if (!string.IsNullOrWhiteSpace(referer))
        {
            query += "&page=" + Uri.EscapeDataString(referer);
        }

        if (durationSeconds is > 0 and < 604800)
        {
            query += "&duration=" + durationSeconds.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(soundtrack))
        {
            query += "&sound=" + Uri.EscapeDataString(soundtrack);
        }

        return "grokplayer://open?" + query;
    }

    private static bool TryParseProtocol(string text, out ExternalOpen open)
    {
        open = new ExternalOpen("");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            var q = text.IndexOf('?', StringComparison.Ordinal);
            if (q < 0)
            {
                return false;
            }

            return TryParseQuery(text[(q + 1)..], out open);
        }

        return TryParseQuery(uri.Query.TrimStart('?'), out open);
    }

    private static bool TryParseQuery(string query, out ExternalOpen open)
    {
        open = new ExternalOpen("");
        string? url = null;
        string? title = null;
        string? audioLang = null;
        string? subLang = null;
        string? captionUrl = null;
        string? referer = null;
        string? soundtrack = null;
        var kind = StreamKind.Unknown;
        var play = true;
        var height = 0;
        double? durationSeconds = null;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var name = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            if (name.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                url = value;
            }
            else if (name.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                title = value;
            }
            else if (name.Equals("kind", StringComparison.OrdinalIgnoreCase))
            {
                kind = value.Equals("live", StringComparison.OrdinalIgnoreCase) ? StreamKind.Live
                    : value.Equals("vod", StringComparison.OrdinalIgnoreCase) ? StreamKind.Vod
                    : StreamKind.Unknown;
            }
            else if (name.Equals("play", StringComparison.OrdinalIgnoreCase))
            {
                play = value is not ("0" or "false" or "no");
            }
            else if (name is "audio" or "lang" or "hl")
            {
                audioLang = MediaLanguage.IsOriginal(value) ? MediaLanguage.Original : MediaLanguage.Normalize(value);
            }
            else if (name is "sub" or "cc")
            {
                subLang = MediaLanguage.IsOff(value)
                    ? "off"
                    : MediaLanguage.IsOriginal(value)
                        ? MediaLanguage.Original
                        : MediaLanguage.Normalize(value, keepKind: true);
            }
            else if (name is "caption" or "ccurl" or "vtt")
            {
                captionUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            else if (name is "page" or "referer" or "referrer")
            {
                referer = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            else if (name is "height" or "quality" or "res")
            {
                if (int.TryParse(value.TrimEnd('p', 'P'), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    height = Download.HlsPlaylist.NormalizeHeight(parsed);
                }
            }
            else if (name is "duration" or "length")
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
                    seconds > 0 &&
                    seconds < 604800)
                {
                    durationSeconds = seconds;
                }
            }
            else if (name is "sound" or "soundtrack" or "audioUrl")
            {
                soundtrack = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        open = new ExternalOpen(url, title, kind, play, audioLang, subLang, height, captionUrl, referer, durationSeconds, soundtrack);
        return true;
    }
}
