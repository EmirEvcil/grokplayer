using Grok.Player.Core.Audio;
using Grok.Player.Core.Player;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.IntegrationTests.Support;

namespace Grok.Player.Core.IntegrationTests;

public sealed class LibMpvEqualizerTests
{
    [LibMpvFact]
    public void Equalizer_graph_applies_and_clears_on_a_live_handle()
    {
        using var host = PlayerHost.CreateHeadless();
        var rock = EqualizerPresets.FindBuiltIn("Rock") ?? throw new InvalidOperationException("missing Rock");
        host.SetEqualizer(true, rock.Bands);

        var filter = host.GetAudioFilter() ?? "";
        Assert.False(string.IsNullOrWhiteSpace(filter), "af should be set");
        Assert.Contains("equalizer", filter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("60", filter);

        host.SetEqualizer(false, rock.Bands);
        var cleared = host.GetAudioFilter() ?? "";
        Assert.True(
            string.IsNullOrWhiteSpace(cleared) ||
            cleared is "none" or "[]" or "null",
            $"af after disable={cleared}");
    }

    [LibMpvFact]
    public void Built_in_presets_all_apply_without_killing_mpv()
    {
        using var host = PlayerHost.CreateHeadless();
        foreach (var preset in EqualizerPresets.BuiltIn)
        {
            host.SetEqualizer(true, preset.Bands);
            var filter = host.GetAudioFilter() ?? "";
            Assert.Contains("equalizer", filter, StringComparison.OrdinalIgnoreCase);
        }

        host.SetEqualizer(false, EqualizerPresets.Default.Bands);
        Assert.Equal(PlayerState.Idle, host.State);
    }

    [LibMpvFact]
    public void View_model_preset_and_band_changes_reach_mpv()
    {
        using var host = PlayerHost.CreateHeadless();
        using var view = new PlaybackViewModel(host);
        view.Equalizer.SetEnabled(true);
        view.Equalizer.SelectPreset("Techno");
        var filter = host.GetAudioFilter() ?? "";
        Assert.Contains("equalizer", filter, StringComparison.OrdinalIgnoreCase);

        view.Equalizer.SetBand(0, 80);
        var updated = host.GetAudioFilter() ?? "";
        Assert.Contains("g=16", updated);

        view.Equalizer.SetEnabled(false);
        var off = host.GetAudioFilter() ?? "";
        Assert.True(string.IsNullOrWhiteSpace(off) || off is "none" or "[]" or "null", off);
    }

    [LibMpvFact]
    public void Equalizer_and_volume_work_together_while_file_plays()
    {
        var sample = GeneratedMedia.TryCreateSample();
        if (sample is null)
        {
            return;
        }

        using var host = PlayerHost.CreateHeadless();
        using var view = new PlaybackViewModel(host);
        var opened = new ManualResetEventSlim(false);
        host.MediaOpened += (_, _) => opened.Set();
        host.Open(sample);
        EventWait.Until(() => opened.IsSet || host.State == PlayerState.Error, TimeSpan.FromSeconds(10), "file-loaded");
        if (host.State == PlayerState.Error)
        {
            throw new InvalidOperationException(host.LastError ?? "Open failed.");
        }

        view.Volume = 55;
        EventWait.Until(() => Math.Abs(host.Volume - 55) < 1, TimeSpan.FromSeconds(3), "volume");

        view.Equalizer.SetEnabled(true);
        view.Equalizer.SelectPreset("Full bass");
        var filter = host.GetAudioFilter() ?? "";
        Assert.Contains("equalizer", filter, StringComparison.OrdinalIgnoreCase);
        Assert.True(host.State is PlayerState.Playing or PlayerState.Paused, $"state={host.State}");

        view.Equalizer.SetBand(9, -40);
        Assert.Contains("equalizer=f=16000", host.GetAudioFilter() ?? "");
        Assert.True(host.HasMedia);
    }

    [LibMpvFact]
    public void Custom_preset_round_trips_and_cannot_overwrite_builtin()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eq-int-{Guid.NewGuid():N}.json");
        try
        {
            var model = new EqualizerModel(path);
            model.SetBand(2, 33);
            Assert.True(model.AddPreset("Room"));
            Assert.False(model.AddPreset("Rock"));
            Assert.False(EqualizerPresets.IsBuiltIn("Room"));
            model.SelectPreset("Default");
            model.SelectPreset("Room");
            Assert.Equal(33, model.Bands[2]);
            Assert.True(model.DeleteSelected());
            Assert.Equal("Default", model.SelectedName);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
