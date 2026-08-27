using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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

    public StoryboardAtlas(StoryboardSpec spec, TimeSpan? duration = null, HttpClient? http = null)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _duration = duration;
        _ownsHttp = http is null;
        _http = http ?? CreateHttp();
        Directory.CreateDirectory(_folder);
    }

    public double IntervalSeconds => _spec.BestLevel?.Interval(_duration).TotalSeconds ?? 10;

    public static StoryboardAtlas? TryCreate(string? spec, TimeSpan? duration = null)
    {
        var parsed = StoryboardSpec.Parse(spec);
        return parsed is null ? null : new StoryboardAtlas(parsed, duration);
    }

    public bool TryGetFrame(TimeSpan time, out string path)
    {
        path = "";
        var cell = _spec.CellAt(time, _duration);
        if (cell is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (_cells.TryGetValue(CellKey(cell.Value), out var cached) && File.Exists(cached))
            {
                path = cached;
                return true;
            }

            if (!_sheets.TryGetValue(cell.Value.Url, out var sheet))
            {
                return false;
            }

            var cropped = Crop(sheet, cell.Value);
            if (cropped is null)
            {
                return false;
            }

            _cells[CellKey(cell.Value)] = cropped;
            path = cropped;
            return true;
        }
    }

    public bool TryGetOrFetch(TimeSpan time, out string path)
    {
        if (TryGetFrame(time, out path))
        {
            return true;
        }

        Prefetch(time);
        return TryGetFrame(time, out path);
    }

    public void Prefetch(TimeSpan time)
    {
        Interlocked.Exchange(ref _hoverBusy, 1);
        try
        {
        var level = _spec.BestLevel;
        var here = level?.CellAt(time, _duration);
        if (here is null)
        {
            return;
        }

        EnsureSheet(here.Value.Url);
        if (level is null)
        {
            return;
        }

        var step = here.Value.Interval;
        if (time - step > TimeSpan.Zero)
        {
            var previous = level.CellAt(time - step, _duration);
            if (previous is not null)
            {
                EnsureSheet(previous.Value.Url);
            }
        }

        var next = level.CellAt(time + step, _duration);
        if (next is not null)
        {
            EnsureSheet(next.Value.Url);
        }
        }
        finally
        {
            Interlocked.Exchange(ref _hoverBusy, 0);
        }
    }

    public void PrefetchCoverage()
    {
        var level = _spec.BestLevel;
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
        var take = Math.Min(sheets, 48);
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

            EnsureSheet(url);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
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

    private void EnsureSheet(string url)
    {
        lock (_gate)
        {
            if (_sheets.ContainsKey(url))
            {
                return;
            }
        }

        byte[] bytes;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
            using var response = _http.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return;
        }

        if (bytes.Length < 800)
        {
            return;
        }

        lock (_gate)
        {
            _sheets[url] = bytes;
            while (_sheets.Count > 10)
            {
                _sheets.Remove(_sheets.Keys.First());
            }
        }
    }

    private string? Crop(byte[] sheet, StoryboardCell cell)
    {
        try
        {
            using var input = new MemoryStream(sheet, writable: false);
            using var bitmap = new Bitmap(input);
            var x = Math.Clamp(cell.Column * cell.CellWidth, 0, Math.Max(0, bitmap.Width - 1));
            var y = Math.Clamp(cell.Row * cell.CellHeight, 0, Math.Max(0, bitmap.Height - 1));
            var width = Math.Min(cell.CellWidth, bitmap.Width - x);
            var height = Math.Min(cell.CellHeight, bitmap.Height - y);
            if (width < 8 || height < 8)
            {
                return null;
            }

            using var tile = bitmap.Clone(new Rectangle(x, y, width, height), bitmap.PixelFormat);
            var path = Path.Combine(_folder, CellKey(cell) + ".jpg");
            var jpeg = ImageCodecInfo.GetImageEncoders().FirstOrDefault(item => item.FormatID == ImageFormat.Jpeg.Guid);
            if (jpeg is null)
            {
                tile.Save(path, ImageFormat.Png);
            }
            else
            {
                using var quality = new EncoderParameters(1);
                quality.Param[0] = new EncoderParameter(Encoder.Quality, 95L);
                tile.Save(path, jpeg, quality);
            }

            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string CellKey(StoryboardCell cell) =>
        cell.Sheet + "-" + cell.Column + "-" + cell.Row + "-" + (int)cell.Time.TotalSeconds;

    private static HttpClient CreateHttp()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
    }

    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
}
