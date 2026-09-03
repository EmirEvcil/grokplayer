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
    public void Enhance_graph_is_null_when_off()
    {
        Assert.Null(VideoEnhanceSpec.D3d11Vpp(false, 2, false));
    }

    [Fact]
    public void Enhance_graph_combines_rtx_super_resolution_and_hdr()
    {
        var vsr = VideoEnhanceSpec.D3d11Vpp(true, 2.4, false);
        Assert.Equal("d3d11vpp=scaling-mode=nvidia:scale=2", vsr);
        var both = VideoEnhanceSpec.D3d11Vpp(true, 3, true);
        Assert.Equal("d3d11vpp=scaling-mode=nvidia:scale=3:format=p010:nvidia-true-hdr", both);
        Assert.Equal(2, VideoEnhanceSpec.Scale(1080, 2160));
        Assert.Equal(1, VideoEnhanceSpec.Scale(2160, 1080));
        Assert.Equal("yes", VideoEnhanceSpec.Hint(HdrOutputMode.Native));
        Assert.Equal("no", VideoEnhanceSpec.Hint(HdrOutputMode.Off));
        Assert.Equal("source", VideoEnhanceSpec.HintMode(HdrOutputMode.Rtx));
        Assert.Equal("source", VideoEnhanceSpec.HintMode(HdrOutputMode.Native));
    }

    [Fact]
    public void Player_writes_hdr_hint_and_rtx_filter()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, new PlayerHostOptions
        {
            Headless = false,
            HardwareDecode = true,
            UseBackgroundEventLoop = false
        });
        host.SetVideoEnhance(true, 2, HdrOutputMode.Rtx);
        Assert.Contains(fake.Lifecycle, item => item == "property:target-colorspace-hint=yes");
        Assert.Contains(fake.Lifecycle, item => item == "property:target-colorspace-hint-mode=source");
        Assert.Contains(fake.Lifecycle, item => item == "property:hwdec=d3d11va");
        Assert.Contains(
            fake.Commands,
            command => command.Length >= 3 &&
                       command[0] == "vf" &&
                       (command[1] == "pre" || command[1] == "add") &&
                       command[2].Contains("d3d11vpp=", StringComparison.Ordinal) &&
                       command[2].Contains("scaling-mode=nvidia", StringComparison.Ordinal) &&
                       command[2].Contains("nvidia-true-hdr", StringComparison.Ordinal));

        host.SetVideoEnhance(false, 2, HdrOutputMode.Off);
        Assert.Contains(fake.Lifecycle, item => item == "property:target-colorspace-hint=no");
        Assert.Contains(fake.Lifecycle, item => item == "property:hwdec=d3d11va-copy");
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
        Assert.False(model.SuperResolution);
        Assert.Equal(HdrOutputMode.Native, model.Hdr);

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
