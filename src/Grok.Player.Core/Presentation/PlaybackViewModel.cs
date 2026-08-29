using System.ComponentModel;
using System.Runtime.CompilerServices;
using Grok.Player.Core.Audio;
using Grok.Player.Core.Download;
using Grok.Player.Core.Launch;
using Grok.Player.Core.Media;
using Grok.Player.Core.Player;
using Grok.Player.Core.Playlist;
using Grok.Player.Core.Subtitles;
using Grok.Player.Core.Video;

namespace Grok.Player.Core.Presentation;

public sealed class PlaybackViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PlayerHost _player;
    private readonly MediaPlaylist _playlist = new();
    private readonly MediaPlaylist _streams = new();
    private readonly ResumeStore _resume;
    private readonly INetworkMonitor _network;
    private readonly bool _ownsNetwork;
    private readonly IStreamInspector? _inspector;
    private readonly SynchronizationContext? _sync;
    private bool _isSeeking;
    private double _seekValue;
    private string? _errorMessage;
    private bool _showRemaining;
    private LoopMode _loop = LoopMode.Off;
    private bool _playlistVisible;
    private double _speed = PlaybackSpec.DefaultSpeed;
    private TimeSpan? _loopA;
    private TimeSpan? _loopB;
    private string _subFont = "Segoe UI";
    private double _subFontSize = 55;
    private int _subPos = PlaybackSpec.DefaultSubtitlePosition;
    private int _subShiftX;
    private string? _styleMedia;
    private bool _streamTab;
    private bool _playingStreams;
    private bool _isLive;
    private string? _resumeApplied;
    private string? _resumeFingerprint;
    private ResumeRecord? _pendingResume;
    private DateTime _lastResumeSave;
    private int _streamRetries;
    private bool _waitForNetwork;
    private bool _streamReady;
    private string? _reconnectPath;
    private string? _contentKey;
    private CancellationTokenSource? _retryCts;
    private int _openSerial;
    private bool _youtubePending;
    private bool _loadHold;
    private string? _audioLang;
    private string? _subLang;
    private double? _pendingStart;
    private YouTubePlayable? _youtube;
    private string? _captionFile;
    private int _captionAppliedSerial;
    private bool _skipStreamCaptions;
    private int _videoHeight;
    private double? _hintDuration;

    public PlaybackViewModel(
        PlayerHost player,
        ResumeStore? resume = null,
        INetworkMonitor? network = null,
        IStreamInspector? inspector = null,
        StreamSubtitleSettings? streamSubtitles = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _resume = resume ?? new ResumeStore();
        _ownsNetwork = network is null;
        _network = network ?? new NetworkMonitor();
        _network.Changed += OnNetworkChanged;
        _inspector = inspector;
        StreamSubtitles = streamSubtitles ?? new StreamSubtitleSettings();
        if (MediaLanguage.IsPlausible(MediaLanguage.Normalize(StreamSubtitles.LastAudio)))
        {
            _audioLang = MediaLanguage.Normalize(StreamSubtitles.LastAudio);
        }

        if (MediaLanguage.IsPlausible(MediaLanguage.Normalize(StreamSubtitles.LastSub)))
        {
            _subLang = MediaLanguage.Normalize(StreamSubtitles.LastSub, keepKind: true);
        }

        _sync = SynchronizationContext.Current;
        _player.StateChanged += OnPlayerChanged;
        _player.TimeChanged += OnTimeChanged;
        _player.DurationChanged += OnPlayerChanged;
        _player.VolumeChanged += OnPlayerChanged;
        _player.Error += OnError;
        _player.MediaOpened += OnPlayerChanged;
        _player.MediaEnded += OnMediaEnded;
        Equalizer = new EqualizerModel();
        Equalizer.Changed += ApplyEqualizer;
        Video = new VideoModel();
        Video.Changed += ApplyVideo;
        Subtitles = new SubtitleModel();
        Subtitles.Changed += OnSubtitlesChanged;
        Scaling = new ScalingQualityModel();
        Scaling.Changed += ApplyScalingLive;
        Resize = new VideoResizeModel();
        RefreshFromPlayer();
    }

    public EqualizerModel Equalizer { get; }

    public VideoModel Video { get; }

    public SubtitleModel Subtitles { get; }

    public StreamSubtitleSettings StreamSubtitles { get; }

    public ScalingQualityModel Scaling { get; }

    public VideoResizeModel Resize { get; }

    public Func<VideoResizeLayout>? LayoutSize { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<string>? Noted;

    public event Action<ResumeRecord>? ResumeOffered;

    internal Func<string, YouTubePlayable?>? ResolveYouTube { get; set; }

    public Action<Action>? PostToUi { get; set; }

    private void OnUi(Action action)
    {
        if (PostToUi is not null)
        {
            PostToUi(action);
            return;
        }

        if (_sync is not null && SynchronizationContext.Current != _sync)
        {
            _sync.Post(_ => action(), null);
            return;
        }

        action();
    }

    private void EnsureStreamLanguages()
    {
        if (!MediaLanguage.IsPlausible(MediaLanguage.Normalize(_audioLang)))
        {
            _audioLang = null;
        }

        if (!MediaLanguage.IsPlausible(MediaLanguage.Normalize(_subLang)))
        {
            _subLang = null;
        }
    }

    public void Note(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Noted?.Invoke(message);
        }
    }

    public string? PreferredAudioLang =>
        string.IsNullOrWhiteSpace(_audioLang) ? null : MediaLanguage.Normalize(_audioLang);

    public string? PreferredSubLang => _subLang;

    public int PreferredVideoHeight
    {
        get
        {
            var item = CurrentItem();
            return item is { VideoHeight: > 0 } ? item.VideoHeight : _videoHeight;
        }
    }

    public string? OnScreenCaption
    {
        get
        {
            if (!Subtitles.Enabled || Subtitles.Applied is null)
            {
                return null;
            }

            var seconds = _isSeeking ? _seekValue : _player.Position.TotalSeconds;
            var cue = Subtitles.Applied.Document.CueAt(
                TimeSpan.FromSeconds(seconds) - TimeSpan.FromSeconds(Subtitles.DelaySeconds));
            return string.IsNullOrWhiteSpace(cue?.Text) ? null : cue.Text;
        }
    }

    public PlayerHost Player => _player;

    public bool IsSeeking => _isSeeking;

    public MediaPlaylist Playlist => _playlist;

    public MediaPlaylist Streams => _streams;

    public MediaPlaylist VisiblePlaylist => _streamTab ? _streams : _playlist;

    public bool StreamTab => _streamTab;

    public bool IsLoading =>
        _loadHold || _youtubePending || _player.State == PlayerState.Opening;

    public bool HoldsTransport => _loadHold || _youtubePending;

    public bool IsLive => _isLive;

    public string? StoryboardSpec
    {
        get
        {
            var item = CurrentItem();
            if (item is not null && !string.IsNullOrWhiteSpace(item.StoryboardSpec))
            {
                return item.StoryboardSpec;
            }

            if (_youtube is null)
            {
                return null;
            }

            if (item is not null &&
                YouTubeCatalog.TryReadVideoId(item.Path, out var id) &&
                !string.Equals(_youtube.VideoId, id, StringComparison.Ordinal))
            {
                return null;
            }

            return _youtube.StoryboardSpec;
        }
    }

    public bool IsAtLive
    {
        get
        {
            if (!_isLive)
            {
                return false;
            }

            if (!_player.IsSeekable)
            {
                return true;
            }

            var tip = LivePlayback.TipSeconds(
                _player.Position.TotalSeconds,
                _player.LiveEdge.TotalSeconds,
                _player.CacheEndSeconds);
            return _player.IsFollowingLive || LivePlayback.IsAtLive(_player.Position.TotalSeconds, tip);
        }
    }

    public bool IsRecording => _player.IsRecording;

    public double CacheEndSeconds => _player.CacheEndSeconds;

    public string PositionText
    {
        get
        {
            if (HoldsTransport)
            {
                return "00:00:00";
            }

            var position = TimeSpan.FromSeconds(_isSeeking ? _seekValue : _player.Position.TotalSeconds);
            if (_showRemaining && !_isLive && _player.Duration is { } duration)
            {
                var left = duration - position;
                return TimeDisplay.FormatClock(left < TimeSpan.Zero ? TimeSpan.Zero : left, remaining: true);
            }

            return TimeDisplay.FormatClock(position);
        }
    }

    public string DurationText =>
        HoldsTransport || _isLive ? (HoldsTransport && !_isLive ? "00:00:00" : string.Empty)
        : TimeDisplay.FormatClock(_player.Duration);

    public string TimePairText
    {
        get
        {
            var position = TimeSpan.FromSeconds(_isSeeking ? _seekValue : _player.Position.TotalSeconds);
            if (_isLive)
            {
                return TimeDisplay.FormatClock(position);
            }

            return TimeDisplay.FormatClockPair(position, _player.Duration, _showRemaining);
        }
    }

    public double SeekOrigin
    {
        get
        {
            if (!_isLive)
            {
                return 0;
            }

            return LivePlayback.WindowStart(LiveTipSeconds);
        }
    }

    public double SeekMaximum
    {
        get
        {
            if (_isLive)
            {
                // The raw manifest/cache tip is not immediately playable. Use the
                // same safe target as Go Live so the slider, hover clock and
                // decoder position share one timeline.
                var playableTip = LivePlayback.SnapTargetSeconds(
                    _player.Position.TotalSeconds,
                    _player.LiveEdge.TotalSeconds,
                    _player.CacheEndSeconds);
                return Math.Max(playableTip, SeekOrigin + 1);
            }

            var window = SeekWindow;
            return window is { } length && length > TimeSpan.Zero
                ? Math.Max(length.TotalSeconds, 1)
                : 1;
        }
    }

    private double LiveTipSeconds =>
        LivePlayback.TipSeconds(
            _player.Position.TotalSeconds,
            _player.LiveEdge.TotalSeconds,
            _player.CacheEndSeconds);

    public double SeekValue
    {
        get => _seekValue;
        set
        {
            if (SetField(ref _seekValue, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(PositionText));
            }
        }
    }

    public bool CanSeek => _player.CanSeek;

    public bool CanStop => _player.CanStop;

    public bool CanTogglePlayback => _player.HasMedia && _player.State != PlayerState.Opening;

    public bool IsPlaying => _player.State == PlayerState.Playing;

    public bool HasMedia => _player.HasMedia;

    public bool ShowEmptyState => !_player.HasMedia && _player.State != PlayerState.Opening;

    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768";

    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    public double Volume
    {
        get => _player.Volume;
        set
        {
            if (Math.Abs(_player.Volume - value) < 0.01)
            {
                return;
            }

            _player.SetVolume(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeGlyph));
        }
    }

    public bool IsMuted => _player.IsMuted || Volume <= 0;

    public string VolumeGlyph => IsMuted || Volume <= 0 ? "\uE74F" : Volume < 50 ? "\uE993" : "\uE767";

    public string Title => string.IsNullOrWhiteSpace(_player.MediaTitle) ? "GrokPlayer" : _player.MediaTitle;

    public string TitleFormat
    {
        get
        {
            var path = _reconnectPath ?? CurrentItem()?.Path ?? _player.MediaPath ?? _playlist.CurrentPath;
            var format = MediaFiles.FormatLabel(path);
            if (string.IsNullOrWhiteSpace(format))
            {
                format = SanitizeFileFormat(_player.FileFormat);
            }

            return format;
        }
    }

    private static string SanitizeFileFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Contains(',') ||
            format.Contains("3gp", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return format;
    }

    public string TitleIndex
    {
        get
        {
            var list = PlayingList;
            return list.Count > 1 && list.CurrentIndex >= 0
                ? $"[{list.CurrentIndex + 1}/{list.Count}]"
                : "";
        }
    }

    public string TitleName
    {
        get
        {
            var item = CurrentItem();
            if (item is { Kind: PlaylistKind.Stream } &&
                !string.IsNullOrWhiteSpace(item.Title) &&
                item.Title is not ("videoplayback" or "watch" or "live"))
            {
                return item.Title;
            }

            if (!string.IsNullOrWhiteSpace(_player.MediaTitle) &&
                _player.MediaTitle is not "GrokPlayer" &&
                !UrlSanitizer.IsUrl(_player.MediaTitle) &&
                _player.MediaTitle is not ("videoplayback" or "watch") &&
                _player.MediaTitle.IndexOf("3gp", StringComparison.OrdinalIgnoreCase) < 0 &&
                !_player.MediaTitle.Contains(','))
            {
                return _player.MediaTitle;
            }

            var path = _player.MediaPath ?? PlayingList.CurrentPath;
            return path is null ? "" : MediaFiles.DisplayName(path);
        }
    }

    public string TitleLine
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TitleName))
            {
                return "GrokPlayer";
            }

            return string.Join(" ", new[] { TitleFormat, TitleIndex, TitleName }.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    public LoopMode Loop
    {
        get => _loop;
        private set
        {
            _loop = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LoopGlyph));
            OnPropertyChanged(nameof(LoopLabel));
            OnPropertyChanged(nameof(LoopIsActive));
        }
    }

    public string LoopGlyph => Loop == LoopMode.One ? "\uE8ED" : "\uE8EE";

    public bool LoopIsActive => Loop != LoopMode.Off;

    public string LoopLabel => Loop switch
    {
        LoopMode.One => "Loop file",
        LoopMode.Playlist => "Loop playlist",
        _ => "No loop"
    };

    public bool PlaylistVisible
    {
        get => _playlistVisible;
        set
        {
            _playlistVisible = value;
            OnPropertyChanged();
        }
    }

    public bool ShowRemaining => _showRemaining;

    public double Speed => _speed;

    public TimeSpan? LoopA => _loopA;

    public TimeSpan? LoopB => _loopB;

    public string SubFont => _subFont;

    public double SubFontSize => _subFontSize;

    public int SubPos => _subPos;

    public int SubShiftX => _subShiftX;

    public string StatusText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_errorMessage))
            {
                return _errorMessage;
            }

            return _player.State switch
            {
                PlayerState.Idle => "Open a video to begin",
                PlayerState.Opening => "Opening…",
                PlayerState.Playing => HwdecLabel,
                PlayerState.Paused => "Paused",
                PlayerState.Stopped => "Stopped",
                PlayerState.Ended => "Ended",
                PlayerState.Error => _player.LastError ?? "Playback error",
                _ => string.Empty
            };
        }
    }

    public string HwdecLabel
    {
        get
        {
            var hwdec = _player.HwdecCurrent;
            if (string.IsNullOrWhiteSpace(hwdec) || hwdec is "no" or "none")
            {
                return "Software decode";
            }

            return hwdec.Contains("d3d11", StringComparison.OrdinalIgnoreCase) ||
                   hwdec.Contains("nvdec", StringComparison.OrdinalIgnoreCase)
                ? $"NVIDIA · {hwdec}"
                : hwdec;
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage) || _player.State == PlayerState.Error;

    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        if (MediaFiles.IsSubtitle(trimmed) && File.Exists(trimmed))
        {
            Subtitles.IngestDropped(trimmed, _playlist.Items.Select(item => item.Path));
            BindSubtitles();
            RefreshFromPlayer();
            return;
        }

        if (UrlSanitizer.IsUrl(trimmed) || YouTubeCatalog.IsWatchUrl(trimmed))
        {
            AddStream(trimmed, play: true);
            return;
        }

        if (!MediaFiles.IsSupported(trimmed) || !File.Exists(trimmed))
        {
            return;
        }

        _playlist.TryAdd(trimmed);
        OpenCurrent(trimmed);
        BindSubtitles();
        RefreshFromPlayer();
    }

    public void AcceptPaths(IEnumerable<string> paths)
    {
        var incoming = paths.ToArray();
        var supported = DropPolicy.FilterSupported(incoming)
            .Where(path => UrlSanitizer.IsUrl(path) || !PlaybackMath.LooksLikeLocalPath(path) || File.Exists(path))
            .ToArray();
        var locals = supported.Where(path => !UrlSanitizer.IsUrl(path)).ToArray();
        var streams = supported.Where(UrlSanitizer.IsUrl).ToArray();
        var subs = incoming.Where(MediaFiles.IsSubtitle).Where(File.Exists).ToArray();
        if (supported.Length == 0 && subs.Length == 0)
        {
            return;
        }

        _errorMessage = null;
        var action = DropPolicy.ForState(_player.State);
        if (locals.Length > 0)
        {
            _playlist.AddMany(locals);
        }

        if (streams.Length > 0)
        {
            _streams.AddMany(streams);
        }

        var play = action == DropAction.PlayFirstEnqueueRest ? supported[0] : null;
        if (play is not null)
        {
            OpenCurrent(play);
        }

        foreach (var sub in subs)
        {
            Subtitles.IngestDropped(sub, _playlist.Items.Select(item => item.Path));
        }

        BindSubtitles();
        RefreshFromPlayer();
    }

    public void PlayIndex(int index) => PlayFrom(VisiblePlaylist, index);

    public void ShowStreamTab(bool stream)
    {
        if (_streamTab == stream)
        {
            return;
        }

        _streamTab = stream;
        OnPropertyChanged(nameof(StreamTab));
        OnPropertyChanged(nameof(VisiblePlaylist));
        RefreshFromPlayer();
    }

    public bool AddStream(string url, bool play) => AddStream(url, play, null);

    public bool AddStream(string url, bool play, string? title, string? audioLang = null, string? subLang = null, int height = 0)
    {
        var trimmed = url.Trim();
        var protocol = trimmed.StartsWith("grokplayer:", StringComparison.OrdinalIgnoreCase);
        string? captionUrl = null;
        string? referer = null;
        string? soundtrack = null;
        var protocolKind = StreamKind.Unknown;
        if (ExternalOpen.TryParse(trimmed, out var external))
        {
            trimmed = external.Url;
            title ??= external.Title;
            if (external.Kind != StreamKind.Unknown)
            {
                protocolKind = external.Kind;
                _isLive = external.Kind == StreamKind.Live;
            }
            else if (external.DurationSeconds is > 0)
            {
                protocolKind = StreamKind.Vod;
                _isLive = false;
            }

            audioLang ??= external.AudioLang;
            subLang ??= external.SubLang;
            if (height <= 0)
            {
                height = external.Height;
            }

            captionUrl ??= external.CaptionUrl;
            referer ??= external.Referer;
            soundtrack = external.Soundtrack;
            if (external.DurationSeconds is > 0)
            {
                _hintDuration = external.DurationSeconds;
            }
        }

        if (height > 0)
        {
            _videoHeight = HlsPlaylist.NormalizeHeight(height);
        }

        if (protocol)
        {
            _audioLang = MediaLanguage.IsPlausible(MediaLanguage.Normalize(audioLang))
                ? MediaLanguage.Normalize(audioLang)
                : null;
            if (protocolKind == StreamKind.Live || MediaLanguage.IsOff(subLang))
            {
                _skipStreamCaptions = true;
                _subLang = null;
                ClearStreamCaptions();
            }
            else
            {
                _skipStreamCaptions = false;
                if (MediaLanguage.IsPlausible(MediaLanguage.Normalize(subLang)))
                {
                    _subLang = MediaLanguage.Normalize(subLang, keepKind: true);
                }
                else if (!string.IsNullOrWhiteSpace(captionUrl))
                {
                    var fromUrl = MediaLanguage.Normalize(
                        YouTubeCatalog.CaptionLanguageFromUrl(captionUrl),
                        keepKind: true);
                    if (fromUrl.Length > 0)
                    {
                        _subLang = fromUrl;
                    }
                }
            }

            StreamSubtitles.LastAudio = _audioLang;
            StreamSubtitles.LastSub = _subLang;
        }
        else if (MediaLanguage.IsPlausible(MediaLanguage.Normalize(audioLang)))
        {
            _audioLang = MediaLanguage.Normalize(audioLang);
            StreamSubtitles.LastAudio = _audioLang;
        }

        if (!protocol && MediaLanguage.IsOff(subLang))
        {
            _skipStreamCaptions = true;
            _subLang = null;
            ClearStreamCaptions();
        }
        else if (!protocol && MediaLanguage.IsPlausible(MediaLanguage.Normalize(subLang)))
        {
            _skipStreamCaptions = false;
            _subLang = MediaLanguage.Normalize(subLang, keepKind: true);
            StreamSubtitles.LastSub = _subLang;
        }

        if (StreamSubtitles.Store is not null &&
            (!string.IsNullOrWhiteSpace(audioLang) || !string.IsNullOrWhiteSpace(subLang)))
        {
            StreamSubtitles.Save();
        }

        EnsureStreamLanguages();

        _pendingStart = YouTubeCatalog.ReadStartSeconds(trimmed);
        if (YouTubeCatalog.TryReadVideoId(trimmed, out var videoId))
        {
            trimmed = "https://www.youtube.com/watch?v=" + videoId;
        }

        if (!UrlSanitizer.IsUrl(trimmed) && !YouTubeCatalog.IsWatchUrl(trimmed))
        {
            return false;
        }

        var added = _streams.TryAdd(trimmed, title);
        var item = _streams.Items.FirstOrDefault(entry =>
            MediaPlaylist.Identity(entry.Path) == MediaPlaylist.Identity(trimmed));
        if (!string.IsNullOrWhiteSpace(title))
        {
            item?.SetTitle(title);
        }

        if (item is not null)
        {
            if (protocolKind != StreamKind.Unknown)
            {
                item.StreamKind = protocolKind;
            }

            if (!string.IsNullOrWhiteSpace(referer))
            {
                item.Referer = referer;
            }

            if (!string.IsNullOrWhiteSpace(soundtrack))
            {
                item.AudioUrl = soundtrack;
            }

            if (height > 0)
            {
                item.VideoHeight = HlsPlaylist.NormalizeHeight(height);
            }

            if (protocol || !string.IsNullOrWhiteSpace(audioLang) || !string.IsNullOrWhiteSpace(subLang) ||
                !string.IsNullOrWhiteSpace(captionUrl))
            {
                var previousSub = item.SubLang;
                item.AudioLang = _audioLang;
                item.SubLang = _subLang;
                item.SkipCaptions = _skipStreamCaptions;
                if (!string.IsNullOrWhiteSpace(captionUrl))
                {
                    item.CaptionUrl = captionUrl;
                }
                else if (protocol && !SameCachedLang(previousSub, _subLang))
                {
                    item.CaptionUrl = null;
                }
            }
        }

        _streamTab = true;
        OnPropertyChanged(nameof(StreamTab));
        OnPropertyChanged(nameof(VisiblePlaylist));
        if (play || !_player.HasMedia)
        {
            OpenCurrent(trimmed);
        }

        RefreshFromPlayer();
        Note(added
            ? ActionFeedback.StreamAdded(title ?? UrlSanitizer.DisplayName(trimmed))
            : ActionFeedback.StreamAdded(title ?? UrlSanitizer.DisplayName(trimmed)));
        return true;
    }

    public void PlayFrom(MediaPlaylist list, int index)
    {
        if (index < 0 || index >= list.Count)
        {
            return;
        }

        list.SetCurrent(list.Items[index].Path);
        if (list.CurrentPath is { } path)
        {
            OpenCurrent(path);
        }

        RefreshFromPlayer();
    }

    public void GoLive()
    {
        if (!_isLive || !_player.HasMedia)
        {
            return;
        }

        try
        {
            _player.SeekLive();
        }
        catch (Grok.Player.Core.Native.MpvException)
        {
            RefreshFromPlayer();
            return;
        }

        RefreshFromPlayer();
        Note(ActionFeedback.GoLive());
    }

    public void StartRecording(string path)
    {
        _player.SetRecording(path);
        OnPropertyChanged(nameof(IsRecording));
        Note(ActionFeedback.RecordingStarted());
    }

    public void StopRecording()
    {
        if (!_player.IsRecording)
        {
            return;
        }

        _player.SetRecording(null);
        OnPropertyChanged(nameof(IsRecording));
        Note(ActionFeedback.RecordingStopped());
    }

    public void TogglePlayPause()
    {
        if (!CanTogglePlayback)
        {
            return;
        }

        _player.TogglePause();
        RefreshFromPlayer();
    }

    public void Stop()
    {
        PersistResume();
        CancelRetry();
        _player.Stop();
        RefreshFromPlayer();
    }

    public void SeekBy(TimeSpan delta)
    {
        if (!_player.HasMedia)
        {
            return;
        }

        ApplySeek(PositionNow().TotalSeconds + delta.TotalSeconds);
    }

    public double ClampSeek(double seconds)
    {
        if (_isLive)
        {
            return Math.Clamp(seconds, SeekOrigin, SeekMaximum);
        }

        return PlaybackMath.ClampSeek(seconds, SeekWindow, _loopA, _loopB);
    }

    private TimeSpan? SeekWindow
    {
        get
        {
            if (_isLive && _player.LiveEdge > TimeSpan.Zero)
            {
                var edge = _player.LiveEdge;
                if (_player.Duration is { } duration && duration > edge)
                {
                    return duration;
                }

                return edge;
            }

            return _player.Duration;
        }
    }

    public void NudgeSpeed(double delta)
    {
        SetSpeed(_speed + delta);
    }

    public void SetSpeed(double value)
    {
        var clamped = PlaybackSpec.ClampSpeed(value);
        if (Math.Abs(_speed - clamped) < 0.001)
        {
            return;
        }

        _speed = clamped;
        _player.SetSpeed(_speed);
        OnPropertyChanged(nameof(Speed));
    }

    public bool MarkLoopA()
    {
        if (!_player.HasMedia)
        {
            return false;
        }

        var now = PositionNow();
        if (_loopB is { } b && now > b)
        {
            return false;
        }

        _loopA = now;
        ApplyAbLoop();
        OnPropertyChanged(nameof(LoopA));
        return true;
    }

    public bool MarkLoopB()
    {
        if (!_player.HasMedia)
        {
            return false;
        }

        var now = PositionNow();
        if (_loopA is { } a && now < a)
        {
            return false;
        }

        _loopB = now;
        ApplyAbLoop();
        OnPropertyChanged(nameof(LoopB));
        return true;
    }

    public void ClearLoopPoints()
    {
        if (_loopA is null && _loopB is null)
        {
            return;
        }

        _loopA = null;
        _loopB = null;
        ApplyAbLoop();
        OnPropertyChanged(nameof(LoopA));
        OnPropertyChanged(nameof(LoopB));
    }

    public void SetSubFont(string name)
    {
        var font = string.IsNullOrWhiteSpace(name) ? "Segoe UI" : name.Trim();
        if (string.Equals(_subFont, font, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _subFont = font;
        _player.SetSubFont(_subFont);
        OnPropertyChanged(nameof(SubFont));
    }

    public void SetSubFontSize(double size)
    {
        var clamped = Math.Clamp(Math.Round(size), 8, 200);
        if (Math.Abs(_subFontSize - clamped) < 0.01)
        {
            return;
        }

        _subFontSize = clamped;
        _player.SetSubFontSize(_subFontSize);
        OnPropertyChanged(nameof(SubFontSize));
    }

    public void NudgeSubPos(int delta)
    {
        var next = Math.Clamp(_subPos + delta, 0, 100);
        if (next == _subPos)
        {
            return;
        }

        _subPos = next;
        _player.SetSubPos(_subPos);
        OnPropertyChanged(nameof(SubPos));
    }

    public void NudgeSubShiftX(int delta)
    {
        var next = Math.Clamp(_subShiftX + delta, -20, 20);
        if (next == _subShiftX)
        {
            return;
        }

        _subShiftX = next;
        _player.SetSubShiftX(_subShiftX);
        OnPropertyChanged(nameof(SubShiftX));
    }

    private TimeSpan PositionNow() =>
        TimeSpan.FromSeconds(_isSeeking ? _seekValue : _player.Position.TotalSeconds);

    private void ApplyAbLoop()
    {
        _player.SetAbLoop(
            _loopA?.TotalSeconds,
            _loopB?.TotalSeconds);
    }

    public void ToggleMute()
    {
        _player.ToggleMute();
        RefreshFromPlayer();
    }

    public void ToggleTimeMode()
    {
        if (_isLive)
        {
            return;
        }

        _showRemaining = !_showRemaining;
        OnPropertyChanged(nameof(ShowRemaining));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(TimePairText));
    }

    public void CycleLoop()
    {
        SetLoop(Loop switch
        {
            LoopMode.Off => LoopMode.Playlist,
            LoopMode.Playlist => LoopMode.One,
            _ => LoopMode.Off
        });
    }

    public void SetLoop(LoopMode mode)
    {
        Loop = mode;
        _player.SetFileLoop(mode == LoopMode.One);
    }

    public void ApplySeek(double seconds)
    {
        if (!_player.CanSeek)
        {
            return;
        }

        SeekValue = ClampSeek(seconds);
        if (_isLive && SeekValue >= SeekMaximum - 0.75)
        {
            _player.SeekLive();
        }
        else
        {
            _player.Seek(TimeSpan.FromSeconds(SeekValue));
        }
    }

    public void TogglePlaylist() => PlaylistVisible = !PlaylistVisible;

    public void BeginSeek()
    {
        if (_isSeeking)
        {
            return;
        }

        _isSeeking = true;
        OnPropertyChanged(nameof(IsSeeking));
    }

    public void UpdateSeekPreview(double seconds)
    {
        if (!_isSeeking)
        {
            return;
        }

        SeekValue = ClampSeek(seconds);
        OnPropertyChanged(nameof(PositionText));
    }

    public void EndSeek(double seconds)
    {
        var wasSeeking = _isSeeking;
        _isSeeking = false;
        if (wasSeeking)
        {
            OnPropertyChanged(nameof(IsSeeking));
        }

        if (!_player.CanSeek)
        {
            RefreshFromPlayer();
            return;
        }

        var target = ClampSeek(seconds);
        if (_isLive && target >= SeekMaximum - 0.75)
        {
            _player.SeekLive();
        }
        else
        {
            _player.Seek(TimeSpan.FromSeconds(target));
        }

        RefreshFromPlayer();
    }

    public void CancelSeek()
    {
        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        OnPropertyChanged(nameof(IsSeeking));
        RefreshFromPlayer();
    }

    public void Dispose()
    {
        _player.StateChanged -= OnPlayerChanged;
        _player.TimeChanged -= OnTimeChanged;
        _player.DurationChanged -= OnPlayerChanged;
        _player.VolumeChanged -= OnPlayerChanged;
        _player.Error -= OnError;
        _player.MediaOpened -= OnPlayerChanged;
        _player.MediaEnded -= OnMediaEnded;
        Equalizer.Changed -= ApplyEqualizer;
        Video.Changed -= ApplyVideo;
        Subtitles.Changed -= OnSubtitlesChanged;
        Scaling.Changed -= ApplyScalingLive;
        PersistResume();
        CancelRetry();
        _network.Changed -= OnNetworkChanged;
        if (_ownsNetwork && _network is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public void ApplyEqualizer() => _player.SetEqualizer(Equalizer.Enabled, Equalizer.Bands);

    public void ApplyVideo()
    {
        _player.SetVideoPicture(Video.Brightness, Video.Contrast, Video.Saturation, Video.Hue);
        _player.SetVideoFilters(Video.Softer, Video.Sharpen, Video.Deblock);
    }

    public void ApplyScalingLive() => _player.SetScalingQuality(Scaling.Live);

    public VideoResizeContext CurrentResizeContext()
    {
        var source = _player.GetVideoPixelSize();
        var layout = LayoutSize?.Invoke() ?? VideoResizeLayout.Empty;
        return new VideoResizeContext(source.W, source.H, layout);
    }

    public void ApplyResizeLive()
    {
        try
        {
            _player.SetVideoResize(Resize.Live, CurrentResizeContext());
        }
        catch (Grok.Player.Core.Native.MpvException)
        {
        }
    }

    public void RefreshPushedVideo()
    {
        if (Scaling.HasBeenPushed)
        {
            ApplyScalingLive();
        }

        if (Resize.HasBeenPushed)
        {
            ApplyResizeLive();
        }
    }

    public void RefreshPushedResize()
    {
        if (Resize.HasBeenPushed)
        {
            ApplyResizeLive();
        }
    }

    public bool NudgeImageWidth(int direction)
    {
        if (!Resize.NudgeHorizontal(direction))
        {
            return false;
        }

        ApplyResizeLive();
        Note(ActionFeedback.ImageWidth(Resize.Live.AdjustX));
        return true;
    }

    public bool NudgeImageHeight(int direction)
    {
        if (!Resize.NudgeVertical(direction))
        {
            return false;
        }

        ApplyResizeLive();
        Note(ActionFeedback.ImageHeight(Resize.Live.AdjustY));
        return true;
    }

    public bool ResetImageAdjust()
    {
        if (!Resize.ResetAdjust())
        {
            return false;
        }

        ApplyResizeLive();
        Note(ActionFeedback.ImageReset());
        return true;
    }

    public void CaptureFrame(string path) => _player.CaptureFrame(path);

    public void ApplySubtitleTrack()
    {
        if (_player.State is PlayerState.Opening)
        {
            return;
        }

        if (_skipStreamCaptions && _playingStreams)
        {
            _player.SetSubtitleFile(null);
            return;
        }

        var track = Subtitles.Applied;
        if (track is not null)
        {
            if (TrackFitsCurrent(track) || Subtitles.CurrentMedia is null)
            {
                var serial = Volatile.Read(ref _openSerial);
                if (_captionAppliedSerial == serial &&
                    string.Equals(_captionFile, track.PlayPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (_player.SetSubtitleFile(track.PlayPath))
                {
                    _captionFile = track.PlayPath;
                    _captionAppliedSerial = serial;
                }
            }
            else
            {
                _player.SetSubtitleFile(null);
                _captionAppliedSerial = 0;
            }

            _player.SetSubDelay(Subtitles.DelaySeconds);
            return;
        }

        _player.SetSubtitleFile(null);
        _player.SetSubDelay(Subtitles.DelaySeconds);
    }

    private bool TrackFitsCurrent(SubtitleTrack track)
    {
        var media = CurrentSubtitleMedia();
        return media is not null && SubtitleModel.BelongsTo(track, media);
    }

    private string? CurrentSubtitleMedia()
    {
        if (!string.IsNullOrWhiteSpace(_contentKey))
        {
            return _contentKey;
        }

        return _player.MediaPath ?? PlayingList.CurrentPath;
    }

    public void SetStreamSubtitleMode(StreamSubtitleMode mode)
    {
        if (StreamSubtitles.Mode == mode)
        {
            return;
        }

        StreamSubtitles.Mode = mode;
        StreamSubtitles.Save();
        if (mode == StreamSubtitleMode.Off)
        {
            _captionFile = null;
            _player.SetSubtitleFile(null);
        }
        else if (_youtube is not null)
        {
            StartCaptionLoad(_youtube);
        }

        OnPropertyChanged(nameof(StreamSubtitles));
        Note(mode == StreamSubtitleMode.Off ? "Stream subtitles off" : "Stream subtitles on");
    }

    public void ApplySubtitleDelay() => _player.SetSubDelay(Subtitles.DelaySeconds);

    private void OnSubtitlesChanged(SubtitleNotify notify)
    {
        if (notify is SubtitleNotify.Track)
        {
            ApplySubtitleTrack();
            return;
        }

        if (notify is SubtitleNotify.Delay)
        {
            ApplySubtitleDelay();
        }
    }

    private void SilenceCurrent()
    {
        try
        {
            if (_player.HasMedia && _player.State == PlayerState.Playing)
            {
                _player.Pause();
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        if (_youtubePending || _loadHold)
        {
            return;
        }

        if (_isLive)
        {
            ScheduleReconnect();
            RefreshFromPlayer();
            return;
        }

        if (_loop == LoopMode.One)
        {
            _player.Seek(TimeSpan.Zero);
            _player.Play();
            RefreshFromPlayer();
            return;
        }

        var next = PlayingList.Next(_loop);
        if (next is not null)
        {
            OpenCurrent(next);
        }

        RefreshFromPlayer();
    }

    private void OnError(object? sender, PlayerErrorEventArgs e)
    {
        _errorMessage = UrlSanitizer.Redact(e.Message);
        ScheduleReconnect();
        RefreshFromPlayer();
    }

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        if (_isSeeking)
        {
            return;
        }

        MaybeSaveResume();
        ShowPendingCaption();
        RefreshFromPlayer();
    }

    private void ApplyHintDuration()
    {
        if (_hintDuration is not > 0)
        {
            return;
        }

        if (_player.State is not PlayerState.Playing and not PlayerState.Paused)
        {
            return;
        }

        if (_player.Duration is { } known && known > TimeSpan.Zero)
        {
            return;
        }

        _player.HintDuration(TimeSpan.FromSeconds(_hintDuration.Value));
    }

    private void OnPlayerChanged(object? sender, EventArgs e)
    {
        if (_player.State is PlayerState.Playing or PlayerState.Paused)
        {
            _streamReady = true;
            SetLoadHold(false);
            if (_pendingStart is { } start)
            {
                _pendingStart = null;
                _player.Seek(TimeSpan.FromSeconds(start));
            }
        }

        if (_player.State is PlayerState.Error or PlayerState.Idle or PlayerState.Stopped)
        {
            SetLoadHold(false);
        }

        ClassifyLive();
        ApplyHintDuration();
        TryResume();
        BindSubtitles();
        if (_player.State is PlayerState.Playing or PlayerState.Paused)
        {
            ShowPendingCaption();
        }

        RefreshFromPlayer();
    }

    private void BindSubtitles()
    {
        var path = _player.MediaPath ?? PlayingList.CurrentPath;
        if (!string.Equals(path, _styleMedia, StringComparison.OrdinalIgnoreCase))
        {
            _styleMedia = path;
            if (_loopA is not null || _loopB is not null)
            {
                ClearLoopPoints();
            }
        }

        if (path is null)
        {
            return;
        }

        if (UrlSanitizer.IsUrl(path))
        {
            if (!string.IsNullOrWhiteSpace(_contentKey) && _player.State != PlayerState.Opening)
            {
                Subtitles.BindForMedia(_contentKey);
                ApplySubtitleTrack();
            }

            return;
        }

        Subtitles.DiscoverSidecar(path);
        if (_player.State != PlayerState.Opening)
        {
            Subtitles.BindForMedia(path);
        }
    }

    private void RefreshFromPlayer()
    {
        if (!_isSeeking)
        {
            _seekValue = ClampSeek(_player.Position.TotalSeconds);
            OnPropertyChanged(nameof(SeekValue));
            OnPropertyChanged(nameof(PositionText));
        }

        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(TimePairText));
        OnPropertyChanged(nameof(SeekOrigin));
        OnPropertyChanged(nameof(SeekMaximum));
        OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanTogglePlayback));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(PlayPauseGlyph));
        OnPropertyChanged(nameof(PlayPauseLabel));
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(VolumeGlyph));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TitleLine));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HwdecLabel));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsSeeking));
        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(TimePairText));
        OnPropertyChanged(nameof(Loop));
        OnPropertyChanged(nameof(LoopGlyph));
        OnPropertyChanged(nameof(LoopLabel));
        OnPropertyChanged(nameof(LoopIsActive));
        OnPropertyChanged(nameof(PlaylistVisible));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsAtLive));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(StreamTab));
        OnPropertyChanged(nameof(VisiblePlaylist));
        OnPropertyChanged(nameof(CacheEndSeconds));
        OnPropertyChanged(nameof(OnScreenCaption));
    }

    private MediaPlaylist PlayingList => _playingStreams ? _streams : _playlist;

    private PlaylistItem? CurrentItem()
    {
        var list = PlayingList;
        if (list.CurrentIndex >= 0 && list.CurrentIndex < list.Count)
        {
            return list.Items[list.CurrentIndex];
        }

        var path = _reconnectPath ?? _player.MediaPath;
        if (path is null)
        {
            return null;
        }

        return list.Items.FirstOrDefault(entry =>
            MediaPlaylist.Identity(entry.Path) == MediaPlaylist.Identity(path));
    }

    private void ApplyItemLanguages(PlaylistItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.AudioLang))
        {
            _audioLang = MediaLanguage.Normalize(item.AudioLang);
        }

        if (item.SkipCaptions)
        {
            _skipStreamCaptions = true;
            _subLang = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.SubLang) || !string.IsNullOrWhiteSpace(item.CaptionUrl))
        {
            _skipStreamCaptions = false;
            if (!string.IsNullOrWhiteSpace(item.SubLang))
            {
                _subLang = MediaLanguage.Normalize(item.SubLang, keepKind: true);
            }
        }
    }

    private void OpenCurrent(string path, bool resetRetries = true)
    {
        PersistResume();
        CancelRetry();
        SilenceCurrent();
        SetLoadHold(true);
        _seekValue = 0;
        OnPropertyChanged(nameof(HoldsTransport));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(SeekValue));
        OnPropertyChanged(nameof(TimePairText));
        var serial = Interlocked.Increment(ref _openSerial);
        _resumeApplied = null;
        _resumeFingerprint = null;
        _contentKey = null;
        ResetSubtitlePlaybackForMediaSwitch();
        if (resetRetries)
        {
            _streamRetries = 0;
        }

        if (_isSeeking)
        {
            _isSeeking = false;
            OnPropertyChanged(nameof(IsSeeking));
        }

        _waitForNetwork = false;
        _streamReady = false;
        SetYouTubePending(false);
        _reconnectPath = path;
        _playingStreams = UrlSanitizer.IsUrl(path) || YouTubeCatalog.IsWatchUrl(path);
        if (_playingStreams)
        {
            _streams.SetCurrent(path);
            _streamTab = true;
            var item = _streams.Items.FirstOrDefault(entry =>
                MediaPlaylist.Identity(entry.Path) == MediaPlaylist.Identity(path));
            ApplyItemLanguages(item);
            if (YouTubeCatalog.IsWatchUrl(path))
            {
                if (_youtube is not null &&
                    YouTubeCatalog.TryReadVideoId(path, out var nextId))
                {
                    var same = string.Equals(_youtube.VideoId, nextId, StringComparison.Ordinal);
                    var subChanged = !string.IsNullOrWhiteSpace(item?.SubLang) &&
                                     !SameCachedLang(item.CachedSubLang ?? item.SubLang, _subLang);
                    if (!same || subChanged)
                    {
                        if (!same)
                        {
                            _youtube = null;
                            OnPropertyChanged(nameof(StoryboardSpec));
                        }

                        ClearStreamCaptions();
                    }
                }

                SetYouTubePending(true);
                OpenYouTube(path, item, serial);
                return;
            }

            if (StreamCatalog.LooksResolvable(path))
            {
                SetYouTubePending(true);
                OpenCatalog(path, item, serial);
                return;
            }

            var ext = StreamProbe.Extension(path);
            if (_inspector is not null)
            {
                QueueInspect(path, item);
            }

            _isLive = item?.StreamKind == StreamKind.Live;
            var openKind = item?.StreamKind == StreamKind.Vod
                ? StreamKind.Vod
                : _isLive ? StreamKind.Live : StreamKind.Unknown;
            LoadPath(path, openKind, item?.AudioUrl, item?.Title, referer: item?.Referer);
            var mediaForCaptions = ext is ".m3u8" or ".m3u" or ".mpd" or ".mp4" or ".mkv" or ".webm" or ".mov" or ".m4v";
            var knownLive = item?.StreamKind == StreamKind.Live;
            if (StreamSubtitles.Enabled &&
                !_skipStreamCaptions &&
                mediaForCaptions &&
                (!knownLive || !string.IsNullOrWhiteSpace(item?.CaptionUrl)))
            {
                QueueDirectCaptions(path, item);
            }

            return;
        }

        _playlist.SetCurrent(path);
        _streamTab = false;
        _isLive = false;
        _youtube = null;
        _captionFile = null;
        _captionAppliedSerial = 0;
        LoadPath(path, StreamKind.Unknown, null, null);
    }

    private static int PlaybackHeight(PlaylistItem? item) =>
        item is { VideoHeight: >= 1440 } ? item.VideoHeight : 1080;

    private void OpenYouTube(string path, PlaylistItem? item, int serial)
    {
        var audioLang = _audioLang;
        var subLang = _subLang;
        var loadCaptions = StreamSubtitles.Enabled && !_skipStreamCaptions;
        var height = PlaybackHeight(item);
        if (CanReplayCached(item, audioLang, subLang, height))
        {
            var cached = ReplayCached(item, path);
            if (cached is not null)
            {
                ApplyYouTube(
                    path,
                    item,
                    cached,
                    serial,
                    loadCaptions ? StreamCaptionLoader.Existing(cached.VideoId, subLang ?? cached.SubLang, item?.CaptionUrl ?? cached.CaptionUrl) : null);
                return;
            }
        }

        if (ResolveYouTube is { } hook)
        {
            var hooked = hook(path);
            string? existing = null;
            if (loadCaptions && hooked is not null)
            {
                var attached = item?.CaptionUrl ?? hooked.CaptionUrl;
                existing = File.Exists(attached)
                    ? attached
                    : StreamCaptionLoader.Existing(hooked.VideoId, subLang ?? hooked.SubLang, attached);
            }

            ApplyYouTube(path, item, hooked, serial, existing);
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            YouTubePlayable? playable = null;
            string? caption = null;
            try
            {
                playable = YouTubeCatalog.Resolve(path, null, audioLang, subLang)
                           ?? YouTubeCatalog.Resolve(path, null, audioLang, subLang);
                if (playable is not null)
                {
                    if (playable.Kind != StreamKind.Live)
                    {
                        playable = YouTubeCatalog.BindHlsRenditions(playable, height);
                    }
                    if (loadCaptions)
                    {
                        var attached = item?.CaptionUrl ?? playable.CaptionUrl;
                        var lang = StreamCaptionLoader.EffectiveLanguage(subLang ?? playable.SubLang, attached);
                        var existing = StreamCaptionLoader.Existing(playable.VideoId, lang, attached);
                        if (existing is not null &&
                            StreamCaptionLoader.CacheMatches(existing, MediaLanguage.Normalize(lang)))
                        {
                            caption = existing;
                        }
                        else
                        {
                            // Resolve the small caption payload before playback so
                            // the first spoken line does not appear seconds late.
                            caption = StreamCaptionLoader.Load(playable.VideoId, lang, attached);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            OnUi(() => ApplyYouTube(path, item, playable, serial, caption));
        });
    }

    private void OpenCatalog(string path, PlaylistItem? item, int serial)
    {
        var audioLang = _audioLang;
        var subLang = _subLang;
        var loadCaptions = StreamSubtitles.Enabled && !_skipStreamCaptions;
        if (CanReplayCached(item, audioLang, subLang, PlaybackHeight(item)))
        {
            var cached = ReplayCached(item, path);
            if (cached is not null)
            {
                ApplyYouTube(
                    path,
                    item,
                    cached,
                    serial,
                    loadCaptions
                        ? StreamCaptionLoader.Existing(
                            cached.VideoId,
                            subLang ?? cached.SubLang,
                            item?.CaptionUrl ?? cached.CaptionUrl)
                        : null);
                return;
            }
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            YouTubePlayable? playable = null;
            string? caption = null;
            try
            {
                playable = StreamCatalog.Resolve(path, audioLang, subLang);
                if (playable is not null &&
                    !string.IsNullOrWhiteSpace(item?.Referer) &&
                    string.IsNullOrWhiteSpace(playable.Referer))
                {
                    playable = playable.WithReferer(item.Referer);
                }

                if (playable is not null && loadCaptions && playable.Kind != StreamKind.Live)
                {
                    var attached = item?.CaptionUrl ?? playable.CaptionUrl;
                    var lang = StreamCaptionLoader.EffectiveLanguage(subLang ?? playable.SubLang, attached);
                    caption = File.Exists(attached)
                        ? attached
                        : StreamCaptionLoader.Existing(playable.VideoId, lang, attached);
                    if (string.IsNullOrWhiteSpace(caption) && !string.IsNullOrWhiteSpace(attached))
                    {
                        caption = StreamCaptionLoader.Load(playable.VideoId, lang, attached);
                    }

                    if (string.IsNullOrWhiteSpace(caption) && playable.Kind != StreamKind.Live)
                    {
                        playable = StreamCatalog.AttachVodCaptions(playable, lang);
                        caption = playable.CaptionUrl;
                    }
                }
            }
            catch (Exception)
            {
            }

            OnUi(() => ApplyYouTube(path, item, playable, serial, caption));
        });
    }

    private void QueueDirectCaptions(string path, PlaylistItem? item)
    {
        var serial = Volatile.Read(ref _openSerial);
        var lang = _subLang ?? item?.SubLang;
        var hinted = item?.CaptionUrl;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string? file = null;
            var fromManifest = false;
            try
            {
                if (!string.IsNullOrWhiteSpace(hinted) && File.Exists(hinted))
                {
                    file = hinted;
                }
                else if (!string.IsNullOrWhiteSpace(hinted))
                {
                    file = StreamCaptionLoader.Load(
                        "direct|" + Math.Abs(path.GetHashCode(StringComparison.Ordinal)).ToString("x", System.Globalization.CultureInfo.InvariantCulture),
                        lang,
                        hinted);
                }

                if (string.IsNullOrWhiteSpace(file))
                {
                    file = HlsCaptions.TryLoad(path, lang, null, path);
                    fromManifest = !string.IsNullOrWhiteSpace(file);
                }
            }
            catch (Exception)
            {
            }

            if (file is null)
            {
                return;
            }

            OnUi(() =>
            {
                if (serial != Volatile.Read(ref _openSerial) || _skipStreamCaptions || !StreamSubtitles.Enabled)
                {
                    return;
                }

                if (fromManifest && item is not null && item.StreamKind == StreamKind.Live)
                {
                    item.StreamKind = StreamKind.Vod;
                    _isLive = false;
                    OnPropertyChanged(nameof(IsLive));
                }

                _captionFile = file;
                Subtitles.AddFile(file, apply: true, attachTo: path);
                Subtitles.BindForMedia(path);
                ShowPendingCaption();
            });
        });
    }

    private void ApplyYouTube(string path, PlaylistItem? item, YouTubePlayable? playable, int serial, string? captionFile)
    {
        if (serial != Volatile.Read(ref _openSerial))
        {
            return;
        }

        item ??= _streams.Items.FirstOrDefault(entry =>
            MediaPlaylist.Identity(entry.Path) == MediaPlaylist.Identity(path));
        if (playable is null)
        {
            playable = ReplayCached(item, path);
        }

        if (playable is null)
        {
            SetYouTubePending(false);
            Note("Stream unavailable");
            return;
        }

        var captionUrl = item?.CaptionUrl ?? playable.CaptionUrl;
        if (!string.IsNullOrWhiteSpace(item?.CaptionUrl))
        {
            playable = playable.WithCaption(item.CaptionUrl);
        }

        var loadLang = StreamCaptionLoader.EffectiveLanguage(_subLang ?? playable.SubLang, captionUrl);
        if (string.IsNullOrWhiteSpace(captionFile) && File.Exists(captionUrl))
        {
            var matches = StreamCaptionLoader.CacheMatches(captionUrl, MediaLanguage.Normalize(loadLang));
            if (matches || !YouTubeCatalog.CaptionUrlIsTranslate(captionUrl))
            {
                captionFile = captionUrl;
            }
        }

        item?.RememberPlayable(playable, PlaybackHeight(item), _audioLang, _subLang);
        _contentKey = StreamCatalog.ContentKey(playable.VideoId);
        item?.SetTitle(playable.Title);
        if (item is not null)
        {
            item.StreamKind = playable.Kind;
        }

        _isLive = playable.Kind == StreamKind.Live;
        if (_isLive)
        {
            _skipStreamCaptions = true;
            captionFile = null;
        }

        if (!string.IsNullOrWhiteSpace(playable.AudioLang))
        {
            _audioLang = MediaLanguage.Normalize(playable.AudioLang);
        }

        if (!_skipStreamCaptions &&
            string.IsNullOrWhiteSpace(_subLang) &&
            !string.IsNullOrWhiteSpace(playable.SubLang))
        {
            _subLang = MediaLanguage.Normalize(playable.SubLang, keepKind: true);
        }

        _youtube = playable;
        OnPropertyChanged(nameof(StoryboardSpec));
        if (_skipStreamCaptions || !StreamSubtitles.Enabled)
        {
            if (_skipStreamCaptions)
            {
                // Record the explicit off choice against the newly resolved
                // content key. During a fast reopen the subtitle model may not
                // have received the prior MediaOpened binding yet.
                Subtitles.BindForMedia(_contentKey);
            }

            ClearStreamCaptions(userOff: _skipStreamCaptions);
        }
        else
        {
            _captionFile = captionFile ?? StreamCaptionLoader.Existing(
                playable.VideoId,
                loadLang,
                item?.CaptionUrl ?? playable.CaptionUrl);
        }

        _captionAppliedSerial = 0;
        LoadPath(
            playable.MediaUrl,
            playable.Kind,
            YouTubeCatalog.UsesSeparateAudio(playable) ? playable.AudioUrl : null,
            playable.Title,
            playable.UserAgent,
            _audioLang,
            "no",
            StreamCaptionLoader.PlayPath(_captionFile),
            playable.Referer ?? item?.Referer);
        if (!string.IsNullOrWhiteSpace(_audioLang) || !string.IsNullOrWhiteSpace(_subLang))
        {
            Note("Audio " + (_audioLang ?? "auto") + " · Subtitles " + (_subLang ?? "off"));
        }
        if (StreamSubtitles.Enabled && !_skipStreamCaptions && !string.IsNullOrWhiteSpace(_captionFile))
        {
            Subtitles.AddFile(_captionFile, apply: true, attachTo: StreamCatalog.ContentKey(playable.VideoId));
            Subtitles.BindForMedia(_contentKey);
            ShowPendingCaption();
        }

        if (StreamSubtitles.Enabled && !_skipStreamCaptions && Subtitles.Applied is null)
        {
            StartCaptionLoad(playable);
        }

        SetYouTubePending(false);
    }

    private bool CanReplayCached(PlaylistItem? item, string? audioLang, string? subLang, int height)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.MediaUrl) || _streamRetries > 0)
        {
            return false;
        }

        if (item.PlayableAt is { } at && DateTime.UtcNow - at > TimeSpan.FromMinutes(20))
        {
            return false;
        }

        if (height > 0 && item.CachedHeight > 0 && height != item.CachedHeight)
        {
            return false;
        }

        return SameCachedLang(item.CachedAudioLang, audioLang) &&
               SameCachedLang(item.CachedSubLang, subLang);
    }

    private static bool SameCachedLang(string? cached, string? requested) =>
        string.Equals(
            MediaLanguage.Normalize(cached, keepKind: true),
            MediaLanguage.Normalize(requested, keepKind: true),
            StringComparison.OrdinalIgnoreCase);

    private static YouTubePlayable? ReplayCached(PlaylistItem? item, string path)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.MediaUrl))
        {
            return null;
        }

        string? id = null;
        if (YouTubeCatalog.TryReadVideoId(path, out var youtubeId) ||
            YouTubeCatalog.TryReadVideoId(item.Path, out youtubeId))
        {
            id = youtubeId;
        }
        else if (StreamCatalog.TryReadKick(path, out _, out var kickId) ||
                 StreamCatalog.TryReadKick(item.Path, out _, out kickId))
        {
            id = "kick|" + kickId;
        }
        else if (StreamCatalog.TryReadTwitch(path, out var twitchKind, out var twitchId) ||
                 StreamCatalog.TryReadTwitch(item.Path, out twitchKind, out twitchId))
        {
            id = twitchKind == "vod" ? "twitch|v" + twitchId : "twitch|" + twitchId;
        }
        else if (StreamCatalog.IsDirectMedia(path) || StreamCatalog.IsDirectMedia(item.Path))
        {
            id = "direct|" + Math.Abs((item.MediaUrl ?? path).GetHashCode(StringComparison.Ordinal))
                .ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new YouTubePlayable(
            id,
            item.MediaUrl!,
            item.Title,
            item.StreamKind == StreamKind.Unknown ? StreamKind.Vod : item.StreamKind,
            item.AudioUrl,
            item.UserAgent,
            item.AudioLang,
            item.SubLang,
            item.CaptionUrl,
            storyboardSpec: item.StoryboardSpec,
            referer: item.Referer);
    }

    private void SetYouTubePending(bool value)
    {
        if (_youtubePending == value)
        {
            return;
        }

        _youtubePending = value;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HoldsTransport));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(SeekValue));
    }

    private void LoadPath(string path, StreamKind kind, string? audio, string? title, string? userAgent = null, string? audioLang = null, string? subLang = null, string? subFile = null, string? referer = null)
    {
        _player.Open(path, kind, audio, title, userAgent, audioLang, subLang, subFile, referer);
        OnPropertyChanged(nameof(IsLoading));
    }

    private void StartCaptionLoad(YouTubePlayable playable)
    {
        var serial = Volatile.Read(ref _openSerial);
        var item = CurrentItem();
        var captionUrl = item?.CaptionUrl ?? playable.CaptionUrl;
        var lang = StreamCaptionLoader.EffectiveLanguage(_subLang ?? playable.SubLang, captionUrl);
        var id = playable.VideoId;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string? file;
            try
            {
                file = StreamCaptionLoader.Load(id, lang, captionUrl);
            }
            catch (Exception)
            {
                return;
            }

            if (file is null)
            {
                return;
            }

            OnUi(() =>
            {
                if (serial != Volatile.Read(ref _openSerial) || !StreamSubtitles.Enabled || _skipStreamCaptions)
                {
                    return;
                }

                _captionFile = file;
                _captionAppliedSerial = 0;
                Subtitles.AddFile(file, apply: true, attachTo: StreamCatalog.ContentKey(id));
                Subtitles.BindForMedia(StreamCatalog.ContentKey(id));
                Note("Subtitles loaded");
                ShowPendingCaption();
            });
        });
    }

    private void ClearStreamCaptions(bool userOff = false)
    {
        _captionFile = null;
        if (Subtitles.Applied is not null)
        {
            Subtitles.Disable(rememberOff: userOff);
        }
        else
        {
            _player.SetSubtitleFile(null);
        }
    }

    private void ResetSubtitlePlaybackForMediaSwitch()
    {
        // Detach the outgoing media's subtitle immediately. Keeping the model
        // bound until the next stream finishes opening lets its cues render on
        // the new media's timeline (most visibly when switching VOD -> live).
        _captionFile = null;
        _captionAppliedSerial = 0;
        _player.SetSubtitleFile(null);
        Subtitles.BindForMedia(null);
        OnPropertyChanged(nameof(OnScreenCaption));
    }

    private void ShowPendingCaption()
    {
        if (!_playingStreams ||
            _skipStreamCaptions ||
            !StreamSubtitles.Enabled ||
            _captionAppliedSerial == Volatile.Read(ref _openSerial) ||
            string.IsNullOrWhiteSpace(_captionFile) ||
            !File.Exists(_captionFile) ||
            _player.State is PlayerState.Opening)
        {
            return;
        }

        var play = Subtitles.Applied is { } applied && TrackFitsCurrent(applied)
            ? applied.PlayPath
            : StreamCaptionLoader.PlayPath(_captionFile);
        if (!_player.SetSubtitleFile(play))
        {
            return;
        }

        _captionAppliedSerial = Volatile.Read(ref _openSerial);
    }

    private void SetLoadHold(bool value)
    {
        if (_loadHold == value)
        {
            return;
        }

        _loadHold = value;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HoldsTransport));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(SeekValue));
    }

    private void QueueInspect(string path, PlaylistItem? item)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var kind = _inspector!.Inspect(path);
                if (item is not null && kind != StreamKind.Unknown)
                {
                    item.StreamKind = kind;
                }
            }
            catch (Exception)
            {
            }
        });
    }

    private void ClassifyLive()
    {
        var path = _reconnectPath ?? _player.MediaPath;
        if (path is null || !UrlSanitizer.IsUrl(path))
        {
            _isLive = false;
            return;
        }

        var item = PlayingList.Items.FirstOrDefault(entry =>
            MediaPlaylist.Identity(entry.Path) == MediaPlaylist.Identity(path) ||
            (!string.IsNullOrWhiteSpace(_contentKey) && MediaPlaylist.Identity(entry.Path) == _contentKey));
        if (item?.StreamKind == StreamKind.Vod)
        {
            _isLive = false;
            return;
        }

        if (item?.StreamKind == StreamKind.Live)
        {
            _isLive = true;
            return;
        }

        var playback = StreamProbe.ClassifyPlayback(
            _player.Duration?.TotalSeconds,
            _player.IsSeekable,
            _player.FileFormat);
        var kind = StreamProbe.Combine(item?.StreamKind ?? StreamKind.Unknown, playback);
        if (item is not null)
        {
            item.StreamKind = kind;
        }

        _isLive = kind == StreamKind.Live;
    }

    private void TryResume()
    {
        if (_isLive || _player.MediaPath is null)
        {
            return;
        }

        if (_player.State is not PlayerState.Playing and not PlayerState.Paused)
        {
            return;
        }

        var fingerprint = FingerprintOf(_player.MediaPath);
        if (fingerprint is null || fingerprint == _resumeApplied)
        {
            return;
        }

        _resumeFingerprint = fingerprint;
        _resumeApplied = fingerprint;
        if (!_resume.TryGet(fingerprint, out var record) || !ResumeStore.ShouldResume(record))
        {
            return;
        }

        _player.Pause();
        if (ResumeOffered is null)
        {
            _player.Seek(TimeSpan.FromSeconds(record.Seconds));
            _player.Play();
            return;
        }

        _pendingResume = record;
        ResumeOffered.Invoke(record);
    }

    public void ContinueResume()
    {
        if (_pendingResume is { } record)
        {
            _player.Seek(TimeSpan.FromSeconds(record.Seconds));
        }

        _pendingResume = null;
        _player.Play();
        RefreshFromPlayer();
    }

    public void DeclineResume()
    {
        if (_resumeFingerprint is { } fingerprint)
        {
            _resume.Forget(fingerprint);
        }

        _pendingResume = null;
        _player.Seek(TimeSpan.Zero);
        _player.Play();
        RefreshFromPlayer();
    }

    private void MaybeSaveResume()
    {
        if (DateTime.UtcNow - _lastResumeSave < TimeSpan.FromSeconds(5))
        {
            return;
        }

        PersistResume();
    }

    private void PersistResume()
    {
        if (_isLive || _player.MediaPath is null || _player.Duration is not { } duration)
        {
            return;
        }

        var fingerprint = _resumeFingerprint ?? FingerprintOf(_player.MediaPath);
        if (fingerprint is null)
        {
            return;
        }

        _lastResumeSave = DateTime.UtcNow;
        _resume.Save(
            fingerprint,
            TitleName,
            _player.Position.TotalSeconds,
            duration.TotalSeconds);
    }

    private string? FingerprintOf(string path)
    {
        if (_isLive)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_contentKey))
        {
            return _contentKey;
        }

        var keyPath = _reconnectPath ?? CurrentItem()?.Path ?? path;
        if (YouTubeCatalog.TryReadVideoId(keyPath, out var videoId) ||
            YouTubeCatalog.TryReadVideoId(path, out videoId))
        {
            return StreamCatalog.ContentKey(videoId);
        }

        if (StreamCatalog.TryReadKick(keyPath, out _, out var kickId) ||
            StreamCatalog.TryReadKick(path, out _, out kickId))
        {
            return "kick|" + kickId;
        }

        if (StreamCatalog.TryReadTwitch(keyPath, out _, out var twitchId) ||
            StreamCatalog.TryReadTwitch(path, out _, out twitchId))
        {
            return "twitch|" + twitchId;
        }

        if (UrlSanitizer.IsUrl(path))
        {
            return ContentFingerprint.ForVod(path, _player.Duration?.TotalSeconds ?? 0);
        }

        return File.Exists(path) ? ContentFingerprint.ForLocalFile(path) : null;
    }

    private void ScheduleReconnect()
    {
        if (_reconnectPath is null || !UrlSanitizer.IsUrl(_reconnectPath))
        {
            return;
        }

        if (!_streamReady)
        {
            Note(_errorMessage ?? "Stream failed");
            return;
        }

        if (!_network.IsAvailable)
        {
            _waitForNetwork = true;
            CancelRetry();
            return;
        }

        if (_streamRetries >= 3)
        {
            return;
        }

        _streamRetries++;
        var delay = TimeSpan.FromSeconds(Math.Pow(2, _streamRetries - 1));
        var cts = new CancellationTokenSource();
        _retryCts?.Cancel();
        _retryCts = cts;
        _ = RetryAfter(delay, _reconnectPath, cts.Token);
    }

    private async Task RetryAfter(TimeSpan delay, string path, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || !_network.IsAvailable)
        {
            _waitForNetwork = !_network.IsAvailable;
            return;
        }

        OpenCurrent(path, resetRetries: false);
    }

    private void OnNetworkChanged(bool available)
    {
        if (!available)
        {
            CancelRetry();
            _waitForNetwork = _reconnectPath is not null && UrlSanitizer.IsUrl(_reconnectPath);
            return;
        }

        if (_waitForNetwork && _reconnectPath is not null)
        {
            _waitForNetwork = false;
            _streamRetries = 0;
            OpenCurrent(_reconnectPath);
        }
    }

    private void CancelRetry()
    {
        _retryCts?.Cancel();
        _retryCts = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private bool SetField(ref double field, double value, [CallerMemberName] string? name = null)
    {
        if (Math.Abs(field - value) < 0.0001)
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
