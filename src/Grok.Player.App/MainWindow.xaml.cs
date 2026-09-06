using System.Diagnostics;
using System.Runtime.InteropServices;
using Grok.Player.App.Native;
using Grok.Player.Core.Download;
using Grok.Player.Core.Launch;
using Grok.Player.Core.Media;
using Grok.Player.Core.Player;
using Grok.Player.Core.Playlist;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Preview;
using Grok.Player.Core.Subtitles;
using Grok.Player.Core.Video;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Grok.Player.App;

public sealed partial class MainWindow : Window
{
    private const int PlaylistPaneWidth = 280;
    private const int ChromeHideMs = 1000;
    private const int CursorHideMs = 3000;

    private readonly NativeVideoSurface _surface;
    private readonly PlayerHost _player;
    private readonly PlaybackViewModel _view;
    private readonly PreviewFlyout _flyout = new();
    private readonly ActionOsd _actionOsd = new();
    private SeekPreviewController? _previewUi;
    private SeekPreviewScheduler? _previewWork;
    private bool _previewCoverageAttached;
    private IPreviewAtlas? _previewAtlas;
    private string? _previewAtlasSpec;
    private string? _previewMediaKey;
    private int _previewGeneration;
    private bool _syncingUi;
    private bool _videoFullscreen;
    private bool _cinemaMode;
    private bool _chromeArmed;
    private bool _alwaysOnTop;
    private bool _windowDrag;
    private Point32 _dragMouse;
    private PointInt32 _dragWindow;
    private bool _viewQueued;
    private bool _playlistGrown;
    private bool _playlistSizePending;
    private bool _topChromeVisible = true;
    private bool _bottomChromeVisible = true;
    private DispatcherTimer? _chromeTimer;
    private DispatcherTimer? _feedbackTimer;
    private DispatcherTimer? _osdFollowTimer;
    private string? _lastFeedback;
    private DispatcherTimer? _seekDebounce;
    private double _pendingSeek = -1;
    private RectInt32? _windowedBounds;
    private int _topAnimId;
    private int _bottomAnimId;
    private int _topCutoutPx;
    private int _bottomCutoutPx;
    private bool _pointerOverTopChrome;
    private bool _pointerOverBottomChrome;
    private bool _wasMaximizedBeforeFullscreen;
    private bool _enteringFullscreen;
    private DateTime _chromeLockUntil;
    private DateTime _appMenuShownAt;
    private FlyoutPlacementMode? _pendingMenuPlacement;
    private ControlPanelWindow? _controlPanel;
    private SubtitleBrowserWindow? _subtitleBrowser;
    private PreferencesWindow? _preferences;
    private AddStreamWindow? _addStream;
    private DownloadsWindow? _downloads;
    private DevicesWindow? _devices;
    private Link.LinkServer? _link;
    private DispatcherTimer? _cursorHideTimer;
    private DispatcherTimer? _livePreviewHarvest;
    private readonly LivePreviewBuffer _livePreviews = new();
    private readonly LiveCachePreviewProvider _liveCachePreviews;
    private readonly LivePreviewCoverageProvider _liveCoveragePreviews;

    private string? _openedPreviewPath;
    private bool _cursorHidden;
    private bool _tornDown;
    private bool _closing;
    private readonly InstanceLaunchArgs _launchArgs =
        InstanceLaunchArgs.Parse(Environment.GetCommandLineArgs().Skip(1));
    private bool _launchApplied;
    private int _syncedPlayIndex = int.MinValue;
    private bool? _shownStreamTab;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
        }

        SetTitleBar(TitleDrag);
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.Resize(new SizeInt32(1180, 740));
        AppWindow.Title = "GrokPlayer";
        RememberWindowedBounds();

        var parent = WindowNative.GetWindowHandle(this);
        WindowChrome.Apply(parent, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), 840, 520);
        _surface = new NativeVideoSurface(parent);
        _player = PlayerHost.CreateForInterface(_surface.Handle);
        _liveCachePreviews = new LiveCachePreviewProvider(_player.ExportCachedPreviewClip, _livePreviews);
        _liveCachePreviews.FrameReady += OnLiveCachePreviewReady;
        _liveCoveragePreviews = new LivePreviewCoverageProvider(_livePreviews);
        _liveCoveragePreviews.CoverageReady += OnLiveCoverageReady;
        _view = new PlaybackViewModel(
            _player,
            network: new NetworkMonitor(),
            inspector: new HttpStreamInspector(),
            streamSubtitles: StreamSubtitleSettings.Load());
        _view.LayoutSize = ReadResizeLayout;
        var ui = DispatcherQueue;
        _view.PostToUi = action =>
        {
            if (!ui.TryEnqueue(() => action()))
            {
                action();
            }
        };
        _view.Noted += ShowActionFeedback;
        _view.ResumeOffered += record => DispatcherQueue.TryEnqueue(() => OfferResume(record));
        _surface.ControlDigit += digit => DispatcherQueue.TryEnqueue(() => HandleImageAdjust(digit));
        _surface.FilesDropped += paths => DispatcherQueue.TryEnqueue(() =>
        {
            var hadMedia = _view.HasMedia;
            var before = _view.Playlist.Count;
            _view.AcceptPaths(paths);
            QueueApplyView();
            ApplyVideoLayout();
            var added = _view.Playlist.Count - before;
            if (paths.Any(MediaFiles.IsSubtitle))
            {
                ShowActionFeedback("Subtitle attached");
            }
            else if (hadMedia && added > 0)
            {
                ShowActionFeedback(ActionFeedback.Added(added));
            }
        });
        _surface.AllowDrag = () =>
            !_videoFullscreen &&
            Presenter.State != OverlappedPresenterState.Maximized &&
            _preferences?.IsOpen != true;
        _surface.ClientHitsOnly = () => _preferences?.IsOpen == true;
        _surface.MouseMoved += (x, y) => DispatcherQueue.TryEnqueue(() => OnSurfaceMouseMoved(x, y));
        _surface.MouseLeft += () => DispatcherQueue.TryEnqueue(ShowVideoCursor);
        _surface.RightClicked += (x, y) => DispatcherQueue.TryEnqueue(() => ShowAppMenuAtScreen(x, y));
        WindowChrome.TryHandleContextMenu = (x, y) =>
        {
            DispatcherQueue.TryEnqueue(() => ShowAppMenuAtScreen(x, y));
            return true;
        };
        WindowChrome.AfterPlayerRaised = () =>
        {
            _actionOsd.RestackAboveOwner();
            _controlPanel?.PlaceAbovePlayerIfPinned();
            _subtitleBrowser?.PlaceAbovePlayerIfPinned();
            _preferences?.PlaceAbovePlayerIfPinned();
            _devices?.PlaceAbovePlayerIfPinned();
        };
        _link = new Link.LinkServer(DispatcherQueue, () => _view);
        _link.PairOffered += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_devices?.IsOpen == true) return;
            Devices_Click(this, new RoutedEventArgs());
        });
        _link.Start();
        _view.PropertyChanged += (_, _) => QueueApplyView();
        BindPlaylistSource();
        LocalPlaylistView.ItemsSource = _view.Playlist.Items;
        StreamPlaylistView.ItemsSource = _view.Streams.Items;
        LocalPlaylistView.AddHandler(UIElement.DragOverEvent, new DragEventHandler(ContentRoot_DragOver), true);
        StreamPlaylistView.AddHandler(UIElement.DragOverEvent, new DragEventHandler(ContentRoot_DragOver), true);
        PlaylistPanel.AddHandler(UIElement.DragOverEvent, new DragEventHandler(ContentRoot_DragOver), true);
        _flyout.AttachOwner(parent);
        _actionOsd.AttachOwner(parent);

        Closed += OnClosed;
        AppWindow.Closing += OnWindowClosing;
        Activated += OnWindowActivated;
        VideoHost.Loaded += OnVideoHostLoaded;
        ContentRoot.Loaded += (_, _) =>
        {
            if (AppMenu.XamlRoot is null && ContentRoot.XamlRoot is not null)
            {
                AppMenu.XamlRoot = ContentRoot.XamlRoot;
            }
        };
        SeekSlider.Loaded += (_, _) => HookSeekSlider();
        SeekSlider.ValueChanged += SeekSlider_ValueChanged;
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        TopBar.SizeChanged += (_, _) => UpdateInputRegions();
        BottomBar.SizeChanged += (_, _) => UpdateInputRegions();
        ContentRoot.SizeChanged += (_, _) => UpdateInputRegions();
        AppWindow.Changed += OnAppWindowChanged;
        _player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(OnMediaOpened);
        _chromeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ChromeHideMs) };
        _chromeTimer.Tick += ChromeTimer_Tick;
        _seekDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _seekDebounce.Tick += SeekDebounce_Tick;
        _feedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            _osdFollowTimer?.Stop();
            _lastFeedback = null;
            _actionOsd.Hide();
        };
        _osdFollowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _osdFollowTimer.Tick += (_, _) => RepositionActionOsd();
        _cursorHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CursorHideMs) };
        _cursorHideTimer.Tick += (_, _) => HideVideoCursor();

        ApplyView();
        UpdateControlPanelChrome();
        if (Environment.GetCommandLineArgs().Any(arg =>
                arg.Equals("--preview-ui", StringComparison.OrdinalIgnoreCase)))
        {
            DispatcherQueue.TryEnqueue(OpenPreviewWindows);
        }
    }

    private void OpenPreviewWindows()
    {
        try
        {
            SeedPreviewDownloads();
            ShowDownloads();
            var owner = WindowNative.GetWindowHandle(this);
            var sample = CaptionMarkup.Parse("<b><i>Sample subtitle</i></b>");
            var style = new SubtitleStyleWindow(owner, _alwaysOnTop, sample);
            style.AppWindow.Move(new PointInt32(AppWindow.Position.X + 40, AppWindow.Position.Y + 70));
            style.Activate();
            style.PlaceAbove();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preview.log"), ex.ToString());
        }
    }

    private static void SeedPreviewDownloads()
    {
        if (App.Downloads.Jobs.Count > 0)
        {
            return;
        }

        var running = App.Downloads.Enqueue("https://example.com/a.m3u8", "Escape from Tarkov — raid one", start: false, "tr", 1080);
        running.State = DownloadState.Paused;
        running.Bytes = 48_000_000;
        running.TotalBytes = 120_000_000;
        running.Height = 1080;
        var paused = App.Downloads.Enqueue("https://example.com/b.m3u8", "Night city drive", start: false, "en", 720);
        paused.State = DownloadState.Paused;
        paused.Bytes = 18_000_000;
        paused.TotalBytes = 80_000_000;
        paused.Height = 720;
        var done = App.Downloads.Enqueue("https://example.com/c.m3u8", "Finished concert cut", start: false);
        done.State = DownloadState.Completed;
        done.Bytes = 64_000_000;
        done.TotalBytes = 64_000_000;
        var failed = App.Downloads.Enqueue("https://example.com/d.m3u8", "Broken source", start: false);
        failed.State = DownloadState.Failed;
        failed.Error = "Source unavailable";
    }

    private OverlappedPresenter Presenter => (OverlappedPresenter)AppWindow.Presenter;

    private bool ChromeImmersive => _videoFullscreen || _cinemaMode;

    private bool ChromeHoverReady => _chromeArmed && DateTime.UtcNow >= _chromeLockUntil;

    private void ArmChromeHover()
    {
        _chromeArmed = false;
        _pointerOverTopChrome = false;
        _pointerOverBottomChrome = false;
        _chromeLockUntil = DateTime.UtcNow.AddMilliseconds(450);
    }

    private void QueueApplyView()
    {
        if (_viewQueued)
        {
            return;
        }

        _viewQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _viewQueued = false;
            ApplyView();
        });
    }

    private void OnMediaOpened()
    {
        var path = _player.MediaPath;
        var same = string.Equals(_openedPreviewPath, path, StringComparison.OrdinalIgnoreCase);
        _openedPreviewPath = path;
        EnsurePreview();
        if (!same)
        {
            ResetPreview(path);
        }

        BindPreviewAtlas();
        _previewUi?.SetMedia(path, _player.Duration);
        var decoderVod = !string.IsNullOrWhiteSpace(path) &&
                         !_view.IsLive &&
                         _previewAtlas is null &&
                         !YouTubeCatalog.IsWatchUrl(_view.VisiblePlaylist.CurrentPath) &&
                         !YouTubeCatalog.TryReadVideoId(_view.VisiblePlaylist.CurrentPath ?? path, out _);
        if (decoderVod && !_previewCoverageAttached && _previewWork is not null)
        {
            _previewCoverageAttached = true;
            _previewWork.AttachCoverage(SeekPreviewEngine.Create());
        }

        _previewWork?.SetMedia(
            path,
            _player.Duration,
            prefetch: decoderVod,
            referer: _view.PlayingReferer);
        _previewWork?.SetAtlas(_previewAtlas);
        if (decoderVod)
        {
            _previewWork?.Warm(path);
        }

        ApplyView();
        ApplyVideoLayout();
        _view.RefreshPushedVideo();
        if (!same)
        {
            ShowActionFeedback(ActionFeedback.Opened(
                _view.VisiblePlaylist.CurrentIndex + 1,
                _view.VisiblePlaylist.Count,
                _view.TitleName));
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        foreach (var ext in MediaFiles.Extensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0)
        {
            return;
        }

        var hadMedia = _view.HasMedia;
        var before = _view.Playlist.Count;
        var picked = files.Select(file => file.Path).ToArray();
        _view.AcceptPaths(picked);
        _view.Open(picked[0]);
        ApplyView();
        ApplyVideoLayout();
        var added = _view.Playlist.Count - before;
        if (hadMedia && added > 0)
        {
            ShowActionFeedback(ActionFeedback.Opened(
                _view.VisiblePlaylist.CurrentIndex + 1,
                _view.VisiblePlaylist.Count,
                _view.TitleName));
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        var playing = _view.IsPlaying;
        _view.TogglePlayPause();
        ShowActionFeedback(playing ? "Paused" : "Playing");
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _view.Stop();
        ApplyVideoLayout();
        ShowActionFeedback("Cleared");
    }

    private void RewindButton_Click(object sender, RoutedEventArgs e)
    {
        AnnounceSkip(TimeSpan.FromSeconds(-5));
        _view.SeekBy(TimeSpan.FromSeconds(-5));
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        AnnounceSkip(TimeSpan.FromSeconds(5));
        _view.SeekBy(TimeSpan.FromSeconds(5));
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _view.ToggleMute();
        ShowActionFeedback(_view.IsMuted ? "Muted" : "Unmuted");
    }

    private void PositionTime_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_view.IsLive)
        {
            e.Handled = true;
            return;
        }

        _view.ToggleTimeMode();
        SetText(PositionTimeText, _view.PositionText);
        ShowActionFeedback(_view.ShowRemaining ? "Remaining time" : "Elapsed time");
        e.Handled = true;
    }

    private void PositionTime_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        PositionTimeText.Foreground = (Brush)Application.Current.Resources["GrokAccentBrush"];
    }

    private void PositionTime_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        PositionTimeText.Foreground = (Brush)Application.Current.Resources["GrokMutedBrush"];
    }
    private void LoopButton_Click(object sender, RoutedEventArgs e)
    {
        _view.CycleLoop();
        ShowActionFeedback(_view.LoopLabel);
    }

    private void RebuildAudioMenu()
    {
        if (AudioMenu is null)
        {
            return;
        }

        AudioMenu.Items.Clear();
        var choices = _view.PlayingAudioChoices();
        if (choices.Count == 0)
        {
            AudioMenu.Items.Add(new MenuFlyoutItem { Text = "No audio tracks", IsEnabled = false });
            return;
        }

        foreach (var choice in choices)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = choice.Label,
                GroupName = "GrokPlayingAudio",
                IsChecked = choice.Selected,
                Tag = choice.Index
            };
            item.Click += PlayingAudio_Click;
            AudioMenu.Items.Add(item);
        }
    }

    private void RebuildPlayingSubtitleMenu()
    {
        if (SubtitlesMenu is null || PlayingSubsSeparator is null)
        {
            return;
        }

        var end = SubtitlesMenu.Items.IndexOf(PlayingSubsSeparator);
        if (end < 0)
        {
            return;
        }

        while (end > 0)
        {
            SubtitlesMenu.Items.RemoveAt(0);
            end--;
        }

        var insert = 0;
        foreach (var choice in _view.PlayingSubtitleChoices())
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = choice.Label,
                GroupName = "GrokPlayingSubs",
                IsChecked = choice.Selected,
                Tag = choice.Index
            };
            item.Click += PlayingSubtitle_Click;
            SubtitlesMenu.Items.Insert(insert++, item);
        }
    }

    private void PlayingAudio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: int index })
        {
            _view.SelectPlayingAudio(index);
            ShowActionFeedback(_view.AudioTrackLabel);
        }
    }

    private void PlayingSubtitle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: int index })
        {
            _view.SelectPlayingSubtitle(index);
            ShowActionFeedback(_view.SubtitleTrackLabel);
        }
    }

    private void PlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        _view.TogglePlaylist();
        ApplyPlaylistPane();
        ApplyPlaylistChrome();
        ShowActionFeedback(_view.PlaylistVisible ? "Playlist" : "Playlist hidden");
    }

    private void CinemaButton_Click(object sender, RoutedEventArgs e)
    {
        SetCinemaMode(!_cinemaMode);
        ShowActionFeedback(_cinemaMode ? "Interface hidden" : "Interface shown");
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        SetAlwaysOnTop(!_alwaysOnTop);
        ShowActionFeedback(_alwaysOnTop ? "Always on top" : "Always on top off");
    }
    private void MinButton_Click(object sender, RoutedEventArgs e) => Presenter.Minimize();
    private void MaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (_videoFullscreen)
        {
            SetVideoFullscreen(false);
            return;
        }

        if (Presenter.State == OverlappedPresenterState.Maximized)
        {
            Presenter.Restore();
        }
        else
        {
            Presenter.Maximize();
        }
    }

    private void FullButton_Click(object sender, RoutedEventArgs e)
    {
        SetVideoFullscreen(!_videoFullscreen);
        DispatcherQueue.TryEnqueue(() =>
            ShowActionFeedback(_videoFullscreen ? "Fullscreen" : "Windowed"));
    }
    private void CloseButton_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void SetAlwaysOnTop(bool value)
    {
        _alwaysOnTop = value;
        Presenter.IsAlwaysOnTop = value;
        PinIcon.Foreground = value
            ? (Brush)Application.Current.Resources["GrokAccentBrush"]
            : (Brush)Application.Current.Resources["GrokMutedBrush"];
        _controlPanel?.SyncPlayerAlwaysOnTop(value);
        _subtitleBrowser?.SyncPlayerAlwaysOnTop(value);
        _preferences?.SyncPlayerAlwaysOnTop(value);
        _devices?.SyncPlayerAlwaysOnTop(value);
        _addStream?.SyncPlayerAlwaysOnTop(value);
        _downloads?.SyncPlayerAlwaysOnTop(value);
    }

    private void ControlPanelButton_Click(object sender, RoutedEventArgs e)
    {
        SetControlPanelOpen(!IsControlPanelOpen);
    }

    private void Preferences_Click(object sender, RoutedEventArgs e)
    {
        EnsurePreferences();
        _preferences!.SetOpen(true);
        ShowActionFeedback("Preferences");
    }

    private void Devices_Click(object sender, RoutedEventArgs e)
    {
        if (_devices is null && _link is not null)
        {
            var player = WindowNative.GetWindowHandle(this);
            _devices = new DevicesWindow(player, _alwaysOnTop, _link);
            _devices.Closed += (_, _) => _devices = null;
            var here = AppWindow.Position;
            _devices.AppWindow.Move(new PointInt32(here.X + 64, here.Y + 72));
        }

        _devices?.SetOpen(true);
        ShowActionFeedback("Devices");
    }

    private void EnsurePreferences()
    {
        if (_preferences is not null)
        {
            return;
        }

        var player = WindowNative.GetWindowHandle(this);
        _preferences = new PreferencesWindow(player, _alwaysOnTop, _view);
        _preferences.Closed += (_, _) => _preferences = null;
        var here = AppWindow.Position;
        _preferences.AppWindow.Move(new PointInt32(here.X + 48, here.Y + 56));
    }

    private void LocalTab_Click(object sender, RoutedEventArgs e) => _view.ShowStreamTab(false);

    private void StreamTab_Click(object sender, RoutedEventArgs e) => _view.ShowStreamTab(true);

    private void BindPlaylistSource() => ApplyPlaylistChrome();

    private void ApplyPlaylistChrome()
    {
        AddStreamButton.Visibility = Visibility.Visible;
        if (DownloadsButton is not null)
        {
            DownloadsButton.Visibility = Visibility.Visible;
        }
        PaintPlaylistTabs();
        var empty = _view.VisiblePlaylist.Count == 0;
        PlaylistEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        PlaylistEmptyIcon.Glyph = _view.StreamTab ? "\uE774" : "\uE8B7";
        PlaylistEmptyTitle.Text = _view.StreamTab ? "No streams" : "No local files";
        PlaylistEmptyHint.Text = _view.StreamTab
            ? "Add a stream or open one from YouTube"
            : "Open a file or drop one here";
        if (_shownStreamTab != _view.StreamTab)
        {
            var first = _shownStreamTab is null;
            _shownStreamTab = _view.StreamTab;
            if (first)
            {
                var show = _view.StreamTab ? StreamPlaylistView : LocalPlaylistView;
                var hide = _view.StreamTab ? LocalPlaylistView : StreamPlaylistView;
                show.Visibility = Visibility.Visible;
                show.Opacity = 1;
                show.IsHitTestVisible = true;
                hide.Visibility = Visibility.Collapsed;
                hide.Opacity = 0;
                hide.IsHitTestVisible = false;
            }
            else
            {
                CrossfadePlaylist(_view.StreamTab);
            }
        }

        var index = _view.VisiblePlaylist.CurrentIndex;
        if (index == _syncedPlayIndex)
        {
            return;
        }

        _syncedPlayIndex = index;
        if (index >= 0 && ActivePlaylistView.SelectedIndex != index)
        {
            ActivePlaylistView.SelectedIndex = index;
        }
    }

    private ListView ActivePlaylistView => _view.StreamTab ? StreamPlaylistView : LocalPlaylistView;

    private void CrossfadePlaylist(bool stream)
    {
        var show = stream ? StreamPlaylistView : LocalPlaylistView;
        var hide = stream ? LocalPlaylistView : StreamPlaylistView;
        if (ReferenceEquals(show, hide) || show.Visibility == Visibility.Visible && hide.Visibility == Visibility.Collapsed)
        {
            show.Visibility = Visibility.Visible;
            show.Opacity = 1;
            show.IsHitTestVisible = true;
            return;
        }

        hide.IsHitTestVisible = false;
        show.Visibility = Visibility.Visible;
        show.IsHitTestVisible = true;
        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (!ReferenceEquals(ActivePlaylistView, hide))
            {
                hide.Visibility = Visibility.Collapsed;
            }
        };
        Storyboard.SetTarget(fadeOut, hide);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        Storyboard.SetTarget(fadeIn, show);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        var board = new Storyboard();
        board.Children.Add(fadeOut);
        board.Children.Add(fadeIn);
        board.Begin();
    }

    private void PaintPlaylistTabs()
    {
        var accent = (Brush)Application.Current.Resources["GrokAccentBrush"];
        var muted = (Brush)Application.Current.Resources["GrokMutedBrush"];
        var panel = (Brush)Application.Current.Resources["GrokPanelBrush"];
        var chrome = (Brush)Application.Current.Resources["GrokChromeBrush"];
        var line = (Brush)Application.Current.Resources["GrokLineBrush"];
        var stream = _view.StreamTab;
        LocalTabButton.Foreground = stream ? muted : accent;
        StreamTabButton.Foreground = stream ? accent : muted;
        LocalTabChrome.Background = stream ? chrome : panel;
        StreamTabChrome.Background = stream ? panel : chrome;
        LocalTabChrome.BorderThickness = new Thickness(0, 0, 1, 0);
        StreamTabChrome.BorderThickness = new Thickness(0);
        LocalTabChrome.BorderBrush = line;
        StreamTabChrome.BorderBrush = line;
    }

    private void ApplyLiveChrome()
    {
        var live = _view.IsLive;
        LiveButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        LoopButton.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        var durationVisible = live ? Visibility.Collapsed : Visibility.Visible;
        DurationSlash.Visibility = durationVisible;
        DurationTimeText.Visibility = durationVisible;
        var accent = (Brush)Application.Current.Resources["GrokAccentBrush"];
        var muted = (Brush)Application.Current.Resources["GrokMutedBrush"];
        var atLive = _view.IsAtLive;
        LiveDot.Fill = atLive ? accent : muted;
        LiveLabel.Foreground = atLive ? accent : muted;
        UpdateCacheFill();
    }

    private void UpdateCacheFill()
    {
        var width = SeekSlider.ActualWidth;
        var max = _view.SeekMaximum;
        if (width < 2 || max <= 0)
        {
            CacheFill.Width = 0;
            return;
        }

        var origin = _view.SeekOrigin;
        var span = Math.Max(0.001, max - origin);
        var end = Math.Clamp(_view.CacheEndSeconds, origin, max);
        if (!_view.IsLive && UrlSanitizer.IsUrl(_player.MediaPath ?? "") == false && _player.HasMedia)
        {
            end = Math.Max(end, max);
        }

        CacheFill.Width = (end - origin) / span * width;
    }

    private void OfferResume(ResumeRecord record)
    {
        var owner = WindowNative.GetWindowHandle(this);
        var at = TimeDisplay.FormatClock(TimeSpan.FromSeconds(record.Seconds));
        var dialog = new ResumeWindow(owner, $"Continue from {at}, or start this video over?");
        dialog.Continued += () => _view.ContinueResume();
        dialog.Declined += () => _view.DeclineResume();
        var here = AppWindow.Position;
        dialog.AppWindow.Move(new PointInt32(here.X + 80, here.Y + 90));
        dialog.AppWindow.Show();
    }

    private void LiveButton_Click(object sender, RoutedEventArgs e) => _view.GoLive();

    private void GoLive_Click(object sender, RoutedEventArgs e) => _view.GoLive();

    private void OpenStream_Click(object sender, RoutedEventArgs e) => ShowAddStream();

    private void AddStream_Click(object sender, RoutedEventArgs e) => ShowAddStream();

    private void ShowAddStream()
    {
        _view.ShowStreamTab(true);
        if (!_view.PlaylistVisible)
        {
            _view.PlaylistVisible = true;
        }

        ApplyPlaylistPane();
        ApplyPlaylistChrome();
        if (_addStream is null)
        {
            var player = WindowNative.GetWindowHandle(this);
            _addStream = new AddStreamWindow(player, _alwaysOnTop);
            _addStream.Submitted += (url, play) =>
            {
                if (_view.AddStream(url, play))
                {
                    ApplyView();
                }
            };
            _addStream.Closed += (_, _) => _addStream = null;
        }

        _addStream.SetOpen(true);
        var here = AppWindow.Position;
        _addStream.AppWindow.Move(new PointInt32(here.X + 64, here.Y + 72));
    }

    private async void StartRecord_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        picker.FileTypeChoices.Add("MPEG-TS", [".ts"]);
        picker.FileTypeChoices.Add("MPEG-4", [".mp4"]);
        picker.SuggestedFileName = "grok-record";
        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            _view.StartRecording(file.Path);
        }
    }

    private void StopRecord_Click(object sender, RoutedEventArgs e) => _view.StopRecording();

    private void AppMenuControlPanel_Click(object sender, RoutedEventArgs e)
    {
        SetControlPanelOpen(AppMenuControlPanelItem.IsChecked);
    }

    private bool IsControlPanelOpen => _controlPanel?.IsOpen == true;

    private void SetControlPanelOpen(bool open)
    {
        if (open)
        {
            EnsureControlPanel();
            _controlPanel!.SetOpen(true);
        }
        else if (_controlPanel is not null)
        {
            _controlPanel.SetOpen(false);
        }

        UpdateControlPanelChrome();
    }

    private void EnsureControlPanel()
    {
        if (_controlPanel is not null)
        {
            return;
        }

        var player = WindowNative.GetWindowHandle(this);
        _controlPanel = new ControlPanelWindow(player, _alwaysOnTop, _view, ShowActionFeedback);
        _controlPanel.OpenChanged += _ => UpdateControlPanelChrome();
        _controlPanel.Closed += (_, _) =>
        {
            _controlPanel = null;
            UpdateControlPanelChrome();
        };
        var here = AppWindow.Position;
        _controlPanel.AppWindow.Move(new PointInt32(here.X + 72, here.Y + 96));
    }

    private void UpdateControlPanelChrome()
    {
        var open = IsControlPanelOpen;
        ControlPanelIcon.Foreground = open
            ? (Brush)Application.Current.Resources["GrokAccentBrush"]
            : (Brush)Application.Current.Resources["GrokMutedBrush"];
        if (AppMenuControlPanelItem is not null)
        {
            AppMenuControlPanelItem.IsChecked = open;
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            _controlPanel?.PlaceAbovePlayerIfPinned();
            _subtitleBrowser?.PlaceAbovePlayerIfPinned();
            _preferences?.PlaceAbovePlayerIfPinned();
        });
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingUi)
        {
            return;
        }

        _view.Volume = e.NewValue;
        ShowActionFeedback(ActionFeedback.Volume(e.NewValue));
    }

    private void VideoHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SyncVideoSurface();
        UpdateInputRegions();
        RepositionActionOsd();
    }

    private void OnVideoHostLoaded(object sender, RoutedEventArgs e)
    {
        if (VideoHost.XamlRoot is not null)
        {
            VideoHost.XamlRoot.Changed += (_, _) => SyncVideoSurface();
        }

        ApplyVideoLayout();
        UpdateInputRegions();
        ApplyLaunchWhenReady();
    }

    private void HookSeekSlider()
    {
        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
        SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(SeekSlider_PointerCaptureLost), true);
        SeekSlider.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(SeekSlider_PointerMoved), true);
        SeekSlider.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(SeekSlider_PointerExited), true);
        if (FindDescendant<Thumb>(SeekSlider) is { } thumb)
        {
            thumb.DragStarted += (_, _) => _view.BeginSeek();
            thumb.DragCompleted += (_, _) => FinishSeek();
        }
    }

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _view.BeginSeek();
        UpdatePreviewFromPointer(e);
    }

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e) => FinishSeek();
    private void SeekSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => FinishSeek();

    private void SeekSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_view.CanSeek)
        {
            UpdatePreviewFromPointer(e);
        }
    }

    private void SeekSlider_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        HideSeekPreview();
    }

    private void SeekSlider_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAbMarks();
        UpdateCacheFill();
    }

    private void UpdateAbMarks()
    {
        var width = SeekSlider.ActualWidth;
        var duration = _view.SeekMaximum;
        PlaceAbMark(LoopAMark, _view.LoopA, width, duration);
        PlaceAbMark(LoopBMark, _view.LoopB, width, duration);
    }

    private static void PlaceAbMark(Microsoft.UI.Xaml.Shapes.Rectangle mark, TimeSpan? point, double width, double duration)
    {
        if (mark is null)
        {
            return;
        }

        if (point is null || width < 2 || duration <= 0)
        {
            mark.Visibility = Visibility.Collapsed;
            return;
        }

        var x = Math.Clamp(point.Value.TotalSeconds / duration, 0, 1) * width;
        mark.Visibility = Visibility.Visible;
        Canvas.SetLeft(mark, Math.Max(0, x - 1));
        Canvas.SetTop(mark, Math.Max(0, (SeekTrackHeight(mark) - mark.Height) / 2));
    }

    private static double SeekTrackHeight(Microsoft.UI.Xaml.Shapes.Rectangle mark)
    {
        return mark.Parent is Canvas canvas && canvas.ActualHeight > 0 ? canvas.ActualHeight : 22;
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingUi)
        {
            return;
        }

        if (_view.IsSeeking)
        {
            var seconds = _view.ClampSeek(e.NewValue);
            if (Math.Abs(seconds - e.NewValue) > 0.001)
            {
                SeekSlider.Value = seconds;
                return;
            }

            _view.UpdateSeekPreview(seconds);
            SetText(PositionTimeText, _view.PositionText);
            UpdatePreviewFromTime(seconds);
            ArmSeekDebounce(seconds);
            ShowActionFeedback(ActionFeedback.SeekTo(TimeSpan.FromSeconds(seconds), SeekFeedbackDuration()));
        }
    }

    private void ArmSeekDebounce(double seconds)
    {
        _pendingSeek = seconds;
        if (_seekDebounce is null)
        {
            return;
        }

        _seekDebounce.Stop();
        _seekDebounce.Start();
    }

    private void SeekDebounce_Tick(object? sender, object e)
    {
        _seekDebounce?.Stop();
        if (_view.IsSeeking && _pendingSeek >= 0)
        {
            _view.ApplySeek(_pendingSeek);
        }
    }

    private double _pointerSeek = -1;

    private void FinishSeek()
    {
        _seekDebounce?.Stop();
        if (!_view.IsSeeking)
        {
            return;
        }

        var target = _pointerSeek >= 0 ? _pointerSeek : SeekSlider.Value;
        _pointerSeek = -1;
        _view.EndSeek(target);
        ShowActionFeedback(ActionFeedback.SeekTo(
            TimeSpan.FromSeconds(target),
            SeekFeedbackDuration()));
    }

    private TimeSpan? SeekFeedbackDuration() => _view.IsLive ? null : _player.Duration;

    private void ContentRoot_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsContentVisible = false;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = string.Empty;
        e.Handled = true;
    }

    private void ContentRoot_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
    }

    private void EmptyArea_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(ContentRoot).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (IsInteractive(e.OriginalSource))
        {
            return;
        }

        BeginWindowDrag(sender as UIElement, e);
        e.Handled = true;
    }

    private void BrandButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppMenu.IsOpen)
        {
            AppMenu.Hide();
            return;
        }

        var top = BrandButton.TransformToVisual(ContentRoot).TransformPoint(new Point(0, 0)).Y;
        var bottom = top + BrandButton.ActualHeight;
        var placement = ChooseMenuPlacement(ContentRoot.ActualHeight - bottom, top);
        _appMenuShownAt = DateTime.UtcNow;
        _pendingMenuPlacement = placement;
        AppMenu.ShowAt(BrandButton, new FlyoutShowOptions
        {
            Placement = placement,
            ShowMode = FlyoutShowMode.Standard
        });
    }

    private void EmptyArea_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (IsInteractive(e.OriginalSource) || IsInsidePlaylist(e.OriginalSource))
        {
            return;
        }

        ShowAppMenuAt(ContentRoot, e.GetPosition(ContentRoot));
        e.Handled = true;
    }

    private void AppMenu_Opening(object sender, object e)
    {
        RebuildAudioMenu();
        RebuildPlayingSubtitleMenu();
        RebuildSubtitleMenu();
        SyncStreamSubtitleMenu();
        SyncVideoEnhanceMenu();
        BrandChevron.Glyph = "\uE70E";
        AppMenuPlaylistItem.IsChecked = _view.PlaylistVisible;
        AppMenuControlPanelItem.IsChecked = IsControlPanelOpen;
        if (GoLiveMenuItem is not null)
        {
            GoLiveMenuItem.IsEnabled = _view.IsLive;
        }

        if (StartRecordMenuItem is not null)
        {
            StartRecordMenuItem.IsEnabled = _view.HasMedia && !_view.IsRecording;
        }

        if (StopRecordMenuItem is not null)
        {
            StopRecordMenuItem.IsEnabled = _view.IsRecording;
        }
        if (_pendingMenuPlacement is { } pending)
        {
            AppMenu.Placement = pending;
            _pendingMenuPlacement = null;
            return;
        }

        var top = BrandButton.TransformToVisual(ContentRoot).TransformPoint(new Point(0, 0)).Y;
        var bottom = top + BrandButton.ActualHeight;
        AppMenu.Placement = ChooseMenuPlacement(
            ContentRoot.ActualHeight - bottom,
            top);
    }

    private void AppMenu_Closed(object sender, object e)
    {
        BrandChevron.Glyph = "\uE70D";
    }

    private void RebuildSubtitleMenu()
    {
        var menu = AddSelectSubtitlesMenu;
        while (menu.Items.Count > 6)
        {
            menu.Items.RemoveAt(menu.Items.Count - 1);
        }

        SubtitlesOffItem.IsChecked = !_view.Subtitles.Enabled;
        foreach (var track in _view.Subtitles.Tracks)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = track.Name,
                GroupName = "GrokSubtitles",
                IsChecked = _view.Subtitles.Enabled &&
                            ReferenceEquals(track, _view.Subtitles.Applied),
                Tag = track.Id
            };
            item.Click += SubtitleTrack_Click;
            menu.Items.Add(item);
        }
    }

    private async void LoadSubtitle_Click(object sender, RoutedEventArgs e)
    {
        var path = await SubtitleFiles.PickAsync(WindowNative.GetWindowHandle(this));
        if (path is null)
        {
            return;
        }

        var track = _view.Subtitles.AddFile(path, apply: true);
        ShowActionFeedback(ActionFeedback.SubtitleLoaded(track.Name));
        _subtitleBrowser?.Refresh();
    }

    private async void AddSubtitle_Click(object sender, RoutedEventArgs e)
    {
        var path = await SubtitleFiles.PickAsync(WindowNative.GetWindowHandle(this));
        if (path is null)
        {
            return;
        }

        var track = _view.Subtitles.AddFile(path, apply: false);
        ShowActionFeedback(ActionFeedback.SubtitleAdded(track.Name));
        SetSubtitleBrowserOpen(true);
    }

    private async void MergeSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (_view.Subtitles.Active is null)
        {
            ShowActionFeedback("No subtitle tab");
            return;
        }

        var path = await SubtitleFiles.PickAsync(WindowNative.GetWindowHandle(this));
        if (path is null)
        {
            return;
        }

        if (_view.Subtitles.MergeFile(path))
        {
            ShowActionFeedback(ActionFeedback.SubtitlesMerged());
            _subtitleBrowser?.Refresh();
        }
    }

    private void SyncStreamSubtitleMenu()
    {
        var mode = _view.StreamSubtitles.Mode;
        if (StreamSubsOffItem is not null)
        {
            StreamSubsOffItem.IsChecked = mode == StreamSubtitleMode.Off;
        }

        if (StreamSubsOnItem is not null)
        {
            StreamSubsOnItem.IsChecked = mode == StreamSubtitleMode.On;
        }

        if (StreamSubsBrowserItem is not null)
        {
            StreamSubsBrowserItem.IsChecked = mode == StreamSubtitleMode.Browser;
        }
    }

    private void StreamSubtitleMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string tag)
        {
            return;
        }

        var mode = tag switch
        {
            "Off" => StreamSubtitleMode.Off,
            "Browser" => StreamSubtitleMode.Browser,
            _ => StreamSubtitleMode.On
        };
        _view.SetStreamSubtitleMode(mode);
        if (mode == StreamSubtitleMode.Browser)
        {
            SetSubtitleBrowserOpen(true);
        }

        SyncStreamSubtitleMenu();
    }

    private void SubtitleCycle_Click(object sender, RoutedEventArgs e) => SetSubtitleBrowserOpen(true);

    private void SubtitlesOff_Click(object sender, RoutedEventArgs e)
    {
        _view.Subtitles.Disable();
        ShowActionFeedback(ActionFeedback.SubtitlesOff());
        _subtitleBrowser?.Refresh();
    }

    private void SubtitleTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string id)
        {
            return;
        }

        var index = -1;
        for (var i = 0; i < _view.Subtitles.Tracks.Count; i++)
        {
            if (_view.Subtitles.Tracks[i].Id == id)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        _view.Subtitles.Apply(index);
        ShowActionFeedback(ActionFeedback.SubtitleLoaded(_view.Subtitles.Tracks[index].Name));
        _subtitleBrowser?.Refresh();
    }

    private void SetSubtitleBrowserOpen(bool open)
    {
        if (open)
        {
            EnsureSubtitleBrowser();
            _subtitleBrowser!.SetOpen(true);
            return;
        }

        _subtitleBrowser?.SetOpen(false);
    }

    private void EnsureSubtitleBrowser()
    {
        if (_subtitleBrowser is not null)
        {
            return;
        }

        var player = WindowNative.GetWindowHandle(this);
        _subtitleBrowser = new SubtitleBrowserWindow(player, _alwaysOnTop, _view, ShowActionFeedback);
        _subtitleBrowser.Closed += (_, _) => _subtitleBrowser = null;
        var here = AppWindow.Position;
        _subtitleBrowser.AppWindow.Move(new PointInt32(here.X + 56, here.Y + 72));
    }

    private void SyncVideoEnhanceMenu()
    {
        if (VsrOffItem is not null)
        {
            VsrOffItem.IsChecked = !_view.Video.SuperResolution;
        }

        if (VsrOnItem is not null)
        {
            VsrOnItem.IsChecked = _view.Video.SuperResolution;
        }

        if (HdrOffItem is not null)
        {
            HdrOffItem.IsChecked = _view.Video.Hdr == HdrOutputMode.Off;
        }

        if (HdrNativeItem is not null)
        {
            HdrNativeItem.IsChecked = _view.Video.Hdr == HdrOutputMode.Native;
        }

        if (HdrRtxItem is not null)
        {
            HdrRtxItem.IsChecked = _view.Video.Hdr == HdrOutputMode.Rtx;
        }
    }

    private void SuperResolution_Click(object sender, RoutedEventArgs e)
    {
        var on = sender is RadioMenuFlyoutItem { Tag: "on" };
        _view.Video.SetSuperResolution(on);
        ShowActionFeedback(ActionFeedback.VideoFilter("Super resolution", on));
    }

    private void HdrMode_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as RadioMenuFlyoutItem)?.Tag as string;
        var mode = tag == "Off"
            ? HdrOutputMode.Off
            : tag == "Rtx"
                ? HdrOutputMode.Rtx
                : HdrOutputMode.Native;
        _view.Video.SetHdr(mode);
        ShowActionFeedback(ActionFeedback.HdrMode(VideoEnhanceSpec.Label(mode)));
    }

    private void AppMenuPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var want = AppMenuPlaylistItem.IsChecked;
        if (_view.PlaylistVisible == want)
        {
            return;
        }

        _view.PlaylistVisible = want;
        ApplyPlaylistPane();
        ShowActionFeedback(want ? "Playlist" : "Playlist hidden");
    }

    private void ShowAppMenuAtScreen(int screenX, int screenY)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var point = new Point32(screenX, screenY);
        ScreenToClient(hwnd, ref point);
        var scale = DpiScale();
        var xaml = new Point(point.X / scale, point.Y / scale);
        if (IsInsidePlaylist(VisualTreeHelper.FindElementsInHostCoordinates(xaml, ContentRoot, true).FirstOrDefault()))
        {
            return;
        }

        ShowAppMenuAt(ContentRoot, xaml);
    }

    private bool IsOverControl(Point xamlPoint)
    {
        if (ContentRoot.XamlRoot is null)
        {
            return false;
        }

        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(xamlPoint, ContentRoot, includeAllElements: true))
        {
            if (element is ButtonBase or Slider or Thumb or Controls.HandCursorHost || IsInsidePlaylist(element))
            {
                return true;
            }
        }

        return false;
    }

    private void ShowAppMenuAt(FrameworkElement target, Point position)
    {
        if ((DateTime.UtcNow - _appMenuShownAt).TotalMilliseconds < 80)
        {
            return;
        }

        ShowAppMenuAt(target, position, ChooseMenuPlacement(
            ContentRoot.ActualHeight - position.Y,
            position.Y));
    }

    private void ShowAppMenuAt(FrameworkElement target, Point position, FlyoutPlacementMode placement)
    {
        _appMenuShownAt = DateTime.UtcNow;
        _pendingMenuPlacement = placement;
        AppMenu.ShowAt(target, new FlyoutShowOptions
        {
            Position = position,
            Placement = placement,
            ShowMode = FlyoutShowMode.Standard
        });
    }

    private static FlyoutPlacementMode ChooseMenuPlacement(double spaceBelow, double spaceAbove)
    {
        const double menuHeight = 780;
        return spaceBelow < menuHeight && spaceAbove > spaceBelow
            ? FlyoutPlacementMode.Top
            : FlyoutPlacementMode.Bottom;
    }

    private void BeginWindowDrag(UIElement? source, PointerRoutedEventArgs e)
    {
        if (_videoFullscreen || Presenter.State == OverlappedPresenterState.Maximized)
        {
            return;
        }

        if (!GetCursorPos(out _dragMouse))
        {
            return;
        }

        _dragWindow = AppWindow.Position;
        _windowDrag = true;
        source?.CapturePointer(e.Pointer);
    }

    private void WindowDrag_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_windowDrag)
        {
            MoveDraggedWindow();
        }
    }

    private void WindowDrag_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_windowDrag)
        {
            return;
        }

        _windowDrag = false;
        if (sender is UIElement element)
        {
            try
            {
                element.ReleasePointerCapture(e.Pointer);
            }
            catch (Exception)
            {
            }
        }
    }

    private void MoveDraggedWindow()
    {
        if (!GetCursorPos(out var now))
        {
            return;
        }

        AppWindow.Move(new PointInt32(
            _dragWindow.X + now.X - _dragMouse.X,
            _dragWindow.Y + now.Y - _dragMouse.Y));
        RepositionActionOsd();
    }

    private static bool IsInteractive(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or Slider or Thumb or ListViewItem or MenuFlyout or MenuFlyoutItem or Controls.HandCursorHost)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInsidePlaylist(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, PlaylistPanel) ||
                ReferenceEquals(current, LocalPlaylistView) ||
                ReferenceEquals(current, StreamPlaylistView))
            {
                return true;
            }
        }

        return false;
    }

    private void VideoHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        WindowDrag_PointerMoved(sender, e);
        if (!IsInteractive(e.OriginalSource))
        {
            NoteVideoPointerActivity();
        }
    }

    private void VideoHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ShowVideoCursor();
    }

    private void PlaylistPanel_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ShowVideoCursor();
    }

    private void NoteVideoPointerActivity()
    {
        ShowVideoCursor();
        _cursorHideTimer?.Stop();
        _cursorHideTimer?.Start();
    }

    private void ShowVideoCursor()
    {
        _cursorHideTimer?.Stop();
        if (!_cursorHidden && !_surface.HideCursor)
        {
            return;
        }

        _cursorHidden = false;
        _surface.HideCursor = false;
        _surface.ApplyCursor();
    }

    private void HideVideoCursor()
    {
        _cursorHideTimer?.Stop();
        _cursorHidden = true;
        _surface.HideCursor = true;
        _surface.ApplyCursor();
    }

    private void OnSurfaceMouseMoved(int x, int y)
    {
        NoteVideoPointerActivity();
        if (!ChromeImmersive)
        {
            return;
        }

        var scale = ContentRoot.XamlRoot?.RasterizationScale ?? 1;
        var yDip = y / scale;
        var topEdge = Math.Max(56, TopBar.ActualHeight + 8);
        var bottomEdge = Math.Max(88, BottomBar.ActualHeight + 8);
        UpdateFullscreenChrome(yDip < topEdge, yDip > ContentRoot.ActualHeight - bottomEdge);
    }

    private void TopBar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ShowVideoCursor();
        if (!ChromeImmersive || !ChromeHoverReady)
        {
            return;
        }

        _pointerOverTopChrome = true;
        _chromeTimer?.Stop();
        SetChromeVisible(top: true, bottom: _bottomChromeVisible, animate: true);
    }

    private void TopBar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!ChromeImmersive)
        {
            return;
        }

        _pointerOverTopChrome = false;
        RestartChromeHideTimer();
    }

    private void BottomBar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ShowVideoCursor();
        if (!ChromeImmersive || !ChromeHoverReady)
        {
            return;
        }

        _pointerOverBottomChrome = true;
        _chromeTimer?.Stop();
        SetChromeVisible(top: _topChromeVisible, bottom: true, animate: true);
    }

    private void BottomBar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!ChromeImmersive)
        {
            return;
        }

        _pointerOverBottomChrome = false;
        RestartChromeHideTimer();
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private async void ContentRoot_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var paths = new List<string>();
        foreach (var item in items)
        {
            if (item is StorageFile file)
            {
                paths.Add(file.Path);
            }
            else if (item is StorageFolder folder)
            {
                foreach (var nested in await folder.GetFilesAsync())
                {
                    paths.Add(nested.Path);
                }
            }
        }

        var hadMedia = _view.HasMedia;
        var before = _view.Playlist.Count;
        _view.AcceptPaths(paths);
        ApplyView();
        ApplyVideoLayout();
        var added = _view.Playlist.Count - before;
        if (paths.Any(MediaFiles.IsSubtitle))
        {
            ShowActionFeedback("Subtitle attached");
        }
        else if (hadMedia && added > 0)
        {
            ShowActionFeedback(ActionFeedback.Added(added));
        }
    }

    private void PlaylistView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SelectPlaylistItemFrom(e.OriginalSource))
        {
            PlaySelectedPlaylistItem();
        }
    }

    private void PlaylistView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        SelectPlaylistItemFrom(e.OriginalSource);
    }

    private void PlaylistMenu_Opening(object sender, object e)
    {
        var enabled = ActivePlaylistView.SelectedIndex >= 0;
        if (PlaylistPlayItem is not null)
        {
            PlaylistPlayItem.IsEnabled = enabled;
        }

        if (PlaylistPlayNewItem is not null)
        {
            PlaylistPlayNewItem.IsEnabled = enabled;
        }

        var vod = enabled && SelectedPlaylistItem() is { } selected && DownloadManager.IsVod(selected);
        var anyVod = _view.Streams.Items.Any(DownloadManager.IsVod);
        if (StreamDownloadItem is not null)
        {
            StreamDownloadItem.IsEnabled = vod;
        }

        if (StreamDownloadAllItem is not null)
        {
            StreamDownloadAllItem.IsEnabled = anyVod;
        }

        if (sender is MenuFlyout flyout)
        {
            foreach (var item in flyout.Items.OfType<MenuFlyoutItem>())
            {
                if (item.Text is "Play" or "Play in new instance")
                {
                    item.IsEnabled = enabled;
                }

                if (item.Text == "Download")
                {
                    item.IsEnabled = vod;
                }

                if (item.Text == "Download all")
                {
                    item.IsEnabled = anyVod;
                }
            }
        }
    }

    private void PlaylistDownload_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlaylistItem() is not { } item || !DownloadManager.IsVod(item))
        {
            ShowActionFeedback("Download is for VOD only");
            return;
        }

        App.Downloads.Enqueue(
            item.Path,
            item.Title,
            start: true,
            item.AudioLang ?? _view.PreferredAudioLang,
            0,
            item.SkipCaptions ? "off" : item.SubLang ?? _view.PreferredSubLang,
            item.CaptionUrl,
            item.CaptionTracks);
        ShowDownloads();
        ShowActionFeedback("Downloading " + item.Title);
    }

    private void PlaylistDownloadAll_Click(object sender, RoutedEventArgs e)
    {
        var count = 0;
        foreach (var item in _view.Streams.Items.Where(DownloadManager.IsVod))
        {
            App.Downloads.Enqueue(
                item.Path,
                item.Title,
                start: false,
                item.AudioLang ?? _view.PreferredAudioLang,
                0,
                item.SkipCaptions ? "off" : item.SubLang ?? _view.PreferredSubLang,
                item.CaptionUrl,
                item.CaptionTracks);
            count++;
        }

        if (count == 0)
        {
            ShowActionFeedback("No VOD streams to download");
            return;
        }

        ShowDownloads();
        ShowActionFeedback(count == 1 ? "Queued 1 download" : "Queued " + count + " downloads");
    }

    private void Downloads_Click(object sender, RoutedEventArgs e) => ShowDownloads();

    private void ShowDownloads()
    {
        var created = _downloads is null;
        if (_downloads is null)
        {
            var player = WindowNative.GetWindowHandle(this);
            _downloads = new DownloadsWindow(player, _alwaysOnTop, App.Downloads);
            _downloads.Closed += (_, _) => _downloads = null;
        }

        _downloads.SetOpen(true);
        if (created)
        {
            var here = AppWindow.Position;
            _downloads.AppWindow.Move(new PointInt32(here.X + 72, here.Y + 80));
        }
    }

    private void PlaylistPlay_Click(object sender, RoutedEventArgs e) =>
        DispatcherQueue.TryEnqueue(PlaySelectedPlaylistItem);

    private void PlaylistPlayNew_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlaylistItem() is not { } item)
        {
            return;
        }

        var launch = CurrentLaunchArgs(item.Path);
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GrokPlayer.exe");
        var info = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in launch.ToArgumentList())
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            Process.Start(info);
            ShowActionFeedback("Play in new instance");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), ex.ToString());
            ShowActionFeedback("Could not open instance");
        }
    }

    private void PlaySelectedPlaylistItem()
    {
        var index = ActivePlaylistView.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        _view.PlayIndex(index);
    }

    private bool SelectPlaylistItemFrom(object? source)
    {
        var item = PlaylistItemFrom(source);
        if (item is null)
        {
            return ActivePlaylistView.SelectedIndex >= 0;
        }

        var index = _view.VisiblePlaylist.Items.ToList().FindIndex(entry => entry.Path == item.Path);
        if (index < 0)
        {
            return false;
        }

        ActivePlaylistView.SelectedIndex = index;
        return true;
    }

    private PlaylistItem? SelectedPlaylistItem()
    {
        var index = ActivePlaylistView.SelectedIndex;
        return index >= 0 && index < _view.VisiblePlaylist.Count ? _view.VisiblePlaylist.Items[index] : null;
    }

    private static PlaylistItem? PlaylistItemFrom(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement element && element.DataContext is PlaylistItem item)
            {
                return item;
            }
        }

        return null;
    }

    private InstanceLaunchArgs CurrentLaunchArgs(string path) => new()
    {
        Path = path,
        Volume = _view.Volume,
        Mute = _view.IsMuted,
        Loop = _view.Loop,
        AlwaysOnTop = _alwaysOnTop,
        Cinema = _cinemaMode,
        NewInstance = true
    };

    public void OpenFromExternal(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        AppWindow.Show();
        Activate();
        if (payload == "--activate") return;
        if (ExternalOpen.TryParse(payload, out var open))
        {
            _view.ShowStreamTab(true);
            _view.AddStream(
                payload.Trim().StartsWith("grokplayer:", StringComparison.OrdinalIgnoreCase) ? payload : open.Url,
                open.Play,
                open.Title,
                open.AudioLang,
                open.SubLang,
                open.Height);
            QueueApplyView();
            return;
        }

        if (UrlSanitizer.IsUrl(payload) || YouTubeCatalog.IsWatchUrl(payload))
        {
            _view.ShowStreamTab(true);
            _view.AddStream(payload, play: true);
            QueueApplyView();
            return;
        }

        _view.AcceptPaths([payload]);
        QueueApplyView();
    }

    private void ApplyLaunchWhenReady()
    {
        if (_launchApplied)
        {
            return;
        }

        _launchApplied = true;
        try
        {
            ApplyInstanceLaunch(_launchArgs);
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), ex.ToString());
            }
            catch
            {
            }
        }
    }

    private void ApplyInstanceLaunch(InstanceLaunchArgs launch)
    {
        _view.Volume = launch.Volume;
        _player.SetMuted(launch.Mute);
        _view.SetLoop(launch.Loop);
        if (launch.AlwaysOnTop)
        {
            SetAlwaysOnTop(true);
        }

        if (launch.Cinema)
        {
            SetCinemaMode(true);
        }

        if (!string.IsNullOrWhiteSpace(launch.Path))
        {
            OpenFromExternal(launch.Path);
        }

        ApplyView();
        ApplyVideoLayout();
    }

    private void ContentRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_windowDrag)
        {
            MoveDraggedWindow();
            return;
        }

        if (!ChromeImmersive)
        {
            return;
        }

        var y = e.GetCurrentPoint(ContentRoot).Position.Y;
        var topEdge = Math.Max(56, TopBar.ActualHeight + 8);
        var bottomEdge = Math.Max(88, BottomBar.ActualHeight + 8);
        UpdateFullscreenChrome(y < topEdge, y > ContentRoot.ActualHeight - bottomEdge);
    }

    private void ApplyView()
    {
        _syncingUi = true;
        try
        {
            SetText(TitleFormatText, _view.TitleFormat);
            SetText(TitleIndexText, _view.TitleIndex);
            SetText(TitleNameText, _view.TitleName);
            var hasFormat = !string.IsNullOrWhiteSpace(_view.TitleFormat);
            var hasIndex = !string.IsNullOrWhiteSpace(_view.TitleIndex);
            var hasName = !string.IsNullOrWhiteSpace(_view.TitleName);
            SetVisible(TitleFormatText, hasFormat);
            SetVisible(SepFormat, hasFormat);
            SetVisible(TitleIndexText, hasIndex);
            SetVisible(SepIndex, hasIndex);
            SetVisible(TitleNameText, hasName);
            SetVisible(SepName, hasName);
            ApplyPlaylistChrome();
            var opening = _player.State == PlayerState.Opening;
            if (!opening && PlayPauseButton.IsEnabled != _view.CanTogglePlayback)
            {
                PlayPauseButton.IsEnabled = _view.CanTogglePlayback;
            }

            if (!opening && PlayPauseIcon.Glyph != _view.PlayPauseGlyph)
            {
                PlayPauseIcon.Glyph = _view.PlayPauseGlyph;
            }

            if (_view.HoldsTransport)
            {
                HideSeekPreview();
                _flyout.Clear();
                _previewUi?.Reset();
                if (SeekSlider.Minimum != 0)
                {
                    SeekSlider.Minimum = 0;
                }

                if (Math.Abs(SeekSlider.Maximum - 1) > 0.0001)
                {
                    SeekSlider.Maximum = 1;
                }

                if (SeekSlider.Value != 0)
                {
                    SeekSlider.Value = 0;
                }

                SeekSlider.IsEnabled = false;
                _surface.Hide();
            }
            else if (!opening)
            {
                _surface.Show();
                var origin = Math.Max(0, _view.SeekOrigin);
                var maximum = Math.Max(origin + 0.001, _view.SeekMaximum);
                if (Math.Abs(SeekSlider.Minimum - origin) > 0.0001)
                {
                    SeekSlider.Minimum = origin;
                }

                if (Math.Abs(SeekSlider.Maximum - maximum) > 0.0001)
                {
                    SeekSlider.Maximum = maximum;
                }

                if (SeekSlider.IsEnabled != _view.CanSeek)
                {
                    SeekSlider.IsEnabled = _view.CanSeek;
                }

                PrefetchLiveWindow();
            }

            if (!_view.IsSeeking && !_view.HoldsTransport)
            {
                var seek = _view.ClampSeek(_view.SeekValue);
                if (Math.Abs(SeekSlider.Value - seek) > 0.04)
                {
                    SeekSlider.Value = seek;
                }
            }

            SetText(PositionTimeText, _view.PositionText);
            SetText(DurationTimeText, _view.DurationText);
            if (Math.Abs(VolumeSlider.Value - _view.Volume) > 0.4)
            {
                VolumeSlider.Value = _view.Volume;
            }

            if (VolumeIcon.Glyph != _view.VolumeGlyph)
            {
                VolumeIcon.Glyph = _view.VolumeGlyph;
            }

            if (LoopIcon.Glyph != _view.LoopGlyph)
            {
                LoopIcon.Glyph = _view.LoopGlyph;
            }

            LoopIcon.Foreground = _view.LoopIsActive
                ? (Brush)Application.Current.Resources["GrokAccentBrush"]
                : (Brush)Application.Current.Resources["GrokMutedBrush"];
            LoopIcon.Opacity = _view.LoopIsActive ? 1 : 0.45;

            LoopButton.SetValue(ToolTipService.ToolTipProperty, _view.LoopLabel);
            // Audio / subtitle selection lives in the video context menu.
            ApplyLiveChrome();
            ArmLivePreviewHarvest();
            if (!string.IsNullOrWhiteSpace(_view.StoryboardSpec))
            {
                EnsurePreview();
                BindPreviewAtlas();
            }

            PaintPlaylistTabs();
            SetVisible(EmptyState, _view.ShowEmptyState && !_view.IsLoading);
            if (!_view.HoldsTransport && !_view.IsLoading)
            {
                _surface.Show();
            }

            SetVisible(LoadOverlay, _view.IsLoading);
            if (_view.IsLoading)
            {
                EnsureLoadSpin();
            }

            ApplyVideoLayout();
            var maxGlyph = Presenter.State == OverlappedPresenterState.Maximized ? "\uE923" : "\uE922";
            if (MaxIcon.Glyph != maxGlyph)
            {
                MaxIcon.Glyph = maxGlyph;
            }

            var fullGlyph = _videoFullscreen ? "\uE73F" : "\uE740";
            if (FullIcon.Glyph != fullGlyph)
            {
                FullIcon.Glyph = fullGlyph;
            }

            CinemaIcon.Foreground = _cinemaMode
                ? (Brush)Application.Current.Resources["GrokAccentBrush"]
                : (Brush)Application.Current.Resources["GrokMutedBrush"];
            CinemaButton.SetValue(ToolTipService.ToolTipProperty, _cinemaMode ? "Show interface" : "Hide interface");
            UpdateAbMarks();
        }
        finally
        {
            _syncingUi = false;
        }
    }

    private void ApplyPlaylistPane()
    {
        var show = _view.PlaylistVisible && !ChromeImmersive;

        var maximized = Presenter.State == OverlappedPresenterState.Maximized;
        if (show && !_playlistGrown && !maximized && !ChromeImmersive)
        {
            _playlistGrown = true;
            _playlistSizePending = true;
            var extra = PlaylistPixels();
            AppWindow.Resize(new SizeInt32(AppWindow.Size.Width + extra, AppWindow.Size.Height));
            return;
        }

        if (!show && _playlistGrown && !maximized && !ChromeImmersive)
        {
            HidePlaylistColumn();
            var extra = PlaylistPixels();
            AppWindow.Resize(new SizeInt32(Math.Max(MinWindowPixels(), AppWindow.Size.Width - extra), AppWindow.Size.Height));
            _playlistGrown = false;
            _playlistSizePending = false;
            ApplyVideoLayout();
            UpdateInputRegions();
            return;
        }

        if (show)
        {
            ShowPlaylistColumn();
        }
        else
        {
            HidePlaylistColumn();
        }

        ApplyVideoLayout();
        UpdateInputRegions();
    }

    private void ShowPlaylistColumn()
    {
        if (Math.Abs(PlaylistColumn.Width.Value - PlaylistPaneWidth) > 0.1)
        {
            PlaylistColumn.Width = new GridLength(PlaylistPaneWidth);
        }

        PlaylistPanel.Visibility = Visibility.Visible;
        if (PlaylistPanel.Opacity < 1)
        {
            FadePlaylist(1);
        }
    }

    private void HidePlaylistColumn()
    {
        PlaylistColumn.Width = new GridLength(0);
        PlaylistPanel.Opacity = 0;
        PlaylistPanel.Visibility = Visibility.Collapsed;
    }

    private void FadePlaylist(double to)
    {
        var fade = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, PlaylistPanel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var board = new Storyboard();
        board.Children.Add(fade);
        board.Begin();
    }

    private int PlaylistPixels() => Math.Max(1, (int)Math.Round(PlaylistPaneWidth * DpiScale()));

    private int MinWindowPixels() => Math.Max(1, (int)Math.Round(840 * DpiScale()));

    private double DpiScale()
    {
        if (ContentRoot.XamlRoot is { RasterizationScale: > 0 } root)
        {
            return root.RasterizationScale;
        }

        var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        return dpi > 0 ? dpi / 96.0 : 1;
    }

    private void SetVideoFullscreen(bool value)
    {
        if (_videoFullscreen == value)
        {
            return;
        }

        if (value)
        {
            _wasMaximizedBeforeFullscreen = Presenter.State == OverlappedPresenterState.Maximized;
            if (!_wasMaximizedBeforeFullscreen)
            {
                RememberWindowedBounds();
            }
            else
            {
                Presenter.Restore();
            }

            _videoFullscreen = true;
            _enteringFullscreen = true;
            ArmChromeHover();
            PlaylistColumn.Width = new GridLength(0);
            PlaylistPanel.Visibility = Visibility.Collapsed;
            SetChromeVisible(top: false, bottom: false, animate: false);
            Presenter.SetBorderAndTitleBar(false, false);
            Presenter.Maximize();
        }
        else
        {
            _videoFullscreen = false;
            StopChromeTimers();
            _pointerOverTopChrome = false;
            _pointerOverBottomChrome = false;
            if (_cinemaMode)
            {
                _chromeArmed = false;
                SetChromeVisible(top: false, bottom: false, animate: false);
            }
            else
            {
                SetChromeVisible(top: true, bottom: true, animate: false);
            }
            Presenter.Restore();
            Presenter.SetBorderAndTitleBar(true, false);
            if (_windowedBounds is { } bounds)
            {
                AppWindow.MoveAndResize(bounds);
            }

            if (_wasMaximizedBeforeFullscreen)
            {
                Presenter.Maximize();
            }

            ApplyPlaylistPane();
        }

        ApplyVideoLayout();
        ApplyView();
    }

    private void SetCinemaMode(bool value)
    {
        if (_cinemaMode == value)
        {
            return;
        }

        _cinemaMode = value;
        _pointerOverTopChrome = false;
        _pointerOverBottomChrome = false;
        if (value)
        {
            ArmChromeHover();
            HidePlaylistColumn();
            SetChromeVisible(top: false, bottom: false, animate: true);
        }
        else if (!_videoFullscreen)
        {
            StopChromeTimers();
            SetChromeVisible(top: true, bottom: true, animate: true);
            ApplyPlaylistPane();
        }

        ApplyVideoLayout();
        ApplyView();
    }

    private void UpdateFullscreenChrome(bool overTop, bool overBottom)
    {
        if (DateTime.UtcNow < _chromeLockUntil)
        {
            return;
        }

        if (!_chromeArmed)
        {
            if (!overTop && !overBottom)
            {
                _chromeArmed = true;
            }

            return;
        }

        if (overTop)
        {
            SetChromeVisible(top: true, bottom: _bottomChromeVisible, animate: true);
        }

        if (overBottom)
        {
            SetChromeVisible(top: _topChromeVisible, bottom: true, animate: true);
        }

        RestartChromeHideTimer();
    }

    private void RestartChromeHideTimer()
    {
        if (!ChromeImmersive || !_chromeArmed || _pointerOverTopChrome || _pointerOverBottomChrome)
        {
            _chromeTimer?.Stop();
            return;
        }

        _chromeTimer?.Stop();
        _chromeTimer?.Start();
    }

    private void ChromeTimer_Tick(object? sender, object e)
    {
        _chromeTimer?.Stop();
        if (!ChromeImmersive || !_chromeArmed)
        {
            return;
        }

        if (GetCursorPos(out var cursor) &&
            ScreenToClient(WindowNative.GetWindowHandle(this), ref cursor))
        {
            var scale = ContentRoot.XamlRoot?.RasterizationScale ?? 1;
            var y = cursor.Y / scale;
            var overTop = y >= 0 && y <= Math.Max(56, TopBar.ActualHeight + 8);
            var overBottom = y <= ContentRoot.ActualHeight &&
                             y >= ContentRoot.ActualHeight - Math.Max(88, BottomBar.ActualHeight + 8);
            if (overTop || overBottom)
            {
                SetChromeVisible(
                    top: overTop || _pointerOverTopChrome,
                    bottom: overBottom || _pointerOverBottomChrome,
                    animate: true);
                return;
            }
        }

        SetChromeVisible(
            top: _pointerOverTopChrome,
            bottom: _pointerOverBottomChrome,
            animate: true);
    }

    private void StopChromeTimers()
    {
        _chromeTimer?.Stop();
    }

    private void SetChromeVisible(bool top, bool bottom, bool animate)
    {
        var changed = false;
        if (_topChromeVisible != top)
        {
            _topChromeVisible = top;
            AnimateChrome(TopBar, TopBarSlide, show: top, fromTop: true, animate);
            changed = true;
        }

        if (_bottomChromeVisible != bottom)
        {
            _bottomChromeVisible = bottom;
            AnimateChrome(BottomBar, BottomBarSlide, show: bottom, fromTop: false, animate);
            changed = true;
        }

        if (changed && !animate)
        {
            ApplyVideoLayout();
        }
    }

    private void AnimateChrome(UIElement bar, TranslateTransform slide, bool show, bool fromTop, bool animate)
    {
        bar.Visibility = Visibility.Visible;
        var height = fromTop
            ? Math.Max(40, TopBar.ActualHeight > 1 ? TopBar.ActualHeight : 40)
            : Math.Max(72, BottomBar.ActualHeight > 1 ? BottomBar.ActualHeight : 84);
        var hiddenOffset = fromTop ? -height : height;
        if (!animate)
        {
            bar.Opacity = show ? 1 : 0;
            slide.Y = show ? 0 : hiddenOffset;
            bar.IsHitTestVisible = show;
            SetCutout(fromTop, show ? MeasurePx(height) : 0);
            return;
        }

        var fromY = slide.Y;
        var toY = show ? 0 : hiddenOffset;
        var fromOpacity = bar.Opacity;
        var toOpacity = show ? 1 : 0;
        var fromCut = fromTop ? _topCutoutPx : _bottomCutoutPx;
        var toCut = show ? MeasurePx(height) : 0;
        var id = fromTop ? ++_topAnimId : ++_bottomAnimId;
        var start = DateTime.UtcNow;
        const double ms = 320;

        void Tick()
        {
            if ((fromTop ? _topAnimId : _bottomAnimId) != id)
            {
                return;
            }

            var t = Math.Clamp((DateTime.UtcNow - start).TotalMilliseconds / ms, 0, 1);
            var eased = t < 0.5 ? 4 * t * t * t : 1 - (Math.Pow(-2 * t + 2, 3) / 2);
            slide.Y = fromY + ((toY - fromY) * eased);
            bar.Opacity = fromOpacity + ((toOpacity - fromOpacity) * eased);
            SetCutout(fromTop, (int)Math.Round(fromCut + ((toCut - fromCut) * eased)));
            if (t < 1)
            {
                DispatcherQueue.TryEnqueue(Tick);
                return;
            }

            bar.IsHitTestVisible = show;
            SetCutout(fromTop, toCut);
        }

        Tick();
    }

    private void SetCutout(bool top, int pixels)
    {
        if (top)
        {
            _topCutoutPx = Math.Max(0, pixels);
        }
        else
        {
            _bottomCutoutPx = Math.Max(0, pixels);
        }

        if (ChromeImmersive)
        {
            _surface.SetOverlayCutouts(_topCutoutPx, _bottomCutoutPx);
        }
    }

    private void ApplyVideoLayout()
    {
        var showSurface = !_view.IsLoading && !_view.ShowEmptyState && _player.HasVideo;
        if (showSurface)
        {
            _surface.Show();
        }
        else
        {
            _surface.Hide();
        }

        if (ChromeImmersive)
        {
            SetMarginIfChanged(VideoHost, new Thickness(0));
            if (!_topChromeVisible)
            {
                _topCutoutPx = 0;
            }
            else if (_topCutoutPx <= 0)
            {
                _topCutoutPx = MeasurePx(TopBar.ActualHeight > 1 ? TopBar.ActualHeight : 40);
            }

            if (!_bottomChromeVisible)
            {
                _bottomCutoutPx = 0;
            }
            else if (_bottomCutoutPx <= 0)
            {
                _bottomCutoutPx = MeasurePx(BottomBar.ActualHeight > 1 ? BottomBar.ActualHeight : 84);
            }

            _surface.SetOverlayCutouts(_topCutoutPx, _bottomCutoutPx);
        }
        else
        {
            var top = TopBar.ActualHeight > 1 ? TopBar.ActualHeight : 40;
            var bottom = BottomBar.ActualHeight > 1 ? BottomBar.ActualHeight : 84;
            SetMarginIfChanged(VideoHost, new Thickness(0, top, 0, bottom));
            _surface.SetOverlayCutouts(0, 0);
        }

        SyncVideoSurface();
        _view.RefreshPushedResize();
    }

    private VideoResizeLayout ReadResizeLayout()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var (displayW, displayH) = DisplayMonitor.SizeFromWindow(hwnd);
        var scale = VideoHost.XamlRoot?.RasterizationScale ?? 1;
        var playerW = Math.Max(1, (int)Math.Round((VideoHost.ActualWidth > 1 ? VideoHost.ActualWidth : 1) * scale));
        var playerH = Math.Max(1, (int)Math.Round((VideoHost.ActualHeight > 1 ? VideoHost.ActualHeight : 1) * scale));
        return new VideoResizeLayout(playerW, playerH, displayW, displayH);
    }

    private void UpdateInputRegions()
    {
        if (ContentRoot.XamlRoot is null || MaxButton.XamlRoot is null)
        {
            return;
        }

        try
        {
            var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            source.SetRegionRects(NonClientRegionKind.Caption, [PixelRect(ContentRoot)]);
            var pass = new List<RectInt32>();
            foreach (var element in DragPassthroughElements())
            {
                if (element.Visibility == Visibility.Visible &&
                    element.IsHitTestVisible &&
                    element.ActualWidth > 1 &&
                    element.ActualHeight > 1)
                {
                    pass.Add(PixelRect(element));
                }
            }

            source.SetRegionRects(NonClientRegionKind.Passthrough, [.. pass]);
            source.SetRegionRects(NonClientRegionKind.Maximize, [PixelRect(MaxButton)]);
        }
        catch (Exception)
        {
        }
    }

    private IEnumerable<FrameworkElement> DragPassthroughElements()
    {
        yield return BrandButton;
        yield return TopBar;
        yield return BottomBar;
        yield return PinButton;
        yield return CinemaButton;
        yield return MinButton;
        yield return MaxButton;
        yield return FullButton;
        yield return CloseButton;
        yield return SeekSlider;
        yield return VolumeSlider;
        yield return MuteButton;
        yield return PlayPauseButton;
        yield return StopButton;
        yield return RewindButton;
        yield return ForwardButton;
        yield return OpenButton;
        yield return LoopButton;
        yield return ControlPanelButton;
        yield return PlaylistButton;
        yield return PositionTimeHost;
        yield return VideoHost;
        if (PlaylistPanel.Visibility == Visibility.Visible)
        {
            yield return PlaylistPanel;
        }
    }

    private RectInt32 PixelRect(FrameworkElement element)
    {
        var scale = element.XamlRoot?.RasterizationScale ?? 1;
        var origin = element.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var pad = ReferenceEquals(element, BottomBar) ? (int)Math.Round(10 * scale) : 0;
        return new RectInt32(
            (int)Math.Round(origin.X * scale),
            (int)Math.Round(origin.Y * scale) - (pad / 2),
            Math.Max(1, (int)Math.Round(element.ActualWidth * scale)),
            Math.Max(1, (int)Math.Round(element.ActualHeight * scale) + pad));
    }

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * DpiScale()));

    private int MeasurePx(double dips)
    {
        var scale = ContentRoot.XamlRoot?.RasterizationScale ?? 1;
        return Math.Max(0, (int)Math.Round(dips * scale));
    }

    private void RememberWindowedBounds()
    {
        if (_videoFullscreen || Presenter.State != OverlappedPresenterState.Restored)
        {
            return;
        }

        _windowedBounds = new RectInt32(
            AppWindow.Position.X,
            AppWindow.Position.Y,
            AppWindow.Size.Width,
            AppWindow.Size.Height);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_enteringFullscreen)
        {
            if (Presenter.State == OverlappedPresenterState.Maximized)
            {
                _enteringFullscreen = false;
            }

            SyncVideoSurface();
            RepositionActionOsd();
            return;
        }

        if (_videoFullscreen && Presenter.State != OverlappedPresenterState.Maximized)
        {
            SetVideoFullscreen(false);
            return;
        }

        if (_playlistSizePending && args.DidSizeChange)
        {
            _playlistSizePending = false;
            ShowPlaylistColumn();
            ApplyVideoLayout();
        }

        if (args.DidSizeChange || args.DidPositionChange)
        {
            RememberWindowedBounds();
            SyncVideoSurface();
            UpdateInputRegions();
            RepositionActionOsd();
            if (args.DidPositionChange)
            {
                _view.RefreshPushedResize();
            }
        }
    }

    private bool _loadSpinArmed;

    private void EnsureLoadSpin()
    {
        if (_loadSpinArmed)
        {
            return;
        }

        _loadSpinArmed = true;
        var board = new Storyboard();
        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(1.15)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(spin, LoadSpin);
        Storyboard.SetTargetProperty(spin, "Angle");
        board.Children.Add(spin);

        var inner = new DoubleAnimation
        {
            From = 360,
            To = 0,
            Duration = new Duration(TimeSpan.FromSeconds(1.7)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(inner, LoadSpinInner);
        Storyboard.SetTargetProperty(inner, "Angle");
        board.Children.Add(inner);

        var pulse = new DoubleAnimation
        {
            From = 0.96,
            To = 1.06,
            Duration = new Duration(TimeSpan.FromSeconds(0.9)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(pulse, LoadPulse);
        Storyboard.SetTargetProperty(pulse, "ScaleX");
        board.Children.Add(pulse);
        var pulseY = new DoubleAnimation
        {
            From = 0.96,
            To = 1.06,
            Duration = new Duration(TimeSpan.FromSeconds(0.9)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(pulseY, LoadPulse);
        Storyboard.SetTargetProperty(pulseY, "ScaleY");
        board.Children.Add(pulseY);

        var glow = new DoubleAnimation
        {
            From = 0.45,
            To = 1,
            Duration = new Duration(TimeSpan.FromSeconds(0.9)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(glow, LoadCore);
        Storyboard.SetTargetProperty(glow, "Opacity");
        board.Children.Add(glow);
        board.Begin();
    }

    private void EnsurePreview()
    {
        if (_previewWork is not null)
        {
            return;
        }

        try
        {
            var engine = SeekPreviewEngine.Create();
            _previewUi = new SeekPreviewController(new NoopRenderer());
            _previewWork = new SeekPreviewScheduler(engine);
            _previewWork.FrameReady += (time, path) =>
            {
                var generation = _previewGeneration;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (generation != _previewGeneration || _previewUi?.Current.IsVisible != true)
                    {
                        return;
                    }

                    var ready = MapPreviewTime(_previewUi.Current);
                    var allowedDelta = _view.IsLive
                        ? 1.5
                        : _previewAtlas is not null ? 0.15 : SeekPreviewDisplay.DecoderDeltaSeconds;
                    if (!SeekPreviewDisplay.Fits(time, ready.Time, allowedDelta))
                    {
                        return;
                    }

                    if (!LivePlayback.IsUsableStill(path))
                    {
                        return;
                    }

                    _previewUi.RememberImage(path);
                    ShowFlyout(ready with { ImagePath = path });
                });
            };
        }
        catch (Exception)
        {
            _previewWork = null;
        }
    }

    private void UpdatePreviewFromPointer(PointerRoutedEventArgs e)
    {
        EnsurePreview();
        if (_previewUi is null || !_view.CanSeek || PreviewWindow() is not { } window)
        {
            HideSeekPreview();
            return;
        }

        BindPreviewMedia(window);
        var x = e.GetCurrentPoint(SeekSlider).Position.X;
        var state = MapPreviewTime(_previewUi.Move(x, SeekSlider.ActualWidth));
        _pointerSeek = state.Time.TotalSeconds;
        _pendingSeek = _pointerSeek;
        ShowLiveAwarePreview(state);
    }

    private void UpdatePreviewFromTime(double seconds)
    {
        if (_previewUi is null || !_view.CanSeek || PreviewWindow() is not { } window)
        {
            return;
        }

        BindPreviewMedia(window);
        var relative = TimeSpan.FromSeconds(Math.Max(0, seconds - _view.SeekOrigin));
        var x = SeekBarMath.OffsetForTime(relative, window, SeekSlider.ActualWidth);
        var state = MapPreviewTime(_previewUi.Move(x, SeekSlider.ActualWidth));
        ShowLiveAwarePreview(state);
    }

    private TimeSpan? PreviewWindow()
    {
        if (_view.IsLive)
        {
            var span = _view.SeekMaximum - _view.SeekOrigin;
            return span > 0 ? TimeSpan.FromSeconds(span) : null;
        }

        return _player.Duration is { } duration && duration > TimeSpan.Zero ? duration : null;
    }

    private void BindPreviewMedia(TimeSpan window)
    {
        var path = _player.MediaPath;
        var key = PreviewMediaKey();
        if (!string.Equals(key, _previewMediaKey, StringComparison.Ordinal))
        {
            ResetPreview(path);
        }

        BindPreviewAtlas();
        _previewUi?.SetMedia(path, window);
        var decoderVod = !string.IsNullOrWhiteSpace(path) &&
                         !_view.IsLive &&
                         _previewAtlas is null &&
                         !YouTubeCatalog.IsWatchUrl(_view.VisiblePlaylist.CurrentPath) &&
                         !YouTubeCatalog.TryReadVideoId(_view.VisiblePlaylist.CurrentPath ?? path, out _);
        if (decoderVod && !_previewCoverageAttached && _previewWork is not null)
        {
            _previewCoverageAttached = true;
            _previewWork.AttachCoverage(SeekPreviewEngine.Create());
        }

        _previewWork?.SetMedia(
            path,
            window,
            prefetch: decoderVod,
            referer: _view.PlayingReferer);
        _previewWork?.SetAtlas(_previewAtlas);
        if (decoderVod)
        {
            _previewWork?.Warm(path);
        }
    }

    private string PreviewMediaKey()
    {
        if (YouTubeCatalog.TryReadVideoId(_view.VisiblePlaylist.CurrentPath ?? _player.MediaPath, out var id))
        {
            return "yt|" + id;
        }

        return _player.MediaPath ?? "";
    }

    private void ResetPreview(string? path)
    {
        _previewGeneration++;
        _liveCachePreviews.Reset();
        _liveCoveragePreviews.Reset();
        _livePreviews.Reset();
        _previewMediaKey = PreviewMediaKey();
        _previewAtlas?.Dispose();
        _previewAtlas = null;
        _previewAtlasSpec = null;
        _previewUi?.Reset();
        _previewWork?.SetAtlas(null);
        if (!string.IsNullOrWhiteSpace(path))
        {
            _previewWork?.SetMedia(path, null, prefetch: false);
        }

        _flyout.Clear();
    }

    private void BindPreviewAtlas()
    {
        var spec = _view.IsLive ? null : _view.StoryboardSpec;
        var key = PreviewMediaKey() + "|" + (spec ?? "");
        if (string.Equals(key, _previewAtlasSpec, StringComparison.Ordinal))
        {
            return;
        }

        _previewAtlas?.Dispose();
        _previewAtlas = string.IsNullOrWhiteSpace(spec)
            ? null
            : spec.StartsWith("webvtt:", StringComparison.OrdinalIgnoreCase)
                ? WebVttPreviewAtlas.TryCreate(spec[7..], _view.PlayingReferer)
                : StoryboardAtlas.TryCreate(spec, PreviewWindow());
        _previewAtlasSpec = key;
        _previewWork?.SetAtlas(_previewAtlas);
        if (_previewAtlas is { } atlas)
        {
            var position = _player.Position;
            var generation = _previewGeneration;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (generation != _previewGeneration)
                {
                    return;
                }

                try
                {
                    atlas.Prefetch(position);
                    atlas.PrefetchCoverage();
                }
                catch (Exception)
                {
                }
            });
        }
    }

    private void PrefetchLiveWindow()
    {
        if (!_view.IsLive || string.IsNullOrWhiteSpace(_player.MediaPath) ||
            _player.PreviewLiveEdgeSeconds() is not { } liveEdge) return;
        _liveCoveragePreviews.Start(
            _player.MediaPath,
            liveEdge,
            LivePlayback.DvrKeepSeconds + 4);
    }

    private void ShowLiveAwarePreview(SeekPreviewState state)
    {
        if (!state.IsVisible)
        {
            HideSeekPreview();
            return;
        }

        // Live: no future. The available window is DVR start → live edge.
        if (_view.IsLive && state.Time.TotalSeconds > _view.SeekMaximum + 0.05)
        {
            HideSeekPreview();
            return;
        }

        var media = _player.MediaPath ?? "";
        var decodedLive = _view.IsLive
            ? _previewWork?.GetCached(state.Time, maxDeltaSeconds: 2)
            : null;
        var cached = _view.IsLive
            ? decodedLive ?? _livePreviews.GetFrame(state.Time)
            : _previewWork?.GetCached(state.Time, PreviewMaxDelta());
        if (cached is not null)
        {
            _previewUi?.RememberImage(cached);
            state = state with { ImagePath = cached };
        }
        else
        {
            state = state with { ImagePath = null };
        }

        if (_view.IsLive)
        {
            // A background coverage frame is intentionally cheap and blurry.
            // Keep it visible immediately, but always upgrade the hovered point
            // into the high rendition unless that decoded frame is already cached.
            if (cached is null) _liveCachePreviews.Request(state.Time);
            if (decodedLive is null && _player.PreviewLiveEdgeSeconds() is { } liveEdge)
            {
                _previewWork?.RequestLiveExact(
                    media,
                    state.Time,
                    Math.Max(0, liveEdge - state.Time.TotalSeconds));
            }
        }
        else
        {
            _previewWork?.Request(media, state.Time);
        }

        ShowFlyout(state);
    }

    private void OnLiveCachePreviewReady(object? sender, TimeSpan time)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || !_view.IsLive || _previewUi?.Current is not { IsVisible: true } current) return;
            var state = MapPreviewTime(_previewUi.Move(
                current.NormalizedPosition * SeekSlider.ActualWidth, SeekSlider.ActualWidth));
            if (Math.Abs((state.Time - time).TotalSeconds) <= 2) ShowLiveAwarePreview(state);
        });
    }

    private void OnLiveCoverageReady(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || !_view.IsLive || _previewUi?.Current is not { IsVisible: true } current) return;
            var state = MapPreviewTime(_previewUi.Move(
                current.NormalizedPosition * SeekSlider.ActualWidth, SeekSlider.ActualWidth));
            ShowLiveAwarePreview(state);
        });
    }

    private double PreviewMaxDelta()
    {
        if (_view.IsLive)
        {
            return 3.0;
        }

        if (_previewAtlas is not null)
        {
            return Math.Max(10, _previewAtlas.IntervalSeconds + 1);
        }

        return SeekPreviewDisplay.DecoderDeltaSeconds;
    }

    private SeekPreviewState MapPreviewTime(SeekPreviewState state)
    {
        if (!_view.IsLive || !state.IsVisible)
        {
            return state;
        }

        var absolute = TimeSpan.FromSeconds(_view.SeekOrigin + state.Time.TotalSeconds);
        return state with
        {
            Time = absolute,
            TimeText = TimeDisplay.FormatClock(absolute)
        };
    }

    private void ArmLivePreviewHarvest()
    {
        if (_view.IsLive && _view.IsPlaying && !_view.IsSeeking && !_view.IsLoading && _player.HasMedia)
        {
            if (_livePreviewHarvest is null)
            {
                _livePreviewHarvest = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _livePreviewHarvest.Tick += (_, _) => HarvestLivePreview();
            }

            if (!_livePreviewHarvest.IsEnabled)
            {
                _livePreviewHarvest.Start();
            }

            return;
        }

        _livePreviewHarvest?.Stop();
    }

    private async void HarvestLivePreview()
    {
        if (_closing || _tornDown || !_view.IsLive || !_view.IsPlaying || _view.IsSeeking || _view.IsLoading)
        {
            return;
        }

        EnsurePreview();
        if (PreviewWindow() is not { } window) return;
        BindPreviewMedia(window);
        var generation = _previewGeneration;
        var captured = await _livePreviews.CaptureAsync(
            file => _player.TryCaptureVideo(file, includeWindow: false), () => _player.Position);
        if (!captured || _closing || _tornDown || generation != _previewGeneration || !_view.IsLive) return;

        // Update a stationary hover as soon as the playing decoder supplies a
        // nearby frame. No pointer movement or second HLS decoder is required.
        if (_previewUi?.Current is { IsVisible: true } current && PreviewWindow() is { } updatedWindow)
        {
            BindPreviewMedia(updatedWindow);
            var state = _previewUi.Move(current.NormalizedPosition * SeekSlider.ActualWidth, SeekSlider.ActualWidth);
            ShowLiveAwarePreview(MapPreviewTime(state));
        }
    }

    private void ShowFlyout(SeekPreviewState state)
    {
        if (!state.IsVisible)
        {
            HideSeekPreview();
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = ContentRoot.XamlRoot?.RasterizationScale ?? 1;
        var slider = SeekSlider.TransformToVisual(ContentRoot).TransformPoint(new Point(0, 0));
        var hoverX = slider.X + (state.NormalizedPosition * SeekSlider.ActualWidth);
        var hoverY = slider.Y;
        var image = LivePlayback.IsUsableStill(state.ImagePath) ? state.ImagePath : null;
        var timeText = PreviewClock.Text(_view.IsLive, state.Time);
        const int dipW = PreviewFlyout.DipWidth;
        const int dipH = PreviewFlyout.DipHeight;
        var point = new Point32((int)Math.Round(hoverX * scale), (int)Math.Round(hoverY * scale));
        ClientToScreen(hwnd, ref point);
        var pixelW = Math.Max(1, (int)Math.Round(dipW * scale));
        var pixelH = Math.Max(1, (int)Math.Round(dipH * scale));
        var clientOrigin = new Point32(0, 0);
        ClientToScreen(hwnd, ref clientOrigin);
        GetClientRect(hwnd, out var clientRect);
        var margin = Math.Max(4, (int)Math.Round(8 * scale));
        var minX = clientOrigin.X + margin;
        var maxX = clientOrigin.X + clientRect.Right - pixelW - margin;
        var x = Math.Clamp(point.X - (pixelW / 2), minX, Math.Max(minX, maxX));
        var y = Math.Max(clientOrigin.Y + margin, point.Y - pixelH - margin);
        _flyout.Show(
            timeText,
            image,
            x,
            y,
            scale,
            holdPreviousImage: !_view.IsLive && image is not null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
        public Point32(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref Point32 lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out Rect32 lpRect);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hWnd, ref Point32 lpPoint);

    private void HideSeekPreview()
    {
        _previewUi?.Hide();
        _flyout.Hide();
    }

    private void SyncVideoSurface()
    {
        if (VideoHost.XamlRoot is null || VideoHost.ActualWidth <= 0 || VideoHost.ActualHeight <= 0)
        {
            return;
        }

        var scale = VideoHost.XamlRoot.RasterizationScale;
        var origin = VideoHost.TransformToVisual(ContentRoot).TransformPoint(new Point(0, 0));
        _surface.Move(
            (int)Math.Round(origin.X * scale),
            (int)Math.Round(origin.Y * scale),
            Math.Max(1, (int)Math.Round(VideoHost.ActualWidth * scale)),
            Math.Max(1, (int)Math.Round(VideoHost.ActualHeight * scale)));
    }

    private void AnnounceSkip(TimeSpan delta)
    {
        ShowActionFeedback(ActionFeedback.Skip(delta));
    }

    private void ImageAdjust_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = HandleImageAdjust(sender.Key);
    }

    private bool HandleImageAdjust(VirtualKey key) => HandleImageAdjust(unchecked((int)key));

    private bool HandleImageAdjust(int key)
    {
        return key switch
        {
            0x31 or 0x61 => _view.NudgeImageWidth(1),
            0x32 or 0x62 => _view.NudgeImageWidth(-1),
            0x33 or 0x63 => _view.NudgeImageHeight(1),
            0x34 or 0x64 => _view.NudgeImageHeight(-1),
            0x35 or 0x65 => _view.ResetImageAdjust(),
            _ => false
        };
    }

    private void ShowActionFeedback(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _lastFeedback = message;
        PlaceActionOsd(message);
        _feedbackTimer?.Stop();
        _feedbackTimer?.Start();
        _osdFollowTimer?.Start();
    }

    private void RepositionActionOsd()
    {
        if (_lastFeedback is not null)
        {
            PlaceActionOsd(_lastFeedback);
        }
    }

    private void PlaceActionOsd(string message)
    {
        if (VideoHost.XamlRoot is null)
        {
            return;
        }

        var scale = VideoHost.XamlRoot.RasterizationScale;
        var origin = VideoHost.TransformToVisual(ContentRoot).TransformPoint(new Point(0, 0));
        var point = new Point32(
            (int)Math.Round((origin.X + 8) * scale),
            (int)Math.Round((origin.Y + 8) * scale));
        ClientToScreen(WindowNative.GetWindowHandle(this), ref point);
        try
        {
            _actionOsd.Show(message, point.X, point.Y, scale);
        }
        catch (Exception)
        {
        }
    }

    private static void SetText(TextBlock block, string value)
    {
        if (block.Text != value)
        {
            block.Text = value;
        }
    }

    private static void SetVisible(UIElement element, bool visible)
    {
        var value = visible ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != value)
        {
            element.Visibility = value;
        }
    }

    private static void SetMarginIfChanged(FrameworkElement element, Thickness margin)
    {
        if (element.Margin != margin)
        {
            element.Margin = margin;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_tornDown)
        {
            return;
        }

        args.Cancel = true;
        RequestClose();
    }

    private void RequestClose()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        try { _cursorHideTimer?.Stop(); } catch (Exception) { }
        try { _livePreviewHarvest?.Stop(); } catch (Exception) { }
        try { HideSeekPreview(); } catch (Exception) { }
        try { _actionOsd.Dispose(); } catch (Exception) { }
        try { _flyout.Dispose(); } catch (Exception) { }
        try { _player.DetachSurface(); } catch (Exception) { }
        try { _surface.Hide(); } catch (Exception) { }
        try { AppWindow.Hide(); } catch (Exception) { }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                TeardownPlayer();
            }
            catch (Exception)
            {
            }

            Environment.Exit(0);
        });
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(4000);
            Environment.Exit(0);
        });
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        WindowChrome.TryHandleContextMenu = null;
        WindowChrome.AfterPlayerRaised = null;
        try
        {
            ShowVideoCursor();
        }
        catch (Exception)
        {
        }

        CloseOwned(_controlPanel, () => _controlPanel = null);
        CloseOwned(_subtitleBrowser, () => _subtitleBrowser = null);
        CloseOwned(_preferences, () => _preferences = null);
        CloseOwned(_downloads, () => _downloads = null);
        CloseOwned(_devices, () => _devices = null);
        _link?.Dispose();
        if (!_closing)
        {
            TeardownPlayer();
        }
    }

    private void TeardownPlayer()
    {
        if (_tornDown)
        {
            return;
        }

        _tornDown = true;
        try { _cursorHideTimer?.Stop(); } catch (Exception) { }
        try { _livePreviewHarvest?.Stop(); } catch (Exception) { }
        try { HideSeekPreview(); } catch (Exception) { }
        try { _previewWork?.Dispose(); } catch (Exception) { }
        try { _liveCachePreviews.Dispose(); } catch (Exception) { }
        try { _liveCoveragePreviews.Dispose(); } catch (Exception) { }
        try { _livePreviews.Dispose(); } catch (Exception) { }
        try { _previewAtlas?.Dispose(); } catch (Exception) { }
        try { _actionOsd.Dispose(); } catch (Exception) { }
        try { _flyout.Dispose(); } catch (Exception) { }
        try { _view.Dispose(); } catch (Exception) { }
        try { _player.DetachSurface(); } catch (Exception) { }
        try { _player.Dispose(); } catch (Exception) { }
        try { _surface.Dispose(); } catch (Exception) { }
    }

    private static void CloseOwned(Window? window, Action clear)
    {
        if (window is null)
        {
            return;
        }

        clear();
        try
        {
            window.Close();
        }
        catch (Exception)
        {
        }
    }

    private sealed class NoopRenderer : ISeekPreviewRenderer
    {
        public void Prepare(string path) { }
        public string? Capture(TimeSpan time) => null;
        public void Reset() { }
        public void Dispose() { }
    }
}
