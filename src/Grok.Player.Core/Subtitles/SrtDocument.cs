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

    public static SrtDocument Parse(string text) => Parse(text, compact: true);

    public static SrtDocument Parse(string text, bool compact)
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
            var styled = CaptionMarkup.HasStyle(CaptionMarkup.Parse(raw));
            if (!styled && raw.Contains('\n', StringComparison.Ordinal) && KaraokeTime.IsMatch(raw))
            {
                raw = YouTubeTimedText.CurrentPhrase(raw);
            }

            var spans = CaptionMarkup.Parse(raw);
            var block = spans.Count > 0 ? CaptionMarkup.Plain(spans) : CleanMarkup(raw);
            if (block.Length > 0)
            {
                var cue = new SrtCue(cues.Count + 1, start, end, block, spans)
                {
                    Karaoke = styled ? [] : ParseKaraoke(raw, start)
                };
                cues.Add(cue);
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
                if (body.Exists(part => part.Trim().Length > 0))
                {
                    Flush();
                }

                continue;
            }

            if (body.Count == 0 && int.TryParse(line, out _))
            {
                continue;
            }

            body.Add(line);
        }

        Flush();
        return new SrtDocument(compact ? Compact(cues) : cues);
    }

    public bool HasKaraoke => Cues.Any(cue => cue.HasKaraoke);

    public SrtDocument Compacted() => new(Compact(Cues.ToList()));

    public SrtDocument ForDisplay()
    {
        var cleaned = Deduped();
        return cleaned.HasKaraoke ? new SrtDocument(DropFlash(cleaned.Cues.ToList())) : cleaned.Compacted();
    }

    public SrtDocument ForReadablePlayback()
    {
        var source = Cues.OrderBy(cue => cue.Start).ThenBy(cue => cue.Index)
            .Select(cue => SemanticLines(cue)).ToList();
        var ordered = source.Select(cue => cue.WithRange(cue.Start, cue.End)).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = source[i - 1];
            var current = source[i];
            var gap = current.Start - previous.End;
            if (!current.HasKaraoke || !previous.HasKaraoke || gap > TimeSpan.FromMilliseconds(250))
                continue;

            // YouTube's ASR captions are consecutive rolling fragments. Keep
            // the completed fragment on the first line while revealing the
            // current fragment on the second, then roll again at the next cue.
            var text = previous.Text.Trim() + "\n" + current.Text.TrimStart();
            var rolling = new SrtCue(current.Index, current.Start, current.End, text,
                [new CaptionSpan(previous.Text.Trim() + "\n", null), .. current.Spans])
            {
                Karaoke = [(current.Start, previous.Text.Trim() + "\n"), .. current.Karaoke]
            };
            ordered[i] = rolling;
        }
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            var current = ordered[i];
            var next = ordered[i + 1];
            if (current.End > next.Start)
                current.End = next.Start;
        }
        return new SrtDocument(ordered.Where(cue => cue.End > cue.Start));
    }

    private static SrtCue SemanticLines(SrtCue cue)
    {
        var copy = cue.WithRange(cue.Start, cue.End);
        if (cue.HasKaraoke || !SemanticGap.IsMatch(cue.Text))
            return copy;

        var broke = false;
        copy.Text = SemanticGap.Replace(cue.Text, _ =>
        {
            if (broke) return " ";
            broke = true;
            return "\n";
        });
        broke = false;
        copy.Spans = cue.Spans.Select(span => span with
        {
            Text = SemanticGap.Replace(span.Text, _ =>
            {
                if (broke) return " ";
                broke = true;
                return "\n";
            })
        }).ToList();
        return copy;
    }

    public SrtDocument ExpandKaraoke()
    {
        var expanded = new List<SrtCue>();
        foreach (var cue in Cues)
        {
            if (!cue.HasKaraoke)
            {
                expanded.Add(cue);
                continue;
            }

            var words = cue.Karaoke;
            var built = "";
            for (var i = 0; i < words.Count; i++)
            {
                built += words[i].Text;
                var from = words[i].At < cue.Start ? cue.Start : words[i].At;
                var until = i + 1 < words.Count ? words[i + 1].At : cue.End;
                if (until <= from)
                {
                    until = from + TimeSpan.FromMilliseconds(40);
                }

                if (until > cue.End)
                {
                    until = cue.End;
                }

                var text = built.TrimEnd();
                if (text.Length == 0)
                {
                    continue;
                }

                expanded.Add(new SrtCue(0, from, until, text, CaptionMarkup.Parse(text)));
            }
        }

        return new SrtDocument(expanded.Count > 0 ? expanded : Cues);
    }

    internal static IReadOnlyList<(TimeSpan At, string Text)> ParseKaraoke(string raw, TimeSpan cueStart)
    {
        var words = new List<(TimeSpan At, string Text)>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return words;
        }

        var last = 0;
        var at = cueStart;
        foreach (Match match in KaraokeTime.Matches(raw))
        {
            var piece = CleanFragment(raw[last..match.Index]);
            if (piece.Length > 0)
            {
                words.Add((at, piece));
            }

            if (SrtTime.TryParse(match.Value.Trim('<', '>'), out var next))
            {
                at = next;
            }

            last = match.Index + match.Length;
        }

        var tail = CleanFragment(raw[last..]);
        if (tail.Length > 0)
        {
            words.Add((at, tail));
        }

        return words;
    }

    private static string CleanFragment(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var cleaned = Tag.Replace(text, "");
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
        return Spaces.Replace(cleaned, " ");
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
    private static readonly Regex SemanticGap = new(@"[ \t]{2,}", RegexOptions.Compiled);

    public static SrtDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        var text = Decode(bytes);
        return Parse(text, compact: !path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase));
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

    public string ToAss(bool revealWords = false)
    {
        var builder = new StringBuilder();
        builder.Append("[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\nWrapStyle: 0\nScaledBorderAndShadow: yes\n\n");
        builder.Append("[V4+ Styles]\n");
        builder.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
        builder.Append("Style: Default,Segoe UI,56,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2.4,0.8,2,60,60,48,1\n\n");
        builder.Append("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        foreach (var cue in Cues)
        {
            if (IsRollingCue(cue))
            {
                // The completed lower line becomes the upper line while the next
                // phrase appears underneath it. Splitting the two lines avoids
                // moving the newly revealed words together with the old phrase.
                AppendDialogue(builder, cue, 0,
                    "{\\an2\\move(960,1032,960,968,0,150)}",
                    CaptionMarkup.ToAssText([cue.Spans[0] with { Text = cue.Spans[0].Text.TrimEnd() }]));

                var currentSpans = cue.Spans.Skip(1).ToArray();
                var current = revealWords && cue.Karaoke.Count > 1 && !CaptionMarkup.HasStyle(currentSpans)
                    ? KaraokeAss(cue.Karaoke.Skip(1), cue.Start)
                    : CaptionMarkup.ToAssText(currentSpans);
                AppendDialogue(builder, cue, 1, "{\\an2\\pos(960,1032)}", current);
                continue;
            }

            var text = revealWords && cue.HasKaraoke && !CaptionMarkup.HasStyle(cue.Spans)
                ? KaraokeAss(cue.Karaoke, cue.Start)
                : CaptionMarkup.ToAssText(cue.Spans);
            AppendDialogue(builder, cue, 0, "", text);
        }

        return builder.ToString();
    }

    private static bool IsRollingCue(SrtCue cue) =>
        cue.HasKaraoke && cue.Karaoke.Count > 1 && cue.Spans.Count > 1 &&
        cue.Karaoke[0].Text.EndsWith('\n') && cue.Spans[0].Text.EndsWith('\n');

    private static string KaraokeAss(IEnumerable<(TimeSpan At, string Text)> words, TimeSpan cueStart)
    {
        var builder = new StringBuilder();
        foreach (var word in words)
        {
            var at = Math.Max(0, (long)(word.At - cueStart).TotalMilliseconds);
            builder.Append(at == 0 ? "{\\alpha&H00&}" :
                $"{{\\alpha&HFF&\\t({at},{at + 1},\\alpha&H00&)}}");
            builder.Append(CaptionMarkup.ToAssText([new CaptionSpan(word.Text, null)]));
        }
        return builder.ToString();
    }

    private static void AppendDialogue(
        StringBuilder builder,
        SrtCue cue,
        int layer,
        string animation,
        string text)
    {
        builder.Append("Dialogue: ");
        builder.Append(layer);
        builder.Append(',');
        builder.Append(AssTime(cue.Start));
        builder.Append(',');
        builder.Append(AssTime(cue.End));
        builder.Append(",Default,,0,0,0,,");
        builder.Append(animation);
        builder.Append(text);
        builder.Append('\n');
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
            if (IsFlash(cue) &&
                ordered.Any(other =>
                    !ReferenceEquals(other, cue) &&
                    !IsFlash(other) &&
                    (string.Equals(other.Text, cue.Text, StringComparison.Ordinal) ||
                     IsRollingUpdate(other.Text, cue.Text))))
            {
                continue;
            }

            var skip = false;
            for (var i = 0; i < keep.Count; i++)
            {
                var have = keep[i];
                var sameLine = Touches(have, cue) && string.Equals(have.Text, cue.Text, StringComparison.Ordinal);
                var rolling = Touches(have, cue) && IsRollingUpdate(have.Text, cue.Text);
                if (!sameLine && !rolling)
                {
                    continue;
                }

                var start = have.Start < cue.Start ? have.Start : cue.Start;
                var end = have.End > cue.End ? have.End : cue.End;
                var winner = BetterCue(cue, have) ? cue : have;
                keep[i] = winner.WithRange(start, end);
                skip = true;
                break;
            }

            if (skip)
            {
                continue;
            }

            keep.Add(cue);
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

    public SrtDocument Deduped()
    {
        var keep = new List<SrtCue>();
        foreach (var cue in Cues.OrderBy(item => item.Start).ThenBy(item => item.End))
        {
            var match = keep.FindIndex(have =>
                have.Start == cue.Start &&
                have.End == cue.End &&
                string.Equals(have.Text, cue.Text, StringComparison.Ordinal));
            if (match < 0)
            {
                keep.Add(cue);
                continue;
            }

            if (CaptionMarkup.StyleScore(cue.Spans) > CaptionMarkup.StyleScore(keep[match].Spans) ||
                cue.Karaoke.Count > keep[match].Karaoke.Count)
            {
                keep[match] = cue;
            }
        }

        return new SrtDocument(keep);
    }

    internal static IReadOnlyList<SrtCue> DropFlash(IReadOnlyList<SrtCue> cues)
    {
        var list = cues.ToList();
        return list.Where(cue =>
            !IsFlash(cue) ||
            !list.Any(other =>
                !ReferenceEquals(other, cue) &&
                !IsFlash(other) &&
                (string.Equals(other.Text, cue.Text, StringComparison.Ordinal) ||
                 IsRollingUpdate(other.Text, cue.Text)))).ToList();
    }

    private static bool IsFlash(SrtCue cue) => (cue.End - cue.Start).TotalMilliseconds < 50;

    private static bool Touches(SrtCue left, SrtCue right)
    {
        if (Overlaps(left, right))
        {
            return true;
        }

        var gap = left.Start <= right.Start ? right.Start - left.End : left.Start - right.End;
        return gap <= TimeSpan.FromMilliseconds(80);
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
