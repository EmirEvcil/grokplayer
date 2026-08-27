using System.Globalization;

namespace Grok.Player.Core.Video;

public enum VideoResizePolicy
{
    Never,
    Always,
    UpscaleOnly,
    DownscaleOnly
}

public enum VideoSizingMode
{
    Fit,
    FillCrop,
    Stretch,
    Original,
    Multiplier,
    CustomResolution,
    MatchDisplay
}

public enum VideoScaleMultiplier
{
    One,
    OnePointFive,
    Two,
    Custom
}

public enum VideoAspectMode
{
    KeepSource,
    Ratio16x9,
    Ratio4x3,
    Ratio32x9,
    Stretch,
    Custom
}

public readonly record struct VideoSize(int W, int H);

public readonly record struct VideoResizeLayout(int PlayerW, int PlayerH, int DisplayW, int DisplayH)
{
    public static VideoResizeLayout Empty => new(0, 0, 0, 0);
}

public readonly record struct VideoResizeContext(
    int SourceW,
    int SourceH,
    int PlayerW,
    int PlayerH,
    int DisplayW,
    int DisplayH)
{
    public VideoResizeContext(int sourceW, int sourceH, VideoResizeLayout layout)
        : this(sourceW, sourceH, layout.PlayerW, layout.PlayerH, layout.DisplayW, layout.DisplayH)
    {
    }
}

public readonly record struct VideoResizePlan(
    bool KeepAspect,
    double Panscan,
    string Unscaled,
    double ScaleX,
    double ScaleY);

public sealed record VideoResizeSettings(
    VideoResizePolicy Policy,
    VideoSizingMode Sizing,
    VideoScaleMultiplier Multiplier,
    double CustomMultiplier,
    int CustomWidth,
    int CustomHeight,
    bool KeepCustomAspect,
    VideoAspectMode Aspect,
    int CustomAspectX,
    int CustomAspectY,
    double AdjustX,
    double AdjustY,
    double ShortcutStep)
{
    public static VideoResizeSettings Default { get; } = new(
        VideoResizePolicy.Always,
        VideoSizingMode.Fit,
        VideoScaleMultiplier.One,
        1.0,
        1920,
        1080,
        true,
        VideoAspectMode.KeepSource,
        21,
        9,
        1.0,
        1.0,
        0.02);
}

public static class VideoResizeSpec
{
    public const int MinPixels = 1;
    public const int MaxPixels = 16384;
    public const double MinMultiplier = 0.05;
    public const double MaxMultiplier = 16;
    public const double MinAdjust = 0.2;
    public const double MaxAdjust = 50.0;
    public const double MinShortcutStep = 0.005;
    public const double MaxShortcutStep = 0.10;

    public static double MultiplierValue(VideoResizeSettings settings) => settings.Multiplier switch
    {
        VideoScaleMultiplier.One => 1.0,
        VideoScaleMultiplier.OnePointFive => 1.5,
        VideoScaleMultiplier.Two => 2.0,
        _ => settings.CustomMultiplier
    };

    public static bool IsValidMultiplier(double value) =>
        double.IsFinite(value) && value >= MinMultiplier && value <= MaxMultiplier;

    public static bool IsValidPixels(int value) => value >= MinPixels && value <= MaxPixels;

    public static bool TryPositiveInt(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            !int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out parsed))
        {
            return false;
        }

        if (!IsValidPixels(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryPositiveDouble(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            !double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
        {
            return false;
        }

        if (!IsValidMultiplier(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryParseRatio(string? text, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Replace('/', ':').Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!TryPositiveInt(parts[0], out x) || !TryPositiveInt(parts[1], out y))
        {
            return false;
        }

        return x > 0 && y > 0;
    }

    public static (int W, int H) AspectPair(VideoResizeSettings settings, int sourceW, int sourceH) =>
        settings.Aspect switch
        {
            VideoAspectMode.Ratio16x9 => (16, 9),
            VideoAspectMode.Ratio4x3 => (4, 3),
            VideoAspectMode.Ratio32x9 => (32, 9),
            VideoAspectMode.Custom => (Math.Max(1, settings.CustomAspectX), Math.Max(1, settings.CustomAspectY)),
            _ => sourceW > 0 && sourceH > 0 ? (sourceW, sourceH) : (16, 9)
        };

    public static int HeightFromWidth(int width, int sourceW, int sourceH) =>
        HeightFromWidth(width, sourceW > 0 && sourceH > 0 ? (sourceW, sourceH) : (16, 9));

    public static int HeightFromWidth(int width, VideoResizeSettings settings, int sourceW, int sourceH) =>
        HeightFromWidth(width, AspectPair(settings, sourceW, sourceH));

    public static int WidthFromHeight(int height, int sourceW, int sourceH) =>
        WidthFromHeight(height, sourceW > 0 && sourceH > 0 ? (sourceW, sourceH) : (16, 9));

    public static int WidthFromHeight(int height, VideoResizeSettings settings, int sourceW, int sourceH) =>
        WidthFromHeight(height, AspectPair(settings, sourceW, sourceH));

    public static double ClampAdjust(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinAdjust, MaxAdjust) : 1.0;

    public static double ClampShortcutStep(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinShortcutStep, MaxShortcutStep) : 0.02;

    private static int HeightFromWidth(int width, (int W, int H) aspect) =>
        Math.Clamp((int)Math.Round(width * (double)aspect.H / aspect.W), MinPixels, MaxPixels);

    private static int WidthFromHeight(int height, (int W, int H) aspect) =>
        Math.Clamp((int)Math.Round(height * (double)aspect.W / aspect.H), MinPixels, MaxPixels);

    public static VideoSize TargetSize(VideoResizeSettings settings, VideoResizeContext ctx) =>
        settings.Sizing switch
        {
            VideoSizingMode.Original => new VideoSize(Math.Max(0, ctx.SourceW), Math.Max(0, ctx.SourceH)),
            VideoSizingMode.Multiplier => ScaleSource(ctx, MultiplierValue(settings)),
            VideoSizingMode.CustomResolution => new VideoSize(settings.CustomWidth, settings.CustomHeight),
            VideoSizingMode.MatchDisplay => new VideoSize(ctx.DisplayW, ctx.DisplayH),
            _ => new VideoSize(ctx.PlayerW, ctx.PlayerH)
        };

    public static bool ShouldScale(VideoResizeSettings settings, VideoResizeContext ctx)
    {
        if (settings.Policy == VideoResizePolicy.Never)
        {
            return false;
        }

        if (settings.Sizing == VideoSizingMode.Original)
        {
            return false;
        }

        if (settings.Policy == VideoResizePolicy.Always)
        {
            return true;
        }

        var target = TargetSize(settings, ctx);
        if (ctx.SourceW <= 0 || ctx.SourceH <= 0 || target.W <= 0 || target.H <= 0)
        {
            return settings.Policy is VideoResizePolicy.Always or VideoResizePolicy.DownscaleOnly;
        }

        var smaller = ctx.SourceW < target.W && ctx.SourceH < target.H;
        var larger = ctx.SourceW > target.W || ctx.SourceH > target.H;
        return settings.Policy switch
        {
            VideoResizePolicy.UpscaleOnly => smaller,
            VideoResizePolicy.DownscaleOnly => larger,
            _ => false
        };
    }

    public static string AspectOverride(VideoResizeSettings settings, VideoResizeContext ctx) =>
        settings.Aspect switch
        {
            VideoAspectMode.Ratio16x9 => "16:9",
            VideoAspectMode.Ratio4x3 => "4:3",
            VideoAspectMode.Ratio32x9 => "32:9",
            VideoAspectMode.Custom => $"{Math.Max(1, settings.CustomAspectX)}:{Math.Max(1, settings.CustomAspectY)}",
            VideoAspectMode.Stretch when ctx.SourceW > 0 && ctx.SourceH > 0 => $"{ctx.SourceW}:{ctx.SourceH}",
            _ => "no"
        };

    public static VideoResizePlan Plan(VideoResizeSettings settings, VideoResizeContext ctx)
    {
        var adjustX = ClampAdjust(settings.AdjustX);
        var adjustY = ClampAdjust(settings.AdjustY);
        VideoResizePlan core;
        if (settings.Policy == VideoResizePolicy.Never || settings.Sizing == VideoSizingMode.Original ||
            !ShouldScale(settings, ctx))
        {
            core = new VideoResizePlan(true, 0, "yes", 1, 1);
        }
        else if (settings.Sizing is VideoSizingMode.Multiplier or VideoSizingMode.CustomResolution
                 or VideoSizingMode.MatchDisplay)
        {
            var (sx, sy) = ExplicitScale(settings, ctx);
            core = new VideoResizePlan(true, 0, "yes", sx, sy);
        }
        else
        {
            // Aspect only sets the video's shape (video-aspect-override).
            // Sizing only places that shape: fit = contain, fill = cover, stretch = fill the player.
            var keepAspect = settings.Sizing != VideoSizingMode.Stretch;
            var panscan = settings.Sizing == VideoSizingMode.FillCrop ? 1.0 : 0.0;
            var unscaled = settings.Policy == VideoResizePolicy.DownscaleOnly &&
                           settings.Sizing is VideoSizingMode.Fit or VideoSizingMode.FillCrop
                               or VideoSizingMode.Stretch
                ? "downscale-big"
                : "no";
            core = new VideoResizePlan(keepAspect, panscan, unscaled, 1, 1);
        }

        return core with { ScaleX = core.ScaleX * adjustX, ScaleY = core.ScaleY * adjustY };
    }

    public static string Label(VideoResizePolicy policy) => policy switch
    {
        VideoResizePolicy.Never => "Never",
        VideoResizePolicy.UpscaleOnly => "Only when the source video is smaller than the target",
        VideoResizePolicy.DownscaleOnly => "Only when the source video is larger than the target",
        _ => "Always"
    };

    public static string Label(VideoSizingMode mode) => mode switch
    {
        VideoSizingMode.FillCrop => "Fill player and crop",
        VideoSizingMode.Stretch => "Stretch to player",
        VideoSizingMode.Original => "Keep original size",
        VideoSizingMode.Multiplier => "Scale by multiplier",
        VideoSizingMode.CustomResolution => "Custom resolution",
        VideoSizingMode.MatchDisplay => "Match display resolution",
        _ => "Fit to player"
    };

    public static string Label(VideoScaleMultiplier multiplier) => multiplier switch
    {
        VideoScaleMultiplier.OnePointFive => "1.5x",
        VideoScaleMultiplier.Two => "2x",
        VideoScaleMultiplier.Custom => "Custom",
        _ => "1x"
    };

    public static string Label(VideoAspectMode aspect) => aspect switch
    {
        VideoAspectMode.Ratio16x9 => "16:9",
        VideoAspectMode.Ratio4x3 => "4:3",
        VideoAspectMode.Ratio32x9 => "32:9",
        VideoAspectMode.Stretch => "Stretch",
        VideoAspectMode.Custom => "Custom",
        _ => "Keep source aspect ratio"
    };

    public static string PolicyTip(VideoResizePolicy policy) => policy switch
    {
        VideoResizePolicy.Never => "Leave the video at its source pixel size. Sizing modes are unused.",
        VideoResizePolicy.Always => "Apply the sizing mode whenever it needs to change the picture size.",
        VideoResizePolicy.UpscaleOnly => "Scale only when the source is smaller than the target on both axes.",
        VideoResizePolicy.DownscaleOnly => "Scale only when the source is larger than the target.",
        _ => ""
    };

    public static string SizingTip(VideoSizingMode mode) => mode switch
    {
        VideoSizingMode.Fit => "Fit the whole picture in the player. Letterbox if the aspect does not match.",
        VideoSizingMode.FillCrop => "Fill the player and crop overflow. Aspect is kept.",
        VideoSizingMode.Stretch => "Fill the player. The picture may distort. A forced aspect is still stored for Fit/Fill.",
        VideoSizingMode.Original => "Keep the coded resolution. The player may show empty space.",
        VideoSizingMode.Multiplier => "Scale the source by 1x, 1.5x, 2x, or a custom factor.",
        VideoSizingMode.CustomResolution => "Scale to an exact width and height.",
        VideoSizingMode.MatchDisplay => "Scale to the pixel size of the monitor that currently holds the player.",
        _ => ""
    };

    public static string AspectTip(VideoAspectMode aspect) => aspect switch
    {
        VideoAspectMode.KeepSource => "Use the aspect stored in the file.",
        VideoAspectMode.Ratio16x9 => "Force a 16:9 picture. Sizing mode still places it in the player.",
        VideoAspectMode.Ratio4x3 => "Force a 4:3 picture. Sizing mode still places it in the player.",
        VideoAspectMode.Ratio32x9 => "Force a 32:9 ultrawide picture. On a 32:9 monitor, Fit in fullscreen fills the screen.",
        VideoAspectMode.Stretch => "Treat pixels as square and ignore a wrong container aspect.",
        VideoAspectMode.Custom => "Force a custom ratio such as 21:9.",
        _ => ""
    };

    private static VideoSize ScaleSource(VideoResizeContext ctx, double factor)
    {
        if (ctx.SourceW <= 0 || ctx.SourceH <= 0 || !IsValidMultiplier(factor))
        {
            return new VideoSize(0, 0);
        }

        return new VideoSize(
            Math.Clamp((int)Math.Round(ctx.SourceW * factor), MinPixels, MaxPixels),
            Math.Clamp((int)Math.Round(ctx.SourceH * factor), MinPixels, MaxPixels));
    }

    private static (double X, double Y) ExplicitScale(VideoResizeSettings settings, VideoResizeContext ctx)
    {
        if (settings.Sizing == VideoSizingMode.Multiplier)
        {
            var n = MultiplierValue(settings);
            return IsValidMultiplier(n) ? (n, n) : (1, 1);
        }

        if (ctx.SourceW <= 0 || ctx.SourceH <= 0)
        {
            return (1, 1);
        }

        var target = TargetSize(settings, ctx);
        if (target.W <= 0 || target.H <= 0)
        {
            return (1, 1);
        }

        return (target.W / (double)ctx.SourceW, target.H / (double)ctx.SourceH);
    }
}
