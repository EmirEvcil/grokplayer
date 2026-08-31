namespace Grok.Player.Core.Preview;

public static class SeekPreviewDisplay
{
    // A still may only be shown when it belongs to this hover. Recycling
    // the last bitmap or a neighbor tens of seconds away looks like the
    // same 2–3 frames stamped on unrelated times.
    public const double DecoderDeltaSeconds = 2;

    // HLS keyframes can sit a few seconds off the requested time. That is
    // still the same moment. Ten minutes away is not.
    public const double KeyframeToleranceSeconds = 8;

    public static bool Fits(TimeSpan frameTime, TimeSpan hoverTime, double allowedSeconds) =>
        Math.Abs((frameTime - hoverTime).TotalSeconds) <= Math.Max(0, allowedSeconds);
}
