using System.Text;
using System.Text.RegularExpressions;

namespace Grok.Player.Core.Subtitles;

public sealed class SrtDocument
{
    public SrtDocument() : this([])
    {
    }

    public SrtDocument(IEnumerable<SrtCue> cues)
    {
        Cues = cues.Select((cue, i) =>
        {
            cue.Index = i + 1;
            return cue;
        }).ToList();
    }

    public IList<SrtCue> Cues { get; }

    public static SrtDocument Parse(string text)
    {
        var cues = new List<SrtCue>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SrtDocument(cues);
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        TimeSpan start = default;
        TimeSpan end = default;
        var body = new List<string>();
        var inCue = false;

        void Flush()
        {
            if (!inCue)
            {
                return;
            }

            var raw = string.Join('\n', body);
            var spans = CaptionMarkup.Parse(raw);
            var block = spans.Count > 0 ? CaptionMarkup.Plain(spans) : CleanMarkup(raw);
            if (block.Length > 0)
            {
                cues.Add(new SrtCue(cues.Count + 1, start, end, block, spans));
            }

            body.Clear();
            inCue = false;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (SrtTime.TryParseRange(line, out var nextStart, out var nextEnd))
            {
                Flush();
                start = nextStart;
                end = nextEnd;
                inCue = true;
                continue;
            }

            if (!inCue)
            {
                continue;
            }

            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (body.Count == 0 && int.TryParse(line, out _))
            {
                continue;
            }

            body.Add(line);
        }

        Flush();
        return new SrtDocument(Compact(cues));
    }

    public static string CleanMarkup(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var cleaned = KaraokeTime.Replace(text, "");
        cleaned = Tag.Replace(cleaned, "");
        cleaned = cleaned.Replace("\u200B", "", StringComparison.Ordinal)
            .Replace("\u200C", "", StringComparison.Ordinal)
            .Replace("\u200D", "", StringComparison.Ordinal)
            .Replace("\uFEFF", "", StringComparison.Ordinal)
            .Replace("\u00A0", " ", StringComparison.Ordinal);
        cleaned = cleaned.Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase);
        cleaned = Spaces.Replace(cleaned, " ");
        return cleaned.Trim();
    }

    private static readonly Regex KaraokeTime = new(
        @"<\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Tag = new(
        @"</?(?:c|lang|v|b|i|u|ruby|rt|font)(?:\.[^>\s]*)?(?:\s[^>]*)?>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    public static SrtDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        var text = Decode(bytes);
        return Parse(text);
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, ToSrt(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public string ToSrt()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < Cues.Count; i++)
        {
            var cue = Cues[i];
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(i + 1);
            builder.Append('\n');
            builder.Append(SrtTime.Format(cue.Start).Replace(".", ",", StringComparison.Ordinal));
            builder.Append(" --> ");
            builder.Append(SrtTime.Format(cue.End).Replace(".", ",", StringComparison.Ordinal));
            builder.Append('\n');
            builder.Append(CaptionMarkup.HasStyle(cue.Spans) ? CaptionMarkup.ToMarked(cue.Spans) : cue.Text);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    public bool HasColors => Cues.Any(cue => CaptionMarkup.HasColor(cue.Spans));

    public bool HasStyle => Cues.Any(cue => CaptionMarkup.HasStyle(cue.Spans));

    public string ToAss()
    {
        var builder = new StringBuilder();
        builder.Append("[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\nWrapStyle: 0\nScaledBorderAndShadow: yes\n\n");
        builder.Append("[V4+ Styles]\n");
        builder.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
        builder.Append("Style: Default,Segoe UI,56,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2.4,0.8,2,60,60,48,1\n\n");
        builder.Append("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        foreach (var cue in Cues)
        {
            builder.Append("Dialogue: 0,");
            builder.Append(AssTime(cue.Start));
            builder.Append(',');
            builder.Append(AssTime(cue.End));
            builder.Append(",Default,,0,0,0,,");
            builder.Append(CaptionMarkup.ToAssText(cue.Spans));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string AssTime(TimeSpan time)
    {
        var clamped = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{(int)clamped.TotalHours}:{clamped.Minutes:00}:{clamped.Seconds:00}.{clamped.Milliseconds / 10:00}");
    }

    public SrtDocument Merge(SrtDocument other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var merged = Cues.Select(cue => new SrtCue(cue.Index, cue.Start, cue.End, cue.Text)).ToList();
        foreach (var incoming in other.Cues)
        {
            var match = merged.FirstOrDefault(cue => Overlaps(cue, incoming));
            if (match is not null)
            {
                if (!HasLine(match.Text, incoming.Text))
                {
                    match.Text = match.Text + "\n" + incoming.Text;
                }

                continue;
            }

            merged.Add(new SrtCue(0, incoming.Start, incoming.End, incoming.Text));
        }

        return new SrtDocument(merged.OrderBy(cue => cue.Start).ThenBy(cue => cue.End));
    }

    public SrtCue? CueAt(TimeSpan time)
    {
        foreach (var cue in Cues)
        {
            if (time >= cue.Start && time < cue.End)
            {
                return cue;
            }
        }

        SrtCue? nearest = null;
        var best = TimeSpan.MaxValue;
        foreach (var cue in Cues)
        {
            var delta = time < cue.Start ? cue.Start - time : time - cue.End;
            if (delta < best)
            {
                best = delta;
                nearest = cue;
            }
        }

        return nearest;
    }

    public SrtCue InsertAt(int index, TimeSpan start, TimeSpan end, string text)
    {
        var cue = new SrtCue(0, start, end, text);
        index = Math.Clamp(index, 0, Cues.Count);
        Cues.Insert(index, cue);
        Renumber();
        return cue;
    }

    public bool Remove(SrtCue cue)
    {
        var removed = Cues.Remove(cue);
        if (removed)
        {
            Renumber();
        }

        return removed;
    }

    private void Renumber()
    {
        for (var i = 0; i < Cues.Count; i++)
        {
            Cues[i].Index = i + 1;
        }
    }

    internal static IReadOnlyList<SrtCue> Compact(IReadOnlyList<SrtCue> cues)
    {
        if (cues.Count < 2)
        {
            return cues.ToList();
        }

        var ordered = cues
            .OrderBy(cue => cue.Start)
            .ThenBy(cue => cue.End)
            .ThenByDescending(cue => cue.Text.Length)
            .ThenByDescending(cue => CaptionMarkup.HasStyle(cue.Spans) ? 1 : 0)
            .ToList();
        var keep = new List<SrtCue>();
        foreach (var cue in ordered)
        {
            var replace = -1;
            var skip = false;
            for (var i = 0; i < keep.Count; i++)
            {
                var have = keep[i];
                var sameLine = string.Equals(have.Text, cue.Text, StringComparison.Ordinal);
                var rolling = Overlaps(have, cue) && IsRollingUpdate(have.Text, cue.Text);
                if (!sameLine && !rolling)
                {
                    continue;
                }

                if (BetterCue(cue, have))
                {
                    replace = i;
                }
                else
                {
                    skip = true;
                }

                break;
            }

            if (skip)
            {
                continue;
            }

            if (replace >= 0)
            {
                keep[replace] = cue;
            }
            else
            {
                keep.Add(cue);
            }
        }

        return keep.OrderBy(cue => cue.Start).ThenBy(cue => cue.End).ToList();
    }

    private static bool BetterCue(SrtCue next, SrtCue have)
    {
        if (next.Text.Length != have.Text.Length)
        {
            return next.Text.Length > have.Text.Length;
        }

        return CaptionMarkup.StyleScore(next.Spans) > CaptionMarkup.StyleScore(have.Spans);
    }

    internal static bool IsRollingUpdate(string left, string right)
    {
        var a = Collapse(left);
        var b = Collapse(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
               a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static bool Overlaps(SrtCue left, SrtCue right)
    {
        return left.Start < right.End && right.Start < left.End;
    }

    private static bool HasLine(string existing, string incoming)
    {
        return existing.Split('\n').Any(line =>
            string.Equals(line.Trim(), incoming.Trim(), StringComparison.Ordinal));
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
