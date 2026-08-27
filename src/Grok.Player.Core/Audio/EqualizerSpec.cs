namespace Grok.Player.Core.Audio;

public static class EqualizerSpec
{
    public const int BandCount = 10;
    public const double MinUi = -100;
    public const double MaxUi = 100;

    public static readonly double[] FrequenciesHz =
        [60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000];

    public static readonly string[] Labels =
        ["60", "170", "310", "600", "1k", "3k", "6k", "12k", "14k", "16k"];

    public static double ClampUi(double value) => Math.Clamp(value, MinUi, MaxUi);

    public static double ToDb(double ui) => ClampUi(ui) * 0.2;
}
