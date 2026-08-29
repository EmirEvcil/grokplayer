namespace Grok.Player.Core.Preview;

public interface IPreviewAtlas : IDisposable
{
    double IntervalSeconds { get; }

    bool NeedsDecodedUpgrade => false;

    bool TryGetFrame(TimeSpan time, out string path);

    void Prefetch(TimeSpan time);

    void Prioritize(TimeSpan time) { }

    bool RepresentsSameFrame(TimeSpan left, TimeSpan right) => false;

    // Return the source timestamp represented by the storyboard cell so the
    // decoded quality tier sharpens the same image instead of changing scene.
    TimeSpan FrameTime(TimeSpan time) => time;

    bool TryGetOrFetch(TimeSpan time, out string path);

    bool TryGetOrFetchBest(TimeSpan time, out string path) => TryGetOrFetch(time, out path);
}
