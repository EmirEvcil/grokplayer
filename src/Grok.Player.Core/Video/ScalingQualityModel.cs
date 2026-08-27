namespace Grok.Player.Core.Video;

public sealed class ScalingQualityModel
{
    public event Action? Changed;

    public ScalingQualitySettings Applied { get; private set; } = ScalingQualitySettings.Default;

    public ScalingQualitySettings Draft { get; private set; } = ScalingQualitySettings.Default;

    public ScalingQualitySettings Live { get; private set; } = ScalingQualitySettings.Default;

    public bool HasBeenPushed { get; private set; }

    public void SelectPreset(ScalingPreset preset)
    {
        var next = preset == ScalingPreset.Custom
            ? Draft with { Preset = ScalingPreset.Custom }
            : ScalingQualitySpec.ForPreset(preset);
        if (next == Draft)
        {
            return;
        }

        Push(next);
    }

    public void SetUpscale(ScaleKernel kernel) => Patch(Draft with { Upscale = kernel });

    public void SetDownscale(ScaleKernel kernel) => Patch(Draft with { Downscale = kernel });

    public void SetChroma(ScaleKernel kernel) => Patch(Draft with { Chroma = kernel });

    public void SetAntiRing(ScaleStrength strength) => Patch(Draft with { AntiRing = strength });

    public void SetDeband(ScaleStrength strength) => Patch(Draft with { Deband = strength });

    private void Patch(ScalingQualitySettings next)
    {
        Push(next with { Preset = ScalingPreset.Custom });
    }

    private void Push(ScalingQualitySettings next)
    {
        if (next == Draft && next == Live && next == Applied)
        {
            return;
        }

        Draft = next;
        Applied = next;
        Live = next;
        HasBeenPushed = true;
        Changed?.Invoke();
    }
}
