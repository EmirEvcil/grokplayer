namespace Grok.Player.Core.Presentation;

public static class SeekBarMath
{
    public static TimeSpan TimeAt(double pointerX, double trackWidth, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || trackWidth <= 0)
        {
            return TimeSpan.Zero;
        }

        var ratio = Math.Clamp(pointerX / trackWidth, 0, 1);
        return TimeSpan.FromSeconds(ratio * duration.TotalSeconds);
    }

    public static double OffsetForTime(TimeSpan time, TimeSpan duration, double trackWidth)
    {
        if (duration <= TimeSpan.Zero || trackWidth <= 0)
        {
            return 0;
        }

        var ratio = Math.Clamp(time.TotalSeconds / duration.TotalSeconds, 0, 1);
        return ratio * trackWidth;
    }
}
