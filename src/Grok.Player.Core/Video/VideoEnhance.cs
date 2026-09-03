using System.Globalization;

namespace Grok.Player.Core.Video;

public enum HdrOutputMode
{
    Off,
    Native,
    Rtx
}

public static class VideoEnhanceSpec
{
    public static string? D3d11Vpp(bool superResolution, double scale, bool rtxHdr)
    {
        if (!superResolution && !rtxHdr)
        {
            return null;
        }

        var parts = new List<string>();
        if (superResolution)
        {
            parts.Add("scaling-mode=nvidia");
            parts.Add("scale=" + ClampScale(scale).ToString("0.#", CultureInfo.InvariantCulture));
        }

        if (rtxHdr)
        {
            parts.Add("format=p010");
            parts.Add("nvidia-true-hdr");
        }

        return "d3d11vpp=" + string.Join(':', parts);
    }

    public static bool NeedsZeroCopyDecode(bool superResolution, HdrOutputMode hdr) =>
        superResolution || hdr != HdrOutputMode.Off;

    public static double ClampScale(double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale < 1)
        {
            return 1;
        }

        return Math.Clamp(Math.Round(scale, MidpointRounding.AwayFromZero), 1, 4);
    }

    public static double Scale(int sourceHeight, int playerHeight)
    {
        if (sourceHeight <= 0 || playerHeight <= 0)
        {
            return 2;
        }

        return ClampScale(playerHeight / (double)sourceHeight);
    }

    public static string Hint(HdrOutputMode mode) => mode == HdrOutputMode.Off ? "no" : "yes";

    public static string HintMode(HdrOutputMode mode) =>
        mode == HdrOutputMode.Off ? "target" : "source";

    public static bool NeedsVideoProcessor(bool superResolution, HdrOutputMode hdr) =>
        superResolution || hdr == HdrOutputMode.Rtx;

    public static string Label(HdrOutputMode mode) => mode switch
    {
        HdrOutputMode.Off => "Off",
        HdrOutputMode.Rtx => "RTX",
        _ => "Native"
    };
}

public readonly record struct VideoEnhanceResult(bool HdrApplied, bool VppNeeded, bool VppApplied)
{
    public bool Ok => HdrApplied && (!VppNeeded || VppApplied);
}
