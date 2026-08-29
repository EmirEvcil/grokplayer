namespace Grok.Player.Core.Preview;

/// <summary>Latest-hover-wins decoder for data already present in mpv's live cache.</summary>
public sealed class LiveCachePreviewProvider : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<TimeSpan, CachedPreviewClip?> _snapshot;
    private readonly LivePreviewBuffer _frames;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private CachedPreviewClip? _clip;
    private int _clipGeneration = -1;
    private TimeSpan? _pending;
    private int _generation;
    private bool _disposed;

    public LiveCachePreviewProvider(Func<TimeSpan, CachedPreviewClip?> snapshot, LivePreviewBuffer frames)
    {
        _snapshot = snapshot;
        _frames = frames;
        _worker = Task.Run(WorkAsync);
    }

    public event EventHandler<TimeSpan>? FrameReady;

    public void Request(TimeSpan time)
    {
        lock (_gate)
        {
            if (_disposed || _frames.GetFrame(time) is not null) return;
            _pending = time;
            if (_signal.CurrentCount == 0) _signal.Release();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _pending = null;
        }
    }

    private async Task WorkAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(_stop.Token); }
            catch (OperationCanceledException) { break; }
            await Task.Delay(100, _stop.Token).ConfigureAwait(false);

            TimeSpan target;
            int generation;
            lock (_gate)
            {
                if (_pending is not { } requested) continue;
                target = requested;
                _pending = null;
                generation = _generation;
            }

            CachedPreviewClip? clip;
            lock (_gate) clip = _clipGeneration == generation ? _clip : null;
            if (clip is null || !clip.Contains(target))
            {
                CachedPreviewClip? replacement = null;
                try { replacement = _snapshot(target); }
                catch (Exception) { }
                lock (_gate)
                {
                    if (_disposed || generation != _generation)
                    {
                        replacement?.Dispose();
                        continue;
                    }
                    _clip?.Dispose();
                    _clip = replacement;
                    _clipGeneration = generation;
                    clip = replacement;
                }
            }

            string? image = null;
            try { image = clip?.Capture(target); }
            catch (Exception) { }
            if (image is null) continue;
            var stored = false;
            lock (_gate)
            {
                if (!_disposed && generation == _generation)
                    stored = _frames.Store(target, image, deleteSource: true);
            }
            if (!stored)
            {
                try { File.Delete(image); } catch (IOException) { }
                continue;
            }
            FrameReady?.Invoke(this, target);

            lock (_gate)
                if (_pending is not null && _signal.CurrentCount == 0) _signal.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _pending = null;
        }
        _stop.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        lock (_gate)
        {
            _clip?.Dispose();
            _clip = null;
            _clipGeneration = -1;
        }
        _signal.Dispose();
        _stop.Dispose();
    }
}
