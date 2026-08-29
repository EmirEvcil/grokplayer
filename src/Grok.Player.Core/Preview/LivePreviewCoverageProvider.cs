namespace Grok.Player.Core.Preview;

/// <summary>Builds a cheap, bounded thumbnail map for the existing HLS DVR window.</summary>
public sealed class LivePreviewCoverageProvider : IDisposable
{
    private readonly object _gate = new();
    private readonly LivePreviewBuffer _frames;
    private CancellationTokenSource? _run;
    private string? _source;
    private int _generation;
    private bool _disposed;

    public LivePreviewCoverageProvider(LivePreviewBuffer frames) =>
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));

    public event EventHandler? CoverageReady;

    public void Start(string source, double liveEdgeSeconds, double keepSeconds)
    {
        CancellationTokenSource cancellation;
        int generation;
        lock (_gate)
        {
            if (_disposed || string.IsNullOrWhiteSpace(source) ||
                string.Equals(_source, source, StringComparison.Ordinal)) return;
            _run?.Cancel();
            _run?.Dispose();
            _run = cancellation = new CancellationTokenSource();
            _source = source;
            generation = ++_generation;
        }

        var requestedUtc = DateTime.UtcNow;
        _ = Task.Run(() => Populate(source, liveEdgeSeconds, keepSeconds, requestedUtc, generation, cancellation.Token));
    }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _source = null;
            _run?.Cancel();
            _run?.Dispose();
            _run = null;
        }
    }

    private void Populate(
        string source,
        double liveEdgeSeconds,
        double keepSeconds,
        DateTime requestedUtc,
        int generation,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<HlsLivePreviewExtractor.CoverageFrame> frames = [];
        try
        {
            frames = HlsLivePreviewExtractor.CaptureCoverage(
                source, liveEdgeSeconds, keepSeconds, requestedUtc, cancellationToken);
            var stored = 0;
            foreach (var frame in frames)
            {
                bool current;
                lock (_gate) current = !_disposed && generation == _generation;
                if (!current || cancellationToken.IsCancellationRequested) break;
                if (_frames.Store(frame.Time, frame.Path, deleteSource: true)) stored++;
            }
            if (stored > 0 && !cancellationToken.IsCancellationRequested)
                CoverageReady?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var frame in frames)
            {
                try { File.Delete(frame.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                var folder = Path.GetDirectoryName(frame.Path);
                if (folder is null) continue;
                try { Directory.Delete(folder, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Reset();
    }
}
