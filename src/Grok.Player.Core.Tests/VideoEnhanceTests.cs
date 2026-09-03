using Grok.Player.Core.Player;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Tests.Fakes;
using Grok.Player.Core.Video;

namespace Grok.Player.Core.Tests;

public sealed class VideoEnhanceTests
{
    [Fact]
    public void Native_hdr_is_passthrough_without_nvidia_vpp()
    {
        Assert.Equal("source", VideoEnhanceSpec.HintMode(HdrOutputMode.Native));
        Assert.Equal("yes", VideoEnhanceSpec.Hint(HdrOutputMode.Native));
        Assert.False(VideoEnhanceSpec.NeedsVideoProcessor(false, HdrOutputMode.Native));
        Assert.True(VideoEnhanceSpec.NeedsZeroCopyDecode(false, HdrOutputMode.Native));
        Assert.Null(VideoEnhanceSpec.D3d11Vpp(false, 2, rtxHdr: false));
    }

    [Fact]
    public void Rtx_hdr_needs_p010_and_true_hdr_flag()
    {
        var graph = VideoEnhanceSpec.D3d11Vpp(false, 2, rtxHdr: true);
        Assert.Equal("d3d11vpp=format=p010:nvidia-true-hdr", graph);
        Assert.True(VideoEnhanceSpec.NeedsVideoProcessor(false, HdrOutputMode.Rtx));
    }

    [Fact]
    public void Super_resolution_does_not_force_hdr_vpp()
    {
        var graph = VideoEnhanceSpec.D3d11Vpp(true, 2.4, rtxHdr: false);
        Assert.Equal("d3d11vpp=scaling-mode=nvidia:scale=2", graph);
        Assert.DoesNotContain("nvidia-true-hdr", graph, StringComparison.Ordinal);
        Assert.True(VideoEnhanceSpec.NeedsVideoProcessor(true, HdrOutputMode.Off));
        Assert.False(VideoEnhanceSpec.NeedsVideoProcessor(false, HdrOutputMode.Off));
    }

    [Fact]
    public void Native_hdr_writes_source_hint_and_zero_copy_decode()
    {
        var fake = Interactive();
        using var host = new PlayerHost(fake, InteractiveOptions());
        var result = host.SetVideoEnhance(false, 2, HdrOutputMode.Native);
        Assert.True(result.Ok);
        Assert.False(result.VppNeeded);
        Assert.Contains(fake.Lifecycle, item => item == "property:target-colorspace-hint=yes");
        Assert.Contains(fake.Lifecycle, item => item == "property:target-colorspace-hint-mode=source");
        Assert.Contains(fake.Lifecycle, item => item == "property:hwdec=d3d11va");
        Assert.DoesNotContain(
            fake.Commands,
            command => command.Any(arg => arg.Contains("nvidia-true-hdr", StringComparison.Ordinal)));
    }

    [Fact]
    public void Hdr_off_disables_hint_and_restores_copy_decode()
    {
        var fake = Interactive();
        using var host = new PlayerHost(fake, InteractiveOptions());
        host.SetVideoEnhance(false, 2, HdrOutputMode.Native);
        var result = host.SetVideoEnhance(false, 2, HdrOutputMode.Off);
        Assert.True(result.Ok);
        Assert.Contains(fake.Lifecycle, item => item == "property:target-colorspace-hint=no");
        Assert.Contains(fake.Lifecycle, item => item == "property:hwdec=d3d11va-copy");
        Assert.Contains(fake.Commands, command => command is ["vf", "remove", "@enhance"]);
    }

    [Fact]
    public void Rtx_hdr_installs_vpp_and_keeps_passthrough_hint()
    {
        var fake = Interactive();
        using var host = new PlayerHost(fake, InteractiveOptions());
        var result = host.SetVideoEnhance(false, 2, HdrOutputMode.Rtx);
        Assert.True(result.Ok);
        Assert.True(result.VppNeeded);
        Assert.True(result.VppApplied);
        Assert.Contains(fake.Lifecycle, item => item == "property:target-colorspace-hint-mode=source");
        Assert.Contains(
            fake.Commands,
            command => command.Length >= 3 &&
                       command[0] == "vf" &&
                       command[2].Contains("nvidia-true-hdr", StringComparison.Ordinal) &&
                       command[2].Contains("format=p010", StringComparison.Ordinal));
    }

    [Fact]
    public void Super_resolution_falls_back_to_add_when_pre_is_rejected()
    {
        var fake = Interactive();
        fake.FailVfPreRemaining = 1;
        using var host = new PlayerHost(fake, InteractiveOptions());
        var result = host.SetVideoEnhance(true, 2, HdrOutputMode.Off);
        Assert.True(result.Ok);
        Assert.True(result.VppApplied);
        Assert.Contains(
            fake.Commands,
            command => command.Length >= 3 &&
                       command[0] == "vf" &&
                       command[1] == "add" &&
                       command[2].StartsWith("@enhance:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            fake.Commands,
            command => command.Length >= 2 && command[0] == "vf" && command[1] == "pre");
    }

    [Fact]
    public void Enhance_failure_is_not_reported_as_success()
    {
        var fake = Interactive();
        fake.FailIfCommandContains = "d3d11vpp=";
        using var host = new PlayerHost(fake, InteractiveOptions());
        var result = host.SetVideoEnhance(true, 2, HdrOutputMode.Rtx);
        Assert.True(result.VppNeeded);
        Assert.False(result.VppApplied);
        Assert.False(result.Ok);
        Assert.True(result.HdrApplied);
    }

    [Fact]
    public void Headless_never_installs_d3d11vpp()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        var result = host.SetVideoEnhance(true, 2, HdrOutputMode.Rtx);
        Assert.DoesNotContain(
            fake.Commands,
            command => command.Any(arg => arg.Contains("d3d11vpp", StringComparison.Ordinal)));
        Assert.False(result.VppApplied);
        Assert.True(result.VppNeeded);
    }

    [Fact]
    public void View_reverts_rtx_when_the_processor_filter_fails()
    {
        var fake = Interactive();
        fake.FailIfCommandContains = "d3d11vpp=";
        using var host = new PlayerHost(fake, InteractiveOptions());
        using var view = new PlaybackViewModel(host);
        var notes = new List<string>();
        view.Noted += notes.Add;
        view.Video.SetSuperResolution(true);
        Assert.False(view.Video.SuperResolution);
        Assert.Contains(notes, note => note.Contains("RTX enhance failed", StringComparison.Ordinal));

        notes.Clear();
        view.Video.SetHdr(HdrOutputMode.Rtx);
        Assert.Equal(HdrOutputMode.Native, view.Video.Hdr);
        Assert.Contains(notes, note => note.Contains("RTX enhance failed", StringComparison.Ordinal));
    }

    [Fact]
    public void View_keeps_native_hdr_when_no_vpp_is_required()
    {
        var fake = Interactive();
        using var host = new PlayerHost(fake, InteractiveOptions());
        using var view = new PlaybackViewModel(host);
        view.Video.SetHdr(HdrOutputMode.Off);
        view.Video.SetHdr(HdrOutputMode.Native);
        Assert.Equal(HdrOutputMode.Native, view.Video.Hdr);
        Assert.Contains(fake.Lifecycle, item => item == "property:target-colorspace-hint-mode=source");
        Assert.DoesNotContain(
            fake.Commands,
            command => command.Any(arg => arg.Contains("nvidia-true-hdr", StringComparison.Ordinal)));
    }

    [Fact]
    public void Interactive_startup_uses_gpu_next_and_source_hint()
    {
        var fake = Interactive();
        using var _ = new PlayerHost(fake, InteractiveOptions());
        Assert.True(fake.HasOption("vo", "gpu-next"));
        Assert.True(fake.HasOption("target-colorspace-hint", "yes"));
        Assert.True(fake.HasOption("target-colorspace-hint-mode", "source"));
    }

    private static FakeMpvNative Interactive() => new();

    private static PlayerHostOptions InteractiveOptions() => new()
    {
        Headless = false,
        HardwareDecode = true,
        UseBackgroundEventLoop = false
    };
}
