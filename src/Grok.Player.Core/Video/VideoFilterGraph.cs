namespace Grok.Player.Core.Video;

public static class VideoFilterGraph
{
    // lavfi deblock: destaircase 8x8 DCT edges. Strong + slightly open
    // thresholds so a high-QP clip changes without smearing faces.
    public const string Deblock =
        "deblock=filter=strong:block=8:alpha=0.16:beta=0.10:gamma=0.08:delta=0.07";

    // Edge-aware soften: spatial+light temporal denoise, then smartblur
    // so grain/ringing drop while hard edges stay. Not a box blur.
    public const string SoftenDenoise = "hqdn3d=3.2:2.2:4.8:3.6";
    public const string SoftenBlur = "smartblur=lr=1.15:ls=0.42:lt=5:cr=0.55:cs=0.22:ct=4";

    // 5x5 unsharp, modest amounts — visible on a soft source, not halo soup.
    public const string Sharpen = "unsharp=5:5:0.78:5:5:0.28";

    public static string Build(bool softer, bool sharpen, bool deblock)
    {
        var parts = new List<string>(4);
        if (deblock)
        {
            parts.Add(Deblock);
        }

        if (softer)
        {
            parts.Add(SoftenDenoise);
            parts.Add(SoftenBlur);
        }

        if (sharpen)
        {
            parts.Add(Sharpen);
        }

        return parts.Count == 0 ? string.Empty : "lavfi=[" + string.Join(",", parts) + "]";
    }
}
