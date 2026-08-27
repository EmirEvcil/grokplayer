using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Grok.Player.Core.Subtitles;

public readonly record struct CaptionSpan(
    string Text,
    string? Color,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strike = false,
    bool Pre = false,
    bool Quote = false,
    bool Code = false,
    bool Mark = false,
    bool Small = false,
    bool Super = false,
    bool Sub = false)
{
    public bool HasEmphasis =>
        Bold || Italic || Underline || Strike || Pre || Quote || Code || Mark || Small || Super || Sub;
}

public readonly record struct CaptionTagOption(string Id, string Label, string Tag);

public static class CaptionMarkup
{
    private static readonly Regex Karaoke = new(
        @"<\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Tag = new(
        @"</?(?:c|font|b|i|u|em|strong|s|strike|del|pre|q|code|tt|kbd|samp|mark|small|sup|sub|cite|blockquote)(?:\.[^>\s]*)?(?:\s[^>]*)?>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly CaptionTagOption[] TagOptions =
    [
        new("b", "Bold", "b"),
        new("i", "Italic", "i"),
        new("u", "Underline", "u"),
        new("s", "Strikethrough", "s"),
        new("pre", "Preformatted", "pre"),
        new("q", "Quote", "q"),
        new("code", "Code", "code"),
        new("mark", "Highlight", "mark"),
        new("small", "Small", "small"),
        new("sup", "Superscript", "sup"),
        new("sub", "Subscript", "sub")
    ];

    private static readonly Regex HexClass = new(
        @"color([0-9A-Fa-f]{6})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HexAttr = new(
        @"color\s*=\s*[""']?#([0-9A-Fa-f]{6})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = "#FFFFFF",
        ["yellow"] = "#FFFF00",
        ["cyan"] = "#00FFFF",
        ["aqua"] = "#00FFFF",
        ["lime"] = "#00FF00",
        ["green"] = "#00FF00",
        ["magenta"] = "#FF00FF",
        ["fuchsia"] = "#FF00FF",
        ["red"] = "#FF0000",
        ["blue"] = "#0000FF",
        ["black"] = "#000000",
        ["orange"] = "#FFA500",
        ["pink"] = "#FFC0CB",
        ["purple"] = "#800080"
    };

    private readonly record struct Style(
        string? Color,
        bool Bold,
        bool Italic,
        bool Underline,
        bool Strike,
        bool Pre,
        bool Quote,
        bool Code,
        bool Mark,
        bool Small,
        bool Super,
        bool Sub);

    public static IReadOnlyList<CaptionSpan> Parse(string? text)
    {
        var raw = Decode(Karaoke.Replace(text ?? "", ""));
        if (raw.Length == 0)
        {
            return [];
        }

        var spans = new List<CaptionSpan>();
        var stack = new Stack<Style>();
        stack.Push(new Style(null, false, false, false, false, false, false, false, false, false, false, false));
        var last = 0;
        foreach (Match match in Tag.Matches(raw))
        {
            Append(spans, raw[last..match.Index], stack.Peek());
            last = match.Index + match.Length;
            ApplyTag(stack, match.Value);
        }

        Append(spans, raw[last..], stack.Peek());
        return Normalize(spans);
    }

    public static string Plain(IEnumerable<CaptionSpan> spans)
    {
        var builder = new StringBuilder();
        foreach (var span in spans)
        {
            builder.Append(span.Text);
        }

        return builder.ToString();
    }

    public static bool HasColor(IEnumerable<CaptionSpan> spans) =>
        spans.Any(span => !string.IsNullOrWhiteSpace(span.Color));

    public static bool HasStyle(IEnumerable<CaptionSpan> spans) =>
        spans.Any(span => !string.IsNullOrWhiteSpace(span.Color) || span.HasEmphasis);

    public static int StyleScore(IEnumerable<CaptionSpan> spans)
    {
        var score = 0;
        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
            {
                continue;
            }

            var weight = 0;
            if (!string.IsNullOrWhiteSpace(span.Color))
            {
                weight += 2;
            }

            if (span.Bold)
            {
                weight++;
            }

            if (span.Italic)
            {
                weight++;
            }

            if (span.Underline)
            {
                weight++;
            }

            if (span.Strike || span.Pre || span.Quote || span.Code || span.Mark || span.Small || span.Super || span.Sub)
            {
                weight++;
            }

            score += span.Text.Length * weight;
        }

        return score;
    }

    public static string? ReadColor(string tag)
    {
        var hex = HexClass.Match(tag);
        if (hex.Success)
        {
            return "#" + hex.Groups[1].Value.ToUpperInvariant();
        }

        hex = HexAttr.Match(tag);
        if (hex.Success)
        {
            return "#" + hex.Groups[1].Value.ToUpperInvariant();
        }

        var namedAttr = Regex.Match(tag, @"color\s*=\s*[""']?([A-Za-z]+)", RegexOptions.IgnoreCase);
        if (namedAttr.Success && Named.TryGetValue(namedAttr.Groups[1].Value, out var mapped))
        {
            return mapped;
        }

        var className = Regex.Match(tag, @"c\.([A-Za-z]+)", RegexOptions.IgnoreCase);
        return className.Success && Named.TryGetValue(className.Groups[1].Value, out mapped) ? mapped : null;
    }

    public static string ToAssColor(string? hex)
    {
        var rgb = (hex ?? "#FFFFFF").TrimStart('#');
        if (rgb.Length != 6)
        {
            rgb = "FFFFFF";
        }

        return "&H00" + rgb[4..] + rgb[2..4] + rgb[..2] + "&";
    }

    public static string ToAssText(IEnumerable<CaptionSpan> spans)
    {
        var builder = new StringBuilder();
        string? last = null;
        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
            {
                continue;
            }

            var color = string.IsNullOrWhiteSpace(span.Color) ? "#FFFFFF" : span.Color;
            var key = StyleKey(span, color);
            if (key != last)
            {
                builder.Append("{\\b");
                builder.Append(span.Bold ? '1' : '0');
                builder.Append("\\i");
                builder.Append(span.Italic ? '1' : '0');
                builder.Append("\\u");
                builder.Append(span.Underline ? '1' : '0');
                if (span.Strike)
                {
                    builder.Append("\\s1");
                }

                builder.Append("\\c");
                builder.Append(ToAssColor(color));
                if (span.Small)
                {
                    builder.Append("\\fscx86\\fscy86");
                }
                else if (span.Super || span.Sub)
                {
                    builder.Append("\\fscx72\\fscy72");
                }

                if (span.Pre || span.Code)
                {
                    builder.Append("\\q2\\fsp1");
                }

                if (span.Mark)
                {
                    builder.Append("\\bord1.4\\3c&H00A089D9&");
                }

                builder.Append('}');
                last = key;
            }

            if (span.Quote)
            {
                builder.Append('“');
            }

            builder.Append(EscapeAss(span.Text));
            if (span.Quote)
            {
                builder.Append('”');
            }
        }

        return builder.ToString();
    }

    public static string ToMarked(IEnumerable<CaptionSpan> spans)
    {
        var builder = new StringBuilder();
        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(span.Color))
            {
                builder.Append("<font color=\"");
                builder.Append(span.Color);
                builder.Append("\">");
            }

            Open(builder, span.Mark, "mark");
            Open(builder, span.Pre, "pre");
            Open(builder, span.Code, "code");
            Open(builder, span.Quote, "q");
            Open(builder, span.Small, "small");
            Open(builder, span.Super, "sup");
            Open(builder, span.Sub, "sub");
            Open(builder, span.Bold, "b");
            Open(builder, span.Italic, "i");
            Open(builder, span.Underline, "u");
            Open(builder, span.Strike, "s");
            builder.Append(span.Text);
            Close(builder, span.Strike, "s");
            Close(builder, span.Underline, "u");
            Close(builder, span.Italic, "i");
            Close(builder, span.Bold, "b");
            Close(builder, span.Sub, "sub");
            Close(builder, span.Super, "sup");
            Close(builder, span.Small, "small");
            Close(builder, span.Quote, "q");
            Close(builder, span.Code, "code");
            Close(builder, span.Pre, "pre");
            Close(builder, span.Mark, "mark");
            if (!string.IsNullOrWhiteSpace(span.Color))
            {
                builder.Append("</font>");
            }
        }

        return builder.ToString();
    }

    internal static IReadOnlyList<CaptionSpan> Normalize(IReadOnlyList<CaptionSpan> spans)
    {
        var merged = new List<CaptionSpan>();
        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
            {
                continue;
            }

            if (merged.Count > 0 && SameStyle(merged[^1], span))
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + span.Text };
                continue;
            }

            if (merged.Count > 0 && string.IsNullOrWhiteSpace(span.Text))
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + span.Text };
                continue;
            }

            merged.Add(span);
        }

        return merged;
    }

    private static bool SameStyle(CaptionSpan left, CaptionSpan right) =>
        string.Equals(left.Color, right.Color, StringComparison.OrdinalIgnoreCase) &&
        left.Bold == right.Bold &&
        left.Italic == right.Italic &&
        left.Underline == right.Underline &&
        left.Strike == right.Strike &&
        left.Pre == right.Pre &&
        left.Quote == right.Quote &&
        left.Code == right.Code &&
        left.Mark == right.Mark &&
        left.Small == right.Small &&
        left.Super == right.Super &&
        left.Sub == right.Sub;

    public static CaptionSpan Combine(IEnumerable<CaptionSpan> spans)
    {
        var list = spans.Where(span => span.Text.Length > 0).ToList();
        if (list.Count == 0)
        {
            return new CaptionSpan("", "#FFFFFF");
        }

        var first = list[0];
        return first with
        {
            Text = "",
            Color = list.Select(span => span.Color).FirstOrDefault(color => !string.IsNullOrWhiteSpace(color)) ?? first.Color,
            Bold = list.Any(span => span.Bold),
            Italic = list.Any(span => span.Italic),
            Underline = list.Any(span => span.Underline),
            Strike = list.Any(span => span.Strike),
            Pre = list.Any(span => span.Pre),
            Quote = list.Any(span => span.Quote),
            Code = list.Any(span => span.Code),
            Mark = list.Any(span => span.Mark),
            Small = list.Any(span => span.Small),
            Super = list.Any(span => span.Super),
            Sub = list.Any(span => span.Sub)
        };
    }

    public static IReadOnlyList<string> SelectedTags(CaptionSpan span)
    {
        var tags = new List<string>();
        if (span.Bold)
        {
            tags.Add("b");
        }

        if (span.Italic)
        {
            tags.Add("i");
        }

        if (span.Underline)
        {
            tags.Add("u");
        }

        if (span.Strike)
        {
            tags.Add("s");
        }

        if (span.Pre)
        {
            tags.Add("pre");
        }

        if (span.Quote)
        {
            tags.Add("q");
        }

        if (span.Code)
        {
            tags.Add("code");
        }

        if (span.Mark)
        {
            tags.Add("mark");
        }

        if (span.Small)
        {
            tags.Add("small");
        }

        if (span.Super)
        {
            tags.Add("sup");
        }

        if (span.Sub)
        {
            tags.Add("sub");
        }

        return tags;
    }

    public static CaptionSpan WithTags(string text, string? color, IEnumerable<string> tags)
    {
        var set = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        return new CaptionSpan(
            text,
            color,
            Bold: set.Contains("b") || set.Contains("strong"),
            Italic: set.Contains("i") || set.Contains("em") || set.Contains("cite"),
            Underline: set.Contains("u"),
            Strike: set.Contains("s") || set.Contains("strike") || set.Contains("del"),
            Pre: set.Contains("pre"),
            Quote: set.Contains("q") || set.Contains("blockquote"),
            Code: set.Contains("code") || set.Contains("tt") || set.Contains("kbd") || set.Contains("samp"),
            Mark: set.Contains("mark"),
            Small: set.Contains("small"),
            Super: set.Contains("sup"),
            Sub: set.Contains("sub"));
    }

    private static void ApplyTag(Stack<Style> stack, string tag)
    {
        if (tag.StartsWith("</", StringComparison.Ordinal))
        {
            if (stack.Count > 1)
            {
                stack.Pop();
            }

            return;
        }

        var current = stack.Peek();
        var name = TagName(tag);
        stack.Push(name switch
        {
            "b" or "strong" => current with { Bold = true },
            "i" or "em" or "cite" => current with { Italic = true },
            "u" => current with { Underline = true },
            "s" or "strike" or "del" => current with { Strike = true },
            "pre" => current with { Pre = true },
            "q" or "blockquote" => current with { Quote = true },
            "code" or "tt" or "kbd" or "samp" => current with { Code = true },
            "mark" => current with { Mark = true },
            "small" => current with { Small = true },
            "sup" => current with { Super = true },
            "sub" => current with { Sub = true },
            _ => current with { Color = ReadColor(tag) ?? current.Color }
        });
    }

    private static string TagName(string tag)
    {
        var i = tag.StartsWith("</", StringComparison.Ordinal) ? 2 : 1;
        var end = i;
        while (end < tag.Length && char.IsAsciiLetter(tag[end]))
        {
            end++;
        }

        return tag[i..end].ToLowerInvariant();
    }

    private static void Append(List<CaptionSpan> spans, string text, Style style)
    {
        if (text.Length == 0)
        {
            return;
        }

        spans.Add(new CaptionSpan(
            text.Replace('\t', ' '),
            style.Color,
            style.Bold,
            style.Italic,
            style.Underline,
            style.Strike,
            style.Pre,
            style.Quote,
            style.Code,
            style.Mark,
            style.Small,
            style.Super,
            style.Sub));
    }

    private static string StyleKey(CaptionSpan span, string color) =>
        color +
        (span.Bold ? "B" : "") +
        (span.Italic ? "I" : "") +
        (span.Underline ? "U" : "") +
        (span.Strike ? "S" : "") +
        (span.Pre ? "P" : "") +
        (span.Quote ? "Q" : "") +
        (span.Code ? "C" : "") +
        (span.Mark ? "M" : "") +
        (span.Small ? "A" : "") +
        (span.Super ? "^" : "") +
        (span.Sub ? "_" : "");

    private static void Open(StringBuilder builder, bool on, string tag)
    {
        if (on)
        {
            builder.Append('<').Append(tag).Append('>');
        }
    }

    private static void Close(StringBuilder builder, bool on, string tag)
    {
        if (on)
        {
            builder.Append("</").Append(tag).Append('>');
        }
    }

    private static string Decode(string text)
    {
        return text
            .Replace("\u200B", "", StringComparison.Ordinal)
            .Replace("\u200C", "", StringComparison.Ordinal)
            .Replace("\u200D", "", StringComparison.Ordinal)
            .Replace("\uFEFF", "", StringComparison.Ordinal)
            .Replace("\u00A0", " ", StringComparison.Ordinal)
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeAss(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("{", "(", StringComparison.Ordinal)
            .Replace("}", ")", StringComparison.Ordinal)
            .Replace("\n", "\\N", StringComparison.Ordinal);
    }
}
