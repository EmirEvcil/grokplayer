using SkiaSharp;
using System.Net.Http;
using System.Runtime.Versioning;

namespace Grok.Player.Core.Preview;

[SupportedOSPlatform("windows")]
public sealed class StoryboardAtlas : IPreviewAtlas
{
    private readonly StoryboardSpec _spec;
    private readonly TimeSpan? _duration;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Dictionary<string, byte[]> _sheets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cells = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "grok-storyboard-" + Guid.NewGuid().ToString("N"));
    private int _hoverBusy;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource _qualityCancellation = new();
    private string? _priorityUrl;
    private bool _disposed;

    public StoryboardAtlas(StoryboardSpec spec, TimeSpan? duration = null, HttpClient? http = null)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _duration = duration;
        _ownsHttp = http is null;
        _http = http ?? CreateHttp();
        Directory.CreateDirectory(_folder);
    }

    public double IntervalSeconds => _spec.BestLevel?.Interval(_duration).TotalSeconds ?? 10;

    public bool NeedsDecodedUpgrade =>
        (_spec.BestLevel?.Width ?? 0) < 512 || (_spec.BestLevel?.Height ?? 0) < 288;

    public static StoryboardAtlas? TryCreate(string? spec, TimeSpan? duration = null)
    {
        var parsed = StoryboardSpec.Parse(spec);
        return parsed is null ? null : new StoryboardAtlas(parsed, duration);
    }

    public bool TryGetFrame(TimeSpan time, out string path)
    {
        path = "";
        lock (_gate)
        {
            foreach (var level in ProgressiveLevels())
            {
                var cell = level.CellAt(time, _duration);
                if (cell is not null && _cells.TryGetValue(CellKey(cell.Value), out var cached) && File.Exists(cached))
                {
                    path = cached;
                    return true;
                }
            }

            // Called by pointer events: never decode a whole sheet on the UI thread.
            return false;
        }
    }

    public bool TryGetOrFetch(TimeSpan time, out string path)
    {
        if (TryGetFrame(time, out path))
        {
            return true;
        }

        return FetchCell(_spec.FastLevel, time, out path, quality: false);
    }

    public bool TryGetOrFetchBest(TimeSpan time, out string path)
    {
        if (TryGetBestFrame(time, out path)) return true;
        var quality = _spec.BestLevel?.Index != _spec.FastLevel?.Index;
        return FetchCell(_spec.BestLevel, time, out path, quality);
    }

    public bool RepresentsSameFrame(TimeSpan left, TimeSpan right)
    {
        var level = _spec.BestLevel;
        var a = level?.CellAt(left, _duration);
        var b = level?.CellAt(right, _duration);
        return a is not null && b is not null &&
               a.Value.Sheet == b.Value.Sheet &&
               a.Value.Column == b.Value.Column &&
               a.Value.Row == b.Value.Row &&
               string.Equals(a.Value.Url, b.Value.Url, StringComparison.Ordinal);
    }

    public TimeSpan FrameTime(TimeSpan time) =>
        _spec.BestLevel?.CellAt(time, _duration)?.Time ?? time;

    private bool TryGetBestFrame(TimeSpan time, out string path)
    {
        path = "";
        var cell = _spec.BestLevel?.CellAt(time, _duration);
        if (cell is null) return false;
        lock (_gate)
        {
            if (!_cells.TryGetValue(CellKey(cell.Value), out var cached) || !File.Exists(cached)) return false;
            path = cached;
            return true;
        }
    }

    private bool FetchCell(StoryboardLevel? level, TimeSpan time, out string path, bool quality)
    {
        path = "";
        var cell = level?.CellAt(time, _duration);
        if (cell is null) return false;
        EnsureSheet(cell.Value.Url, quality);
        byte[]? sheet;
        lock (_gate) _sheets.TryGetValue(cell.Value.Url, out sheet);
        if (sheet is null) return false;
        var cropped = Crop(sheet, cell.Value);
        if (cropped is null) return false;
        lock (_gate) _cells[CellKey(cell.Value)] = cropped;
        path = cropped;
        return true;
    }

    public void Prefetch(TimeSpan time)
    {
        Interlocked.Exchange(ref _hoverBusy, 1);
        try
        {
        var fast = _spec.FastLevel;
        if (fast?.CellAt(time, _duration) is { } fastCell) EnsureSheet(fastCell.Url, quality: false);
        var level = _spec.BestLevel;
        var here = level?.CellAt(time, _duration);
        if (here is null)
        {
            return;
        }

        EnsureSheet(here.Value.Url, quality: level?.Index != fast?.Index);
        }
        finally
        {
            Interlocked.Exchange(ref _hoverBusy, 0);
        }
    }

    public void Prioritize(TimeSpan time)
    {
        var url = string.Join('|', ProgressiveLevels()
            .Select(level => level.CellAt(time, _duration)?.Url)
            .Where(value => value is not null));
        lock (_gate)
        {
            if (_disposed || url == _priorityUrl) return;
            _priorityUrl = url;
            // Rapid pointer movement should only supersede the expensive quality
            // upgrade. Let the small fast sheet finish so the flyout can paint a
            // useful frame instead of repeatedly returning to a black card.
            _qualityCancellation.Cancel();
            _qualityCancellation.Dispose();
            _qualityCancellation = new CancellationTokenSource();
        }
    }

    public void PrefetchCoverage()
    {
        // Warm the inexpensive tier across the video. Exact hover work can then
        // crop a local sheet immediately, while the best tier remains strictly
        // demand-driven and follows the latest pointer position.
        var level = _spec.FastLevel;
        if (level is null)
        {
            return;
        }

        var interval = level.Interval(_duration).TotalSeconds;
        if (interval <= 0)
        {
            return;
        }

        var perSheet = Math.Max(1, level.FramesPerSheet);
        var sheets = Math.Max(1, (int)Math.Ceiling(level.Count / (double)perSheet));
        var take = Math.Min(sheets, 24);
        var stride = Math.Max(1, sheets / take);
        var urls = new List<string>();
        for (var i = 0; i < sheets; i += stride)
        {
            var time = TimeSpan.FromSeconds(Math.Min(level.Count - 1, i * perSheet) * interval);
            var cell = level.CellAt(time, _duration);
            if (cell is not null)
            {
                urls.Add(cell.Value.Url);
            }
        }

        foreach (var url in urls)
        {
            if (Volatile.Read(ref _hoverBusy) != 0)
            {
                return;
            }

            EnsureSheet(url, quality: false);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _qualityCancellation.Cancel();
            _qualityCancellation.Dispose();
            _lifetimeCancellation.Cancel();
            _lifetimeCancellation.Dispose();
            _sheets.Clear();
            _cells.Clear();
        }

        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private void EnsureSheet(string url, bool quality)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || _sheets.ContainsKey(url))
            {
                return;
            }
            token = quality ? _qualityCancellation.Token : _lifetimeCancellation.Token;
        }

        byte[] bytes;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
            using var response = _http.Send(request, token);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            bytes = response.Content.ReadAsByteArrayAsync(token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return;
        }

        // A valid compressed WebP sheet may be much smaller than a JPEG sheet.
        if (bytes.Length < 16)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed) return;
            _sheets[url] = bytes;
            while (_sheets.Count > 32)
            {
                _sheets.Remove(_sheets.Keys.First());
            }
        }
    }

    private string? Crop(byte[] sheet, StoryboardCell cell)
    {
        try
        {
            // YouTube also serves WebP from URLs ending in .jpg. GDI+ cannot
            // decode those sheets, so inspect the encoded data rather than the suffix.
            using var bitmap = SKBitmap.Decode(sheet);
            if (bitmap is null) return null;
            var x = cell.Column * cell.CellWidth;
            var y = cell.Row * cell.CellHeight;
            var width = cell.CellWidth;
            var height = cell.CellHeight;
            if (x < 0 || y < 0 || width < 8 || height < 8 ||
                x + width > bitmap.Width || y + height > bitmap.Height)
            {
                return null;
            }

            using var tile = new SKBitmap();
            if (!bitmap.ExtractSubset(tile, SKRectI.Create(x, y, width, height))) return null;
            var path = Path.Combine(_folder, CellKey(cell) + ".png");
            // Save native pixels. Upscaling here and downscaling in XAML blurs detail.
            using var image = SKImage.FromBitmap(tile);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            using (var output = File.Create(path)) encoded.SaveTo(output);
            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private IEnumerable<StoryboardLevel> ProgressiveLevels()
    {
        if (_spec.BestLevel is { } best) yield return best;
        if (_spec.FastLevel is { } fast && fast.Index != _spec.BestLevel?.Index) yield return fast;
    }

    private static string CellKey(StoryboardCell cell) =>
        cell.CellWidth + "x" + cell.CellHeight + "-" + cell.Sheet + "-" + cell.Column + "-" + cell.Row + "-" + (int)cell.Time.TotalSeconds;

    private static HttpClient CreateHttp()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
    }

    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
}
