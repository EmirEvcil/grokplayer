using System.Globalization;
using System.Text.RegularExpressions;

namespace Grok.Player.Core.Media;

public static class MediaLanguage
{
    public const string Original = "original";

    private static readonly Regex CodePattern = new(
        @"^(?:\.?a?[.\-])?([A-Za-z]{2,3})(?:[-_]([A-Za-z]{2,8}))?(?:\.\d+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TagPattern = new(
        @"(?:^|[;:&?])lang=([A-Za-z]{2,3}(?:[-_][A-Za-z]{2,8})?)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Lazy<IReadOnlyList<(string Code, string[] Names)>> Cultures = new(BuildCultures);

    public static bool IsOriginal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            text = text[..colon];
        }

        return text.Equals(Original, StringComparison.OrdinalIgnoreCase) ||
               text.Equals("orig", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("und", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOff(string? value) =>
        value is "off" or "0" or "no" or "false";

    public static string Normalize(string? value, bool keepKind = false)
    {
        if (string.IsNullOrWhiteSpace(value) || IsOff(value))
        {
            return "";
        }

        if (IsOriginal(value))
        {
            return Original;
        }

        var text = value.Trim();
        var kind = Kind(text);
        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            text = text[..colon];
        }

        var tagged = TagPattern.Match(text);
        if (tagged.Success)
        {
            text = tagged.Groups[1].Value;
        }

        var code = ParseCode(text);
        if (code.Length == 0)
        {
            code = FromName(text);
        }

        if (code.Length == 0)
        {
            var i = 0;
            while (i < text.Length && char.IsAsciiLetter(text[i]))
            {
                i++;
            }

            code = i is 2 or 3 ? text[..i].ToLowerInvariant() : "";
        }

        if (keepKind && !string.IsNullOrWhiteSpace(kind) && code.Length > 0)
        {
            return code + ":" + kind.ToLowerInvariant();
        }

        return IsPlausible(code) ? code : "";
    }

    public static bool IsPlausible(string? value)
    {
        if (IsOriginal(value))
        {
            return true;
        }

        var code = value ?? "";
        var colon = code.IndexOf(':');
        if (colon > 0)
        {
            code = code[..colon];
        }

        var dash = code.IndexOf('-');
        if (dash == 2 || dash == 3)
        {
            var script = code[(dash + 1)..];
            return script.Length == 4 && script.All(char.IsAsciiLetter);
        }

        if (code.Length is not (2 or 3))
        {
            return false;
        }

        return code is not ("alt" or "sub" or "cc" or "cap" or "aud" or "ses" or "ori" or "off" or "lab");
    }

    public static string FromName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var lower = value.ToLowerInvariant();
        if (lower.Contains("türk", StringComparison.Ordinal) ||
            lower.Contains("turk", StringComparison.Ordinal))
        {
            return "tr";
        }

        if (lower.Contains("english", StringComparison.Ordinal) ||
            lower.Contains("ingiliz", StringComparison.Ordinal) ||
            lower.Contains("ngiliz", StringComparison.Ordinal))
        {
            return "en";
        }

        if (lower.Contains("arab", StringComparison.Ordinal) ||
            lower.Contains("arap", StringComparison.Ordinal) ||
            lower.Contains("عربي", StringComparison.Ordinal))
        {
            return "ar";
        }

        return MatchCulture(value)?.Code ?? "";
    }

    public static string? Kind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var colon = value.IndexOf(':');
        return colon > 0 && colon < value.Length - 1 ? value[(colon + 1)..].Trim() : null;
    }

    public static bool Matches(string? requested, string? available)
    {
        var want = Normalize(requested);
        var have = Normalize(available);
        if (want.Length == 0 || have.Length == 0)
        {
            return false;
        }

        if (want.Equals(have, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var wantScript = Script(want);
        var haveScript = Script(have);
        if (wantScript.Length > 0 && haveScript.Length > 0 &&
            !wantScript.Equals(haveScript, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (want.Length >= 2 && have.Length >= 2 &&
            want[..2].Equals(have[..2], StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SameIsoLanguage(want, have);
    }

    public static string DisplayName(string? value)
    {
        var code = Normalize(value);
        if (code.Length == 0)
        {
            return "";
        }

        if (Matches(code, "tr"))
        {
            return "Türkçe";
        }

        if (Matches(code, "en"))
        {
            return "English";
        }

        try
        {
            var name = CultureInfo.GetCultureInfo(code).EnglishName;
            var cut = name.IndexOf('(');
            return cut > 0 ? name[..cut].Trim() : name;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    public static string ShortCode(string? value)
    {
        var lang = Normalize(value, keepKind: true);
        if (lang.Length == 0 || IsOriginal(lang))
        {
            return "";
        }

        var kind = Kind(lang);
        var core = FoldIso(Normalize(lang));
        return string.IsNullOrWhiteSpace(kind) ? core : core + ":" + kind;
    }

    private static bool SameIsoLanguage(string left, string right) =>
        FoldIso(left) == FoldIso(right);

    internal static string FoldIso(string value)
    {
        value = value.Trim().ToLowerInvariant();
        return value switch
        {
            "ger" or "deu" => "de",
            "tur" or "trk" => "tr",
            "eng" => "en",
            "fra" or "fre" => "fr",
            "spa" => "es",
            "ita" => "it",
            "por" => "pt",
            "jpn" => "ja",
            "kor" => "ko",
            "chi" or "zho" => "zh",
            "ara" => "ar",
            "rus" => "ru",
            _ => value.Length > 2 ? value[..2] : value
        };
    }

    public static bool MatchesName(string? requested, string? name)
    {
        var want = Normalize(requested);
        if (want.Length == 0 || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (Matches(want, name))
        {
            return true;
        }

        var lower = name.ToLowerInvariant();
        if (IsOriginal(requested) || want == Original)
        {
            return lower.Contains("original", StringComparison.Ordinal) &&
                   !lower.Contains("dub", StringComparison.Ordinal);
        }

        if (CultureLabels(want).Any(label =>
                label.Length >= 4 &&
                (lower.Contains(label, StringComparison.Ordinal) || label.Contains(lower, StringComparison.Ordinal))))
        {
            return true;
        }

        var culture = MatchCulture(name);
        if (culture is not null && Matches(want, culture.Value.Code))
        {
            return true;
        }

        return want switch
        {
            "tr" => lower.Contains("türk", StringComparison.Ordinal) ||
                    lower.Contains("turk", StringComparison.Ordinal),
            "en" => lower.Contains("english", StringComparison.Ordinal) ||
                    lower.Contains("ingiliz", StringComparison.Ordinal) ||
                    lower.Contains("ngiliz", StringComparison.Ordinal),
            "ar" => lower.Contains("arab", StringComparison.Ordinal) ||
                    lower.Contains("arap", StringComparison.Ordinal) ||
                    lower.Contains("عربي", StringComparison.Ordinal),
            "bn" => lower.Contains("bangla", StringComparison.Ordinal) ||
                    lower.Contains("bengali", StringComparison.Ordinal),
            _ => false
        };
    }

    private static string ParseCode(string text)
    {
        var match = CodePattern.Match(text.Trim());
        if (!match.Success)
        {
            return "";
        }

        var lang = match.Groups[1].Value.ToLowerInvariant();
        var extra = match.Groups[2].Value;
        if (string.IsNullOrEmpty(extra))
        {
            return lang;
        }

        if (extra.Length == 4)
        {
            return lang + "-" + char.ToUpperInvariant(extra[0]) + extra[1..].ToLowerInvariant();
        }

        return lang;
    }

    private static string Script(string code)
    {
        var dash = code.IndexOf('-');
        return dash > 0 ? code[(dash + 1)..] : "";
    }

    private static (string Code, string[] Names)? MatchCulture(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        if (lower.Length < 3)
        {
            return null;
        }

        (string Code, string[] Names)? best = null;
        var bestLen = 0;
        foreach (var item in Cultures.Value)
        {
            foreach (var label in item.Names)
            {
                if (label.Length < 4)
                {
                    continue;
                }

                if ((lower.Contains(label, StringComparison.Ordinal) ||
                     label.Contains(lower, StringComparison.Ordinal)) &&
                    label.Length > bestLen)
                {
                    best = item;
                    bestLen = label.Length;
                }
            }
        }

        return best;
    }

    private static IReadOnlyList<(string Code, string[] Names)> BuildCultures()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures))
        {
            if (string.IsNullOrWhiteSpace(culture.Name) || culture.Name.Length < 2)
            {
                continue;
            }

            var code = ParseCode(culture.Name);
            if (string.IsNullOrEmpty(code))
            {
                code = culture.TwoLetterISOLanguageName.ToLowerInvariant();
            }

            if (!IsPlausible(code) || code == Original)
            {
                continue;
            }

            if (!map.TryGetValue(code, out var names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                map[code] = names;
            }

            AddName(names, culture.EnglishName);
            AddName(names, culture.NativeName);
            AddName(names, culture.DisplayName);
            if (code == "bn")
            {
                names.Add("bangla");
                names.Add("bengali");
            }
        }

        if (!map.TryGetValue("bn", out var bangla))
        {
            bangla = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            map["bn"] = bangla;
        }

        bangla.Add("bangla");
        bangla.Add("bengali");

        return map
            .Select(pair => (pair.Key, pair.Value.ToArray()))
            .ToArray();
    }

    private static IEnumerable<string> CultureLabels(string code)
    {
        var labels = new List<string>();
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            labels.Add(culture.EnglishName.ToLowerInvariant());
            labels.Add(culture.NativeName.ToLowerInvariant());
            labels.Add(culture.DisplayName.ToLowerInvariant());
        }
        catch (CultureNotFoundException)
        {
        }

        if (code == "bn")
        {
            labels.Add("bangla");
            labels.Add("bengali");
        }

        return labels;
    }

    private static void AddName(HashSet<string> names, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var lower = value.Trim().ToLowerInvariant();
        names.Add(lower);
        var cut = lower.IndexOf('(');
        if (cut > 3)
        {
            names.Add(lower[..cut].Trim());
        }
    }
}
