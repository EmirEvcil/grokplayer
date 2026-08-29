namespace Grok.Player.Core.Player;

public static class PlaybackSpec
{
    public const double MinSpeed = 0.2;
    public const double MaxSpeed = 12;
    public const double DefaultSpeed = 1;
    public const double SpeedStep = 0.1;
    // The previous 88 plus three 4-point "subtitle down" nudges clamps here.
    public const int DefaultSubtitlePosition = 100;

    public static double ClampSpeed(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return DefaultSpeed;
        }

        return Math.Clamp(Math.Round(value, 1), MinSpeed, MaxSpeed);
    }
}
