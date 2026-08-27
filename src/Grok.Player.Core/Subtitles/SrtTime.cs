using System.Globalization;
using System.Text.RegularExpressions;

namespace Grok.Player.Core.Subtitles;

public static class SrtTime
{
    private static readonly Regex Line = new(
        @"(\d+):(\d+):(\d+)[,.](\d+)\s*-->\s*(\d+):(\d+):(\d+)[,.](\d+)",
        RegexOptions.CultureInvariant);

    private static readonly Regex Instant = new(
        @"^(-)?(\d+):(\d+):(\d+)[,.](\d+)$",
        RegexOptions.CultureInvariant);

    public static bool TryParse(string text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = Instant.Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        var hours = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups[5].Value;
        var ms = int.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
        time = new TimeSpan(0, hours, minutes, seconds, ms);
        if (match.Groups[1].Success)
        {
            time = -time;
        }

        return true;
    }

    public static bool TryParseRange(string line, out TimeSpan start, out TimeSpan end)
    {
        start = TimeSpan.Zero;
        end = TimeSpan.Zero;
        var match = Line.Match(line);
        if (!match.Success)
        {
            return false;
        }

        start = Read(match, 1);
        end = Read(match, 5);
        return true;
    }

    public static string Format(TimeSpan time)
    {
        var signed = ToMs(time);
        var sign = signed < 0 ? "-" : "";
        var ms = Math.Abs(signed);
        var hours = ms / 3_600_000;
        ms %= 3_600_000;
        var minutes = ms / 60_000;
        ms %= 60_000;
        var seconds = ms / 1_000;
        ms %= 1_000;
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{hours:D2}:{minutes:D2}:{seconds:D2}.{ms:D3}");
    }

    public static long ToMs(TimeSpan time) => (long)Math.Round(time.TotalMilliseconds);

    private static TimeSpan Read(Match match, int offset)
    {
        var hours = int.Parse(match.Groups[offset].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[offset + 1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[offset + 2].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups[offset + 3].Value;
        var ms = int.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
        return new TimeSpan(0, hours, minutes, seconds, ms);
    }
}
