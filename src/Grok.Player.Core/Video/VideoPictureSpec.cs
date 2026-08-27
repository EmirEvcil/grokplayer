namespace Grok.Player.Core.Video;

public static class VideoPictureSpec
{
    public const double MinUi = 0;
    public const double MaxUi = 100;
    public const double DefaultUi = 50;

    public static double ClampUi(double value) => Math.Clamp(value, MinUi, MaxUi);

    public static double ToMpv(double ui) => (ClampUi(ui) - DefaultUi) * 2;
}
