using Grok.Player.Core.Media;

namespace Grok.Player.Core.Download;

public sealed class HlsVariant
{
    public HlsVariant(string url, int width, int height, int bandwidth, string? codecs = null, string? audio = null)
    {
        Url = url;
        Width = width;
        Height = height;
        Bandwidth = bandwidth;
        Codecs = codecs ?? "";
        Audio = audio;
    }

    public string Url { get; }
    public int Width { get; }
    public int Height { get; }
    public int Bandwidth { get; }
    public string Codecs { get; }
    public string? Audio { get; }

    public bool HasAudioCodec =>
        Codecs.Contains("mp4a", StringComparison.OrdinalIgnoreCase) ||
        Codecs.Contains("opus", StringComparison.OrdinalIgnoreCase) ||
        Codecs.Contains("ac-3", StringComparison.OrdinalIgnoreCase) ||
        Codecs.Contains("ec-3", StringComparison.OrdinalIgnoreCase);

    public bool LooksVideoOnly =>
        !HasAudioCodec &&
        (Codecs.Contains("avc1", StringComparison.OrdinalIgnoreCase) ||
         Codecs.Contains("hev1", StringComparison.OrdinalIgnoreCase) ||
         Codecs.Contains("hvc1", StringComparison.OrdinalIgnoreCase) ||
         Codecs.Contains("vp09", StringComparison.OrdinalIgnoreCase) ||
         Codecs.Contains("av01", StringComparison.OrdinalIgnoreCase));
}

public sealed class HlsSegment
{
    public HlsSegment(string url, double duration, long? rangeStart = null, int? rangeLength = null)
    {
        Url = url;
        Duration = duration;
        RangeStart = rangeStart;
        RangeLength = rangeLength;
    }

    public string Url { get; }
    public double Duration { get; }
    public long? RangeStart { get; }
    public int? RangeLength { get; }
}

public static class HlsPlaylist
{
    public static bool IsMaster(string text) =>
        text.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase);

    public static bool IsLive(string text) =>
        !text.Contains("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase) &&
        !text.Contains("PLAYLIST-TYPE:VOD", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<HlsVariant> Variants(string text, string baseUrl)
    {
        var list = new List<HlsVariant>();
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolution = AttributeValue(line, "RESOLUTION");
            var height = 0;
            var width = 0;
            if (!string.IsNullOrWhiteSpace(resolution))
            {
                var bits = resolution.Split('x', 'X');
                if (bits.Length >= 2)
                {
                    int.TryParse(bits[0], out width);
                    int.TryParse(bits[1], out height);
                }
            }

            var bandwidth = 0;
            int.TryParse(AttributeValue(line, "BANDWIDTH"), out bandwidth);
            var uri = NextUri(lines, i + 1);
            if (uri is null)
            {
                continue;
            }

            list.Add(new HlsVariant(
                Resolve(baseUrl, uri),
                width,
                height,
                bandwidth,
                AttributeValue(line, "CODECS"),
                AttributeValue(line, "AUDIO")));
        }

        return list;
    }

    public static HlsVariant? Pick(IReadOnlyList<HlsVariant> variants, int maxHeight, bool preferVideoOnly = false)
    {
        if (variants.Count == 0)
        {
            return null;
        }

        var usable = variants.ToList();
        if (preferVideoOnly)
        {
            var separated = variants
                .Where(item => item.LooksVideoOnly || !string.IsNullOrWhiteSpace(item.Audio))
                .ToList();
            if (separated.Count > 0)
            {
                usable = separated;
            }
        }

        var ordered = usable.OrderBy(item => item.Height).ThenBy(item => item.Bandwidth).ToList();
        if (maxHeight <= 0)
        {
            return ordered[^1];
        }

        return ordered.LastOrDefault(item => item.Height > 0 && item.Height <= maxHeight)
               ?? ordered.LastOrDefault(item => item.Height <= 0 && item.Bandwidth > 0)
               ?? ordered.FirstOrDefault(item => item.Height > 0)
               ?? ordered[0];
    }

    public static int NormalizeHeight(int height)
    {
        if (height <= 0)
        {
            return 0;
        }

        foreach (var cap in new[] { 2160, 1440, 1080, 720, 480, 360, 240, 144 })
        {
            if (height >= cap - 24)
            {
                return cap;
            }
        }

        return height;
    }

    public static string? AudioUri(string text, string baseUrl, string? language, string? group = null, bool fallback = true)
    {
        string? exact = null;
        string? named = null;
        string? prefix = null;
        string? found = null;
        var want = MediaLanguage.Normalize(language);
        var lines = text.Replace("\r", "").Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase) ||
                !line.Contains("TYPE=AUDIO", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(group))
            {
                var id = AttributeValue(line, "GROUP-ID");
                if (!id.Equals(group, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var uri = Quoted(line, "URI") ?? AttributeValue(line, "URI");
            if (string.IsNullOrWhiteSpace(uri))
            {
                continue;
            }

            var resolved = Resolve(baseUrl, uri);
            var lang = AttributeValue(line, "LANGUAGE");
            var name = AttributeValue(line, "NAME");
            if (MediaLanguage.IsOriginal(language))
            {
                if (IsOriginalAudio(line, name))
                {
                    return resolved;
                }

                found ??= !IsDubbedAudio(line, name) ? resolved : found;
                continue;
            }

            if (want.Length > 0 &&
                MediaLanguage.Normalize(lang).Equals(want, StringComparison.OrdinalIgnoreCase))
            {
                exact ??= resolved;
                continue;
            }

            if (MediaLanguage.MatchesName(language, name))
            {
                named ??= resolved;
                continue;
            }

            if (MediaLanguage.Matches(language, lang))
            {
                prefix ??= resolved;
                continue;
            }

            if (fallback && string.IsNullOrWhiteSpace(language))
            {
                found ??= resolved;
                if (line.Contains("DEFAULT=YES", StringComparison.OrdinalIgnoreCase))
                {
                    found = resolved;
                }
            }
        }

        return exact ?? named ?? prefix ?? found;
    }

    internal static bool IsOriginalAudio(string line, string name)
    {
        var hay = line + " " + name;
        return hay.Contains("original", StringComparison.OrdinalIgnoreCase) &&
               !IsDubbedAudio(line, name);
    }

    internal static bool IsDubbedAudio(string line, string name)
    {
        var hay = line + " " + name;
        return hay.Contains("dubbed", StringComparison.OrdinalIgnoreCase) ||
               hay.Contains("acont=dubbed", StringComparison.OrdinalIgnoreCase) ||
               hay.Contains("acont%3Ddubbed", StringComparison.OrdinalIgnoreCase);
    }

    public static string? SubtitleUri(string text, string baseUrl, string? language)
    {
        string? fallback = null;
        var lines = text.Replace("\r", "").Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase) ||
                !line.Contains("TYPE=SUBTITLES", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var uri = Quoted(line, "URI") ?? AttributeValue(line, "URI");
            if (string.IsNullOrWhiteSpace(uri))
            {
                continue;
            }

            var resolved = Resolve(baseUrl, uri);
            fallback ??= resolved;
            var lang = AttributeValue(line, "LANGUAGE");
            var name = AttributeValue(line, "NAME");
            if (MediaLanguage.Matches(language, lang) || MediaLanguage.MatchesName(language, name))
            {
                return resolved;
            }
        }

        return string.IsNullOrWhiteSpace(language) ? fallback : null;
    }

    public static string PinRenditions(string text, string? audioLang, string? subLang)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var type = AttributeValue(line, "TYPE");
            if (type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(audioLang))
                {
                    continue;
                }

                var match = MediaMatches(line, audioLang);
                lines[i] = SetFlag(SetFlag(line, "DEFAULT", match), "AUTOSELECT", match);
            }
            else if (type.Equals("SUBTITLES", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(subLang))
                {
                    lines[i] = SetFlag(SetFlag(line, "DEFAULT", false), "AUTOSELECT", false);
                    continue;
                }

                var match = MediaMatches(line, subLang);
                lines[i] = SetFlag(SetFlag(line, "DEFAULT", match), "AUTOSELECT", match);
            }
        }

        return string.Join('\n', lines);
    }

    public static string WritePinned(string videoId, string text)
    {
        var dir = Path.Combine(Path.GetTempPath(), "GrokPlayer", "hls");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, (string.IsNullOrWhiteSpace(videoId) ? "stream" : videoId) + ".m3u8");
        File.WriteAllText(path, text);
        return path;
    }

    internal static bool MediaMatches(string line, string? language)
    {
        return MediaLanguage.Matches(language, AttributeValue(line, "LANGUAGE")) ||
               MediaLanguage.MatchesName(language, AttributeValue(line, "NAME")) ||
               MediaLanguage.Matches(language, AttributeValue(line, "YT-EXT-AUDIO-CONTENT-ID"));
    }

    internal static string SetFlag(string line, string name, bool on)
    {
        var value = on ? "YES" : "NO";
        var key = name + "=";
        var at = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return line.TrimEnd(',') + "," + name + "=" + value;
        }

        var start = at + key.Length;
        var end = start;
        if (start < line.Length && line[start] == '"')
        {
            end = line.IndexOf('"', start + 1);
            end = end < 0 ? line.Length : end + 1;
        }
        else
        {
            while (end < line.Length && line[end] is not ',')
            {
                end++;
            }
        }

        return line[..at] + name + "=" + value + line[end..];
    }

    public static (string? MapUrl, IReadOnlyList<HlsSegment> Segments) Media(string text, string baseUrl)
    {
        string? map = null;
        var segments = new List<HlsSegment>();
        var lines = text.Replace("\r", "").Split('\n');
        var duration = 0d;
        long? rangeStart = null;
        int? rangeLength = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var uri = Quoted(line, "URI");
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    map = Resolve(baseUrl, uri);
                }

                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = line[8..].Split(',')[0];
                double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out duration);
                continue;
            }

            if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
            {
                ParseByteRange(line[17..], out rangeLength, out rangeStart);
                continue;
            }

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            segments.Add(new HlsSegment(Resolve(baseUrl, line), duration, rangeStart, rangeLength));
            duration = 0;
            rangeStart = null;
            rangeLength = null;
        }

        return (map, segments);
    }

    public static string Resolve(string baseUrl, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) &&
            Uri.TryCreate(root, href, out var combined))
        {
            return combined.ToString();
        }

        return href;
    }

    private static string? NextUri(string[] lines, int start)
    {
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                return line;
            }
        }

        return null;
    }

    internal static string AttributeValue(string line, string name)
    {
        var key = name + "=";
        var at = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return "";
        }

        var start = at + key.Length;
        if (start < line.Length && line[start] == '"')
        {
            var close = line.IndexOf('"', start + 1);
            return close > start ? line[(start + 1)..close] : "";
        }

        var end = start;
        while (end < line.Length && line[end] is not ',')
        {
            end++;
        }

        return line[start..end].Trim();
    }

    private static string? Quoted(string line, string name)
    {
        var key = name + "=\"";
        var at = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var start = at + key.Length;
        var end = line.IndexOf('"', start);
        return end > start ? line[start..end] : null;
    }

    private static void ParseByteRange(string text, out int? length, out long? start)
    {
        length = null;
        start = null;
        var bits = text.Split('@');
        if (int.TryParse(bits[0], out var len))
        {
            length = len;
        }

        if (bits.Length > 1 && long.TryParse(bits[1], out var from))
        {
            start = from;
        }
    }
}
