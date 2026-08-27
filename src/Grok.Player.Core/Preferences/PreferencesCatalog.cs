namespace Grok.Player.Core.Preferences;

public static class PreferencesCatalog
{
    public const string DefaultPresetName = "Default preset";

    public static readonly IReadOnlyList<PreferencesPage> Roots =
    [
        new("general", "General"),
        new("downloads", "Downloads"),
        new("playback", "Playback"),
        new("subtitles", "Subtitles"),
        new("device", "Device"),
        new("filters", "Filter management"),
        new("video", "Video",
            new("video-shaders", "Pixel shaders"),
            new("video-3d", "3D mode"),
            new("video-color", "Color spaces"),
            new("video-resize", "Resizing"),
            new("video-deinterlace", "Deinterlacing"),
            new("video-crop", "Extend & crop"),
            new("video-levels", "Levels & balance"),
            new("video-effects", "Effects"),
            new("video-avisynth", "AviSynth"),
            new("video-vapoursynth", "VapourSynth")),
        new("audio", "Audio"),
        new("extensions", "Extensions"),
        new("accessibility", "Accessibility"),
        new("location", "Location"),
        new("connection", "Connection"),
        new("configuration", "Configuration"),
        new("screensaver", "Screensaver")
    ];

    public static PreferencesPage? Find(string id)
    {
        foreach (var root in Roots)
        {
            if (Find(root, id) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    public static IReadOnlyList<PreferencesPage> Search(string query)
    {
        var needle = query.Trim();
        if (needle.Length == 0)
        {
            return Roots;
        }

        var hits = new List<PreferencesPage>();
        foreach (var root in Roots)
        {
            Collect(root, needle, hits);
        }

        return hits;
    }

    public static bool Matches(PreferencesPage page, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        if (page.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return page.Children.Any(child => Matches(child, query));
    }

    private static PreferencesPage? Find(PreferencesPage page, string id)
    {
        if (string.Equals(page.Id, id, StringComparison.Ordinal))
        {
            return page;
        }

        foreach (var child in page.Children)
        {
            if (Find(child, id) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static void Collect(PreferencesPage page, string query, List<PreferencesPage> hits)
    {
        if (page.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            hits.Add(page);
        }

        foreach (var child in page.Children)
        {
            Collect(child, query, hits);
        }
    }
}
