using Grok.Player.Core.Media;
using Grok.Player.Core.Native;
using Grok.Player.Core.Video;
using Grok.Player.Core.Preview;
using System.Globalization;
using System.Text.Json;

namespace Grok.Player.Core.Player;

public sealed class PlayerHost : IDisposable
{
    private readonly IMpvNative _mpv;
    private readonly PlayerHostOptions _options;
    private readonly object _gate = new();
    private readonly object _cacheExportGate = new();
    private readonly SynchronizationContext? _sync;
    private Thread? _eventThread;
    private volatile bool _runEvents;
    private bool _disposed;
    private bool _ownsNative;
    private bool _filterPads;
    private bool _softerOn;
    private bool _sharpenOn;
    private bool _deblockOn;
    private bool _catchUpLive;
    private bool _followLive;
    private int _catchUpSeeks;
    private DateTime _catchUpUntil;
    private bool _networkMedia;
    private TimeSpan _seekTarget;
    private DateTime _seekGuardUntil;
    private string? _audioLang;
    private string? _subLang;
    private string? _extraAudio;
    private string? _pendingSubFile;
    private bool _wantMuted;
    private bool _styledSubtitle;
    private string _subtitleFont = "Segoe UI";
    private double _subtitleFontSize = 55;
    private int _subtitleShift;
    private int _langSelectLeft;
    private int _mediaRevision;
    private bool _durationFromDemuxer;

    public PlayerHost(IMpvNative mpv, PlayerHostOptions? options = null, bool ownsNative = true)
    {
        _mpv = mpv ?? throw new ArgumentNullException(nameof(mpv));
        _options = options ?? new PlayerHostOptions();
        _ownsNative = ownsNative;
        _sync = SynchronizationContext.Current;
        Volume = PlaybackMath.ClampVolume(_options.InitialVolume);

        ApplyStartupOptions();
        _mpv.Initialize();
        ObservePlaybackProperties();
        ApplyInitialVolume();
        InstallVideoFilterPads();

        if (_options.UseBackgroundEventLoop)
        {
            StartEventLoop();
        }
    }

    public PlayerState State { get; private set; } = PlayerState.Idle;

    public TimeSpan Position { get; private set; }

    public TimeSpan? Duration { get; private set; }

    public double Volume { get; private set; }

    public bool IsPaused { get; private set; } = true;

    public string? MediaPath { get; private set; }

    public string? MediaTitle { get; private set; }

    public string? HwdecCurrent { get; private set; }

    public string? LastError { get; private set; }

    public bool IsMuted { get; private set; }

    public bool HasVideo { get; private set; } = true;

    public bool IsRecording { get; private set; }

    public bool IsSeekable { get; private set; } = true;

    public double CacheEndSeconds { get; private set; }

    public TimeSpan LiveEdge { get; private set; }

    public bool LiveWindow { get; private set; }

    public bool IsFollowingLive { get; private set; }

    public string? FileFormat { get; private set; }

    public bool HasMedia => State is PlayerState.Opening
        or PlayerState.Playing
        or PlayerState.Paused
        or PlayerState.Stopped
        or PlayerState.Ended;

    public bool CanPlay => HasMedia && State != PlayerState.Playing && State != PlayerState.Opening;

    public bool CanPause => State == PlayerState.Playing;

    public bool CanStop => HasMedia && State is not PlayerState.Stopped and not PlayerState.Idle;

    public bool CanSeek =>
        HasMedia &&
        (Duration is { } duration && duration > TimeSpan.Zero ||
         LiveWindow && LiveEdge > TimeSpan.FromSeconds(1));

    public event EventHandler? StateChanged;
    public event EventHandler? TimeChanged;
    public event EventHandler? DurationChanged;
    public event EventHandler? VolumeChanged;
    public event EventHandler<PlayerErrorEventArgs>? Error;
    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;

    public static PlayerHost CreateForInterface(nint hwnd)
    {
        return new PlayerHost(new MpvNative(), PlayerHostOptions.ForUserInterface(hwnd));
    }

    public static PlayerHost CreateHeadless()
    {
        return new PlayerHost(new MpvNative(), new PlayerHostOptions
        {
            Headless = true,
            HardwareDecode = false,
            VideoOutput = "null",
            AudioOutput = "null",
            UseBackgroundEventLoop = true
        });
    }

    public void Open(string path) => Open(path, StreamKind.Unknown);

    public void Open(string path, StreamKind kind, string? audioUrl = null, string? title = null, string? userAgent = null, string? audioLang = null, string? subLang = null, string? subFile = null, string? referer = null, string? formatHint = null, string? httpHeaders = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureNotDisposed();

        var trimmed = path.Trim();
        if (PlaybackMath.LooksLikeLocalPath(trimmed) && !File.Exists(trimmed))
        {
            throw new FileNotFoundException("Media file was not found.", trimmed);
        }

        lock (_gate)
        {
            _mediaRevision++;
            LastError = null;
            MediaPath = trimmed;
            MediaTitle = !string.IsNullOrWhiteSpace(title)
                ? title.Trim()
                : UrlSanitizer.IsUrl(trimmed)
                    ? UrlSanitizer.DisplayName(trimmed)
                    : Path.GetFileName(trimmed);
            IsRecording = false;
            CacheEndSeconds = 0;
            LiveEdge = TimeSpan.Zero;
            LiveWindow = kind == StreamKind.Live ||
                         (kind != StreamKind.Vod && LooksLikeLiveWindow(trimmed));
            _networkMedia = UrlSanitizer.IsUrl(trimmed);
            _seekGuardUntil = DateTime.MinValue;
            _audioLang = string.IsNullOrWhiteSpace(audioLang) ? null : MediaLanguage.Normalize(audioLang);
            _subLang = string.IsNullOrWhiteSpace(subLang) ? null : MediaLanguage.Normalize(subLang);
            _extraAudio = string.IsNullOrWhiteSpace(audioUrl) ? null : audioUrl.Trim();
            _pendingSubFile = string.IsNullOrWhiteSpace(subFile) ? null : subFile;
            _langSelectLeft = 12;
            _catchUpLive = false;
            _followLive = false;
            _catchUpSeeks = 0;
            IsFollowingLive = false;
            Position = TimeSpan.Zero;
            Duration = null;
            _durationFromDemuxer = false;
            SetState_NoLock(PlayerState.Opening);
        }

        ApplyOpenProfile(trimmed, kind, userAgent, referer, httpHeaders);
        TrySetProperty("demuxer-lavf-format", string.IsNullOrWhiteSpace(formatHint) ? "" : formatHint.Trim());
        if (!string.IsNullOrWhiteSpace(_extraAudio))
        {
            TrySetProperty("audio-files", _extraAudio);
            TrySetProperty("audio-file", _extraAudio);
        }
        else
        {
            try
            {
                _mpv.Command("audio-remove");
            }
            catch (MpvException)
            {
            }

            TrySetProperty("audio-files", "");
            TrySetProperty("audio-file", "");
            TrySetProperty("aid", "auto");
        }

        if (!string.IsNullOrWhiteSpace(_audioLang))
        {
            TrySetProperty("alang", _audioLang + ",en");
        }

        if (kind == StreamKind.Live || (!string.IsNullOrWhiteSpace(subFile) && File.Exists(subFile)))
        {
            TrySetProperty("slang", "no");
        }
        else if (!string.IsNullOrWhiteSpace(_subLang))
        {
            TrySetProperty("slang", _subLang);
        }
        else
        {
            TrySetProperty("slang", _networkMedia ? "no" : "");
        }

        SetSubtitleFile(null);

        try
        {
            _mpv.Command("loadfile", trimmed, "replace");
            _mpv.SetPropertyFlag("pause", false);
        }
        catch (MpvException ex)
        {
            lock (_gate)
            {
                LastError = ex.Message;
                SetState_NoLock(PlayerState.Error);
            }

            Raise(StateChanged);
            RaiseError(ex.Message);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_extraAudio))
        {
            try
            {
                _mpv.Command("audio-add", _extraAudio);
            }
            catch (MpvException)
            {
            }
        }
        Raise(TimeChanged);
        Raise(DurationChanged);
        Raise(StateChanged);
    }

    public void Play()
    {
        EnsureNotDisposed();
        if (!HasMedia)
        {
            throw new InvalidOperationException("Nothing is loaded.");
        }

        if (State == PlayerState.Ended)
        {
            Seek(TimeSpan.Zero);
        }

        if (IsMuted || Volume <= 0)
        {
            SilenceOutput();
        }

        _mpv.SetPropertyFlag("pause", false);
        lock (_gate)
        {
            IsPaused = false;
            SetState_NoLock(PlayerState.Playing);
        }

        Raise(StateChanged);
    }

    public void Pause()
    {
        EnsureNotDisposed();
        if (!HasMedia)
        {
            throw new InvalidOperationException("Nothing is loaded.");
        }

        _mpv.SetPropertyFlag("pause", true);
        lock (_gate)
        {
            IsPaused = true;
            if (State != PlayerState.Stopped && State != PlayerState.Ended)
            {
                SetState_NoLock(PlayerState.Paused);
            }
        }

        Raise(StateChanged);
    }

    public void TogglePause()
    {
        if (State == PlayerState.Playing)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    public void Stop()
    {
        EnsureNotDisposed();
        if (!HasMedia && State == PlayerState.Idle)
        {
            return;
        }

        try
        {
            _mpv.SetPropertyFlag("pause", true);
            _mpv.Command("stop");
        }
        catch (MpvException)
        {
        }

        lock (_gate)
        {
            MediaPath = null;
            MediaTitle = null;
            FileFormat = null;
            HwdecCurrent = null;
            Duration = null;
            Position = TimeSpan.Zero;
            IsPaused = true;
            HasVideo = true;
            IsRecording = false;
            CacheEndSeconds = 0;
            LiveEdge = TimeSpan.Zero;
            LiveWindow = false;
            SetState_NoLock(PlayerState.Idle);
        }

        Raise(TimeChanged);
        Raise(DurationChanged);
        Raise(StateChanged);
    }

    public void Seek(TimeSpan position)
    {
        EnsureNotDisposed();
        if (!HasMedia)
        {
            throw new InvalidOperationException("Nothing is loaded.");
        }

        TimeSpan? duration;
        lock (_gate)
        {
            duration = SeekLimit_NoLock();
        }

        var clamped = PlaybackMath.ClampPosition(position, duration);
        if (LiveWindow)
        {
            var tip = LivePlayback.TipSeconds(clamped.TotalSeconds, LiveEdge.TotalSeconds, CacheEndSeconds);
            clamped = TimeSpan.FromSeconds(LivePlayback.ClampToWindow(clamped.TotalSeconds, tip));
        }

        var seconds = clamped.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _catchUpLive = false;
        _followLive = false;
        IsFollowingLive = false;
        var mode = LiveWindow || _networkMedia ? "absolute" : "absolute+exact";
        if (!TrySeek(seconds, mode) && (LiveWindow || _networkMedia || !TrySeek(seconds, "absolute")))
        {
            return;
        }

        _seekTarget = clamped;
        _seekGuardUntil = DateTime.UtcNow.AddSeconds(2.5);
        lock (_gate)
        {
            Position = clamped;
        }

        Raise(TimeChanged);
    }

    public void SeekLive()
    {
        EnsureNotDisposed();
        if (!HasMedia)
        {
            throw new InvalidOperationException("Nothing is loaded.");
        }

        double target;
        lock (_gate)
        {
            if (LiveWindow)
            {
                var tip = LivePlayback.TipSeconds(
                    Position.TotalSeconds,
                    LiveEdge.TotalSeconds,
                    CacheEndSeconds);
                if (LivePlayback.IsAtLive(Position.TotalSeconds, tip))
                {
                    IsFollowingLive = true;
                    return;
                }

                target = LivePlayback.SnapTargetSeconds(
                    Position.TotalSeconds,
                    LiveEdge.TotalSeconds,
                    CacheEndSeconds);
            }
            else
            {
                target = -1;
            }
        }

        var seeked = false;
        if (target < 0)
        {
            seeked = TrySeek("100", "absolute-percent");
        }
        else
        {
            seeked = TrySeek(target.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute") ||
                     TrySeek("100", "absolute-percent");
        }

        if (!seeked)
        {
            return;
        }

        _followLive = false;
        _catchUpLive = false;
        IsFollowingLive = true;
        lock (_gate)
        {
            var expected = target >= 0
                ? target
                : SeekLimit_NoLock()?.TotalSeconds ?? Position.TotalSeconds;
            _seekTarget = TimeSpan.FromSeconds(Math.Max(0, expected));
            // mpv commonly reports the pre-seek DVR position once or twice after
            // jumping to the live edge. Keep those stale events from cancelling
            // follow-live and visually pushing the seek thumb backwards.
            _seekGuardUntil = DateTime.UtcNow.AddSeconds(2.5);
            Position = _seekTarget;
        }

        Raise(TimeChanged);
    }

    public void SetRecording(string? path)
    {
        EnsureNotDisposed();
        var value = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();
        _mpv.SetPropertyString("stream-record", value);
        IsRecording = value.Length > 0;
    }

    public void SetVolume(double volume)
    {
        EnsureNotDisposed();
        var clamped = PlaybackMath.ClampVolume(volume);
        _mpv.SetPropertyDouble("volume", clamped);
        if (clamped <= 0)
        {
            _mpv.SetPropertyFlag("mute", true);
        }
        else
        {
            _mpv.SetPropertyFlag("mute", false);
        }

        lock (_gate)
        {
            Volume = clamped;
            IsMuted = clamped <= 0;
        }

        Raise(VolumeChanged);
    }

    public void SetMuted(bool muted)
    {
        EnsureNotDisposed();
        _wantMuted = muted;
        if (muted)
        {
            SilenceOutput();
        }
        else
        {
            RestoreOutput();
        }

        lock (_gate)
        {
            IsMuted = muted;
        }

        Raise(VolumeChanged);
    }

    private void ApplyDesiredAudio()
    {
        if (_wantMuted || Volume <= 0)
        {
            SilenceOutput();
            lock (_gate)
            {
                IsMuted = true;
            }

            return;
        }

        RestoreOutput();
        lock (_gate)
        {
            IsMuted = false;
        }
    }

    private void RestoreOutput()
    {
        TrySetProperty("ao-volume", "100");
        _mpv.SetPropertyFlag("mute", false);
    }

    private void SilenceOutput()
    {
        _mpv.SetPropertyFlag("mute", true);
        TrySetProperty("ao-volume", "0");
        // Drop WASAPI samples queued before mute. Otherwise unpause plays them.
        if (IsPaused || State == PlayerState.Paused)
        {
            try
            {
                _mpv.Command("seek", "0", "relative");
            }
            catch (MpvException)
            {
            }
        }
    }

    public void ToggleMute() => SetMuted(!IsMuted);

    public void HintDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (Duration is { } known && known > TimeSpan.Zero)
            {
                return;
            }

            Duration = duration;
        }

        Raise(DurationChanged);
    }

    public void SetEqualizer(bool enabled, IReadOnlyList<double> uiGains)
    {
        EnsureNotDisposed();
        if (!enabled || uiGains.Count == 0)
        {
            _mpv.SetPropertyString("af", string.Empty);
            return;
        }

        var count = Math.Min(Audio.EqualizerSpec.BandCount, uiGains.Count);
        var parts = new string[count];
        for (var i = 0; i < count; i++)
        {
            var db = Audio.EqualizerSpec.ToDb(uiGains[i]).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            parts[i] = $"equalizer=f={Audio.EqualizerSpec.FrequenciesHz[i]}:t=o:w=1:g={db}";
        }

        _mpv.SetPropertyString("af", "lavfi=[" + string.Join(",", parts) + "]");
    }

    public string? GetAudioFilter()
    {
        EnsureNotDisposed();
        return _mpv.GetPropertyString("af");
    }

    public void SetVideoPicture(double brightnessUi, double contrastUi, double saturationUi, double hueUi)
    {
        EnsureNotDisposed();
        _mpv.SetPropertyDouble("brightness", VideoPictureSpec.ToMpv(brightnessUi));
        _mpv.SetPropertyDouble("contrast", VideoPictureSpec.ToMpv(contrastUi));
        _mpv.SetPropertyDouble("saturation", VideoPictureSpec.ToMpv(saturationUi));
        _mpv.SetPropertyDouble("hue", VideoPictureSpec.ToMpv(hueUi));
    }

    public void SetVideoFilters(bool softer, bool sharpen, bool deblock)
    {
        EnsureNotDisposed();
        if (_filterPads)
        {
            ToggleFilterPad("@deblock", deblock, ref _deblockOn);
            ToggleFilterPad("@softer", softer, ref _softerOn);
            ToggleFilterPad("@sharpen", sharpen, ref _sharpenOn);
            return;
        }

        var graph = VideoFilterGraph.Build(softer, sharpen, deblock);
        _mpv.SetPropertyString("vf", graph);
    }

    public string? GetVideoFilter()
    {
        EnsureNotDisposed();
        return _mpv.GetPropertyString("vf");
    }

    public VideoSize GetVideoPixelSize()
    {
        EnsureNotDisposed();
        var width = _mpv.GetPropertyLong("video-params/w") ?? _mpv.GetPropertyLong("width") ?? 0;
        var height = _mpv.GetPropertyLong("video-params/h") ?? _mpv.GetPropertyLong("height") ?? 0;
        return new VideoSize((int)width, (int)height);
    }

    public void SetScalingQuality(ScalingQualitySettings settings)
    {
        EnsureNotDisposed();
        _mpv.SetPropertyString("scale", ScalingQualitySpec.MpvName(settings.Upscale));
        _mpv.SetPropertyString("dscale", ScalingQualitySpec.MpvName(settings.Downscale));
        _mpv.SetPropertyString("cscale", ScalingQualitySpec.MpvName(settings.Chroma));
        var ring = ScalingQualitySpec.AntiRing(settings.AntiRing);
        _mpv.SetPropertyDouble("scale-antiring", ring);
        _mpv.SetPropertyDouble("dscale-antiring", ring);
        _mpv.SetPropertyDouble("cscale-antiring", ring);
        var deband = ScalingQualitySpec.DebandEnabled(settings.Deband);
        _mpv.SetPropertyFlag("deband", deband);
        if (deband)
        {
            _mpv.SetPropertyLong("deband-iterations", ScalingQualitySpec.DebandIterations(settings.Deband));
            _mpv.SetPropertyDouble("deband-threshold", ScalingQualitySpec.DebandThreshold(settings.Deband));
            _mpv.SetPropertyDouble("deband-range", ScalingQualitySpec.DebandRange(settings.Deband));
            _mpv.SetPropertyDouble("deband-grain", ScalingQualitySpec.DebandGrain(settings.Deband));
        }
    }

    public void SetVideoResize(VideoResizeSettings settings, VideoResizeContext context)
    {
        EnsureNotDisposed();
        var plan = VideoResizeSpec.Plan(settings, context);
        TrySetProperty("video-aspect-override", VideoResizeSpec.AspectOverride(settings, context));
        try { _mpv.SetPropertyFlag("keepaspect", plan.KeepAspect); } catch (MpvException) { }
        try { _mpv.SetPropertyDouble("panscan", plan.Panscan); } catch (MpvException) { }
        TrySetProperty("video-unscaled", plan.Unscaled);
        try { _mpv.SetPropertyDouble("video-scale-x", plan.ScaleX); } catch (MpvException) { }
        try { _mpv.SetPropertyDouble("video-scale-y", plan.ScaleY); } catch (MpvException) { }
    }

    internal double? GetMpvDouble(string name)
    {
        EnsureNotDisposed();
        return _mpv.GetPropertyDouble(name);
    }

    public string? GetMpvString(string name)
    {
        EnsureNotDisposed();
        return _mpv.GetPropertyString(name);
    }

    public long? GetMpvLong(string name)
    {
        EnsureNotDisposed();
        return _mpv.GetPropertyLong(name);
    }

    public bool SetSubtitleFile(string? path)
    {
        EnsureNotDisposed();
        _styledSubtitle = path?.EndsWith(".ass", StringComparison.OrdinalIgnoreCase) == true;
        try
        {
            _mpv.SetPropertyString("sid", "no");
        }
        catch (MpvException)
        {
        }

        try
        {
            _mpv.Command("sub-remove");
        }
        catch (MpvException)
        {
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.IsNullOrWhiteSpace(path);
        }

        try
        {
            _mpv.Command("sub-add", path, "select");
            TrySetProperty("slang", "no");
            TrySetProperty("sub-visibility", "yes");
            SelectAddedSubtitle(path);
            if (path.EndsWith(".ass", StringComparison.OrdinalIgnoreCase))
            {
                // Override the base font/size with the control panel settings,
                // while preserving inline color and emphasis tags.
                TrySetProperty("sub-ass-override", "yes");
                ApplyAssBaseStyle();
            }

            if (!_options.Headless)
            {
                TrySetProperty("blend-subtitles", "yes");
            }

            return true;
        }
        catch (MpvException)
        {
            return false;
        }
    }

    private void SelectAddedSubtitle(string path)
    {
        var count = _mpv.GetPropertyLong("track-list/count") ?? 0;
        long? fallback = null;
        for (var i = 0; i < count && i < 80; i++)
        {
            var prefix = "track-list/" + i + "/";
            var type = _mpv.GetPropertyString(prefix + "type") ?? "";
            if (!type.Equals("sub", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = _mpv.GetPropertyLong(prefix + "id");
            if (id is null)
            {
                continue;
            }

            fallback = id;
            var src = _mpv.GetPropertyString(prefix + "external-filename") ?? "";
            if (src.Length > 0 && src.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                TrySetProperty("sid", id.Value.ToString());
                return;
            }
        }

        if (fallback is { } sid)
        {
            TrySetProperty("sid", sid.ToString());
        }
    }

    public void SetSubFont(string name)
    {
        EnsureNotDisposed();
        _subtitleFont = string.IsNullOrWhiteSpace(name) ? "Segoe UI" : name.Trim();
        _mpv.SetPropertyString("sub-font", _subtitleFont);
        ApplyAssBaseStyle();
    }

    public void SetSubFontSize(double size)
    {
        EnsureNotDisposed();
        _subtitleFontSize = Math.Clamp(size, 8, 200);
        _mpv.SetPropertyDouble("sub-font-size", _subtitleFontSize);
        ApplyAssBaseStyle();
    }

    public void SetSubPos(int pos)
    {
        EnsureNotDisposed();
        _mpv.SetPropertyLong("sub-pos", Math.Clamp(pos, 0, 100));
    }

    public void SetSubShiftX(int steps)
    {
        EnsureNotDisposed();
        _subtitleShift = Math.Clamp(steps, -20, 20);
        ApplyAssBaseStyle();
    }

    private void ApplyAssBaseStyle()
    {
        if (!_styledSubtitle) return;
        var left = Math.Max(0, _subtitleShift * 24);
        var right = Math.Max(0, -_subtitleShift * 24);
        var safeFont = _subtitleFont.Replace(",", " ", StringComparison.Ordinal);
        _mpv.SetPropertyString("sub-ass-override", "yes");
        _mpv.SetPropertyString("sub-ass-force-style",
            $"FontName={safeFont},FontSize={_subtitleFontSize:0.##},MarginL={left},MarginR={right}");
    }

    public void SetSpeed(double speed)
    {
        EnsureNotDisposed();
        _mpv.SetPropertyDouble("speed", PlaybackSpec.ClampSpeed(speed));
    }

    public void SetAbLoop(double? start, double? end)
    {
        EnsureNotDisposed();
        try
        {
            _mpv.SetPropertyString(
                "ab-loop-a",
                start is { } a
                    ? a.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "no");
            _mpv.SetPropertyString(
                "ab-loop-b",
                end is { } b
                    ? b.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "no");
            _mpv.SetPropertyString(
                "ab-loop-count",
                start is not null && end is not null ? "inf" : "0");
        }
        catch (MpvException)
        {
        }
    }

    public void SetSubDelay(double seconds)
    {
        EnsureNotDisposed();
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return;
        }

        try
        {
            _mpv.SetPropertyDouble("sub-delay", Math.Clamp(seconds, -3600, 3600));
        }
        catch (MpvException)
        {
        }
    }

    public void CaptureFrame(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureNotDisposed();
        if (!HasMedia)
        {
            throw new InvalidOperationException("Nothing is loaded.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // window = the frame on screen (picture + vf). Headless has no VO.
        _mpv.Command("screenshot-to-file", path, _options.Headless ? "video" : "window");
    }

    public bool TryCaptureVideo(string path, bool includeWindow = true)
    {
        if (string.IsNullOrWhiteSpace(path) || _disposed || !HasMedia || State == PlayerState.Opening)
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var targets = _options.Headless || !includeWindow ? new[] { "video" } : new[] { "window", "video" };
            foreach (var target in targets)
            {
                try
                {
                    _mpv.Command("screenshot-to-file", path, target);
                    if (LivePlayback.IsUsableStill(path))
                    {
                        return true;
                    }
                }
                catch (MpvException)
                {
                }
            }

            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public CachedPreviewClip? ExportCachedPreviewClip(TimeSpan requested)
    {
        if (_disposed || !LiveWindow || State == PlayerState.Opening) return null;
        var output = Path.Combine(Path.GetTempPath(), $"grok-live-cache-{Guid.NewGuid():N}.mkv");
        lock (_cacheExportGate)
        {
            if (_disposed || !LiveWindow || State == PlayerState.Opening) return null;
            var revision = _mediaRevision;
            try
            {
                var json = _mpv.GetPropertyString("demuxer-cache-state");
                if (string.IsNullOrWhiteSpace(json)) return null;
                using var state = JsonDocument.Parse(json);
                if (state.RootElement.TryGetProperty("total-bytes", out var bytes) &&
                    bytes.GetInt64() > 64L * 1024 * 1024) return null;
                if (!state.RootElement.TryGetProperty("seekable-ranges", out var ranges)) return null;
                foreach (var item in ranges.EnumerateArray())
                {
                    var start = item.GetProperty("start").GetDouble();
                    var end = item.GetProperty("end").GetDouble();
                    if (requested.TotalSeconds < start || requested.TotalSeconds >= end) continue;
                    _mpv.Command("dump-cache",
                        start.ToString("R", CultureInfo.InvariantCulture),
                        end.ToString("R", CultureInfo.InvariantCulture), output);
                    if (_disposed || !LiveWindow || revision != _mediaRevision || !File.Exists(output) ||
                        new FileInfo(output).Length < 4096) break;
                    return new CachedPreviewClip(output, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));
                }
            }
            catch (Exception) { }
        }
        try { File.Delete(output); } catch (IOException) { }
        return null;
    }

    public double? PreviewLiveEdgeSeconds()
    {
        if (_disposed || !LiveWindow) return null;
        try
        {
            var state = _mpv.GetPropertyString("demuxer-cache-state");
            if (string.IsNullOrWhiteSpace(state)) return null;
            using var json = System.Text.Json.JsonDocument.Parse(state);
            return json.RootElement.TryGetProperty("cache-end", out var edge) &&
                   double.IsFinite(edge.GetDouble())
                ? edge.GetDouble()
                : null;
        }
        catch (MpvException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public void SetFileLoop(bool enabled)
    {
        EnsureNotDisposed();
        _mpv.SetPropertyString("loop-file", enabled ? "inf" : "no");
    }

    public void SeekBy(TimeSpan delta)
    {
        EnsureNotDisposed();
        if (!HasMedia)
        {
            return;
        }

        Seek(Position + delta);
    }

    public void AttachSurface(nint hwnd)
    {
        EnsureNotDisposed();
        _mpv.SetPropertyLong("wid", hwnd.ToInt64());
    }

    public void DetachSurface()
    {
        if (_disposed || _mpv.IsTerminated)
        {
            return;
        }

        try
        {
            _mpv.SetPropertyLong("wid", 0);
        }
        catch (MpvException)
        {
            // Destroy order must continue even if the VO is already gone.
        }
    }

    public void ProcessPendingEvents()
    {
        EnsureNotDisposed();
        DrainEvents(0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _runEvents = false;

        try
        {
            if (!_mpv.IsTerminated)
            {
                try
                {
                    _mpv.SetPropertyFlag("pause", true);
                }
                catch (MpvException)
                {
                }

                DetachSurface();
            }
        }
        finally
        {
            _disposed = true;
            _mpv.Wakeup();
            if (_eventThread is { IsAlive: true } && Thread.CurrentThread != _eventThread)
            {
                _eventThread.Join(TimeSpan.FromSeconds(3));
            }

            if (_ownsNative)
            {
                try
                {
                    _mpv.TerminateDestroy();
                }
                catch (MpvException)
                {
                }

                try
                {
                    _mpv.Dispose();
                }
                catch (MpvException)
                {
                }
            }
        }
    }

    private void ApplyStartupOptions()
    {
        _mpv.SetOption("config", "no");
        _mpv.SetOption("osc", "no");
        _mpv.SetOption("input-default-bindings", "no");
        _mpv.SetOption("input-vo-keyboard", "no");
        _mpv.SetOption("keep-open", "yes");
        _mpv.SetOption("keep-open-pause", "yes");
        _mpv.SetOption("idle", "yes");
        _mpv.SetOption("osd-level", "0");
        _mpv.SetOption("osd-on-seek", "no");
        _mpv.SetOption("input-cursor", "no");
        _mpv.SetOption("sub-auto", "exact");
        _mpv.SetOption("sub-visibility", "yes");
        if (!_options.Headless)
        {
            _mpv.SetOption("blend-subtitles", "yes");
            _mpv.SetOption("sub-use-margins", "yes");
            _mpv.SetOption("sub-font-size", "55");
            _mpv.SetOption("sub-pos", PlaybackSpec.DefaultSubtitlePosition.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _mpv.SetOption("sub-color", "#FFFFFFFF");
            _mpv.SetOption("sub-border-color", "#FF000000");
            _mpv.SetOption("sub-border-size", "2.5");
            _mpv.SetOption("sub-shadow-offset", "1");
        }
        _mpv.SetOption("ytdl", "no");
        _mpv.SetOption("user-agent", "Mozilla/5.0 GrokPlayer/1.0");
        _mpv.SetOption("network-timeout", "20");
        _mpv.SetOption("cache", "yes");
        _mpv.SetOption("demuxer-max-bytes", "150MiB");
        _mpv.SetOption("demuxer-readahead-secs", "8");
        _mpv.SetOption("cache-pause", "yes");
        _mpv.SetOption("cache-pause-initial", "yes");
        _mpv.SetOption("audio-stream-silence", "yes");
        _mpv.SetOption("screenshot-format", "jpeg");
        _mpv.SetOption("screenshot-jpeg-quality", "60");
        _mpv.SetOption("screenshot-high-bit-depth", "no");

        if (_options.Headless)
        {
            _mpv.SetOption("vo", "null");
            _mpv.SetOption("ao", "null");
            _mpv.SetOption("hwdec", "no");
        }
        else
        {
            _mpv.SetOption("vo", _options.VideoOutput);
            _mpv.SetOption("gpu-api", _options.GpuApi);
            _mpv.SetOption("ao", _options.AudioOutput);
            _mpv.SetOption("hwdec", _options.HardwareDecode ? _options.Hwdec : "no");
            if (_options.HardwareDecode)
            {
                _mpv.SetOption("hwdec-codecs", "h264,hevc,av1,vp9,av01");
            }
        }

        if (_options.WindowHandle != 0)
        {
            _mpv.SetOptionLong("wid", _options.WindowHandle.ToInt64());
        }
    }

    private void InstallVideoFilterPads()
    {
        if (_options.Headless)
        {
            return;
        }

        try
        {
            _mpv.Command("vf", "add", "@deblock:enabled=no:lavfi=[" + VideoFilterGraph.Deblock + "]");
            _mpv.Command(
                "vf",
                "add",
                "@softer:enabled=no:lavfi=[" + VideoFilterGraph.SoftenDenoise + "," + VideoFilterGraph.SoftenBlur + "]");
            _mpv.Command("vf", "add", "@sharpen:enabled=no:lavfi=[" + VideoFilterGraph.Sharpen + "]");
            _filterPads = true;
        }
        catch (MpvException)
        {
            _filterPads = false;
        }
    }

    private void ToggleFilterPad(string label, bool on, ref bool current)
    {
        if (on == current)
        {
            return;
        }

        _mpv.Command("vf", "toggle", label);
        current = on;
    }

    private void ObservePlaybackProperties()
    {
        _mpv.ObserveProperty("time-pos", MpvFormat.Double);
        _mpv.ObserveProperty("duration", MpvFormat.Double);
        _mpv.ObserveProperty("pause", MpvFormat.Flag);
        _mpv.ObserveProperty("volume", MpvFormat.Double);
        _mpv.ObserveProperty("hwdec-current", MpvFormat.String);
        _mpv.ObserveProperty("media-title", MpvFormat.String);
        _mpv.ObserveProperty("mute", MpvFormat.Flag);
        _mpv.ObserveProperty("vid", MpvFormat.String);
        _mpv.ObserveProperty("file-format", MpvFormat.String);
        _mpv.ObserveProperty("eof-reached", MpvFormat.Flag);
        _mpv.ObserveProperty("seekable", MpvFormat.Flag);
        _mpv.ObserveProperty("demuxer-cache-time", MpvFormat.Double);
        _mpv.ObserveProperty("demuxer-cache-duration", MpvFormat.Double);
    }

    private void ApplyInitialVolume()
    {
        _mpv.SetPropertyDouble("volume", Volume);
    }

    private void StartEventLoop()
    {
        _runEvents = true;
        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "libmpv-events"
        };
        _eventThread.Start();
    }

    private void EventLoop()
    {
        while (_runEvents && !_mpv.IsTerminated)
        {
            try
            {
                DrainEvents(0.05);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (MpvException)
            {
                break;
            }
        }
    }

    private void DrainEvents(double timeoutSeconds)
    {
        while (!_disposed && !_mpv.IsTerminated)
        {
            var ev = _mpv.WaitEvent(timeoutSeconds);
            if (ev.Id == MpvEventId.None)
            {
                break;
            }

            HandleEvent(ev);
            timeoutSeconds = 0;
        }
    }

    private void HandleEvent(MpvEvent ev)
    {
        switch (ev.Id)
        {
            case MpvEventId.FileLoaded:
                OnFileLoaded();
                break;
            case MpvEventId.EndFile:
                OnEndFile(ev);
                break;
            case MpvEventId.PropertyChange:
                OnPropertyChange(ev);
                break;
            case MpvEventId.Shutdown:
                break;
        }
    }

    private void OnFileLoaded()
    {
        var duration = ReadDuration();
        var paused = _mpv.GetPropertyFlag("pause") ?? false;
        var title = _mpv.GetPropertyString("media-title");
        var hwdec = _mpv.GetPropertyString("hwdec-current");
        var position = ReadPosition();

        lock (_gate)
        {
            _mediaRevision++;
            Duration = duration;
            if (duration is { } opened && opened > TimeSpan.Zero)
            {
                _durationFromDemuxer = true;
            }

            IsPaused = paused;
            if (!string.IsNullOrWhiteSpace(title) && !KeepsDisplayTitle(MediaTitle))
            {
                MediaTitle = UrlSanitizer.IsUrl(title) || UrlSanitizer.IsUrl(MediaPath)
                    ? UrlSanitizer.DisplayName(MediaPath ?? title)
                    : title;
            }

            HwdecCurrent = hwdec;
            Position = position;
            HasVideo = ReadHasVideo(HasVideo);
            FileFormat = _mpv.GetPropertyString("file-format");
            IsSeekable = _mpv.GetPropertyFlag("seekable") ?? true;
            if (duration is { } loaded && !UrlSanitizer.IsUrl(MediaPath ?? ""))
            {
                CacheEndSeconds = loaded.TotalSeconds;
            }

            BumpLiveEdge_NoLock(duration?.TotalSeconds ?? 0);
            BumpLiveEdge_NoLock(position.TotalSeconds);
            if (LiveWindow &&
                duration is { } ready &&
                ready.TotalSeconds > 2 &&
                IsSeekable)
            {
                var format = FileFormat ?? "";
                if (format.Length > 0 &&
                    !format.Contains("hls", StringComparison.OrdinalIgnoreCase) &&
                    !format.Contains("dash", StringComparison.OrdinalIgnoreCase) &&
                    !format.Contains("mpegts", StringComparison.OrdinalIgnoreCase))
                {
                    LiveWindow = false;
                }
            }

            SetState_NoLock(paused ? PlayerState.Paused : PlayerState.Playing);
        }

        ApplyDesiredAudio();
        SelectLanguageTracks();
        SelectAttachedAudio();
        if (!string.IsNullOrWhiteSpace(_pendingSubFile))
        {
            TrySetProperty("slang", "no");
            SetSubtitleFile(_pendingSubFile);
        }

        if (LiveWindow)
        {
            SeekLive();
        }

        Raise(DurationChanged);
        Raise(TimeChanged);
        Raise(StateChanged);
        Raise(MediaOpened);
    }

    private void OnEndFile(MpvEvent ev)
    {
        if (ev.EndFileReason == MpvEndFileReason.Error)
        {
            var message = $"Playback failed: {MpvException.Describe(ev.EndFileError)}.";
            lock (_gate)
            {
                LastError = message;
                SetState_NoLock(PlayerState.Error);
            }

            Raise(StateChanged);
            RaiseError(message);
            return;
        }

        if (ev.EndFileReason == MpvEndFileReason.Eof)
        {
            if (IsPrematureEof())
            {
                lock (_gate)
                {
                    if (State is PlayerState.Ended or PlayerState.Error)
                    {
                        SetState_NoLock(IsPaused ? PlayerState.Paused : PlayerState.Playing);
                    }
                }

                Raise(StateChanged);
                return;
            }

            lock (_gate)
            {
                if (Duration is { } duration && (_durationFromDemuxer || Position > TimeSpan.FromMilliseconds(250)))
                {
                    Position = duration;
                }

                IsPaused = true;
                SetState_NoLock(PlayerState.Ended);
            }

            Raise(TimeChanged);
            Raise(StateChanged);
            Raise(MediaEnded);
        }
    }

    private void OnPropertyChange(MpvEvent ev)
    {
        switch (ev.PropertyName)
        {
            case "time-pos":
                if (ev.PropertyValue is double seconds && !double.IsNaN(seconds) && seconds >= 0)
                {
                    var catchUp = false;
                    lock (_gate)
                    {
                        if (DateTime.UtcNow < _seekGuardUntil &&
                            Math.Abs(seconds - _seekTarget.TotalSeconds) > 0.4)
                        {
                            break;
                        }

                        Position = TimeSpan.FromSeconds(seconds);
                        BumpLiveEdge_NoLock(seconds);
                        var liveTip = LivePlayback.TipSeconds(
                            Position.TotalSeconds,
                            LiveEdge.TotalSeconds,
                            CacheEndSeconds);
                        // mpv can emit the initial zero position after the live-edge seek.
                        // Ignore only that startup sample; a real DVR position leaves follow mode.
                        if (IsFollowingLive &&
                            Position.TotalSeconds > 0.5 &&
                            !LivePlayback.CanKeepFollowing(Position.TotalSeconds, liveTip))
                        {
                            IsFollowingLive = false;
                        }

                        catchUp = ShouldCatchUpLive_NoLock();
                    }

                    if (_langSelectLeft > 0)
                    {
                        SelectLanguageTracks();
                    }

                    if (catchUp)
                    {
                        CatchUpLive();
                    }

                    Raise(TimeChanged);
                }

                break;
            case "duration":
                if (ev.PropertyValue is double durationSeconds && durationSeconds > 0 && !double.IsNaN(durationSeconds))
                {
                    lock (_gate)
                    {
                        Duration = TimeSpan.FromSeconds(durationSeconds);
                        _durationFromDemuxer = true;
                        BumpLiveEdge_NoLock(durationSeconds);
                    }

                    Raise(DurationChanged);
                }

                break;
            case "pause":
                if (ev.PropertyValue is bool paused)
                {
                    lock (_gate)
                    {
                        IsPaused = paused;
                        if (State is PlayerState.Playing or PlayerState.Paused)
                        {
                            SetState_NoLock(paused ? PlayerState.Paused : PlayerState.Playing);
                        }
                    }

                    Raise(StateChanged);
                }

                break;
            case "volume":
                if (ev.PropertyValue is double volume)
                {
                    lock (_gate)
                    {
                        Volume = PlaybackMath.ClampVolume(volume);
                    }

                    Raise(VolumeChanged);
                }

                break;
            case "hwdec-current":
                if (ev.PropertyValue is string hwdec)
                {
                    lock (_gate)
                    {
                        HwdecCurrent = hwdec;
                    }

                    Raise(StateChanged);
                }

                break;
            case "media-title":
                if (ev.PropertyValue is string title && !string.IsNullOrWhiteSpace(title))
                {
                    lock (_gate)
                    {
                        if (!KeepsDisplayTitle(MediaTitle))
                        {
                            MediaTitle = UrlSanitizer.IsUrl(title) || UrlSanitizer.IsUrl(MediaPath)
                                ? UrlSanitizer.DisplayName(MediaPath ?? title)
                                : title;
                        }
                    }

                    Raise(StateChanged);
                }

                break;
            case "mute":
                if (ev.PropertyValue is bool muted)
                {
                    if (!_wantMuted && muted && Volume > 0)
                    {
                        RestoreOutput();
                        break;
                    }

                    lock (_gate)
                    {
                        IsMuted = muted || _wantMuted;
                    }

                    Raise(VolumeChanged);
                }

                break;
            case "vid":
                lock (_gate)
                {
                    HasVideo = InterpretHasVideo(ev.PropertyValue, HasVideo);
                }

                Raise(StateChanged);
                break;
            case "eof-reached":
                if (ev.PropertyValue is true)
                {
                    OnEndFile(MpvEvent.EndFile(MpvEndFileReason.Eof));
                }

                break;
            case "file-format":
                if (ev.PropertyValue is string format)
                {
                    lock (_gate)
                    {
                        FileFormat = format;
                    }

                    Raise(StateChanged);
                }

                break;
            case "seekable":
                if (ev.PropertyValue is bool seekable)
                {
                    lock (_gate)
                    {
                        IsSeekable = seekable;
                    }

                    Raise(StateChanged);
                }

                break;
            case "demuxer-cache-time":
                if (ev.PropertyValue is double cacheTime)
                {
                    var catchUp = false;
                    lock (_gate)
                    {
                        CacheEndSeconds = Math.Max(CacheEndSeconds, cacheTime);
                        BumpLiveEdge_NoLock(cacheTime);
                        catchUp = ShouldCatchUpLive_NoLock();
                    }

                    if (catchUp)
                    {
                        CatchUpLive();
                    }

                    Raise(TimeChanged);
                }

                break;
            case "demuxer-cache-duration":
                if (ev.PropertyValue is double ahead)
                {
                    lock (_gate)
                    {
                        var end = Position.TotalSeconds + ahead;
                        CacheEndSeconds = Math.Max(CacheEndSeconds, end);
                        BumpLiveEdge_NoLock(end);
                    }

                    Raise(TimeChanged);
                }

                break;
        }
    }

    private bool ReadHasVideo(bool current)
    {
        var asLong = _mpv.GetPropertyLong("vid");
        if (asLong is not null)
        {
            return InterpretHasVideo(asLong.Value, current);
        }

        return InterpretHasVideo(_mpv.GetPropertyString("vid"), current);
    }

    internal static bool InterpretHasVideo(object? value, bool current)
    {
        switch (value)
        {
            case null:
                return current;
            case bool flag:
                return flag;
            case long id:
                return id > 0;
            case int id:
                return id > 0;
            case double id when !double.IsNaN(id):
                return id > 0;
            case string text when string.IsNullOrWhiteSpace(text):
                return current;
            case string text:
                if (text is "no" or "false" or "0")
                {
                    return false;
                }

                if (text is "yes" or "true")
                {
                    return true;
                }

                if (long.TryParse(text, out var parsed))
                {
                    return parsed > 0;
                }

                return true;
            default:
                return current;
        }
    }

    private void ApplyOpenProfile(string path, StreamKind kind, string? userAgent = null, string? referer = null, string? httpHeaders = null)
    {
        var hlsFile = path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) ||
                      path.Contains("master.txt", StringComparison.OrdinalIgnoreCase) ||
                      path.Contains("playlist.txt", StringComparison.OrdinalIgnoreCase);
        var url = UrlSanitizer.IsUrl(path) || hlsFile;
        var live = url && kind switch
        {
            StreamKind.Live => true,
            StreamKind.Vod => false,
            _ => LooksLikeLiveWindow(path)
        };

        ApplyNetworkIdentity(path, userAgent, referer, httpHeaders);
        TrySetProperty("force-seekable", live ? "no" : "yes");
        if (!live &&
            (path.Contains("tiktok", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("byteoversea", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("musical.ly", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("instagram", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("cdninstagram", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("fbcdn.net", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("scontent", StringComparison.OrdinalIgnoreCase)))
        {
            TrySetProperty("demuxer-lavf-analyzeduration", "10");
            TrySetProperty("demuxer-lavf-probesize", "8388608");
        }
        if (live)
        {
            // Start at the live edge (not the oldest DVR segment). Keep a few
            // seconds of forward cache so playback stays smooth without pulling
            // the whole sliding window.
            TrySetProperty("cache-pause-initial", "no");
            TrySetProperty("cache-pause", "yes");
            TrySetProperty("cache-pause-wait", "0.05");
            TrySetProperty("demuxer-readahead-secs", "1.2");
            TrySetProperty("demuxer-max-bytes", "16MiB");
            TrySetProperty("demuxer-max-back-bytes", "24MiB");
            TrySetProperty("demuxer-lavf-o", "live_start_index=-1,allowed_extensions=ALL");
            TrySetProperty("demuxer-lavf-analyzeduration", "0.15");
            TrySetProperty("demuxer-lavf-probesize", "65536");
            TrySetProperty("hls-live-edge", "1");
            TrySetProperty("prefetch-playlist", "yes");
            TrySetProperty("hr-seek", "no");
            TrySetProperty("framedrop", "vo");
            TrySetProperty("video-sync", "audio");
            TrySetProperty("audio-buffer", "0.05");
            TrySetProperty("network-timeout", "8");
            return;
        }

        TrySetProperty("cache-pause-initial", url ? "no" : "yes");
        TrySetProperty("cache-pause", url ? "no" : "yes");
        TrySetProperty("cache-pause-wait", url ? "2" : "1");
        TrySetProperty("demuxer-readahead-secs", url ? "20" : "20");
        TrySetProperty("demuxer-max-bytes", url ? "160MiB" : "150MiB");
        TrySetProperty("demuxer-max-back-bytes", url ? "64MiB" : "75MiB");
        TrySetProperty("demuxer-lavf-o", url ? "allowed_extensions=ALL" : "");
        TrySetProperty("prefetch-playlist", url ? "yes" : "no");
        TrySetProperty("hr-seek", "yes");
        TrySetProperty("hr-seek-framedrop", url ? "no" : "yes");
        TrySetProperty("framedrop", "vo");
        TrySetProperty("video-sync", "audio");
        TrySetProperty("audio-buffer", url ? "0.4" : "0.2");
        TrySetProperty("sub-visibility", "yes");
    }

    private bool IsPrematureEof()
    {
        if (DateTime.UtcNow >= _seekGuardUntil)
        {
            return false;
        }

        TimeSpan? duration;
        lock (_gate)
        {
            duration = Duration;
        }

        return duration is { } length &&
               length > TimeSpan.FromSeconds(2) &&
               _seekTarget + TimeSpan.FromSeconds(1.5) < length;
    }

    private void SelectLanguageTracks()
    {
        if (_langSelectLeft <= 0 || string.IsNullOrWhiteSpace(_audioLang))
        {
            _langSelectLeft = 0;
            return;
        }

        var count = _mpv.GetPropertyLong("track-list/count") ?? 0;
        long? audioId = null;
        for (var i = 0; i < count && i < 80; i++)
        {
            var prefix = "track-list/" + i + "/";
            var type = _mpv.GetPropertyString(prefix + "type") ?? "";
            var lang = _mpv.GetPropertyString(prefix + "lang") ?? "";
            var title = _mpv.GetPropertyString(prefix + "title") ?? "";
            var id = _mpv.GetPropertyLong(prefix + "id");
            if (id is null)
            {
                continue;
            }

            if (type.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
                (MediaLanguage.Matches(_audioLang, lang) || MediaLanguage.MatchesName(_audioLang, title)))
            {
                audioId = id;
            }
        }

        if (audioId is { } aid)
        {
            TrySetProperty("aid", aid.ToString());
            _langSelectLeft = 0;
            return;
        }

        _langSelectLeft--;
    }

    private void SelectAttachedAudio()
    {
        if (string.IsNullOrWhiteSpace(_extraAudio))
        {
            return;
        }

        var count = _mpv.GetPropertyLong("track-list/count") ?? 0;
        for (var i = 0; i < count && i < 80; i++)
        {
            var prefix = "track-list/" + i + "/";
            var type = _mpv.GetPropertyString(prefix + "type") ?? "";
            if (!type.Equals("audio", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var external = _mpv.GetPropertyFlag(prefix + "external") ?? false;
            var src = _mpv.GetPropertyString(prefix + "external-filename") ?? "";
            var id = _mpv.GetPropertyLong(prefix + "id");
            if (id is null || !external)
            {
                continue;
            }

            if (src.Length == 0 ||
                src.Equals(_extraAudio, StringComparison.OrdinalIgnoreCase) ||
                src.Contains("lang%3D" + _audioLang, StringComparison.OrdinalIgnoreCase) ||
                src.Contains("lang=" + _audioLang, StringComparison.OrdinalIgnoreCase))
            {
                TrySetProperty("aid", id.Value.ToString());
                return;
            }
        }
    }

    private void TrySetProperty(string name, string value)
    {
        try
        {
            _mpv.SetPropertyString(name, value);
        }
        catch (MpvException)
        {
        }
    }

    private bool TrySeek(string target, string mode)
    {
        try
        {
            _mpv.Command("seek", target, mode);
            return true;
        }
        catch (MpvException)
        {
            return false;
        }
    }

    private void ArmLiveCatchUp()
    {
        _catchUpLive = true;
        _catchUpSeeks = 0;
        _catchUpUntil = DateTime.UtcNow.AddSeconds(2.5);
    }

    private bool ShouldCatchUpLive_NoLock()
    {
        if (!LiveWindow || (!_catchUpLive && !_followLive))
        {
            return false;
        }

        if (!_followLive && (_catchUpSeeks >= 1 || DateTime.UtcNow > _catchUpUntil))
        {
            _catchUpLive = false;
            return false;
        }

        if (_followLive && DateTime.UtcNow < _catchUpUntil && _catchUpSeeks > 0)
        {
            return false;
        }

        var tip = LivePlayback.TipSeconds(Position.TotalSeconds, LiveEdge.TotalSeconds, CacheEndSeconds);
        return LivePlayback.NeedsCatchUp(Position.TotalSeconds, tip, _followLive ? 0.35 : LivePlayback.CatchUpSlackSeconds);
    }

    private void CatchUpLive()
    {
        double tip;
        lock (_gate)
        {
            if (!ShouldCatchUpLive_NoLock())
            {
                return;
            }

            tip = LivePlayback.SnapTargetSeconds(Position.TotalSeconds, LiveEdge.TotalSeconds, CacheEndSeconds);
            _catchUpSeeks++;
            _catchUpUntil = DateTime.UtcNow.AddSeconds(_followLive ? 0.7 : 1.25);
        }

        TrySeek(tip.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
    }

    private static bool KeepsDisplayTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) &&
        title is not ("videoplayback" or "watch" or "live" or "GrokPlayer") &&
        !UrlSanitizer.IsUrl(title);

    private void ApplyNetworkIdentity(string path, string? userAgent, string? referer = null, string? httpHeaders = null)
    {
        if (LooksLikeYouTubeMedia(path))
        {
            TrySetProperty(
                "user-agent",
                string.IsNullOrWhiteSpace(userAgent)
                    ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
                    : userAgent);
            TrySetProperty("referrer", "https://www.youtube.com");
            TrySetProperty("http-header-fields", "Referer: https://www.youtube.com,Origin: https://www.youtube.com");
            return;
        }

        if (!UrlSanitizer.IsUrl(path))
        {
            TrySetProperty("user-agent", "Mozilla/5.0 GrokPlayer/1.0");
            TrySetProperty("referrer", "");
            TrySetProperty("http-header-fields", "");
            return;
        }

        var page = StreamCatalog.SiteReferer(path, referer);
        var origin = StreamCatalog.PageOrigin(page) ?? StreamCatalog.PageOrigin(path) ?? page;
        TrySetProperty("user-agent", string.IsNullOrWhiteSpace(userAgent) ? StreamCatalog.ChromeUa : userAgent);
        TrySetProperty("referrer", page);
        var headers = "Referer: " + page + ",Origin: " + origin.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(httpHeaders) &&
            !httpHeaders.Contains('\r') && !httpHeaders.Contains('\n'))
        {
            headers += "," + httpHeaders.Trim();
        }
        TrySetProperty("http-header-fields", headers);
    }

    private static bool LooksLikeYouTubeMedia(string path) =>
        path.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeLiveWindow(string path)
    {
        if (!UrlSanitizer.IsUrl(path) || LooksLikeYouTubeMedia(path))
        {
            return false;
        }

        return StreamProbe.ClassifyUrl(path) == StreamKind.Live;
    }

    private TimeSpan? SeekLimit_NoLock()
    {
        var limit = Duration;
        if (LiveWindow && LiveEdge > TimeSpan.Zero && (limit is null || LiveEdge > limit))
        {
            return LiveEdge;
        }

        return limit;
    }

    private void BumpLiveEdge_NoLock(double seconds)
    {
        if (!LiveWindow || seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return;
        }

        var edge = TimeSpan.FromSeconds(seconds);
        if (edge > LiveEdge)
        {
            LiveEdge = edge;
        }
    }

    private TimeSpan? ReadDuration()
    {
        var value = _mpv.GetPropertyDouble("duration");
        if (value is null or <= 0 or double.NaN)
        {
            return null;
        }

        return TimeSpan.FromSeconds(value.Value);
    }

    private TimeSpan ReadPosition()
    {
        var value = _mpv.GetPropertyDouble("time-pos");
        if (value is null or < 0 or double.NaN)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(value.Value);
    }

    private void SetState_NoLock(PlayerState state)
    {
        State = state;
    }

    private void Raise(EventHandler? handler)
    {
        if (handler is null)
        {
            return;
        }

        void Invoke()
        {
            handler(this, EventArgs.Empty);
        }

        if (_sync is not null && SynchronizationContext.Current != _sync)
        {
            _sync.Post(_ => Invoke(), null);
        }
        else
        {
            Invoke();
        }
    }

    private void RaiseError(string message)
    {
        var handler = Error;
        if (handler is null)
        {
            return;
        }

        void Invoke()
        {
            handler(this, new PlayerErrorEventArgs(message));
        }

        if (_sync is not null && SynchronizationContext.Current != _sync)
        {
            _sync.Post(_ => Invoke(), null);
        }
        else
        {
            Invoke();
        }
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
