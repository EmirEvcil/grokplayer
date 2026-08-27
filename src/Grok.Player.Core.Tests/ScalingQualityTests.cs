using Grok.Player.Core.Player;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Tests.Fakes;
using Grok.Player.Core.Video;

namespace Grok.Player.Core.Tests;

public sealed class ScalingQualityTests
{
    [Fact]
    public void Presets_match_their_names()
    {
        var performance = ScalingQualitySpec.ForPreset(ScalingPreset.Performance);
        Assert.Equal(ScaleKernel.FastBilinear, performance.Upscale);
        Assert.Equal(ScaleKernel.FastBilinear, performance.Downscale);
        Assert.Equal(ScaleKernel.FastBilinear, performance.Chroma);
        Assert.Equal(ScaleStrength.Off, performance.AntiRing);
        Assert.Equal(ScaleStrength.Off, performance.Deband);

        var cinema = ScalingQualitySpec.ForPreset(ScalingPreset.Cinema);
        Assert.Equal(ScaleKernel.Spline, cinema.Upscale);
        Assert.Equal(ScaleKernel.Gaussian, cinema.Downscale);
        Assert.Equal(ScaleStrength.Medium, cinema.AntiRing);
        Assert.Equal(ScaleStrength.Medium, cinema.Deband);

        var sharp = ScalingQualitySpec.ForPreset(ScalingPreset.Sharp);
        Assert.Equal(ScaleKernel.Lanczos, sharp.Upscale);
        Assert.Equal(ScaleStrength.High, sharp.AntiRing);
        Assert.Equal(ScaleStrength.Off, sharp.Deband);

        Assert.Equal(ScalingPreset.Balanced, ScalingQualitySettings.Default.Preset);
        Assert.Equal(ScaleKernel.Bicubic, ScalingQualitySettings.Default.Upscale);
    }

    [Fact]
    public void Kernels_map_to_mpv_filters()
    {
        Assert.Equal("bilinear", ScalingQualitySpec.MpvName(ScaleKernel.FastBilinear));
        Assert.Equal("triangle", ScalingQualitySpec.MpvName(ScaleKernel.Bilinear));
        Assert.Equal("bicubic", ScalingQualitySpec.MpvName(ScaleKernel.Bicubic));
        Assert.Equal("lanczos", ScalingQualitySpec.MpvName(ScaleKernel.Lanczos));
        Assert.Equal("gaussian", ScalingQualitySpec.MpvName(ScaleKernel.Gaussian));
        Assert.Equal("spline36", ScalingQualitySpec.MpvName(ScaleKernel.Spline));
    }

    [Fact]
    public void Manual_edit_switches_preset_to_custom()
    {
        var model = new ScalingQualityModel();
        model.SelectPreset(ScalingPreset.Cinema);
        Assert.Equal(ScalingPreset.Cinema, model.Draft.Preset);
        model.SetUpscale(ScaleKernel.Bilinear);
        Assert.Equal(ScalingPreset.Custom, model.Draft.Preset);
        Assert.Equal(ScaleKernel.Bilinear, model.Draft.Upscale);
        Assert.Equal(ScaleKernel.Gaussian, model.Draft.Downscale);
    }

    [Fact]
    public void Selecting_a_preset_applies_it_immediately()
    {
        var model = new ScalingQualityModel();
        model.SelectPreset(ScalingPreset.Sharp);
        Assert.Equal(ScalingPreset.Sharp, model.Draft.Preset);
        Assert.Equal(ScalingPreset.Sharp, model.Live.Preset);
        Assert.Equal(ScalingPreset.Sharp, model.Applied.Preset);
        Assert.True(model.HasBeenPushed);
    }

    [Fact]
    public void Player_writes_scale_and_deband_not_geometry()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.SetScalingQuality(ScalingQualitySpec.ForPreset(ScalingPreset.Cinema));

        Assert.Contains(fake.Lifecycle, item => item == "property:scale=spline36");
        Assert.Contains(fake.Lifecycle, item => item == "property:dscale=gaussian");
        Assert.Contains(fake.Lifecycle, item => item == "property:cscale=bicubic");
        Assert.Contains(fake.Lifecycle, item => item == "property:scale-antiring=0.6");
        Assert.Contains(fake.Lifecycle, item => item == "property:deband=True");
        Assert.DoesNotContain(fake.Lifecycle, item => item.StartsWith("property:keepaspect=", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.Lifecycle, item => item.StartsWith("property:panscan=", StringComparison.Ordinal));
    }

    [Fact]
    public void View_model_pushes_live_scaling()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.Scaling.SelectPreset(ScalingPreset.Performance);
        Assert.Contains(fake.Lifecycle, item => item == "property:scale=bilinear");
        Assert.Contains(fake.Lifecycle, item => item == "property:deband=False");
    }
}
