namespace Grok.Player.Core.Preview;

public sealed class CachedPreviewClip : IDisposable
{
    private readonly SeekPreviewEngine _decoder = SeekPreviewEngine.Create();
    private bool _disposed;

    public CachedPreviewClip(string path, TimeSpan origin, TimeSpan end)
    {
        Path = path;
        Origin = origin;
        End = end;
    }

    public string Path { get; }
    public TimeSpan Origin { get; }
    public TimeSpan End { get; }
    public bool Contains(TimeSpan time) => time >= Origin && time < End;

    public string? Capture(TimeSpan absoluteTime)
    {
        if (_disposed || !Contains(absoluteTime)) return null;
        _decoder.Prepare(Path);
        return _decoder.Capture(absoluteTime - Origin, exact: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _decoder.Dispose();
        try { File.Delete(Path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
