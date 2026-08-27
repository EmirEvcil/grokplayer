namespace Grok.Player.Core.Presentation;

public static class TimeDisplay
{
    public const string Unknown = "--:--";

    public static string Format(TimeSpan? time)
    {
        if (time is null)
        {
            return Unknown;
        }

        var value = time.Value;
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var totalSeconds = Math.Floor(value.TotalSeconds);
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds))
        {
            return Unknown;
        }

        var rounded = TimeSpan.FromSeconds(totalSeconds);
        if (rounded.TotalHours >= 1)
        {
            return $"{(int)rounded.TotalHours}:{rounded.Minutes:D2}:{rounded.Seconds:D2}";
        }

        return $"{(int)rounded.TotalMinutes}:{rounded.Seconds:D2}";
    }

    public static string FormatPair(TimeSpan position, TimeSpan? duration) =>
        $"{Format(position)} / {Format(duration)}";

    public static string FormatClock(TimeSpan? time, bool remaining = false)
    {
        if (time is null)
        {
            return remaining ? "--:--:--" : "00:00:00";
        }

        var value = time.Value;
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var totalSeconds = Math.Floor(value.TotalSeconds);
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds))
        {
            return remaining ? "--:--:--" : "00:00:00";
        }

        var rounded = TimeSpan.FromSeconds(totalSeconds);
        return $"{(int)rounded.TotalHours:D2}:{rounded.Minutes:D2}:{rounded.Seconds:D2}";
    }

    public static string FormatSeek(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        var totalSeconds = Math.Floor(time.TotalSeconds);
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds))
        {
            return "00:00";
        }

        var rounded = TimeSpan.FromSeconds(totalSeconds);
        if (rounded.TotalHours >= 1)
        {
            return $"{(int)rounded.TotalHours}:{rounded.Minutes:D2}:{rounded.Seconds:D2}";
        }

        return $"{rounded.Minutes:D2}:{rounded.Seconds:D2}";
    }

    public static string FormatClockPair(TimeSpan position, TimeSpan? duration, bool showRemaining)
    {
        if (showRemaining && duration is { } total)
        {
            var left = total - position;
            if (left < TimeSpan.Zero)
            {
                left = TimeSpan.Zero;
            }

            return $"{FormatClock(left, remaining: true)} / {FormatClock(total)}";
        }

        return $"{FormatClock(position)} / {FormatClock(duration)}";
    }
}
