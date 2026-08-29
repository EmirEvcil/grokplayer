namespace Grok.Player.Core.Preview;

public interface ISeekPreviewRenderer : IDisposable
{
    void Prepare(string path);
    string? Capture(TimeSpan time);
    void Reset();
}

public interface IExactSeekPreviewRenderer
{
    string? CaptureExact(TimeSpan time);
}

public interface ILiveSeekPreviewRenderer
{
    string? CaptureBehindLive(string path, double behindLiveSeconds, DateTime requestedUtc);
}
