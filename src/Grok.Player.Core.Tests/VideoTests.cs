using Grok.Player.Core.Player;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Tests.Fakes;
using Grok.Player.Core.Video;

namespace Grok.Player.Core.Tests;

public sealed class VideoTests
{
    [Fact]
    public void Picture_ui_maps_50_to_mpv_zero()
    {
        Assert.Equal(0, VideoPictureSpec.ToMpv(50));
        Assert.Equal(-100, VideoPictureSpec.ToMpv(0));
        Assert.Equal(100, VideoPictureSpec.ToMpv(100));
        Assert.Equal(-40, VideoPictureSpec.ToMpv(30));
        Assert.Equal(100, VideoPictureSpec.ToMpv(250));
        Assert.Equal(-100, VideoPictureSpec.ToMpv(-20));
    }

    [Fact]
    public void Filter_graph_is_empty_when_all_off()
    {
        Assert.Equal(string.Empty, VideoFilterGraph.Build(false, false, false));
    }

    [Fact]
    public void Filter_graph_uses_restoration_order()
    {
        var all = VideoFilterGraph.Build(softer: true, sharpen: true, deblock: true);
        Assert.StartsWith("lavfi=[", all);
        var deblockAt = all.IndexOf("deblock=", StringComparison.Ordinal);
        var denoiseAt = all.IndexOf("hqdn3d=", StringComparison.Ordinal);
        var blurAt = all.IndexOf("smartblur=", StringComparison.Ordinal);
        var sharpAt = all.IndexOf("unsharp=", StringComparison.Ordinal);
        Assert.True(deblockAt >= 0 && denoiseAt > deblockAt && blurAt > denoiseAt && sharpAt > blurAt, all);
    }

    [Fact]
    public void Each_toggle_is_independent()
    {
        var softer = VideoFilterGraph.Build(true, false, false);
        Assert.Contains("hqdn3d=", softer);
        Assert.Contains("smartblur=", softer);
        Assert.DoesNotContain("unsharp=", softer);
        Assert.DoesNotContain("deblock=", softer);

        var sharpen = VideoFilterGraph.Build(false, true, false);
        Assert.Contains("unsharp=", sharpen);
        Assert.DoesNotContain("hqdn3d=", sharpen);
        Assert.DoesNotContain("deblock=", sharpen);

        var deblock = VideoFilterGraph.Build(false, false, true);
        Assert.Contains("deblock=", deblock);
        Assert.DoesNotContain("unsharp=", deblock);
    }

    [Fact]
    public void Model_defaults_and_reset_to_fifty()
    {
        var model = new VideoModel();
        Assert.Equal(50, model.Brightness);
        Assert.Equal(50, model.Contrast);
        Assert.Equal(50, model.Saturation);
        Assert.Equal(50, model.Hue);
        Assert.False(model.Softer);
        Assert.False(model.Sharpen);
        Assert.False(model.Deblock);

        model.SetBrightness(12);
        model.ResetBrightness();
        Assert.Equal(50, model.Brightness);
    }

    [Fact]
    public void Player_writes_picture_and_vf()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.SetVideoPicture(30, 50, 70, 50);
        Assert.Contains(fake.Lifecycle, item => item == "property:brightness=-40");
        Assert.Contains(fake.Lifecycle, item => item == "property:contrast=0");
        Assert.Contains(fake.Lifecycle, item => item == "property:saturation=40");
        Assert.Contains(fake.Lifecycle, item => item == "property:hue=0");

        host.SetVideoFilters(softer: false, sharpen: true, deblock: true);
        Assert.Contains(
            fake.Lifecycle,
            item => item.StartsWith("property:vf=lavfi=[", StringComparison.Ordinal) &&
                    item.Contains("deblock=") &&
                    item.Contains("unsharp="));

        host.SetVideoFilters(false, false, false);
        Assert.Contains(fake.Lifecycle, item => item == "property:vf=");
    }

    [Fact]
    public void View_model_forwards_filters_and_capture()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        host.Open(Support.TestMedia.CreateTempFile());
        view.Video.SetSharpen(true);
        Assert.Contains("unsharp=", host.GetVideoFilter() ?? "");

        var path = Path.Combine(Path.GetTempPath(), $"grok-cap-{Guid.NewGuid():N}.png");
        try
        {
            view.CaptureFrame(path);
            Assert.True(File.Exists(path));
            Assert.Contains(fake.Commands, command => command.Length >= 3 && command[0] == "screenshot-to-file" && command[2] == "video");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Capture_without_media_throws()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        Assert.Throws<InvalidOperationException>(() => host.CaptureFrame(Path.Combine(Path.GetTempPath(), "x.png")));
    }
}
