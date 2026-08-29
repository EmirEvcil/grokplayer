using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Grok.Player.Core.Media;

namespace Grok.Player.Core.Subtitles;

internal static class YouTubeTimedText
{
    public static bool LooksLike(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains("<timedtext", StringComparison.OrdinalIgnoreCase) ||
         (text.Contains("tStartMs", StringComparison.Ordinal) && text.Contains("segs", StringComparison.Ordinal)));

    public static string? ToVtt(string text, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Contains("WEBVTT", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var rolling = string.Equals(MediaLanguage.Kind(language), "asr", StringComparison.OrdinalIgnoreCase);
        var cues = text.Contains("<timedtext", StringComparison.OrdinalIgnoreCase)
            ? FromSrv3(text, rolling)
            : FromJson3(text, rolling);
        return cues.Count == 0 ? null : WriteVtt(cues, language);
    }

    internal static List<(TimeSpan Start, TimeSpan End, string Text)> FromJson3(string text, bool rolling = false)
    {
        var cues = new List<(TimeSpan, TimeSpan, string)>();
        try
        {
            using var document = JsonDocument.Parse(text);
            var pens = new Dictionary<int, Pen>();
            if (document.RootElement.TryGetProperty("pens", out var jsonPens) && jsonPens.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var pen in jsonPens.EnumerateArray())
                {
                    pens[index++] = new Pen(
                        pen.TryGetProperty("fcForeColor", out var color) ? "#" + color.GetInt32().ToString("X6") : null,
                        JsonNumber(pen, "bAttr") == 1, JsonNumber(pen, "iAttr") == 1, JsonNumber(pen, "uAttr") == 1,
                        JsonNumber(pen, "foForeAlpha", 255) == 0);
                }
            }
            var styled = pens.Values.Any(pen => pen.Styled);
            if (!document.RootElement.TryGetProperty("events", out var events) ||
                events.ValueKind != JsonValueKind.Array)
            {
                return cues;
            }

            foreach (var item in events.EnumerateArray())
            {
                if (!item.TryGetProperty("tStartMs", out var startEl) ||
                    !item.TryGetProperty("dDurationMs", out var durEl))
                {
                    continue;
                }

                var start = TimeSpan.FromMilliseconds(startEl.GetInt64());
                var end = start + TimeSpan.FromMilliseconds(Math.Max(1, durEl.GetInt64()));
                var raw = ReadJsonSegs(item, start, pens, styled);
                // Authored QTL captions use multiple lines inside one event.
                // Only ASR rolling windows should discard their older line.
                var line = !styled && rolling ? CurrentPhrase(raw) : raw;
                if (line.Length > 0)
                {
                    cues.Add((start, end, line));
                }
            }
        }
        catch (Exception)
        {
        }

        return cues;
    }

    internal static List<(TimeSpan Start, TimeSpan End, string Text)> FromSrv3(string text, bool rolling = false)
    {
        var cues = new List<(TimeSpan, TimeSpan, string)>();
        try
        {
            var xml = XDocument.Parse(text);
            var pens = xml.Descendants().Where(node => node.Name.LocalName == "pen")
                .Where(node => node.Attribute("id") is not null)
                .GroupBy(node => (int)ReadMs(node, "id"))
                .ToDictionary(group => group.Key, group =>
                {
                    var pen = group.Last();
                    return new Pen(pen.Attribute("fc")?.Value,
                        ReadMs(pen, "b") == 1, ReadMs(pen, "i") == 1, ReadMs(pen, "u") == 1,
                        pen.Attribute("fo")?.Value == "0");
                });
            var styled = pens.Values.Any(pen => pen.Styled);
            foreach (var paragraph in xml.Descendants().Where(node => node.Name.LocalName == "p"))
            {
                var startMs = ReadMs(paragraph, "t");
                var durationMs = ReadMs(paragraph, "d");
                if (durationMs <= 0)
                {
                    continue;
                }

                var start = TimeSpan.FromMilliseconds(startMs);
                var end = start + TimeSpan.FromMilliseconds(durationMs);
                var raw = ReadSrv3Segs(paragraph, start, pens, styled);
                var line = !styled && rolling ? CurrentPhrase(raw) : raw;
                if (line.Length > 0)
                {
                    cues.Add((start, end, line));
                }
            }
        }
        catch (Exception)
        {
        }

        return cues;
    }

    internal static string CurrentPhrase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length > 0)
            {
                return lines[i].TrimEnd();
            }
        }

        return "";
    }

    private static string ReadJsonSegs(JsonElement item, TimeSpan cueStart, Dictionary<int, Pen> pens, bool styled)
    {
        if (!item.TryGetProperty("segs", out var segs) || segs.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var builder = new StringBuilder();
        var lineStart = true;
        foreach (var seg in segs.EnumerateArray())
        {
            var word = seg.TryGetProperty("utf8", out var utf) ? utf.GetString() ?? "" : "";
            if (word == "\n")
            {
                builder.Append('\n');
                lineStart = true;
                continue;
            }

            if (word.Length == 0)
            {
                continue;
            }

            var pen = pens.GetValueOrDefault(JsonNumber(seg, "pPenId", JsonNumber(item, "pPenId", -1)));
            if (pen.Hidden) continue;
            if (!styled && !lineStart && seg.TryGetProperty("tOffsetMs", out var offset))
            {
                builder.Append('<');
                builder.Append(Stamp(cueStart + TimeSpan.FromMilliseconds(offset.GetInt64())));
                builder.Append('>');
            }

            builder.Append(pen.Mark(word));
            lineStart = false;
        }

        return builder.ToString();
    }

    private static string ReadSrv3Segs(XElement paragraph, TimeSpan cueStart, Dictionary<int, Pen> pens, bool styled)
    {
        var parts = paragraph.Elements().Where(node => node.Name.LocalName == "s").ToList();
        if (parts.Count == 0)
        {
            return pens.GetValueOrDefault((int)ReadMs(paragraph, "p")).Mark(paragraph.Value);
        }

        var builder = new StringBuilder();
        var lineStart = true;
        foreach (var part in parts)
        {
            var word = part.Value;
            var offset = ReadMs(part, "t");
            if (word == "\n")
            {
                builder.Append('\n');
                lineStart = true;
                continue;
            }

            var penId = part.Attribute("p") is not null ? ReadMs(part, "p") : ReadMs(paragraph, "p");
            var pen = pens.GetValueOrDefault((int)penId);
            if (pen.Hidden) continue;
            if (!styled && !lineStart && offset > 0)
            {
                builder.Append('<');
                builder.Append(Stamp(cueStart + TimeSpan.FromMilliseconds(offset)));
                builder.Append('>');
            }

            builder.Append(pen.Mark(word));
            lineStart = false;
        }

        return builder.ToString();
    }

    private static int JsonNumber(JsonElement element, string name, int fallback = 0) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : fallback;

    private readonly record struct Pen(string? Color, bool Bold, bool Italic, bool Underline, bool Hidden)
    {
        public bool Styled => !Hidden && (Color is not null || Bold || Italic || Underline);
        public string Mark(string text)
        {
            if (Hidden) return "";
            var safe = text.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
            if (Underline) safe = "<u>" + safe + "</u>";
            if (Italic) safe = "<i>" + safe + "</i>";
            if (Bold) safe = "<b>" + safe + "</b>";
            if (Color is { Length: 7 } && Color[0] == '#' && Color.AsSpan(1).ToString().All(Uri.IsHexDigit))
                safe = "<font color=\"" + Color + "\">" + safe + "</font>";
            return safe;
        }
    }

    private static long ReadMs(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : 0;
    }

    private static string Stamp(TimeSpan time)
    {
        var clamped = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)clamped.TotalHours:00}:{clamped.Minutes:00}:{clamped.Seconds:00}.{clamped.Milliseconds:000}");
    }

    private static string WriteVtt(List<(TimeSpan Start, TimeSpan End, string Text)> cues, string? language)
    {
        var builder = new StringBuilder();
        builder.Append("WEBVTT\n");
        var lang = MediaLanguage.Normalize(language);
        if (lang.Length > 0)
        {
            builder.Append("Language: ");
            builder.Append(lang);
            builder.Append('\n');
        }

        builder.Append('\n');
        foreach (var cue in cues)
        {
            builder.Append(Stamp(cue.Start));
            builder.Append(" --> ");
            builder.Append(Stamp(cue.End));
            builder.Append('\n');
            builder.Append(cue.Text.TrimEnd());
            builder.Append("\n\n");
        }

        return builder.ToString();
    }
}
