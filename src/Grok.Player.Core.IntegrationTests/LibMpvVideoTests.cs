using Grok.Player.Core.Player;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.IntegrationTests.Support;

namespace Grok.Player.Core.IntegrationTests;

public sealed class LibMpvVideoTests
{
    [LibMpvFact]
    public void Picture_properties_and_filter_graph_apply_on_a_live_handle()
    {
        using var host = PlayerHost.CreateHeadless();
        host.SetVideoPicture(25, 60, 50, 80);
        Assert.Equal(-50, host.GetMpvDouble("brightness") ?? double.NaN, 0.01);
        Assert.Equal(20, host.GetMpvDouble("contrast") ?? double.NaN, 0.01);
        Assert.Equal(0, host.GetMpvDouble("saturation") ?? double.NaN, 0.01);
        Assert.Equal(60, host.GetMpvDouble("hue") ?? double.NaN, 0.01);

        host.SetVideoFilters(softer: true, sharpen: true, deblock: true);
        var filter = host.GetVideoFilter() ?? "";
        Assert.False(string.IsNullOrWhiteSpace(filter), "vf should be set");
        Assert.Contains("deblock", filter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unsharp", filter, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            filter.Contains("hqdn3d", StringComparison.OrdinalIgnoreCase) ||
            filter.Contains("smartblur", StringComparison.OrdinalIgnoreCase),
            filter);

        host.SetVideoFilters(false, false, false);
        var cleared = host.GetVideoFilter() ?? "";
        Assert.True(
            string.IsNullOrWhiteSpace(cleared) || cleared is "none" or "[]" or "null",
            $"vf after disable={cleared}");
    }

    [LibMpvFact]
    public void Filters_and_capture_work_while_a_file_plays()
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

        view.Video.SetDeblock(true);
        view.Video.SetSofter(true);
        view.Video.SetSharpen(true);
        var filter = host.GetVideoFilter() ?? "";
        Assert.Contains("deblock", filter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unsharp", filter, StringComparison.OrdinalIgnoreCase);

        var path = Path.Combine(Path.GetTempPath(), $"grok-int-cap-{Guid.NewGuid():N}.png");
        try
        {
            view.CaptureFrame(path);
            Assert.True(File.Exists(path) && new FileInfo(path).Length > 32, "screenshot should be written");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        view.Video.SetDeblock(false);
        view.Video.SetSofter(false);
        view.Video.SetSharpen(false);
        var off = host.GetVideoFilter() ?? "";
        Assert.True(string.IsNullOrWhiteSpace(off) || off is "none" or "[]" or "null", off);
        Assert.True(host.HasMedia);
    }
}
