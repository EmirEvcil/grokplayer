namespace Grok.Player.Core.Preview;

public sealed class SeekPreviewScheduler : IDisposable
{
    private static readonly double[] NetworkHoverOffsets = [-2, 2, -5, 5, -10, 10, -20, 20];
    private static readonly double[] LocalHoverOffsets = [-1, 1, -3, 3, -6, 6];
    private readonly ISeekPreviewRenderer _renderer;
    private ISeekPreviewRenderer? _coverage;
    private readonly double _bucketSeconds;
    private readonly int _atlasUpgradeDelayMs;
    private readonly Dictionary<int, CachedStill> _cache = [];
    private readonly HashSet<int> _upgradeTried = [];
    private readonly LinkedList<int> _lru = [];
    private readonly Queue<TimeSpan> _prefetch = [];
    private readonly Queue<TimeSpan> _dense = [];
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly AutoResetEvent _coverageSignal = new(false);
    private readonly Thread _thread;
    private Thread? _coverageThread;
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
        int atlasUpgradeDelayMs = 260,
        ISeekPreviewRenderer? coverageRenderer = null)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _coverage = coverageRenderer is not null && !ReferenceEquals(coverageRenderer, renderer)
            ? coverageRenderer
            : null;
        _bucketSeconds = Math.Max(0.05, bucketSeconds);
        _atlasUpgradeDelayMs = Math.Max(0, atlasUpgradeDelayMs);
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "seek-preview",
            Priority = ThreadPriority.BelowNormal
        };
        _thread.Start();
        if (_coverage is not null)
        {
            _coverageThread = new Thread(CoverageLoop)
            {
                IsBackground = true,
                Name = "seek-preview-cover",
                Priority = ThreadPriority.Lowest
            };
            _coverageThread.Start();
        }
    }

    public void AttachCoverage(ISeekPreviewRenderer coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        lock (_gate)
        {
            if (_coverage is not null || ReferenceEquals(coverage, _renderer))
            {
                if (!ReferenceEquals(coverage, _coverage) && !ReferenceEquals(coverage, _renderer))
                {
                    coverage.Dispose();
                }

                return;
            }

            _coverage = coverage;
            _coverageThread = new Thread(CoverageLoop)
            {
                IsBackground = true,
                Name = "seek-preview-cover",
                Priority = ThreadPriority.Lowest
            };
            _coverageThread.Start();
        }

        _coverageSignal.Set();
    }

    public event Action<TimeSpan, string>? FrameReady;

    public void SetAtlas(IPreviewAtlas? atlas)
    {
        lock (_gate)
        {
            _atlas = atlas;
            if (atlas is not null)
            {
                _prefetch.Clear();
                _dense.Clear();
            }
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
        _coverageSignal.Set();
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
                !LooksLikeYouTube(_path) &&
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
            _coverageSignal.Set();
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
            if (string.IsNullOrWhiteSpace(path) || _warmRequested || _atlas is not null || LooksLikeYouTube(path))
            {
                return;
            }

            _warmRequested = true;
            _warmPending = true;
        }
        _signal.Set();
        _coverageSignal.Set();
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
                _dense.Clear();
                _atlas.Prioritize(time);
            }
            else if (_network && includeNeighbors)
            {
                // Keep the background coverage grid. Only pull nearby
                // uncached neighbors in front of it so a long VOD still fills.
                EnqueueAroundFront_NoLock(time);
            }
        }

        _signal.Set();
        _coverageSignal.Set();
    }

    public void Request(TimeSpan time) => Request(_path ?? "", time);

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        _coverageSignal.Set();
        if (!_thread.Join(1500))
        {
            // Worker is in native capture; it will exit on next loop.
        }

        _coverageThread?.Join(1500);

        lock (_gate)
        {
            ClearCache_NoLock();
        }

        _renderer.Dispose();
        if (_coverage is not null)
        {
            _coverage.Dispose();
        }

        _signal.Dispose();
        _coverageSignal.Dispose();
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
                    requested = TakeNext_NoLock(
                        out behindLiveSeconds,
                        out requestedUtc,
                        hoverOnly: _coverage is not null);
                    atlas = _atlas;
                    warm = requested < TimeSpan.Zero && _warmPending;
                    if (warm) _warmPending = false;
                }

                if (warm && atlas is null && !LooksLikeYouTube(path) && !string.IsNullOrWhiteSpace(path))
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

                    if (LooksLikeYouTube(path) && atlas is not null)
                    {
                        continue;
                    }

                    if (atlasServed)
                    {
                        if (atlas is null || !atlas.NeedsDecodedUpgrade)
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

                    if (!hover &&
                        decodedLow is not null &&
                        atlas is null &&
                        _renderer is IFastSeekPreviewRenderer)
                    {
                        var upgradedCoverage = _renderer.Capture(captureTime);
                        if (upgradedCoverage is null)
                        {
                            continue;
                        }

                        if (!KeepAndPublish(path, atlas, requested, bucket, upgradedCoverage, high: true))
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

    private void CoverageLoop()
    {
        var renderer = _coverage;
        if (renderer is null)
        {
            return;
        }

        while (_running)
        {
            _coverageSignal.WaitOne();
            while (_running)
            {
                string? path;
                TimeSpan requested;
                lock (_gate)
                {
                    if (_atlas is not null || LooksLikeYouTube(_path))
                    {
                        path = null;
                        requested = TimeSpan.FromSeconds(-1);
                    }
                    else
                    {
                        path = _path;
                        requested = TakeCoverage_NoLock();
                    }
                }

                if (string.IsNullOrWhiteSpace(path) || requested < TimeSpan.Zero)
                {
                    break;
                }

                try
                {
                    PrepareRenderer(path, renderer);
                    var image = renderer is IFastSeekPreviewRenderer fast
                        ? fast.CaptureFast(requested)
                        : renderer.Capture(requested);
                    if (image is null)
                    {
                        continue;
                    }

                    if (!KeepAndPublish(path, atlas: null, requested, Bucket(requested), image, high: false))
                    {
                        break;
                    }
                }
                catch
                {
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

    private TimeSpan TakeNext_NoLock(
        out double? behindLiveSeconds,
        out DateTime requestedUtc,
        bool hoverOnly = false)
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

        if (hoverOnly)
        {
            return TimeSpan.FromSeconds(-1);
        }

        return TakeCoverage_NoLock();
    }

    private TimeSpan TakeCoverage_NoLock()
    {
        while (_prefetch.Count > 0)
        {
            var next = _prefetch.Dequeue();
            if (!_cache.ContainsKey(Bucket(next)))
            {
                return next;
            }
        }

        while (_dense.Count > 0)
        {
            var next = _dense.Dequeue();
            if (!_cache.ContainsKey(Bucket(next)))
            {
                return next;
            }
        }

        if (_coverage is null &&
            _atlas is null &&
            _hover >= TimeSpan.Zero &&
            _renderer is IFastSeekPreviewRenderer)
        {
            return NextUpgrade_NoLock();
        }

        return TimeSpan.FromSeconds(-1);
    }

    private TimeSpan NextUpgrade_NoLock()
    {
        var target = _hover.TotalSeconds;
        var best = TimeSpan.FromSeconds(-1);
        var bestBucket = int.MinValue;
        var bestScore = double.MaxValue;
        foreach (var (bucket, still) in _cache)
        {
            if (still.High || _upgradeTried.Contains(bucket))
            {
                continue;
            }

            var at = (bucket * ActiveBucket) + (ActiveBucket * 0.5);
            var score = Math.Abs(at - target);
            if (score < bestScore)
            {
                bestScore = score;
                bestBucket = bucket;
                best = TimeSpan.FromSeconds(at);
            }
        }

        if (bestBucket != int.MinValue)
        {
            _upgradeTried.Add(bestBucket);
        }

        return best;
    }

    private void PrepareRenderer(string path) => PrepareRenderer(path, _renderer);

    private void PrepareRenderer(string path, ISeekPreviewRenderer renderer)
    {
        string? referer;
        lock (_gate)
        {
            referer = _referer;
        }

        if (renderer is INetworkSeekPreviewRenderer networked)
        {
            networked.Prepare(path, referer);
            return;
        }

        renderer.Prepare(path);
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

        var coarse = _network ? Math.Min(16, count) : count;
        var stride = Math.Max(1, count / coarse);
        var step = span / count;
        var seen = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var time = TimeSpan.FromSeconds(Math.Clamp(from + (i + 0.5) * step, from, to));
            var bucket = Bucket(time);
            if (_cache.ContainsKey(bucket) || !seen.Add(bucket))
            {
                continue;
            }

            if (i % stride == 0)
            {
                _prefetch.Enqueue(time);
            }
            else
            {
                _dense.Enqueue(time);
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
        _upgradeTried.Clear();
        _lru.Clear();
        _prefetch.Clear();
        _dense.Clear();
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

    private static bool LooksLikeYouTube(string? path) =>
        path is not null &&
        (path.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("youtu.be", StringComparison.OrdinalIgnoreCase));

    private readonly record struct CachedStill(string Path, bool High);
}
