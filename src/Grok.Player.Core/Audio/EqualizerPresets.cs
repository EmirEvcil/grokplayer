namespace Grok.Player.Core.Audio;

public static class EqualizerPresets
{
    public const string DefaultName = "Default";

    public static EqualizerPreset Default { get; } = Built("Default", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public static IReadOnlyList<EqualizerPreset> BuiltIn { get; } =
    [
        Default,
        Built("Classical", 0, 0, 0, 0, 0, 0, -37, -37, -37, -47),
        Built("Club", 0, 0, 40, 28, 28, 28, 15, 0, 0, 0),
        Built("Dance", 48, 36, 12, 0, 0, -28, -37, -37, 0, 0),
        Built("Full bass", 48, 48, 48, 28, 8, -20, -40, -52, -56, -56),
        Built("Full bass and treble", 37, 28, 0, -36, -24, 8, 40, 56, 64, 64),
        Built("Full treble", -48, -48, -48, -20, 12, 56, 80, 80, 80, 83),
        Built("Laptop", 24, 56, 28, -17, -12, 8, 20, 48, 64, 72),
        Built("Large hall", 52, 52, 28, 28, 0, -24, -24, -24, 0, 0),
        Built("Live", -24, 0, 20, 28, 28, 28, 20, 12, 12, 12),
        Built("Loudness", 50, 33, 0, 0, -17, 0, -8, -17, 42, 50),
        Built("Party", 36, 36, 0, 0, 0, 0, 0, 0, 36, 36),
        Built("Pop", -8, 24, 36, 40, 28, -4, -12, -12, -8, -8),
        Built("Reggae", 0, 0, 0, -28, 0, 32, 32, 0, 0, 0),
        Built("Rock", 40, 24, -28, -40, -17, 20, 44, 56, 56, 56),
        Built("Ska", -12, -24, -20, 0, 20, 28, 44, 48, 56, 48),
        Built("Soft", 24, 8, 0, -12, 0, 20, 40, 48, 56, 60),
        Built("Soft rock", 20, 20, 12, 0, -20, -28, -17, 0, 12, 44),
        Built("Techno", 40, 28, 0, -28, -24, 0, 40, 48, 48, 44)
    ];

    public static EqualizerPreset? FindBuiltIn(string name) =>
        BuiltIn.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool IsBuiltIn(string name) => FindBuiltIn(name) is not null;

    private static EqualizerPreset Built(string name, params double[] bands) => new(name, bands, builtIn: true);
}
