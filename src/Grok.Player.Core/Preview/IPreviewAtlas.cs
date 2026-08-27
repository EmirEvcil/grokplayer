namespace Grok.Player.Core.Preview;

public interface IPreviewAtlas : IDisposable
{
    double IntervalSeconds { get; }

    bool TryGetFrame(TimeSpan time, out string path);

    void Prefetch(TimeSpan time);

    bool TryGetOrFetch(TimeSpan time, out string path);
}
