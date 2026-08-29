using Grok.Player.Core.Player;
using SkiaSharp;

namespace Grok.Player.Core.Preview;

/// <summary>Bounded history of the playing decoder; never seeks the live stream.</summary>
public sealed class LivePreviewBuffer : IDisposable
{
    private readonly object _gate = new();
    private readonly object _captureGate = new();
    private readonly SortedDictionary<int, (TimeSpan Time, string Path)> _frames = [];
    private bool _busy;
    private bool _disposed;
    private int _generation;
    private double _latest;
    private const double KeepSeconds = LivePlayback.DvrKeepSeconds + 4;

    public int Count { get { lock (_gate) return _frames.Count; } }

    public string? GetFrame(TimeSpan time, double maxDeltaSeconds = 2)
    {
        lock (_gate)
        {
            string? best = null;
            var distance = maxDeltaSeconds;
            foreach (var frame in _frames.Values)
            {
                var delta = Math.Abs((frame.Time - time).TotalSeconds);
                if (delta <= distance && File.Exists(frame.Path))
                {
                    best = frame.Path;
                    distance = delta;
                }
            }
            return best;
        }
    }

    public bool Store(TimeSpan time, string sourcePath, bool deleteSource = false)
    {
        var thumbnail = Path.Combine(Path.GetTempPath(), $"grok-live-{Guid.NewGuid():N}.thumb.jpg");
        var stored = false;
        try
        {
            using var source = SKBitmap.Decode(sourcePath);
            if (source is null) return false;
            var scale = Math.Min(1, Math.Min(512d / source.Width, 288d / source.Height));
            using var small = source.Resize(new SKImageInfo(
                Math.Max(1, (int)Math.Round(source.Width * scale)),
                Math.Max(1, (int)Math.Round(source.Height * scale))), new SKSamplingOptions(SKFilterMode.Linear));
            if (small is null) return false;
            using var image = SKImage.FromBitmap(small);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            using (var file = File.Create(thumbnail)) encoded.SaveTo(file);
            lock (_gate)
            {
                if (_disposed) return false;
                _latest = Math.Max(_latest, time.TotalSeconds);
                if (time.TotalSeconds < _latest - KeepSeconds) return false;
                var bucket = (int)Math.Floor(time.TotalSeconds);
                if (_frames.Remove(bucket, out var previous)) Delete(previous.Path);
                _frames[bucket] = (time, thumbnail);
                stored = true;
            }
            return true;
        }
        catch (Exception) { return false; }
        finally
        {
            if (!stored) Delete(thumbnail);
            if (deleteSource) Delete(sourcePath);
        }
    }

    public Task<bool> CaptureAsync(Func<string, bool> capture, Func<TimeSpan> position)
    {
        int generation;
        lock (_gate)
        {
            if (_disposed || _busy) return Task.FromResult(false);
            _busy = true;
            generation = _generation;
        }

        return Task.Run(() =>
        {
            var raw = Path.Combine(Path.GetTempPath(), $"grok-live-{Guid.NewGuid():N}.jpg");
            var thumbnail = Path.ChangeExtension(raw, ".thumb.jpg");
            var stored = false;
            try
            {
                TimeSpan before, after;
                // Dispose drains the native capture before PlayerHost is destroyed.
                lock (_captureGate)
                {
                    lock (_gate)
                        if (_disposed || generation != _generation) return false;
                    before = position();
                    lock (_gate)
                        if (_frames.ContainsKey((int)Math.Floor(before.TotalSeconds))) return false;
                    if (!capture(raw)) return false;
                    after = position();
                }

                // A seek/reconnect during capture must not label a new scene with
                // the old time, or vice versa.
                if (Math.Abs((after - before).TotalSeconds) > 2) return false;
                using var source = SKBitmap.Decode(raw);
                if (source is null) return false;
                var scale = Math.Min(1, Math.Min(512d / source.Width, 288d / source.Height));
                using var small = source.Resize(new SKImageInfo(
                    Math.Max(1, (int)Math.Round(source.Width * scale)),
                    Math.Max(1, (int)Math.Round(source.Height * scale))), new SKSamplingOptions(SKFilterMode.Linear));
                if (small is null) return false;
                using var image = SKImage.FromBitmap(small);
                using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                using (var file = File.Create(thumbnail)) encoded.SaveTo(file);

                lock (_gate)
                {
                    if (_disposed || generation != _generation) return false;
                    _latest = Math.Max(_latest, before.TotalSeconds);
                    if (before.TotalSeconds < _latest - KeepSeconds) return false;
                    var bucket = (int)Math.Floor(before.TotalSeconds);
                    if (_frames.Remove(bucket, out var previous)) Delete(previous.Path);
                    _frames[bucket] = (before, thumbnail);
                    stored = true;
                    while (_frames.Count > KeepSeconds + 2 ||
                           _frames.First().Value.Time.TotalSeconds < _latest - KeepSeconds)
                    {
                        var oldest = _frames.First();
                        _frames.Remove(oldest.Key);
                        Delete(oldest.Value.Path);
                    }
                }
                return true;
            }
            catch (Exception)
            {
                // A failed thumbnail is never a playback failure.
                return false;
            }
            finally
            {
                Delete(raw);
                if (!stored) Delete(thumbnail);
                lock (_gate) _busy = false;
            }
        });
    }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            foreach (var frame in _frames.Values) Delete(frame.Path);
            _frames.Clear();
            _latest = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Reset();
        lock (_captureGate) { }
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
