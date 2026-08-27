using Grok.Player.Core.Audio;
using Grok.Player.Core.Player;
using Grok.Player.Core.Tests.Fakes;

namespace Grok.Player.Core.Tests;

public sealed class EqualizerTests
{
    [Fact]
    public void Built_in_catalog_has_default_and_named_shapes()
    {
        Assert.Equal(19, EqualizerPresets.BuiltIn.Count);
        Assert.True(EqualizerPresets.IsBuiltIn("Rock"));
        Assert.True(EqualizerPresets.Default.Bands.All(band => Math.Abs(band) < 0.01));
        var rock = EqualizerPresets.FindBuiltIn("Rock");
        Assert.NotNull(rock);
        Assert.True(rock.Bands[0] > 0);
        Assert.True(rock.Bands[3] < 0);
    }

    [Fact]
    public void ToDb_maps_ui_range_to_plus_minus_20()
    {
        Assert.Equal(0, EqualizerSpec.ToDb(0));
        Assert.Equal(20, EqualizerSpec.ToDb(100));
        Assert.Equal(-20, EqualizerSpec.ToDb(-100));
        Assert.Equal(20, EqualizerSpec.ToDb(250));
    }

    [Fact]
    public void Custom_presets_can_be_added_saved_and_deleted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eq-{Guid.NewGuid():N}.json");
        try
        {
            var model = new EqualizerModel(path);
            model.SetBand(0, 40);
            Assert.True(model.AddPreset("My mix"));
            Assert.Equal("My mix", model.SelectedName);
            Assert.True(model.CanSaveSelected);
            model.SetBand(1, -20);
            Assert.True(model.SaveSelected());

            var reloaded = new EqualizerModel(path);
            reloaded.SelectPreset("My mix");
            Assert.Equal(40, reloaded.Bands[0]);
            Assert.Equal(-20, reloaded.Bands[1]);
            Assert.True(reloaded.DeleteSelected());
            Assert.Equal(EqualizerPresets.DefaultName, reloaded.SelectedName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Built_in_presets_cannot_be_saved_or_deleted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eq-{Guid.NewGuid():N}.json");
        try
        {
            var model = new EqualizerModel(path);
            model.SelectPreset("Pop");
            Assert.False(model.CanSaveSelected);
            Assert.False(model.SaveSelected());
            Assert.False(model.DeleteSelected());
            Assert.Equal("Pop", model.SelectedName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Player_writes_lavfi_graph_when_enabled()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        var bands = new double[EqualizerSpec.BandCount];
        bands[0] = 50;
        host.SetEqualizer(true, bands);
        Assert.Contains(fake.Lifecycle, item => item.StartsWith("property:af=lavfi=[") && item.Contains("equalizer=f=60"));
        host.SetEqualizer(false, bands);
        Assert.Contains(fake.Lifecycle, item => item == "property:af=");
    }
}
