using Grok.Player.Core.Player;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Tests.Fakes;
using Grok.Player.Core.Video;

namespace Grok.Player.Core.Tests;

public sealed class VideoResizeTests
{
    private static readonly VideoResizeContext HdIn1080 = new(1280, 720, 1920, 1080, 1920, 1080);
    private static readonly VideoResizeContext UhdIn1080 = new(3840, 2160, 1920, 1080, 1920, 1080);

    [Fact]
    public void Rejects_zero_and_invalid_numbers()
    {
        Assert.False(VideoResizeSpec.TryPositiveInt("0", out _));
        Assert.False(VideoResizeSpec.TryPositiveInt("-12", out _));
        Assert.False(VideoResizeSpec.TryPositiveInt("nope", out _));
        Assert.False(VideoResizeSpec.TryPositiveInt("", out _));
        Assert.False(VideoResizeSpec.TryPositiveDouble("0", out _));
        Assert.False(VideoResizeSpec.TryPositiveDouble("NaN", out _));
        Assert.True(VideoResizeSpec.TryPositiveInt("1920", out var width));
        Assert.Equal(1920, width);
        Assert.True(VideoResizeSpec.TryParseRatio("21:9", out var x, out var y));
        Assert.Equal(21, x);
        Assert.Equal(9, y);
    }

    [Fact]
    public void Keep_aspect_computes_the_other_side()
    {
        Assert.Equal(1080, VideoResizeSpec.HeightFromWidth(1920, 1920, 1080));
        Assert.Equal(1920, VideoResizeSpec.WidthFromHeight(1080, 1920, 1080));
        Assert.Equal(810, VideoResizeSpec.HeightFromWidth(1440, 1920, 1080));
    }

    [Fact]
    public void Policy_decides_whether_to_scale()
    {
        var always = VideoResizeSettings.Default;
        Assert.True(VideoResizeSpec.ShouldScale(always, HdIn1080));
        Assert.False(VideoResizeSpec.ShouldScale(always with { Policy = VideoResizePolicy.Never }, HdIn1080));
        Assert.True(VideoResizeSpec.ShouldScale(always with { Policy = VideoResizePolicy.UpscaleOnly }, HdIn1080));
        Assert.False(VideoResizeSpec.ShouldScale(always with { Policy = VideoResizePolicy.UpscaleOnly }, UhdIn1080));
        Assert.True(VideoResizeSpec.ShouldScale(always with { Policy = VideoResizePolicy.DownscaleOnly }, UhdIn1080));
        Assert.False(VideoResizeSpec.ShouldScale(always with { Policy = VideoResizePolicy.DownscaleOnly }, HdIn1080));
    }

    [Fact]
    public void Fit_fill_stretch_and_aspect_do_not_overwrite_each_other()
    {
        var fourByThree = VideoResizeSettings.Default with { Aspect = VideoAspectMode.Ratio4x3 };
        var fit = VideoResizeSpec.Plan(fourByThree, HdIn1080);
        Assert.True(fit.KeepAspect);
        Assert.Equal(0, fit.Panscan);
        Assert.Equal("no", fit.Unscaled);
        Assert.Equal("4:3", VideoResizeSpec.AspectOverride(fourByThree, HdIn1080));

        var fill = VideoResizeSpec.Plan(fourByThree with { Sizing = VideoSizingMode.FillCrop }, HdIn1080);
        Assert.True(fill.KeepAspect);
        Assert.Equal(1, fill.Panscan);
        Assert.Equal("4:3", VideoResizeSpec.AspectOverride(fourByThree with { Sizing = VideoSizingMode.FillCrop }, HdIn1080));

        var stretch = VideoResizeSpec.Plan(fourByThree with { Sizing = VideoSizingMode.Stretch }, HdIn1080);
        Assert.False(stretch.KeepAspect);
        Assert.Equal(0, stretch.Panscan);
        Assert.Equal("4:3", VideoResizeSpec.AspectOverride(fourByThree with { Sizing = VideoSizingMode.Stretch }, HdIn1080));

        Assert.Equal("16:9", VideoResizeSpec.AspectOverride(
            VideoResizeSettings.Default with { Aspect = VideoAspectMode.Ratio16x9 },
            HdIn1080));
        Assert.Equal("1280:720", VideoResizeSpec.AspectOverride(
            VideoResizeSettings.Default with { Aspect = VideoAspectMode.Stretch },
            HdIn1080));
        Assert.Equal("21:9", VideoResizeSpec.AspectOverride(
            VideoResizeSettings.Default with { Aspect = VideoAspectMode.Custom, CustomAspectX = 21, CustomAspectY = 9 },
            HdIn1080));
        Assert.Equal("no", VideoResizeSpec.AspectOverride(VideoResizeSettings.Default, HdIn1080));
    }

    [Fact]
    public void Original_size_does_not_fit_the_window()
    {
        var plan = VideoResizeSpec.Plan(
            VideoResizeSettings.Default with { Sizing = VideoSizingMode.Original },
            HdIn1080);
        Assert.Equal("yes", plan.Unscaled);
        Assert.Equal(1, plan.ScaleX);
        Assert.Equal(1, plan.ScaleY);
    }

    [Fact]
    public void Custom_resolution_ignores_window_size()
    {
        var settings = VideoResizeSettings.Default with
        {
            Sizing = VideoSizingMode.CustomResolution,
            CustomWidth = 640,
            CustomHeight = 360
        };
        var smallWindow = VideoResizeSpec.Plan(settings, new VideoResizeContext(1280, 720, 800, 600, 1920, 1080));
        var largeWindow = VideoResizeSpec.Plan(settings, new VideoResizeContext(1280, 720, 2560, 1440, 1920, 1080));
        Assert.Equal(smallWindow.ScaleX, largeWindow.ScaleX);
        Assert.Equal(smallWindow.ScaleY, largeWindow.ScaleY);
        Assert.Equal(0.5, smallWindow.ScaleX);
        Assert.Equal(0.5, smallWindow.ScaleY);
        Assert.Equal("yes", smallWindow.Unscaled);
    }

    [Fact]
    public void Match_display_uses_the_monitor_not_the_player()
    {
        var settings = VideoResizeSettings.Default with { Sizing = VideoSizingMode.MatchDisplay };
        var plan = VideoResizeSpec.Plan(settings, new VideoResizeContext(1280, 720, 800, 600, 2560, 1440));
        Assert.Equal(2, plan.ScaleX);
        Assert.Equal(2, plan.ScaleY);
    }

    [Fact]
    public void Keep_aspect_follows_the_selected_ratio()
    {
        var sixteen = VideoResizeSettings.Default with { Aspect = VideoAspectMode.Ratio16x9 };
        Assert.Equal(900, VideoResizeSpec.HeightFromWidth(1600, sixteen, 1440, 1080));
        var four = VideoResizeSettings.Default with { Aspect = VideoAspectMode.Ratio4x3 };
        Assert.Equal(1200, VideoResizeSpec.HeightFromWidth(1600, four, 1920, 1080));
        var ultra = VideoResizeSettings.Default with { Aspect = VideoAspectMode.Ratio32x9 };
        Assert.Equal(900, VideoResizeSpec.HeightFromWidth(3200, ultra, 1920, 1080));
        Assert.Equal("32:9", VideoResizeSpec.AspectOverride(ultra, HdIn1080));
        Assert.Equal(50, VideoResizeSpec.ClampAdjust(50));
        Assert.Equal(50, VideoResizeSpec.ClampAdjust(80));
    }

    [Fact]
    public void Shortcuts_nudge_and_reset_image_adjust()
    {
        var model = new VideoResizeModel();
        Assert.True(model.NudgeHorizontal(1));
        Assert.Equal(1.02, model.Live.AdjustX, 3);
        Assert.True(model.NudgeVertical(-1));
        Assert.Equal(0.98, model.Live.AdjustY, 3);
        Assert.True(model.ResetAdjust());
        Assert.Equal(1, model.Live.AdjustX);
        Assert.Equal(1, model.Live.AdjustY);
        Assert.False(model.ResetAdjust());
    }

    [Fact]
    public void Adjust_composes_on_top_of_fit_and_multiplier()
    {
        var fit = VideoResizeSpec.Plan(
            VideoResizeSettings.Default with { AdjustX = 1.04, AdjustY = 1 },
            HdIn1080);
        Assert.True(fit.KeepAspect);
        Assert.Equal(0, fit.Panscan);
        Assert.Equal(1.04, fit.ScaleX, 3);
        Assert.Equal(1, fit.ScaleY);

        var two = VideoResizeSpec.Plan(
            VideoResizeSettings.Default with
            {
                Sizing = VideoSizingMode.Multiplier,
                Multiplier = VideoScaleMultiplier.Two,
                AdjustX = 1.1,
                AdjustY = 0.9
            },
            HdIn1080);
        Assert.Equal(2.2, two.ScaleX, 3);
        Assert.Equal(1.8, two.ScaleY, 3);
    }

    [Fact]
    public void Multiplier_and_custom_resolution_set_explicit_scale()
    {
        var two = VideoResizeSpec.Plan(
            VideoResizeSettings.Default with
            {
                Sizing = VideoSizingMode.Multiplier,
                Multiplier = VideoScaleMultiplier.Two
            },
            HdIn1080);
        Assert.Equal("yes", two.Unscaled);
        Assert.Equal(2, two.ScaleX);
        Assert.Equal(2, two.ScaleY);

        var custom = VideoResizeSpec.Plan(
            VideoResizeSettings.Default with
            {
                Sizing = VideoSizingMode.CustomResolution,
                CustomWidth = 640,
                CustomHeight = 360
            },
            HdIn1080);
        Assert.Equal(0.5, custom.ScaleX);
        Assert.Equal(0.5, custom.ScaleY);

        var display = VideoResizeSpec.Plan(
            VideoResizeSettings.Default with { Sizing = VideoSizingMode.MatchDisplay },
            new VideoResizeContext(1280, 720, 800, 600, 2560, 1440));
        Assert.Equal(2, display.ScaleX);
        Assert.Equal(2, display.ScaleY);
    }

    [Fact]
    public void Named_multiplier_does_not_need_source_size()
    {
        var plan = VideoResizeSpec.Plan(
            VideoResizeSettings.Default with
            {
                Sizing = VideoSizingMode.Multiplier,
                Multiplier = VideoScaleMultiplier.Two
            },
            new VideoResizeContext(0, 0, 1920, 1080, 1920, 1080));
        Assert.Equal(2, plan.ScaleX);
        Assert.Equal(2, plan.ScaleY);
        Assert.Equal(1.5, VideoResizeSpec.MultiplierValue(
            VideoResizeSettings.Default with { Multiplier = VideoScaleMultiplier.OnePointFive }));
        Assert.Equal(1, VideoResizeSpec.MultiplierValue(
            VideoResizeSettings.Default with { Multiplier = VideoScaleMultiplier.One }));
    }

    [Fact]
    public void Preview_keeps_named_multiplier_and_aspect()
    {
        var model = new VideoResizeModel();
        model.SetSizing(VideoSizingMode.Multiplier);
        model.SetMultiplier(VideoScaleMultiplier.Two);
        model.SetAspect(VideoAspectMode.Ratio16x9);
        model.Preview();
        Assert.Equal(VideoScaleMultiplier.Two, model.Live.Multiplier);
        Assert.Equal(VideoAspectMode.Ratio16x9, model.Live.Aspect);
        Assert.NotEqual(VideoScaleMultiplier.Custom, model.Live.Multiplier);
        Assert.NotEqual(VideoAspectMode.Custom, model.Live.Aspect);
    }

    [Fact]
    public void Player_writes_named_multiplier()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.SetVideoResize(
            VideoResizeSettings.Default with
            {
                Sizing = VideoSizingMode.Multiplier,
                Multiplier = VideoScaleMultiplier.OnePointFive
            },
            HdIn1080);
        Assert.Contains(fake.Lifecycle, item => item == "property:video-scale-x=1.5");
        Assert.Contains(fake.Lifecycle, item => item == "property:video-scale-y=1.5");
        Assert.Contains(fake.Lifecycle, item => item == "property:video-unscaled=yes");
    }

    [Fact]
    public void Custom_width_keeps_source_aspect()
    {
        var model = new VideoResizeModel();
        Assert.True(model.SetCustomWidth(1280, 1920, 1080));
        Assert.Equal(1280, model.Draft.CustomWidth);
        Assert.Equal(720, model.Draft.CustomHeight);
        Assert.False(model.SetCustomWidth(0, 1920, 1080));
        Assert.Equal(1280, model.Draft.CustomWidth);
    }

    [Fact]
    public void Player_writes_geometry_not_scale_kernels()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.SetVideoResize(
            VideoResizeSettings.Default with { Sizing = VideoSizingMode.FillCrop, Aspect = VideoAspectMode.Ratio16x9 },
            HdIn1080);

        Assert.Contains(fake.Lifecycle, item => item == "property:video-aspect-override=16:9");
        Assert.Contains(fake.Lifecycle, item => item == "property:keepaspect=True");
        Assert.Contains(fake.Lifecycle, item => item == "property:panscan=1");
        Assert.Contains(fake.Lifecycle, item => item == "property:video-unscaled=no");
        Assert.DoesNotContain(fake.Lifecycle, item => item.StartsWith("property:scale=", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.Lifecycle, item => item.StartsWith("property:deband=", StringComparison.Ordinal));
    }

    [Fact]
    public void View_model_pushes_live_resize()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.LayoutSize = () => new VideoResizeLayout(1920, 1080, 1920, 1080);
        fake.Seed("width", 1280L);
        fake.Seed("height", 720L);
        view.Resize.SetSizing(VideoSizingMode.Stretch);
        view.Resize.Preview();
        view.ApplyResizeLive();
        Assert.Contains(fake.Lifecycle, item => item == "property:keepaspect=False");
    }

    [Fact]
    public void View_model_nudges_image_and_logs()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        string? last = null;
        view.Noted += text => last = text;
        Assert.True(view.NudgeImageWidth(1));
        Assert.Equal("Width 102%", last);
        Assert.Contains(fake.Lifecycle, item => item.StartsWith("property:video-scale-x=", StringComparison.Ordinal));
        Assert.True(view.ResetImageAdjust());
        Assert.Equal("Image reset", last);
    }
}
