using System.Globalization;
using Grok.Player.Core.Native;

namespace Grok.Player.Core.Preview;

public sealed class SeekPreviewEngine : ISeekPreviewRenderer
{
    private readonly IMpvNative _mpv;
    private readonly bool _ownsNative;
    private string? _path;
    private string? _lastFile;
    private bool _ready;

    public SeekPreviewEngine(IMpvNative mpv, bool ownsNative = true)
    {
        _mpv = mpv ?? throw new ArgumentNullException(nameof(mpv));
        _ownsNative = ownsNative;
        ApplyOptions();
        _mpv.Initialize();
        _mpv.SetPropertyFlag("pause", true);
    }

    public static SeekPreviewEngine Create() => new(new MpvNative());

    public void Prepare(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) && _ready)
        {
            return;
        }

        var youtube = LooksLikeYouTube(path);
        if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) && !_ready)
        {
            _ready = WaitForFile(youtube ? 10 : 1.6);
            return;
        }

        _path = path;
        _ready = false;
        ApplyNetworkIdentity(path);
        _mpv.Command("loadfile", path, "replace");
        _mpv.SetPropertyFlag("pause", true);
        var wait = youtube ? 10 : path.Contains("://", StringComparison.Ordinal) ? 2.4 : 0.8;
        _ready = WaitForFile(wait);
    }

    public string? Capture(TimeSpan time)
    {
        if (string.IsNullOrWhiteSpace(_path) || !_ready)
        {
            return null;
        }

        var seconds = Math.Max(0, time.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        var network = _path.Contains("://", StringComparison.Ordinal);
        if (!TrySeek(seconds, network ? "absolute+keyframes" : "absolute") &&
            !TrySeek(seconds, "absolute"))
        {
            return null;
        }

        WaitForSeek();
        if (network && !SeekLanded(time))
        {
            return null;
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

        _lastFile = Grok.Player.Core.Player.LivePlayback.IsUsableStill(file) ? file : null;
        return _lastFile;
    }

    public void Reset()
    {
        _path = null;
        _ready = false;
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
        _mpv.SetOption("vo", "null");
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
        _mpv.SetOption("vd-lavc-skiploopfilter", "nonkey");
        _mpv.SetOption("screenshot-sw", "yes");
        _mpv.SetOption("screenshot-format", "jpeg");
        _mpv.SetOption("screenshot-jpeg-quality", "82");
        _mpv.SetOption("screenshot-high-bit-depth", "no");
        _mpv.SetOption("ytdl", "no");
        _mpv.SetOption("user-agent", ChromeUa);
        _mpv.SetOption("network-timeout", "8");
        _mpv.SetOption("cache", "yes");
        _mpv.SetOption("demuxer-readahead-secs", "0.4");
        _mpv.SetOption("demuxer-max-bytes", "8MiB");
        _mpv.SetOption("demuxer-lavf-o", "allowed_extensions=ALL");
        _mpv.SetOption("cache-pause-initial", "no");
        _mpv.SetOption("hls-bitrate", "min");
        _mpv.SetOption("vf", "scale=512:-2");
    }

    private bool SeekLanded(TimeSpan requested)
    {
        try
        {
            var actual = _mpv.GetPropertyDouble("time-pos");
            if (actual is null)
            {
                return true;
            }

            return Math.Abs(actual.Value - requested.TotalSeconds) <= 12;
        }
        catch (MpvException)
        {
            return true;
        }
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

    private void ApplyNetworkIdentity(string path)
    {
        if (!LooksLikeYouTube(path))
        {
            TrySet("user-agent", ChromeUa);
            TrySet("referrer", "");
            TrySet("http-header-fields", "");
            return;
        }

        TrySet("user-agent", ChromeUa);
        TrySet("referrer", "https://www.youtube.com");
        TrySet("http-header-fields", "Referer: https://www.youtube.com,Origin: https://www.youtube.com");
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

    private bool WaitForFile(double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var ev = _mpv.WaitEvent(0.04);
            if (ev.Id == MpvEventId.FileLoaded)
            {
                Settle(0.05);
                return true;
            }
        }

        Settle(0.08);
        return false;
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
