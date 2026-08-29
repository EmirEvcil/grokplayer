namespace Grok.Player.Core.Preview;

public sealed class SeekPreviewScheduler : IDisposable
{
    private static readonly double[] NetworkHoverOffsets = [-2, 2, -5, 5, -10, 10, -20, 20];
    private static readonly double[] LocalHoverOffsets = [-1, 1, -3, 3, -6, 6];
    private readonly ISeekPreviewRenderer _renderer;
    private readonly double _bucketSeconds;
    private readonly int _atlasUpgradeDelayMs;
    private readonly Dictionary<int, string> _cache = [];
    private readonly LinkedList<int> _lru = [];
    private readonly Queue<TimeSpan> _prefetch = [];
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private string? _path;
    private bool _network;
    private TimeSpan _pending = TimeSpan.FromSeconds(-1);
    private TimeSpan _hover = TimeSpan.FromSeconds(-1);
    private double? _pendingBehindLiveSeconds;
    private DateTime _pendingRequestedUtc;
    private bool _storyboardQueued;
    private bool _warmRequested;
    private bool _warmPending;
    private IPreviewAtlas? _atlas;
    private volatile bool _running = true;

    public SeekPreviewScheduler(
        ISeekPreviewRenderer renderer,
        double bucketSeconds = 0.2,
        int atlasUpgradeDelayMs = 260)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _bucketSeconds = Math.Max(0.05, bucketSeconds);
        _atlasUpgradeDelayMs = Math.Max(0, atlasUpgradeDelayMs);
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "seek-preview",
            Priority = ThreadPriority.BelowNormal
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
                _hover = TimeSpan.FromSeconds(-1);
                _pendingBehindLiveSeconds = null;
                _pendingRequestedUtc = default;
                _prefetch.Clear();
                _storyboardQueued = false;
                _warmRequested = false;
                _warmPending = false;
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
            var bucket = Bucket(time);
            // A decoder capture is the final quality tier. Do not downgrade it
            // back to a 160x90 storyboard when the pointer moves over the same bucket.
            if (_cache.TryGetValue(bucket, out var decoded) && File.Exists(decoded))
            {
                Touch_NoLock(bucket);
                return decoded;
            }

            if (_atlas is not null && _atlas.TryGetFrame(time, out var atlas) && File.Exists(atlas))
            {
                return atlas;
            }

            // With a storyboard source, a nearby decoded frame belongs to a
            // different hover target. Showing it while the requested cell is
            // loading creates a visible stale-image flash. Wait for the correct
            // storyboard/decoder result instead of borrowing a neighbor.
            if (_atlas is not null)
            {
                return null;
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

    public void Request(string path, TimeSpan time) => Request(path, time, includeNeighbors: true);

    public void RequestExact(string path, TimeSpan time) =>
        Request(path, time, includeNeighbors: false);

    public void RequestLiveExact(string path, TimeSpan time, double behindLiveSeconds) =>
        Request(path, time, includeNeighbors: false, behindLiveSeconds, DateTime.UtcNow);

    public void Warm(string path)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(path) || _warmRequested) return;
            _warmRequested = true;
            _warmPending = true;
        }
        _signal.Set();
    }

    private void Request(
        string path,
        TimeSpan time,
        bool includeNeighbors,
        double? behindLiveSeconds = null,
        DateTime requestedUtc = default)
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
            _pendingBehindLiveSeconds = behindLiveSeconds;
            _pendingRequestedUtc = requestedUtc;
            _hover = time;
            if (_atlas is not null)
            {
                _prefetch.Clear();
                _atlas.Prioritize(time);
                var step = _atlas.IntervalSeconds;
                foreach (var offset in new[] { -1, 1, -2, 2 })
                    _prefetch.Enqueue(TimeSpan.FromSeconds(Math.Max(0, time.TotalSeconds + step * offset)));
            }
            else if (_network && includeNeighbors)
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
                bool warm;
                double? behindLiveSeconds;
                DateTime requestedUtc;
                lock (_gate)
                {
                    path = _path;
                    requested = TakeNext_NoLock(out behindLiveSeconds, out requestedUtc);
                    atlas = _atlas;
                    warm = requested < TimeSpan.Zero && _warmPending;
                    if (warm) _warmPending = false;
                }

                if (warm && !string.IsNullOrWhiteSpace(path))
                {
                    try { _renderer.Prepare(path); } catch { }
                    continue;
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

                // Once the final decoder tier exists, it always wins. Checking
                // this only after publishing atlas frames made repeated pointer
                // events alternate 512x288 -> 320x180 -> 512x288.
                var decodedBucket = Bucket(requested);
                string? decoded;
                lock (_gate)
                {
                    _cache.TryGetValue(decodedBucket, out decoded);
                    if (decoded is not null && File.Exists(decoded))
                        Touch_NoLock(decodedBucket);
                    else
                        decoded = null;
                }
                if (decoded is not null)
                {
                    PublishCurrent(path, atlas, requested, decoded, allowSameAtlasFrame: false);
                    continue;
                }

                if (atlas is not null)
                {
                    var atlasServed = false;
                    try
                    {
                        if (atlas.TryGetOrFetch(requested, out var tile) && File.Exists(tile))
                        {
                            atlasServed = true;
                            PublishCurrent(path, atlas, requested, tile, allowSameAtlasFrame: true);
                            if (atlas.TryGetOrFetchBest(requested, out var best) &&
                                File.Exists(best) && !string.Equals(best, tile, StringComparison.OrdinalIgnoreCase))
                                PublishCurrent(path, atlas, requested, best, allowSameAtlasFrame: true);
                        }
                    }
                    catch
                    {
                    }

                    if (atlasServed)
                    {
                        if (!atlas.NeedsDecodedUpgrade)
                        {
                            continue;
                        }

                        // Storyboards often top out at 160x90. Upgrade only after
                        // a short dwell; a new pointer request wakes this wait and
                        // replaces the pending target before decoder work starts.
                        if (!WaitForStableHover(path, atlas, requested))
                        {
                            continue;
                        }
                    }
                }

                var bucket = Bucket(requested);
                string? cached;
                lock (_gate)
                {
                    if (_path != path || _pending >= TimeSpan.Zero) continue;
                    if (_cache.TryGetValue(bucket, out cached))
                    {
                        Touch_NoLock(bucket);
                    }
                }

                if (cached is not null)
                {
                    PublishCurrent(path, atlas, requested, cached, allowSameAtlasFrame: false);
                    continue;
                }

                try
                {
                    string? image;
                    if (behindLiveSeconds is { } behind)
                    {
                        image = _renderer is ILiveSeekPreviewRenderer liveRenderer
                            ? liveRenderer.CaptureBehindLive(path, behind, requestedUtc)
                            : null;
                    }
                    else
                    {
                        _renderer.Prepare(path);
                        // Match the discrete storyboard cell in the final decoder
                        // tier. PlaybackRestart still guarantees this is a fresh
                        // frame rather than stale decoder output.
                        var captureTime = atlas?.FrameTime(requested) ?? requested;
                        image = atlas is not null && _renderer is IExactSeekPreviewRenderer exactRenderer
                            ? exactRenderer.CaptureExact(captureTime)
                            : _renderer.Capture(captureTime);
                    }
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

                    PublishCurrent(path, atlas, requested, image, allowSameAtlasFrame: false);
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

    private void PublishCurrent(
        string path,
        IPreviewAtlas? atlas,
        TimeSpan time,
        string image,
        bool allowSameAtlasFrame)
    {
        var publish = false;
        lock (_gate)
        {
            // Speculative neighbors are cached, but must never replace the exact
            // hovered frame or leak from an earlier media/atlas generation.
            var currentFrame = Math.Abs((_hover - time).TotalSeconds) <= _bucketSeconds / 2 ||
                               allowSameAtlasFrame && atlas?.RepresentsSameFrame(_hover, time) == true;
            if (!_running || _path != path || !ReferenceEquals(_atlas, atlas) || !currentFrame)
                return;
            publish = true;
        }
        if (publish) FrameReady?.Invoke(time, image);
    }

    private bool IsCurrentHover(string path, IPreviewAtlas? atlas, TimeSpan time)
    {
        lock (_gate)
        {
            return _running && _path == path && ReferenceEquals(_atlas, atlas) &&
                   _pending < TimeSpan.Zero &&
                   Math.Abs((_hover - time).TotalSeconds) <= _bucketSeconds / 2;
        }
    }

    private bool WaitForStableHover(string path, IPreviewAtlas? atlas, TimeSpan time)
    {
        var deadline = Environment.TickCount64 + _atlasUpgradeDelayMs;
        while (true)
        {
            if (!IsCurrentHover(path, atlas, time)) return false;
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return true;

            // A signal may be left over from the request that produced `time`.
            // Consume it, but only cancel when the actual hover target changed.
            _signal.WaitOne((int)Math.Min(int.MaxValue, remaining));
        }
    }

    private void Touch_NoLock(int bucket)
    {
        _lru.Remove(bucket);
        _lru.AddFirst(bucket);
    }

    private TimeSpan TakeNext_NoLock(out double? behindLiveSeconds, out DateTime requestedUtc)
    {
        behindLiveSeconds = null;
        requestedUtc = default;
        if (_pending >= TimeSpan.Zero)
        {
            var hover = _pending;
            behindLiveSeconds = _pendingBehindLiveSeconds;
            requestedUtc = _pendingRequestedUtc;
            _pending = TimeSpan.FromSeconds(-1);
            _pendingBehindLiveSeconds = null;
            _pendingRequestedUtc = default;
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
