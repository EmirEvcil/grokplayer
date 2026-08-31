namespace Grok.Player.Core.Preview;

public sealed class SeekPreviewScheduler : IDisposable
{
    private static readonly double[] NetworkHoverOffsets = [-2, 2, -5, 5, -10, 10, -20, 20];
    private static readonly double[] LocalHoverOffsets = [-1, 1, -3, 3, -6, 6];
    private readonly ISeekPreviewRenderer _renderer;
    private readonly double _bucketSeconds;
    private readonly int _atlasUpgradeDelayMs;
    private readonly Dictionary<int, CachedStill> _cache = [];
    private readonly LinkedList<int> _lru = [];
    private readonly Queue<TimeSpan> _prefetch = [];
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private string? _path;
    private string? _referer;
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

    public void SetMedia(string? path, TimeSpan? duration, bool prefetch) =>
        SetMedia(path, duration, prefetch, referer: null);

    public void SetMedia(string? path, TimeSpan? duration, bool prefetch, string? referer)
    {
        var wake = false;
        lock (_gate)
        {
            _referer = referer;
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
            if (_cache.TryGetValue(bucket, out var decoded) && File.Exists(decoded.Path))
            {
                Touch_NoLock(bucket);
                return decoded.Path;
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

            Store_NoLock(Bucket(time), image, high: true);
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
                // Keep the background coverage grid. Only pull nearby
                // uncached neighbors in front of it so a long VOD still fills.
                EnqueueAroundFront_NoLock(time);
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
                    try { PrepareRenderer(path); } catch { }
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

                // A high-tier decoder still always wins. A low-tier coverage
                // frame is shown immediately, then the hover upgrades it.
                var decodedBucket = Bucket(requested);
                string? decodedHigh = null;
                string? decodedLow = null;
                lock (_gate)
                {
                    if (_cache.TryGetValue(decodedBucket, out var decoded) && File.Exists(decoded.Path))
                    {
                        Touch_NoLock(decodedBucket);
                        if (decoded.High)
                        {
                            decodedHigh = decoded.Path;
                        }
                        else
                        {
                            decodedLow = decoded.Path;
                        }
                    }
                }
                if (decodedHigh is not null)
                {
                    PublishCurrent(path, atlas, requested, decodedHigh, allowSameAtlasFrame: false);
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
                lock (_gate)
                {
                    if (_path != path || _pending >= TimeSpan.Zero) continue;
                    if (decodedLow is null &&
                        _cache.TryGetValue(bucket, out var cached) &&
                        File.Exists(cached.Path))
                    {
                        Touch_NoLock(bucket);
                        if (cached.High)
                        {
                            decodedHigh = cached.Path;
                        }
                        else
                        {
                            decodedLow = cached.Path;
                        }
                    }
                }

                if (decodedHigh is not null)
                {
                    PublishCurrent(path, atlas, requested, decodedHigh, allowSameAtlasFrame: false);
                    continue;
                }

                try
                {
                    if (behindLiveSeconds is { } behind)
                    {
                        var live = _renderer is ILiveSeekPreviewRenderer liveRenderer
                            ? liveRenderer.CaptureBehindLive(path, behind, requestedUtc)
                            : null;
                        if (live is null)
                        {
                            continue;
                        }

                        if (!KeepAndPublish(path, atlas, requested, bucket, live, high: true))
                        {
                            break;
                        }

                        continue;
                    }

                    PrepareRenderer(path);
                    var captureTime = atlas?.FrameTime(requested) ?? requested;
                    var hover = Math.Abs((_hover - requested).TotalSeconds) <= _bucketSeconds;
                    if (atlas is not null && _renderer is IExactSeekPreviewRenderer exactRenderer)
                    {
                        var exact = exactRenderer.CaptureExact(captureTime);
                        if (exact is null)
                        {
                            continue;
                        }

                        if (!KeepAndPublish(path, atlas, requested, bucket, exact, high: true))
                        {
                            break;
                        }

                        continue;
                    }

                    if (hover && _renderer is IFastSeekPreviewRenderer fast)
                    {
                        if (decodedLow is not null)
                        {
                            PublishCurrent(path, atlas, requested, decodedLow, allowSameAtlasFrame: false);
                        }
                        else
                        {
                            var low = fast.CaptureFast(captureTime);
                            if (low is not null &&
                                !KeepAndPublish(path, atlas, requested, bucket, low, high: false))
                            {
                                break;
                            }
                        }

                        if (!IsCurrentHover(path, atlas, requested))
                        {
                            continue;
                        }

                        var upgraded = _renderer.Capture(captureTime);
                        if (upgraded is null)
                        {
                            continue;
                        }

                        if (!KeepAndPublish(path, atlas, requested, bucket, upgraded, high: true))
                        {
                            break;
                        }

                        continue;
                    }

                    var image = hover || _renderer is not IFastSeekPreviewRenderer cheap
                        ? _renderer.Capture(captureTime)
                        : cheap.CaptureFast(captureTime);
                    if (image is null)
                    {
                        continue;
                    }

                    if (!KeepAndPublish(path, atlas, requested, bucket, image, high: hover))
                    {
                        break;
                    }
                }
                catch
                {
                    // Preview must never take down playback.
                }
            }
        }
    }

    private bool KeepAndPublish(
        string path,
        IPreviewAtlas? atlas,
        TimeSpan time,
        int bucket,
        string image,
        bool high)
    {
        lock (_gate)
        {
            if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(image);
                return false;
            }

            if (!Store_NoLock(bucket, image, high))
            {
                return true;
            }
        }

        PublishCurrent(path, atlas, time, image, allowSameAtlasFrame: false);
        return true;
    }

    private bool Store_NoLock(int bucket, string image, bool high)
    {
        if (_cache.TryGetValue(bucket, out var previous))
        {
            if (!high && previous.High)
            {
                TryDelete(image);
                return false;
            }

            if (previous.Path != image)
            {
                TryDelete(previous.Path);
            }
        }

        foreach (var (other, still) in _cache)
        {
            if (other == bucket ||
                !string.Equals(still.Path, image, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var gap = Math.Abs((other - bucket) * ActiveBucket);
            if (gap > SeekPreviewDisplay.DecoderDeltaSeconds)
            {
                return false;
            }
        }

        _cache[bucket] = new CachedStill(image, high);
        Touch_NoLock(bucket);
        while (_cache.Count > 160)
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
                TryDelete(evicted.Path);
            }
        }

        return true;
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

    private void PrepareRenderer(string path)
    {
        string? referer;
        lock (_gate)
        {
            referer = _referer;
        }

        if (_renderer is INetworkSeekPreviewRenderer networked)
        {
            networked.Prepare(path, referer);
            return;
        }

        _renderer.Prepare(path);
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

    private void EnqueueAroundFront_NoLock(TimeSpan center)
    {
        var extras = new List<TimeSpan>();
        var seconds = Math.Max(0, center.TotalSeconds);
        var offsets = _network ? NetworkHoverOffsets : LocalHoverOffsets;
        foreach (var offset in offsets)
        {
            var time = TimeSpan.FromSeconds(Math.Max(0, seconds + offset));
            if (!_cache.ContainsKey(Bucket(time)))
            {
                extras.Add(time);
            }
        }

        if (extras.Count == 0)
        {
            return;
        }

        var rest = _prefetch.ToArray();
        _prefetch.Clear();
        foreach (var time in extras)
        {
            _prefetch.Enqueue(time);
        }

        foreach (var time in rest)
        {
            _prefetch.Enqueue(time);
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
            ? (int)Math.Clamp(Math.Round(span / 20.0), 16, 96)
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
        foreach (var (bucket, still) in _cache)
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
                best = still.Path;
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
        foreach (var still in _cache.Values)
        {
            TryDelete(still.Path);
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

    private readonly record struct CachedStill(string Path, bool High);
}
