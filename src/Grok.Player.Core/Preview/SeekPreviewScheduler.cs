namespace Grok.Player.Core.Preview;

public sealed class SeekPreviewScheduler : IDisposable
{
    private static readonly double[] NetworkHoverOffsets = [-2, 2, -5, 5, -10, 10, -20, 20];
    private static readonly double[] LocalHoverOffsets = [-1, 1, -3, 3, -6, 6];
    private readonly ISeekPreviewRenderer _renderer;
    private readonly double _bucketSeconds;
    private readonly Dictionary<int, string> _cache = [];
    private readonly LinkedList<int> _lru = [];
    private readonly Queue<TimeSpan> _prefetch = [];
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private string? _path;
    private bool _network;
    private TimeSpan _pending = TimeSpan.FromSeconds(-1);
    private bool _storyboardQueued;
    private IPreviewAtlas? _atlas;
    private volatile bool _running = true;

    public SeekPreviewScheduler(ISeekPreviewRenderer renderer, double bucketSeconds = 0.2)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _bucketSeconds = Math.Max(0.05, bucketSeconds);
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "seek-preview",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    public event Action<TimeSpan, string>? FrameReady;

    public void SetAtlas(IPreviewAtlas? atlas)
    {
        lock (_gate)
        {
            _atlas = atlas;
        }
    }

    public void SetMedia(string? path) => SetMedia(path, null);

    public void SetMedia(string? path, TimeSpan? duration) => SetMedia(path, duration, prefetch: true);

    public void PrefetchRange(TimeSpan start, TimeSpan end)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_path) || end <= start)
            {
                return;
            }

            EnqueueRange_NoLock(start, end);
        }

        _signal.Set();
    }

    public void SetMedia(string? path, TimeSpan? duration, bool prefetch)
    {
        var wake = false;
        lock (_gate)
        {
            if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
            {
                _path = path;
                _network = path is not null && path.Contains("://", StringComparison.Ordinal);
                ClearCache_NoLock();
                _pending = TimeSpan.FromSeconds(-1);
                _prefetch.Clear();
                _storyboardQueued = false;
                wake = true;
            }

            if (prefetch &&
                !_storyboardQueued &&
                !_network &&
                duration is { } length &&
                length > TimeSpan.Zero &&
                !string.IsNullOrWhiteSpace(_path))
            {
                EnqueueRange_NoLock(TimeSpan.Zero, length);
                _storyboardQueued = true;
                wake = true;
            }
        }

        if (wake)
        {
            _signal.Set();
        }
    }

    public string? GetCached(TimeSpan time) => GetCached(time, maxDeltaSeconds: 10);

    public string? GetCached(TimeSpan time, double maxDeltaSeconds)
    {
        lock (_gate)
        {
            if (_atlas is not null && _atlas.TryGetFrame(time, out var atlas) && File.Exists(atlas))
            {
                return atlas;
            }

            var bucket = Bucket(time);
            if (_cache.TryGetValue(bucket, out var path))
            {
                Touch_NoLock(bucket);
                return path;
            }

            var behind = maxDeltaSeconds < 0 ? (_network ? 10 : double.MaxValue) : maxDeltaSeconds;
            var ahead = maxDeltaSeconds < 0 ? (_network ? 3 : double.MaxValue) : Math.Min(3, maxDeltaSeconds);
            return Nearest_NoLock(time, behind, ahead);
        }
    }

    public void Remember(TimeSpan time, string image)
    {
        if (string.IsNullOrWhiteSpace(image) || !File.Exists(image))
        {
            return;
        }

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_path))
            {
                return;
            }

            Store_NoLock(Bucket(time), image);
        }
    }

    public void Request(string path, TimeSpan time)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                !string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
            {
                _path = path;
                _network = path.Contains("://", StringComparison.Ordinal);
                ClearCache_NoLock();
            }

            _pending = time;
            if (_atlas is not null)
            {
                // Worker will pull the atlas cell first.
            }
            else if (_network)
            {
                // A new hover supersedes speculative work for the old location.
                // The exact point is handled by _pending, then its closest neighbors.
                _prefetch.Clear();
                EnqueueAround_NoLock(time);
            }
        }

        _signal.Set();
    }

    public void Request(TimeSpan time) => Request(_path ?? "", time);

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        if (!_thread.Join(1500))
        {
            // Worker is in native capture; it will exit on next loop.
        }

        lock (_gate)
        {
            ClearCache_NoLock();
        }

        _renderer.Dispose();
        _signal.Dispose();
    }

    private void Loop()
    {
        while (_running)
        {
            _signal.WaitOne();
            while (_running)
            {
                string? path;
                TimeSpan requested;
                IPreviewAtlas? atlas;
                lock (_gate)
                {
                    path = _path;
                    requested = TakeNext_NoLock();
                    atlas = _atlas;
                }

                if (string.IsNullOrWhiteSpace(path) || requested < TimeSpan.Zero)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        try
                        {
                            _renderer.Reset();
                        }
                        catch
                        {
                        }
                    }

                    break;
                }

                if (atlas is not null)
                {
                    try
                    {
                        if (atlas.TryGetOrFetch(requested, out var tile) && File.Exists(tile))
                        {
                            FrameReady?.Invoke(requested, tile);
                            continue;
                        }
                    }
                    catch
                    {
                    }
                }

                var bucket = Bucket(requested);
                string? cached;
                lock (_gate)
                {
                    if (_cache.TryGetValue(bucket, out cached))
                    {
                        Touch_NoLock(bucket);
                    }
                }

                if (cached is not null)
                {
                    FrameReady?.Invoke(requested, cached);
                    continue;
                }

                try
                {
                    _renderer.Prepare(path);
                    var image = _renderer.Capture(requested);
                    if (image is null)
                    {
                        continue;
                    }

                    lock (_gate)
                    {
                        if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
                        {
                            TryDelete(image);
                            break;
                        }

                        Store_NoLock(bucket, image);
                    }

                    FrameReady?.Invoke(requested, image);
                }
                catch
                {
                    // Preview must never take down playback.
                }
            }
        }
    }

    private void Store_NoLock(int bucket, string image)
    {
        if (_cache.TryGetValue(bucket, out var previous) && previous != image)
        {
            TryDelete(previous);
        }

        _cache[bucket] = image;
        Touch_NoLock(bucket);
        while (_cache.Count > 64)
        {
            var oldestNode = _lru.Last;
            if (oldestNode is null)
            {
                break;
            }

            var oldest = oldestNode.Value;
            _lru.RemoveLast();
            if (_cache.Remove(oldest, out var evicted))
            {
                TryDelete(evicted);
            }
        }
    }

    private void Touch_NoLock(int bucket)
    {
        _lru.Remove(bucket);
        _lru.AddFirst(bucket);
    }

    private TimeSpan TakeNext_NoLock()
    {
        if (_pending >= TimeSpan.Zero)
        {
            var hover = _pending;
            _pending = TimeSpan.FromSeconds(-1);
            return hover;
        }

        while (_prefetch.Count > 0)
        {
            var next = _prefetch.Dequeue();
            if (!_cache.ContainsKey(Bucket(next)))
            {
                return next;
            }
        }

        return TimeSpan.FromSeconds(-1);
    }

    private void EnqueueAround_NoLock(TimeSpan center)
    {
        var seconds = Math.Max(0, center.TotalSeconds);
        var offsets = _network ? NetworkHoverOffsets : LocalHoverOffsets;
        foreach (var offset in offsets)
        {
            var time = TimeSpan.FromSeconds(Math.Max(0, seconds + offset));
            if (!_cache.ContainsKey(Bucket(time)))
            {
                _prefetch.Enqueue(time);
            }
        }
    }

    private void EnqueueRange_NoLock(TimeSpan start, TimeSpan end)
    {
        var from = Math.Max(0, start.TotalSeconds);
        var to = Math.Max(from, end.TotalSeconds);
        var span = to - from;
        if (span < 0.25)
        {
            return;
        }

        var count = _network
            ? (int)Math.Clamp(Math.Round(span / 8.0), 3, 8)
            : (int)Math.Clamp(Math.Round(span / 3.0), 6, 48);
        if (span < 6)
        {
            count = Math.Max(2, (int)Math.Ceiling(span));
        }

        var step = span / count;
        for (var i = 0; i < count; i++)
        {
            var time = TimeSpan.FromSeconds(Math.Clamp(from + (i + 0.5) * step, from, to));
            if (!_cache.ContainsKey(Bucket(time)))
            {
                _prefetch.Enqueue(time);
            }
        }
    }

    private string? Nearest_NoLock(TimeSpan time, double maxBehind, double maxAhead)
    {
        if (_cache.Count == 0)
        {
            return null;
        }

        var target = time.TotalSeconds;
        string? best = null;
        var bestBucket = 0;
        var bestScore = double.MaxValue;
        foreach (var (bucket, path) in _cache)
        {
            var at = (bucket * ActiveBucket) + (ActiveBucket * 0.5);
            var offset = at - target;
            if (offset < -maxBehind || offset > maxAhead)
            {
                continue;
            }

            // Prefer a frame just behind the hover over one slightly ahead.
            var score = offset <= 0 ? -offset : offset + 0.75;
            if (score < bestScore)
            {
                bestScore = score;
                best = path;
                bestBucket = bucket;
            }
        }

        if (best is not null)
        {
            Touch_NoLock(bestBucket);
        }

        return best;
    }

    private void ClearCache_NoLock()
    {
        foreach (var path in _cache.Values)
        {
            TryDelete(path);
        }

        _cache.Clear();
        _lru.Clear();
        _prefetch.Clear();
        _storyboardQueued = false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private double ActiveBucket => _network ? Math.Max(2, _bucketSeconds * 10) : _bucketSeconds;

    private int Bucket(TimeSpan time) =>
        (int)Math.Floor(Math.Max(0, time.TotalSeconds) / ActiveBucket);
}
