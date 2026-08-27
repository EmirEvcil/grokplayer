using Grok.Player.Core.Presentation;

namespace Grok.Player.Core.Preview;

public sealed class SeekPreviewController : IDisposable
{
    private readonly ISeekPreviewRenderer _renderer;
    private readonly TimeSpan _minInterval;
    private readonly double _minDeltaSeconds;
    private string? _path;
    private TimeSpan? _duration;
    private DateTime _lastCaptureUtc = DateTime.MinValue;
    private TimeSpan _lastCaptureTime = TimeSpan.FromSeconds(-1);
    private string? _lastImagePath;

    public SeekPreviewController(ISeekPreviewRenderer renderer, TimeSpan? minInterval = null, double minDeltaSeconds = 0.2)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _minInterval = minInterval ?? TimeSpan.FromMilliseconds(120);
        _minDeltaSeconds = minDeltaSeconds;
    }

    public SeekPreviewState Current { get; private set; }

    public void SetMedia(string? path, TimeSpan? duration)
    {
        if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
        {
            _path = path;
            _lastImagePath = null;
            _lastCaptureTime = TimeSpan.FromSeconds(-1);
            if (string.IsNullOrWhiteSpace(path))
            {
                _renderer.Reset();
            }
            else
            {
                _renderer.Prepare(path);
            }
        }

        _duration = duration;
        if (string.IsNullOrWhiteSpace(path) || duration is null || duration <= TimeSpan.Zero)
        {
            Hide();
        }
    }

    public SeekPreviewState Move(double pointerX, double trackWidth, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(_path) || _duration is not { } duration || duration <= TimeSpan.Zero)
        {
            return Hide();
        }

        var time = SeekBarMath.TimeAt(pointerX, trackWidth, duration);
        _ = nowUtc;

        Current = new SeekPreviewState(
            IsVisible: true,
            Time: time,
            TimeText: TimeDisplay.FormatSeek(time),
            ImagePath: null,
            NormalizedPosition: duration.TotalSeconds <= 0 ? 0 : Math.Clamp(time.TotalSeconds / duration.TotalSeconds, 0, 1));
        return Current;
    }

    public bool ShouldRequestCapture(SeekPreviewState state) =>
        state.IsVisible && ShouldCapture(state.Time, DateTime.UtcNow);

    public void RememberImage(string path)
    {
        _lastImagePath = path;
        _lastCaptureTime = Current.Time;
        _lastCaptureUtc = DateTime.UtcNow;
        if (Current.IsVisible)
        {
            Current = Current with { ImagePath = path };
        }
    }

    public void Reset()
    {
        _path = null;
        _duration = null;
        _lastImagePath = null;
        _lastCaptureTime = TimeSpan.FromSeconds(-1);
        Current = default;
    }

    public SeekPreviewState Hide()
    {
        Current = default;
        _lastImagePath = null;
        _lastCaptureTime = TimeSpan.FromSeconds(-1);
        return Current;
    }

    public void Dispose() => _renderer.Dispose();

    private bool ShouldCapture(TimeSpan time, DateTime nowUtc)
    {
        if (_lastImagePath is null)
        {
            return true;
        }

        if (Math.Abs((time - _lastCaptureTime).TotalSeconds) < _minDeltaSeconds)
        {
            return false;
        }

        return nowUtc - _lastCaptureUtc >= _minInterval;
    }
}
