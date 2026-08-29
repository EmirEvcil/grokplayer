using System.Globalization;

namespace Grok.Player.Core.Preview;

public readonly record struct StoryboardCell(
    string Url,
    int Sheet,
    int Column,
    int Row,
    int CellWidth,
    int CellHeight,
    TimeSpan Time,
    TimeSpan Interval);

public sealed class StoryboardLevel
{
    public StoryboardLevel(
        int index,
        string template,
        int width,
        int height,
        int count,
        int columns,
        int rows,
        int intervalMs)
    {
        Index = index;
        Template = template;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Count = Math.Max(1, count);
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        IntervalMs = Math.Max(0, intervalMs);
    }

    public int Index { get; }
    public string Template { get; }
    public int Width { get; }
    public int Height { get; }
    public int Count { get; }
    public int Columns { get; }
    public int Rows { get; }
    public int IntervalMs { get; }
    public int FramesPerSheet => Columns * Rows;

    public TimeSpan Interval(TimeSpan? duration)
    {
        if (IntervalMs > 0)
        {
            return TimeSpan.FromMilliseconds(IntervalMs);
        }

        if (duration is { } length && length > TimeSpan.Zero && Count > 0)
        {
            return TimeSpan.FromSeconds(length.TotalSeconds / Count);
        }

        return TimeSpan.FromSeconds(10);
    }

    public StoryboardCell? CellAt(TimeSpan time, TimeSpan? duration = null)
    {
        var step = Interval(duration);
        if (step <= TimeSpan.Zero)
        {
            return null;
        }

        var max = Math.Max(0, Count - 1);
        var index = (int)Math.Clamp(Math.Floor(Math.Max(0, time.TotalSeconds) / step.TotalSeconds), 0, max);
        var perSheet = Math.Max(1, FramesPerSheet);
        var sheet = index / perSheet;
        var cell = index % perSheet;
        var url = Template
            .Replace("$M", sheet.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return new StoryboardCell(
            url,
            sheet,
            cell % Columns,
            cell / Columns,
            Width,
            Height,
            TimeSpan.FromSeconds(index * step.TotalSeconds),
            step);
    }
}

public sealed class StoryboardSpec
{
    public StoryboardSpec(IReadOnlyList<StoryboardLevel> levels)
    {
        Levels = levels ?? [];
    }

    public IReadOnlyList<StoryboardLevel> Levels { get; }

    public StoryboardLevel? BestLevel =>
        Levels
            .Where(level => level.Width >= 80)
            .OrderByDescending(level => level.Width)
            .ThenBy(level => level.IntervalMs)
            .FirstOrDefault() ??
        Levels.LastOrDefault();

    public StoryboardLevel? FastLevel =>
        Levels.Where(level => level.Width >= 120)
            .OrderBy(level => Math.Abs(level.Width - 160))
            .ThenBy(level => level.Width)
            .FirstOrDefault() ?? BestLevel;

    public static StoryboardSpec? Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        var text = spec.Trim();
        if (text.Contains('#') && !text.Contains('|') && text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return ParseLive(text);
        }

        var parts = text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !parts[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var levels = new List<StoryboardLevel>();
        for (var i = 1; i < parts.Length; i++)
        {
            var fields = parts[i].Split('#');
            if (fields.Length < 5 ||
                !int.TryParse(fields[0], out var width) ||
                !int.TryParse(fields[1], out var height) ||
                !int.TryParse(fields[2], out var count) ||
                !int.TryParse(fields[3], out var columns) ||
                !int.TryParse(fields[4], out var rows))
            {
                continue;
            }

            var interval = fields.Length > 5 && int.TryParse(fields[5], out var ms) ? ms : 0;
            var name = fields.Length > 6 && !string.IsNullOrWhiteSpace(fields[6]) ? fields[6] : "M$M";
            var sigh = fields.Length > 7 ? fields[7] : "";
            var template = BuildTemplate(parts[0], i - 1, name, sigh);
            if (string.IsNullOrWhiteSpace(template))
            {
                continue;
            }

            levels.Add(new StoryboardLevel(i - 1, template, width, height, count, columns, rows, interval));
        }

        return levels.Count == 0 ? null : new StoryboardSpec(levels);
    }

    public static string? FromPlayerJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("storyboards", out var boards))
            {
                return null;
            }

            if (boards.TryGetProperty("playerStoryboardSpecRenderer", out var vod) &&
                vod.TryGetProperty("spec", out var spec) &&
                spec.GetString() is { Length: > 8 } vodSpec)
            {
                return vodSpec;
            }

            if (boards.TryGetProperty("playerLiveStoryboardSpecRenderer", out var live) &&
                live.TryGetProperty("spec", out var liveSpec))
            {
                return liveSpec.GetString();
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    public StoryboardCell? CellAt(TimeSpan time, TimeSpan? duration = null) =>
        BestLevel?.CellAt(time, duration);

    private static StoryboardSpec? ParseLive(string spec)
    {
        var fields = spec.Split('#');
        if (fields.Length < 1 || !fields[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var width = fields.Length > 1 && int.TryParse(fields[1], out var w) ? w : 160;
        var height = fields.Length > 2 && int.TryParse(fields[2], out var h) ? h : 90;
        var columns = fields.Length > 3 && int.TryParse(fields[3], out var c) ? c : 3;
        var rows = fields.Length > 4 && int.TryParse(fields[4], out var r) ? r : 3;
        return new StoryboardSpec([
            new StoryboardLevel(0, fields[0], width, height, 10_000, columns, rows, 5000)
        ]);
    }

    private static string BuildTemplate(string baseUrl, int level, string name, string sigh)
    {
        var url = baseUrl
            .Replace("$L", level.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$N", name, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(sigh))
        {
            return url;
        }

        if (url.Contains("sigh=", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return url + (url.Contains('?') ? "&" : "?") + "sigh=" + Uri.EscapeDataString(sigh);
    }
}
