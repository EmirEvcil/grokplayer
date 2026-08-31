namespace Grok.Player.Core.Preview;

public interface ISeekPreviewRenderer : IDisposable
{
    void Prepare(string path);
    string? Capture(TimeSpan time);
    void Reset();
}

public interface INetworkSeekPreviewRenderer
{
    void Prepare(string path, string? referer);
}

public interface IExactSeekPreviewRenderer
{
    string? CaptureExact(TimeSpan time);
}

public interface IFastSeekPreviewRenderer
{
    string? CaptureFast(TimeSpan time);
}

public interface ILiveSeekPreviewRenderer
{
    string? CaptureBehindLive(string path, double behindLiveSeconds, DateTime requestedUtc);
}
