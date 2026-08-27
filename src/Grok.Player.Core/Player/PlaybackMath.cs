namespace Grok.Player.Core.Player;

public static class PlaybackMath
{
    public const double MinVolume = 0;
    public const double MaxVolume = 100;

    public static double ClampVolume(double volume)
    {
        if (double.IsNaN(volume) || double.IsInfinity(volume))
        {
            return MinVolume;
        }

        return Math.Clamp(volume, MinVolume, MaxVolume);
    }

    public static TimeSpan ClampPosition(TimeSpan position, TimeSpan? duration)
    {
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (duration is { } limit && limit >= TimeSpan.Zero && position > limit)
        {
            return limit;
        }

        return position;
    }

    public static double ClampSeek(double seconds, TimeSpan? duration, TimeSpan? loopA, TimeSpan? loopB)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            seconds = 0;
        }

        var min = loopA is { } a ? Math.Max(0, a.TotalSeconds) : 0;
        var max = loopB is { } b ? Math.Max(0, b.TotalSeconds) : duration?.TotalSeconds ?? seconds;
        if (duration is { } limit && limit.TotalSeconds >= 0)
        {
            max = Math.Min(max, limit.TotalSeconds);
        }

        if (max < min)
        {
            max = min;
        }

        return Math.Clamp(seconds, min, max);
    }

    public static bool LooksLikeLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("srt://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
