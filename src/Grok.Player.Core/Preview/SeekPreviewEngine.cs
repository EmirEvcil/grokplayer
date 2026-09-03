using System.Globalization;
using System.Security.Cryptography;
using Grok.Player.Core.Media;
using Grok.Player.Core.Native;
using SkiaSharp;

namespace Grok.Player.Core.Preview;

public sealed class SeekPreviewEngine : ISeekPreviewRenderer, IExactSeekPreviewRenderer, IFastSeekPreviewRenderer, ILiveSeekPreviewRenderer, INetworkSeekPreviewRenderer
{
    private readonly IMpvNative _mpv;
    private readonly bool _ownsNative;
    private readonly string _imageDump;
    private string? _path;
    private string? _referer;
    private string? _lastFile;
    private string? _lastStillHash;
    private TimeSpan _lastStillTime = TimeSpan.FromSeconds(-1);
    private bool _ready;
    private bool _firstNetworkPaint = true;
    private string? _paintedDump;

    public SeekPreviewEngine(IMpvNative mpv, bool ownsNative = true)
    {
        _mpv = mpv ?? throw new ArgumentNullException(nameof(mpv));
        _ownsNative = ownsNative;
        _imageDump = Path.Combine(Path.GetTempPath(), "grok-player-preview-vo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_imageDump);
        ApplyOptions();
        _mpv.Initialize();
        _mpv.SetPropertyFlag("pause", true);
    }

    public static SeekPreviewEngine Create() => new(new MpvNative());

    public TimeSpan Position
    {
        get
        {
            try { return TimeSpan.FromSeconds(Math.Max(0, _mpv.GetPropertyDouble("time-pos") ?? 0)); }
            catch (MpvException) { return TimeSpan.Zero; }
        }
    }

    public void Prepare(string path) => Prepare(path, null);

    public void Prepare(string path, string? referer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var youtube = LooksLikeYouTube(path);
        var network = path.Contains("://", StringComparison.Ordinal);
        var wait = youtube ? 3.5 : network ? 12.0 : 0.8;
        if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_referer, referer, StringComparison.Ordinal))
        {
            if (_ready || FileLooksOpen())
            {
                _ready = true;
                return;
            }

            _ready = WaitForFile(wait);
            if (_ready)
            {
                return;
            }

            Reload(path, referer, youtube);
            return;
        }

        Reload(path, referer, youtube);
    }

    private void Reload(string path, string? referer, bool youtube)
    {
        _path = path;
        _referer = referer;
        _ready = false;
        _firstNetworkPaint = true;
        _lastStillHash = null;
        _lastStillTime = TimeSpan.FromSeconds(-1);
        ApplyNetworkIdentity(path, referer);
        TrySet("hls-bitrate", youtube ? "max" : "600000");
        ApplyTier(high: false);
        TrySet("demuxer-lavf-o", "allowed_extensions=ALL");
        _mpv.Command("loadfile", path, "replace");
        _mpv.SetPropertyFlag("pause", true);
        var wait = youtube ? 3.5 : path.Contains("://", StringComparison.Ordinal) ? 12.0 : 0.8;
        _ready = WaitForFile(wait);
        if (_ready && !youtube && path.Contains("://", StringComparison.Ordinal))
        {
            try { _mpv.SetPropertyFlag("pause", false); } catch (MpvException) { }
            Settle(0.7);
            try { _mpv.SetPropertyFlag("pause", true); } catch (MpvException) { }
        }
    }

    public string? CaptureFast(TimeSpan time)
    {
        EnsureReady();
        if (string.IsNullOrWhiteSpace(_path) || !_ready)
        {
            return null;
        }

        ApplyTier(high: false);
        if (!SeekAndPaint(time, exact: false, high: false))
        {
            return null;
        }

        return TakeStill(time, minBytes: 800);
    }

    public string? Capture(TimeSpan time) => Capture(time, exact: false);

    public string? CaptureExact(TimeSpan time) => Capture(time, exact: true);

    public string? CaptureBehindLive(string path, double behindLiveSeconds, DateTime requestedUtc) =>
        HlsLivePreviewExtractor.Capture(path, behindLiveSeconds, requestedUtc);

    public string? Capture(TimeSpan time, bool exact)
    {
        EnsureReady();
        if (string.IsNullOrWhiteSpace(_path) || !_ready)
        {
            return null;
        }

        ApplyTier(high: true);
        if (!SeekAndPaint(time, exact, high: true))
        {
            return null;
        }

        return TakeStill(time, minBytes: 2400);
    }

    private bool SeekAndPaint(TimeSpan time, bool exact, bool high)
    {
        var seconds = Math.Max(0, time.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        var network = _path is not null && _path.Contains("://", StringComparison.Ordinal);
        var youtube = _path is not null && LooksLikeYouTube(_path);
        var haveStill = SeekPreviewDisplay.Fits(
            time,
            _lastStillTime,
            SeekPreviewDisplay.DecoderDeltaSeconds);
        var alreadyThere = !exact &&
                           haveStill &&
                           SeekLanded(time, SeekPreviewDisplay.KeyframeToleranceSeconds);

        DrainPendingEvents();
        SweepImageDump();
        var beforeDump = DumpSnapshot();
        _paintedDump = null;
        if (exact)
        {
            if (!TrySeek(seconds, "absolute+exact"))
            {
                return false;
            }
        }
        else if (!alreadyThere &&
                 !TrySeek(seconds, network ? "absolute+keyframes" : "absolute") &&
                 !TrySeek(seconds, "absolute"))
        {
            return false;
        }

        try
        {
            _mpv.SetPropertyFlag("pause", false);
        }
        catch (MpvException)
        {
        }

        var tolerance = exact ? 0.35
            : youtube ? 2.5
            : network ? SeekPreviewDisplay.KeyframeToleranceSeconds
            : 1.0;
        var jump = Math.Abs(time.TotalSeconds - Math.Max(0, _lastStillTime.TotalSeconds));
        var timeout = exact ? 1.0
            : alreadyThere ? 0.8
            : !network ? 0.8
            : _firstNetworkPaint ? 6.0
            : jump > 20 ? 3.2
            : high ? 2.2
            : 1.6;
        var painted = WaitForPaint(time, tolerance, timeout, beforeDump, requireRestart: !alreadyThere);

        try { _mpv.SetPropertyFlag("pause", true); } catch (MpvException) { }
        DrainPendingEvents();
        if (!painted)
        {
            return false;
        }

        return !network || SeekLanded(time, exact ? 0.35 : SeekPreviewDisplay.KeyframeToleranceSeconds);
    }

    private string? TakeStill(TimeSpan requested, int minBytes)
    {
        if (_paintedDump is not null &&
            Grok.Player.Core.Player.LivePlayback.IsUsableStill(_paintedDump) &&
            !LooksBlank(_paintedDump, minBytes))
        {
            var kept = Path.Combine(Path.GetTempPath(), $"grok-player-seek-{Guid.NewGuid():N}.jpg");
            try
            {
                File.Copy(_paintedDump, kept, overwrite: true);
                _paintedDump = null;
                return KeepStill(requested, kept);
            }
            catch (IOException)
            {
                TryDelete(kept);
            }
        }

        var file = Path.Combine(Path.GetTempPath(), $"grok-player-seek-{Guid.NewGuid():N}.jpg");
        try
        {
            _mpv.Command("screenshot-to-file", file, "video");
        }
        catch (MpvException)
        {
            return null;
        }

        return KeepStill(requested, file, minBytes);
    }

    private string? KeepStill(TimeSpan requested, string file, int minBytes = 800)
    {
        if (!Grok.Player.Core.Player.LivePlayback.IsUsableStill(file) || LooksBlank(file, minBytes))
        {
            TryDelete(file);
            _lastFile = null;
            return null;
        }

        var hash = StillHash(file);
        var network = _path is not null && _path.Contains("://", StringComparison.Ordinal);
        if (network &&
            hash is not null &&
            hash == _lastStillHash &&
            _lastStillTime >= TimeSpan.Zero &&
            !SeekPreviewDisplay.Fits(requested, _lastStillTime, SeekPreviewDisplay.DecoderDeltaSeconds))
        {
            TryDelete(file);
            _lastFile = null;
            return null;
        }

        if (hash is not null)
        {
            _lastStillHash = hash;
            _lastStillTime = requested;
        }

        SweepImageDump();
        _lastFile = file;
        if (network)
        {
            _firstNetworkPaint = false;
        }

        return _lastFile;
    }

    public void Reset()
    {
        _path = null;
        _ready = false;
        _firstNetworkPaint = true;
        _lastStillHash = null;
        _lastStillTime = TimeSpan.FromSeconds(-1);
        try
        {
            _mpv.Command("stop");
        }
        catch (MpvException)
        {
        }

        Settle(0.2);
    }

    public void Dispose()
    {
        try
        {
            if (_lastFile is not null)
            {
                File.Delete(_lastFile);
            }
        }
        catch (IOException)
        {
        }

        SweepImageDump();
        if (_ownsNative)
        {
            _mpv.TerminateDestroy();
            _mpv.Dispose();
        }
    }

    private void ApplyOptions()
    {
        _mpv.SetOption("config", "no");
        _mpv.SetOption("osc", "no");
        _mpv.SetOption("input-default-bindings", "no");
        _mpv.SetOption("idle", "yes");
        _mpv.SetOption("force-window", "no");
        // vo=null never paints a video frame for screenshot-sw on HLS, so
        // every still is a valid black JPEG. vo=image decodes into a frame.
        _mpv.SetOption("vo", "image");
        _mpv.SetOption("vo-image-outdir", _imageDump.Replace('\\', '/'));
        _mpv.SetOption("vo-image-format", "jpg");
        _mpv.SetOption("untimed", "yes");
        _mpv.SetOption("ao", "null");
        _mpv.SetOption("aid", "no");
        _mpv.SetOption("sid", "no");
        _mpv.SetOption("hwdec", "no");
        _mpv.SetOption("pause", "yes");
        _mpv.SetOption("keep-open", "yes");
        _mpv.SetOption("osd-level", "0");
        _mpv.SetOption("osd-on-seek", "no");
        _mpv.SetOption("hr-seek", "no");
        _mpv.SetOption("hr-seek-framedrop", "yes");
        _mpv.SetOption("vd-lavc-fast", "yes");
        _mpv.SetOption("vd-lavc-threads", "1");
        _mpv.SetOption("vd-lavc-skiploopfilter", "nonkey");
        _mpv.SetOption("screenshot-sw", "yes");
        _mpv.SetOption("screenshot-format", "jpeg");
        _mpv.SetOption("screenshot-jpeg-quality", "48");
        _mpv.SetOption("screenshot-high-bit-depth", "no");
        _mpv.SetOption("ytdl", "no");
        _mpv.SetOption("user-agent", ChromeUa);
        _mpv.SetOption("network-timeout", "8");
        _mpv.SetOption("cache", "yes");
        _mpv.SetOption("demuxer-readahead-secs", "0.4");
        _mpv.SetOption("demuxer-max-bytes", "8MiB");
        _mpv.SetOption("demuxer-lavf-o", "allowed_extensions=ALL");
        _mpv.SetOption("cache-pause-initial", "no");
        // This decoder is the quality-upgrade tier after the tiny storyboard,
        // so selecting the minimum HLS rendition would defeat its purpose.
        _mpv.SetOption("hls-bitrate", "600000");
        _mpv.SetOption("vf", "scale=160:-2");
    }

    private void ApplyTier(bool high)
    {
        if (high)
        {
            TrySet("vf", "scale=320:-2");
            TrySet("screenshot-jpeg-quality", "85");
            return;
        }

        TrySet("vf", "scale=160:-2");
        TrySet("screenshot-jpeg-quality", "48");
    }

    private void EnsureReady()
    {
        if (_ready || string.IsNullOrWhiteSpace(_path))
        {
            return;
        }

        Prepare(_path, _referer);
    }

    private bool FileLooksOpen()
    {
        try
        {
            if ((_mpv.GetPropertyDouble("duration") ?? 0) > 0.4)
            {
                return true;
            }

            if ((_mpv.GetPropertyLong("track-list/count") ?? 0) > 0)
            {
                return true;
            }

            var width = _mpv.GetPropertyLong("width") ?? _mpv.GetPropertyLong("video-params/w");
            return width is > 0;
        }
        catch (MpvException)
        {
            return false;
        }
    }

    private bool SeekLanded(TimeSpan requested, double toleranceSeconds)
    {
        try
        {
            var actual = _mpv.GetPropertyDouble("time-pos");
            if (actual is null) return false;

            return Math.Abs(actual.Value - requested.TotalSeconds) <= toleranceSeconds;
        }
        catch (MpvException)
        {
            return false;
        }
    }

    private bool WaitForPaint(
        TimeSpan requested,
        double toleranceSeconds,
        double timeoutSeconds,
        HashSet<string> beforeDump,
        bool requireRestart)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var frameRestarted = !requireRestart;
        while (DateTime.UtcNow < deadline)
        {
            var ev = _mpv.WaitEvent(0.02);
            if (ev.Id is MpvEventId.PlaybackRestart or MpvEventId.VideoReconfig)
            {
                frameRestarted = true;
            }

            var dumped = NewestDump(beforeDump);
            if (dumped is not null)
            {
                _paintedDump = dumped;
                return true;
            }
        }

        var last = NewestDump(beforeDump);
        if (last is not null)
        {
            _paintedDump = last;
            return true;
        }

        return frameRestarted && SeekLanded(requested, toleranceSeconds);
    }

    private HashSet<string> DumpSnapshot()
    {
        try
        {
            if (!Directory.Exists(_imageDump))
            {
                return [];
            }

            return Directory.EnumerateFiles(_imageDump, "*.jpg").ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private string? NewestDump(HashSet<string> before)
    {
        try
        {
            if (!Directory.Exists(_imageDump))
            {
                return null;
            }

            string? best = null;
            var bestWrite = DateTime.MinValue;
            foreach (var file in Directory.EnumerateFiles(_imageDump, "*.jpg"))
            {
                if (before.Contains(file))
                {
                    continue;
                }

                var info = new FileInfo(file);
                if (info.Length < 800 || info.LastWriteTimeUtc < bestWrite)
                {
                    continue;
                }

                if (LooksBlank(file, 800))
                {
                    continue;
                }

                best = file;
                bestWrite = info.LastWriteTimeUtc;
            }

            return best;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void DrainPendingEvents()
    {
        for (var i = 0; i < 64; i++)
            if (_mpv.WaitEvent(0).Id == MpvEventId.None) break;
    }

    private bool TrySeek(string seconds, string mode)
    {
        try
        {
            _mpv.Command("seek", seconds, mode);
            return true;
        }
        catch (MpvException)
        {
            return false;
        }
    }

    private const string ChromeUa =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";

    private void ApplyNetworkIdentity(string path, string? referer = null)
    {
        if (!UrlSanitizer.IsUrl(path))
        {
            TrySet("user-agent", ChromeUa);
            TrySet("referrer", "");
            TrySet("http-header-fields", "");
            return;
        }

        var page = StreamCatalog.SiteReferer(path, referer);
        var origin = StreamCatalog.PageOrigin(page) ?? StreamCatalog.PageOrigin(path) ?? page;
        TrySet("user-agent", StreamCatalog.ChromeUa);
        TrySet("referrer", page);
        TrySet("http-header-fields", "Referer: " + page + ",Origin: " + origin.TrimEnd('/'));
    }

    private void TrySet(string name, string value)
    {
        try
        {
            _mpv.SetPropertyString(name, value);
        }
        catch (MpvException)
        {
        }
    }

    private static bool LooksLikeYouTube(string path) =>
        path.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

    private static string? StillHash(string file)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool LooksBlank(string file, int minBytes)
    {
        try
        {
            if (new FileInfo(file).Length < minBytes)
            {
                return true;
            }

            using var decoded = SKBitmap.Decode(file);
            if (decoded is null)
            {
                return false;
            }

            if (decoded.Width < 8 || decoded.Height < 8)
            {
                return false;
            }

            var luma = AverageLuma(decoded);
            if (luma < 8)
            {
                return true;
            }

            // Limited-range black JPEGs decode around luma 16 with no texture.
            return luma < 22 && IsFlat(decoded);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static double AverageLuma(SKBitmap bitmap)
    {
        long luma = 0;
        var n = 0;
        var stepX = Math.Max(1, bitmap.Width / 32);
        var stepY = Math.Max(1, bitmap.Height / 32);
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                var c = bitmap.GetPixel(x, y);
                luma += (c.Red * 3) + (c.Green * 6) + c.Blue;
                n++;
            }
        }

        return n == 0 ? 0 : luma / (n * 10.0);
    }

    private static bool IsFlat(SKBitmap bitmap)
    {
        var stepX = Math.Max(1, bitmap.Width / 32);
        var stepY = Math.Max(1, bitmap.Height / 32);
        var min = 255;
        var max = 0;
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                var c = bitmap.GetPixel(x, y);
                var v = ((c.Red * 3) + (c.Green * 6) + c.Blue) / 10;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        return max - min <= 4;
    }

    private void SweepImageDump()
    {
        try
        {
            if (!Directory.Exists(_imageDump))
            {
                return;
            }

            foreach (var leftover in Directory.EnumerateFiles(_imageDump))
            {
                TryDelete(leftover);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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

    private bool WaitForFile(double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var ev = _mpv.WaitEvent(0.04);
            if (ev.Id == MpvEventId.FileLoaded || FileLooksOpen())
            {
                Settle(0.05);
                return true;
            }
        }

        Settle(0.08);
        return FileLooksOpen();
    }

    private void Settle(double seconds)
    {
        var idle = 0;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var ev = _mpv.WaitEvent(0.03);
            if (ev.Id == MpvEventId.None)
            {
                idle++;
                if (idle >= 2)
                {
                    return;
                }
            }
            else
            {
                idle = 0;
            }
        }
    }
}
