using System.Text.RegularExpressions;

namespace Grok.Player.Core.Media;

public static class MediaOrder
{
    private static readonly Regex Series = new(
        @"^(.*?)[\s._-]*[\[\(]?(?:s(\d{1,2})[\s._-]*e(\d{1,3})|(\d{1,2})\s*[x×]\s*(\d{1,3}))[\]\)]?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FilmNum = new(
        @"^(.*?)[\s._-]+(?:part|pt|bölüm|bolum|episode|ep)?[\s._-]*(\d{1,3})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int Compare(string left, string right)
    {
        var a = Key(left);
        var b = Key(right);
        var bySeries = string.CompareOrdinal(a.Series, b.Series);
        if (bySeries != 0) return bySeries;
        var bySeason = a.Season.CompareTo(b.Season);
        if (bySeason != 0) return bySeason;
        var byEpisode = a.Episode.CompareTo(b.Episode);
        if (byEpisode != 0) return byEpisode;
        var byFilm = a.Film.CompareTo(b.Film);
        if (byFilm != 0) return byFilm;
        return string.Compare(a.Raw, b.Raw, StringComparison.OrdinalIgnoreCase);
    }

    public static List<T> SortByTitle<T>(IEnumerable<T> items, Func<T, string> titleOf) =>
        items.OrderBy(item => item, Comparer<T>.Create((a, b) => Compare(titleOf(a), titleOf(b)))).ToList();

    private static SortKey Key(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name).Trim();
        var series = Series.Match(stem);
        if (series.Success)
        {
            var season = Parse(series.Groups[2].Value, series.Groups[4].Value);
            var episode = Parse(series.Groups[3].Value, series.Groups[5].Value);
            var prefix = series.Groups[1].Value.Trim().ToLowerInvariant();
            return new SortKey(string.IsNullOrWhiteSpace(prefix) ? stem.ToLowerInvariant() : prefix, season, episode, 0, stem);
        }

        var film = FilmNum.Match(stem);
        if (film.Success && film.Groups[1].Value.Trim().Length >= 2)
        {
            return new SortKey(
                film.Groups[1].Value.Trim().ToLowerInvariant(),
                0,
                0,
                int.TryParse(film.Groups[2].Value, out var n) ? n : 0,
                stem);
        }

        return new SortKey("", 0, 0, 0, stem);
    }

    private static int Parse(string first, string second) =>
        int.TryParse(string.IsNullOrWhiteSpace(first) ? second : first, out var value) ? value : 0;

    private readonly record struct SortKey(string Series, int Season, int Episode, int Film, string Raw);
}
