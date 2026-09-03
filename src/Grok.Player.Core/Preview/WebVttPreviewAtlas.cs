using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Grok.Player.Core.Preview;

/// <summary>Preview atlas backed by the WebVTT thumbnail sprites used by many web players.</summary>
[SupportedOSPlatform("windows")]
public sealed class WebVttPreviewAtlas : IPreviewAtlas
{
    private readonly Uri _manifest;
    private readonly string? _referer;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _sheets = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _frames = [];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "grok-vtt-storyboard-" + Guid.NewGuid().ToString("N"));
    private List<Cue>? _cues;
    private bool _loadAttempted;
    private bool _disposed;

    internal WebVttPreviewAtlas(string manifestUrl, string? referer = null, HttpClient? http = null)
    {
        _manifest = new Uri(manifestUrl, UriKind.Absolute);
        _referer = referer;
        _ownsHttp = http is null;
        _http = http ?? CreateHttp();
        Directory.CreateDirectory(_folder);
    }

    public static WebVttPreviewAtlas? TryCreate(string? manifestUrl, string? referer = null)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return new WebVttPreviewAtlas(uri.AbsoluteUri, referer);
    }

    public double IntervalSeconds
    {
        get
        {
            EnsureManifest();
            lock (_gate)
            {
                return _cues is { Count: > 0 }
                    ? Math.Max(0.1, (_cues[0].End - _cues[0].Start).TotalSeconds)
                    : 10;
            }
        }
    }

    // Native site storyboards are already sized for the preview card. Avoid a
    // second network decoder seeking the same remote VOD after a tile is ready.
    public bool NeedsDecodedUpgrade => false;

    public bool TryGetFrame(TimeSpan time, out string path)
    {
        path = "";
        var index = CueIndex(time);
        if (index < 0) return false;
        lock (_gate)
        {
            return _frames.TryGetValue(index, out path!) && File.Exists(path);
        }
    }

    public bool TryGetOrFetch(TimeSpan time, out string path)
    {
        if (TryGetFrame(time, out path)) return true;
        var index = CueIndex(time);
        if (index < 0) return false;
        Cue cue;
        lock (_gate) cue = _cues![index];
        var sheet = GetSheet(cue.SheetUrl);
        if (sheet is null) return false;
        var cropped = Crop(sheet, cue, index);
        if (cropped is null) return false;
        lock (_gate) _frames[index] = cropped;
        path = cropped;
        return true;
    }

    public bool TryGetOrFetchBest(TimeSpan time, out string path) => TryGetOrFetch(time, out path);

    public void Prefetch(TimeSpan time) => TryGetOrFetch(time, out _);

    public void PrefetchCoverage()
    {
        EnsureManifest();
        List<string> urls;
        lock (_gate)
        {
            urls = (_cues ?? []).Select(cue => cue.SheetUrl).Distinct(StringComparer.Ordinal).Take(16).ToList();
        }

        // Most providers store every tile in one sprite. Fetching that single
        // image makes the entire seekbar available without decoding the video.
        foreach (var url in urls)
        {
            if (_disposed) return;
            _ = GetSheet(url);
        }
    }

    public bool RepresentsSameFrame(TimeSpan left, TimeSpan right) => CueIndex(left) is var a && a >= 0 && a == CueIndex(right);

    public TimeSpan FrameTime(TimeSpan time)
    {
        var index = CueIndex(time);
        lock (_gate) return index >= 0 ? _cues![index].Start : time;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _sheets.Clear();
            _frames.Clear();
        }

        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (_ownsHttp) _http.Dispose();
    }

    internal static IReadOnlyList<Cue> Parse(string text, Uri manifest)
    {
        var list = new List<Cue>();
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var timing = Regex.Match(lines[i], @"^\s*(\d{1,3}:\d{2}(?::\d{2})?\.\d{3})\s+-->\s+(\d{1,3}:\d{2}(?::\d{2})?\.\d{3})");
            if (!timing.Success || !TryTime(timing.Groups[1].Value, out var start) || !TryTime(timing.Groups[2].Value, out var end)) continue;
            var image = "";
            while (++i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                if (lines[i].Contains("#xywh=", StringComparison.OrdinalIgnoreCase)) image = lines[i].Trim();
            }

            var crop = Regex.Match(image, @"#xywh=(\d+),(\d+),(\d+),(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!crop.Success) continue;
            var rawUrl = image[..crop.Index];
            if (!Uri.TryCreate(manifest, rawUrl, out var sheet) || sheet.Scheme is not ("http" or "https")) continue;
            list.Add(new Cue(
                start,
                end,
                sheet.AbsoluteUri,
                int.Parse(crop.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(crop.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(crop.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(crop.Groups[4].Value, CultureInfo.InvariantCulture)));
        }

        return list.OrderBy(cue => cue.Start).ToList();
    }

    private int CueIndex(TimeSpan time)
    {
        EnsureManifest();
        lock (_gate)
        {
            if (_cues is not { Count: > 0 }) return -1;
            var lo = 0;
            var hi = _cues.Count - 1;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) / 2);
                var cue = _cues[mid];
                if (time < cue.Start) hi = mid - 1;
                else if (time >= cue.End) lo = mid + 1;
                else return mid;
            }

            return Math.Clamp(hi, 0, _cues.Count - 1);
        }
    }

    private void EnsureManifest()
    {
        lock (_gate)
        {
            if (_loadAttempted || _disposed) return;
            _loadAttempted = true;
        }

        try
        {
            using var request = Request(_manifest);
            using var response = _http.Send(request);
            if (!response.IsSuccessStatusCode) return;
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var parsed = Parse(text, _manifest).ToList();
            lock (_gate) _cues = parsed;
        }
        catch (Exception) { }
    }

    private byte[]? GetSheet(string url)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (_sheets.TryGetValue(url, out var found)) return found;
        }

        try
        {
            using var request = Request(new Uri(url));
            using var response = _http.Send(request);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (bytes.Length < 16) return null;
            lock (_gate)
            {
                if (_disposed) return null;
                _sheets[url] = bytes;
            }
            return bytes;
        }
        catch (Exception) { return null; }
    }

    private HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
        var referer = Uri.TryCreate(_referer, UriKind.Absolute, out var page)
            ? page.GetLeftPart(UriPartial.Authority) + "/"
            : _manifest.GetLeftPart(UriPartial.Authority) + "/";
        request.Headers.TryAddWithoutValidation("Referer", referer);
        return request;
    }

    private string? Crop(byte[] bytes, Cue cue, int index)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null || cue.Width < 8 || cue.Height < 8 || cue.X < 0 || cue.Y < 0 || cue.X + cue.Width > bitmap.Width || cue.Y + cue.Height > bitmap.Height) return null;
            using var tile = new SKBitmap();
            if (!bitmap.ExtractSubset(tile, SKRectI.Create(cue.X, cue.Y, cue.Width, cue.Height))) return null;
            var path = Path.Combine(_folder, index.ToString(CultureInfo.InvariantCulture) + ".png");
            using var image = SKImage.FromBitmap(tile);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            using (var stream = File.Create(path)) encoded.SaveTo(stream);
            return path;
        }
        catch (Exception) { return null; }
    }

    private static bool TryTime(string value, out TimeSpan time)
    {
        var parts = value.Split(':');
        if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds2) && int.TryParse(parts[0], out var minutes))
        {
            time = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds2);
            return true;
        }
        if (parts.Length == 3 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds3) && int.TryParse(parts[1], out minutes) && int.TryParse(parts[0], out var hours))
        {
            time = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds3);
            return true;
        }
        time = default;
        return false;
    }

    internal sealed record Cue(TimeSpan Start, TimeSpan End, string SheetUrl, int X, int Y, int Width, int Height);

    private static HttpClient CreateHttp() => new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(8) };

    private const string ChromeUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
}
