namespace Grok.Player.Core.Preview;

public interface ISeekPreviewRenderer : IDisposable
{
    void Prepare(string path);
    string? Capture(TimeSpan time);
    void Reset();
}
