using System.Globalization;
using System.Security.Cryptography;
using Grok.Player.Core.Media;
using Grok.Player.Core.Native;

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

    public SeekPreviewEngine(IMpvNative mpv, bool ownsNative = true)
    {
        _mpv = mpv ?? throw new ArgumentNullException(nameof(mpv));
        _ownsNative = ownsNative;
        _imageDump = Path.Combine(Path.GetTempPath(), "grok-player-preview-vo");
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
    }

    public string? CaptureFast(TimeSpan time)
    {
        EnsureReady();
        if (string.IsNullOrWhiteSpace(_path) || !_ready)
        {
            return null;
        }

        var seconds = Math.Max(0, time.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        ApplyTier(high: false);
        DrainPendingEvents();
        if (!TrySeek(seconds, "absolute+keyframes") && !TrySeek(seconds, "absolute"))
        {
            return null;
        }

        if (_path is not null && _path.Contains("://", StringComparison.Ordinal))
        {
            if (!WaitForSeekLanding(time, SeekPreviewDisplay.KeyframeToleranceSeconds, 3.0) ||
                !SeekLanded(time, SeekPreviewDisplay.KeyframeToleranceSeconds))
            {
                return null;
            }
        }
        else
        {
            Settle(0.05);
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

        var seconds = Math.Max(0, time.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        var network = _path.Contains("://", StringComparison.Ordinal);
        var youtube = LooksLikeYouTube(_path);
        ApplyTier(high: true);
        DrainPendingEvents();
        var alreadyLanded = !exact && SeekLanded(time, 2.5);
        if (exact
            ? !TrySeek(seconds, "absolute+exact")
            : !alreadyLanded &&
              !TrySeek(seconds, network ? "absolute+keyframes" : "absolute") &&
              !TrySeek(seconds, "absolute"))
        {
            return null;
        }

        if (exact)
        {
            if (!WaitForSeekLanding(time, 0.35, 1.0) || !SeekLanded(time, 0.35))
            {
                return null;
            }
        }
        else if (youtube)
        {
            if (!WaitForSeekLanding(time, 2.5, alreadyLanded ? 0.8 : 3.0) || !SeekLanded(time, 2.5))
            {
                return null;
            }
        }
        else if (network)
        {
            if (!WaitForSeekLanding(
                    time,
                    SeekPreviewDisplay.KeyframeToleranceSeconds,
                    alreadyLanded ? 0.6 : 3.0) ||
                !SeekLanded(time, SeekPreviewDisplay.KeyframeToleranceSeconds))
            {
                return null;
            }
        }
        else
        {
            WaitForSeek();
        }

        return TakeStill(time, minBytes: 2400);
    }

    private string? TakeStill(TimeSpan requested, int minBytes)
    {
        var file = Path.Combine(Path.GetTempPath(), $"grok-player-seek-{Guid.NewGuid():N}.jpg");
        try
        {
            _mpv.Command("screenshot-to-file", file, "video");
        }
        catch (MpvException)
        {
            return null;
        }

        if (!Grok.Player.Core.Player.LivePlayback.IsUsableStill(file) || LooksBlank(file, minBytes))
        {
            TryDelete(file);
            _lastFile = null;
            return null;
        }

        if (_path is not null &&
            _path.Contains("://", StringComparison.Ordinal) &&
            !SeekLanded(requested, SeekPreviewDisplay.KeyframeToleranceSeconds))
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
        return _lastFile;
    }

    public void Reset()
    {
        _path = null;
        _ready = false;
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
            TrySet("vf", "scale=512:-2");
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
            return true;
        }
    }

    private bool WaitForSeekLanding(TimeSpan requested, double toleranceSeconds, double timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var frameRestarted = false;
        while (DateTime.UtcNow < deadline)
        {
            var ev = _mpv.WaitEvent(0.02);
            if (ev.Id is MpvEventId.PlaybackRestart or MpvEventId.VideoReconfig)
            {
                frameRestarted = true;
            }

            // time-pos can update before the decoder replaces its previous
            // video frame. Waiting for PlaybackRestart prevents a screenshot
            // from carrying an older hover image under the new timestamp.
            if (frameRestarted && SeekLanded(requested, toleranceSeconds))
            {
                Settle(0.03);
                return SeekLanded(requested, toleranceSeconds);
            }
        }
        return false;
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
            return new FileInfo(file).Length < minBytes;
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

    private void WaitForSeek()
    {
        var wait = _path is not null && _path.Contains("://", StringComparison.Ordinal) ? 700 : 140;
        var deadline = DateTime.UtcNow.AddMilliseconds(wait);
        while (DateTime.UtcNow < deadline)
        {
            var ev = _mpv.WaitEvent(0.015);
            if (ev.Id is MpvEventId.PlaybackRestart or MpvEventId.FileLoaded)
            {
                Settle(0.01);
                return;
            }
        }

        Settle(0.02);
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
