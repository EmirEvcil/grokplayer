using Grok.Player.Core.Media;

namespace Grok.Player.Core.Tests;

public class MediaOrderTests
{
    [Fact]
    public void Episode_codes_sort_by_season_then_episode()
    {
        var names = new[] { "Show S01E10", "Show S01E02", "Show S02E01", "Show S01E03" };
        Assert.Equal(
            new[] { "Show S01E02", "Show S01E03", "Show S01E10", "Show S02E01" },
            MediaOrder.SortByTitle(names, name => name));
    }

    [Fact]
    public void Numbered_films_sort_by_trailing_number()
    {
        var names = new[] { "Toy Story 2", "Toy Story 10", "Toy Story 1" };
        Assert.Equal(
            new[] { "Toy Story 1", "Toy Story 2", "Toy Story 10" },
            MediaOrder.SortByTitle(names, name => name));
    }
}
