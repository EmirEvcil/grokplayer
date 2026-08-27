namespace Grok.Player.Core.Audio;

public sealed class EqualizerPreset
{
    public EqualizerPreset(string name, IReadOnlyList<double> bands, bool builtIn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (bands.Count != EqualizerSpec.BandCount)
        {
            throw new ArgumentException("Preset must have 10 bands.", nameof(bands));
        }

        Name = name.Trim();
        var copy = new double[EqualizerSpec.BandCount];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = EqualizerSpec.ClampUi(bands[i]);
        }

        Bands = copy;
        BuiltIn = builtIn;
    }

    public string Name { get; }

    public IReadOnlyList<double> Bands { get; }

    public bool BuiltIn { get; }

    public bool Matches(IReadOnlyList<double> bands)
    {
        if (bands.Count != Bands.Count)
        {
            return false;
        }

        for (var i = 0; i < Bands.Count; i++)
        {
            if (Math.Abs(Bands[i] - bands[i]) > 0.51)
            {
                return false;
            }
        }

        return true;
    }
}
