using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Grok.Player.App.Native;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Subtitles;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Grok.Player.App;

public sealed partial class SubtitleBrowserWindow : Window
{
    public const int WidthPx = 980;
    public const int HeightPx = 560;
    public const int MinWidthPx = 720;
    public const int MinHeightPx = 420;

    private readonly nint _playerHwnd;
    private readonly PlaybackViewModel _view;
    private readonly Action<string> _log;
    private bool _stayAbove;
    private bool _playerAlwaysOnTop;
    private bool _dragging;
    private bool _autoScroll;
    private Point32 _dragMouse;
    private PointInt32 _dragWindow;
    private SetSubtitleSyncWindow? _syncWindow;
    private SubtitleStyleWindow? _styleWindow;
    private CueRow? _selected;
    private readonly ObservableCollection<CueRow> _rows = [];
    private DispatcherTimer? _searchDebounce;
    private string _search = "";
    private int _shownTrack = int.MinValue;

    public SubtitleBrowserWindow(nint playerHwnd, bool playerAlwaysOnTop, PlaybackViewModel view, Action<string> log)
    {
        _playerHwnd = playerHwnd;
        _playerAlwaysOnTop = playerAlwaysOnTop;
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = playerAlwaysOnTop;
        }

        SetTitleBar(TitleDrag);
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.Title = "Subtitle browser";
        AppWindow.Resize(new SizeInt32(WidthPx, HeightPx));
        AppWindow.Closing += OnClosing;

        var hwnd = WindowNative.GetWindowHandle(this);
        WindowChrome.ApplyLook(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        WindowChrome.LimitSize(hwnd, MinWidthPx, MinHeightPx);
        BindOwner(_stayAbove);

        _view.Subtitles.Changed += OnSubtitlesChanged;
        _view.PropertyChanged += OnViewChanged;
        CueList.ItemsSource = _rows;
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            SyncRows(force: true);
        };
        Closed += (_, _) =>
        {
            _view.Subtitles.Changed -= OnSubtitlesChanged;
            _view.PropertyChanged -= OnViewChanged;
            _searchDebounce?.Stop();
            CloseSync();
            CloseStyle();
        };
        UpdatePinVisual();
        SyncTopmost();
        Refresh();
    }

    public event Action<bool>? OpenChanged;

    public bool IsOpen => AppWindow.IsVisible;

    public void SetOpen(bool open)
    {
        if (open)
        {
            AppWindow.Show();
            AppWindow.Resize(new SizeInt32(WidthPx, HeightPx));
            SyncTopmost();
            Activate();
            if (_stayAbove)
            {
                PlaceAbovePlayer();
            }

            Refresh();
            OpenChanged?.Invoke(true);
            return;
        }

        CloseSync();
        CloseStyle();
        AppWindow.Hide();
        OpenChanged?.Invoke(false);
    }

    public void SyncPlayerAlwaysOnTop(bool value)
    {
        _playerAlwaysOnTop = value;
        SyncTopmost();
        if (_stayAbove)
        {
            PlaceAbovePlayer();
        }
    }

    public void PlaceAbovePlayerIfPinned()
    {
        if (_stayAbove)
        {
            PlaceAbovePlayer();
        }
    }

    public void Refresh()
    {
        RebuildTabs();
        SyncRows(force: true);
        UpdatePlayPause();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        SetOpen(false);
    }

    private void OnSubtitlesChanged(SubtitleNotify notify)
    {
        if (notify is SubtitleNotify.Delay)
        {
            RefreshTimes();
            return;
        }

        RebuildTabs();
        SyncRows(force: _shownTrack != _view.Subtitles.ActiveIndex);
    }

    private void OnViewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.PlayPauseGlyph) or nameof(PlaybackViewModel.IsPlaying) or null)
        {
            UpdatePlayPause();
        }

        if (!_autoScroll || e.PropertyName is not (nameof(PlaybackViewModel.PositionText) or nameof(PlaybackViewModel.SeekValue) or null))
        {
            return;
        }

        HighlightCurrent(scroll: true);
    }

    private void RebuildTabs()
    {
        TrackTabs.Children.Clear();
        var panel = (Brush)Application.Current.Resources["GrokPanelBrush"];
        var chrome = (Brush)Application.Current.Resources["GrokChromeBrush"];
        var accent = (Brush)Application.Current.Resources["GrokAccentBrush"];
        var muted = (Brush)Application.Current.Resources["GrokMutedBrush"];
        var visible = new List<int>();
        for (var i = 0; i < _view.Subtitles.Tracks.Count; i++)
        {
            if (_view.Subtitles.IsVisible(_view.Subtitles.Tracks[i]))
            {
                visible.Add(i);
            }
        }

        for (var n = 0; n < visible.Count; n++)
        {
            var index = visible[n];
            var track = _view.Subtitles.Tracks[index];
            var on = index == _view.Subtitles.ActiveIndex;
            var frame = new Border
            {
                Background = on ? panel : chrome,
                BorderBrush = (Brush)Application.Current.Resources["GrokLineBrush"],
                BorderThickness = new Thickness(0, 0, n == visible.Count - 1 ? 0 : 1, on ? 0 : 1),
                MinWidth = 92
            };
            var tab = new Button
            {
                Content = track.Name,
                Style = (Style)Application.Current.Resources["PanelTabButton"],
                Foreground = on ? accent : muted,
                Height = 32,
                Padding = new Thickness(12, 0, 12, 0)
            };
            tab.Click += (_, _) => _view.Subtitles.SelectTab(index);
            tab.DoubleTapped += (_, _) =>
            {
                _view.Subtitles.Apply(index);
                _log(ActionFeedback.SubtitleLoaded(track.Name));
            };
            frame.Child = tab;
            TrackTabs.Children.Add(frame);
        }
    }

    private void SyncRows(bool force)
    {
        var delay = TimeSpan.FromSeconds(_view.Subtitles.DelaySeconds);
        var track = _view.Subtitles.Active;
        var wanted = new List<SrtCue>();
        if (track is not null)
        {
            foreach (var cue in track.Document.Cues)
            {
                if (_search.Length > 0 && cue.Text.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                wanted.Add(cue);
            }
        }

        if (!force && _shownTrack == _view.Subtitles.ActiveIndex && SameCues(wanted))
        {
            RefreshTimes();
            return;
        }

        _shownTrack = _view.Subtitles.ActiveIndex;
        var selectedCue = _selected?.Cue;
        var i = 0;
        for (; i < wanted.Count; i++)
        {
            if (i < _rows.Count && ReferenceEquals(_rows[i].Cue, wanted[i]))
            {
                _rows[i].ApplyDelay(delay);
                continue;
            }

            if (i < _rows.Count)
            {
                _rows[i] = new CueRow(wanted[i], delay);
            }
            else
            {
                _rows.Add(new CueRow(wanted[i], delay));
            }
        }

        while (_rows.Count > wanted.Count)
        {
            _rows.RemoveAt(_rows.Count - 1);
        }

        if (selectedCue is not null)
        {
            _selected = _rows.FirstOrDefault(row => ReferenceEquals(row.Cue, selectedCue));
            CueList.SelectedItem = _selected;
        }
    }

    private bool SameCues(List<SrtCue> wanted)
    {
        if (wanted.Count != _rows.Count)
        {
            return false;
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (!ReferenceEquals(_rows[i].Cue, wanted[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void RefreshTimes()
    {
        var delay = TimeSpan.FromSeconds(_view.Subtitles.DelaySeconds);
        foreach (var row in _rows)
        {
            row.ApplyDelay(delay);
        }
    }

    private void CueList_ItemClick(object sender, ItemClickEventArgs e)
    {
        _selected = e.ClickedItem as CueRow;
        FillEdit();
    }

    private void CueList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        var when = _selected.Cue.Start + TimeSpan.FromSeconds(_view.Subtitles.DelaySeconds);
        _view.ApplySeek(Math.Max(0, when.TotalSeconds));
    }

    private void NudgeBack_Click(object sender, RoutedEventArgs e) => Nudge(-0.5);

    private void NudgeForward_Click(object sender, RoutedEventArgs e) => Nudge(0.5);

    private void Nudge(double delta)
    {
        _view.Subtitles.NudgeDelay(delta);
        _log(ActionFeedback.SubtitleSync(_view.Subtitles.DelaySeconds));
    }

    private void SetSync_Click(object sender, RoutedEventArgs e)
    {
        if (_syncWindow is not null)
        {
            _syncWindow.Activate();
            _syncWindow.PlaceAbove();
            return;
        }

        var owner = WindowNative.GetWindowHandle(this);
        _syncWindow = new SetSubtitleSyncWindow(owner, _playerAlwaysOnTop || _stayAbove, _view.Subtitles.DelaySeconds);
        _syncWindow.Applied += value =>
        {
            _view.Subtitles.SetDelay(value);
            _log(ActionFeedback.SubtitleSync(_view.Subtitles.DelaySeconds));
        };
        _syncWindow.Closed += (_, _) => _syncWindow = null;
        var here = AppWindow.Position;
        _syncWindow.AppWindow.Move(new PointInt32(here.X + 40, here.Y + 80));
        _syncWindow.Activate();
        _syncWindow.PlaceAbove();
    }

    private void ResetAllToSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            _log("Select a cue");
            return;
        }

        var position = TimeSpan.FromSeconds(_view.SeekValue);
        _view.Subtitles.SyncSelectedToPosition(_selected.Cue, position);
        _log(ActionFeedback.SubtitleSync(_view.Subtitles.DelaySeconds));
    }

    private void ResetSync_Click(object sender, RoutedEventArgs e)
    {
        _view.Subtitles.ResetDelay();
        _log(ActionFeedback.SubtitleSync(0));
    }

    private void FindCurrent_Click(object sender, RoutedEventArgs e) => HighlightCurrent(scroll: true);

    private void AutoScroll_Click(object sender, RoutedEventArgs e)
    {
        _autoScroll = AutoScrollToggle.IsChecked == true;
        var accent = (Brush)Application.Current.Resources["GrokAccentBrush"];
        var muted = (Brush)Application.Current.Resources["GrokMutedBrush"];
        AutoScrollToggle.Foreground = _autoScroll ? accent : muted;
        AutoScrollIcon.Foreground = _autoScroll ? accent : muted;
        if (_autoScroll)
        {
            HighlightCurrent(scroll: true);
        }
    }

    private void HighlightCurrent(bool scroll)
    {
        var cue = _view.Subtitles.CueAtPlayback(TimeSpan.FromSeconds(_view.SeekValue));
        if (cue is null)
        {
            return;
        }

        var row = _rows.FirstOrDefault(item => ReferenceEquals(item.Cue, cue));
        if (row is null)
        {
            return;
        }

        _selected = row;
        CueList.SelectedItem = row;
        if (scroll)
        {
            CueList.ScrollIntoView(row);
        }
    }

    private async void Load_Click(object sender, RoutedEventArgs e) => await ImportAsync(apply: true);

    private async void Add_Click(object sender, RoutedEventArgs e) => await ImportAsync(apply: false);

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (_view.Subtitles.Active is null)
        {
            _log("No subtitle tab");
            return;
        }

        var path = await SubtitleFiles.PickAsync(WindowNative.GetWindowHandle(this));
        if (path is null)
        {
            return;
        }

        if (_view.Subtitles.MergeFile(path))
        {
            _log(ActionFeedback.SubtitlesMerged());
        }
    }

    private async Task ImportAsync(bool apply)
    {
        var path = await SubtitleFiles.PickAsync(WindowNative.GetWindowHandle(this));
        if (path is null)
        {
            return;
        }

        var track = _view.Subtitles.AddFile(path, apply);
        _log(apply ? ActionFeedback.SubtitleLoaded(track.Name) : ActionFeedback.SubtitleAdded(track.Name));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text?.Trim() ?? "";
        _searchDebounce?.Stop();
        _searchDebounce?.Start();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var show = EditStrip.Visibility != Visibility.Visible;
        EditStrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var accent = (Brush)Application.Current.Resources["GrokAccentBrush"];
        var muted = (Brush)Application.Current.Resources["GrokMutedBrush"];
        EditButton.Foreground = show ? accent : muted;
        EditIcon.Foreground = show ? accent : muted;
        FillEdit();
        if (show)
        {
            EditBox.Focus(FocusState.Programmatic);
        }
    }

    private void FillEdit()
    {
        if (EditStrip.Visibility != Visibility.Visible)
        {
            return;
        }

        EditStartBox.Text = _selected is null ? "" : SrtTime.Format(_selected.Cue.Start);
        EditEndBox.Text = _selected is null ? "" : SrtTime.Format(_selected.Cue.End);
        EditBox.Text = _selected is null
            ? ""
            : CaptionMarkup.HasStyle(_selected.Cue.Spans)
                ? CaptionMarkup.ToMarked(_selected.Cue.Spans)
                : _selected.Cue.Text;
    }

    private void EditFields_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _view.Subtitles.Active is null)
        {
            return;
        }

        var cue = _selected.Cue;
        var changed = false;
        if (SrtTime.TryParse(EditStartBox.Text ?? "", out var start) && cue.Start != start)
        {
            cue.Start = start;
            changed = true;
        }

        if (SrtTime.TryParse(EditEndBox.Text ?? "", out var end) && cue.End != end)
        {
            cue.End = end < cue.Start ? cue.Start : end;
            changed = true;
        }

        var text = EditBox.Text ?? "";
        var spans = CaptionMarkup.Parse(text);
        var plain = spans.Count > 0 ? CaptionMarkup.Plain(spans) : text;
        if (!string.Equals(cue.Text, plain, StringComparison.Ordinal) ||
            !SpansEqual(cue.Spans, spans))
        {
            cue.Text = plain;
            cue.Spans = spans.Count > 0 ? spans : [new CaptionSpan(plain, null)];
            cue.Karaoke = [];
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        _selected.RefreshText();
        _selected.ApplyDelay(TimeSpan.FromSeconds(_view.Subtitles.DelaySeconds));
        _view.Subtitles.PersistActive();
        _log("Subtitle edited");
    }

    private static bool SpansEqual(IReadOnlyList<CaptionSpan> left, IReadOnlyList<CaptionSpan> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        var index = _selected is null || _view.Subtitles.Active is null
            ? -1
            : _view.Subtitles.Active.Document.Cues.IndexOf(_selected.Cue);
        var cue = _view.Subtitles.InsertCue(index);
        if (cue is null)
        {
            _log("No subtitle tab");
            return;
        }

        SyncRows(force: true);
        _selected = _rows.FirstOrDefault(row => ReferenceEquals(row.Cue, cue));
        CueList.SelectedItem = _selected;
        CueList.ScrollIntoView(_selected);
        FillEdit();
        _log("Cue added");
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || !_view.Subtitles.DeleteCue(_selected.Cue))
        {
            _log("Select a cue");
            return;
        }

        _selected = null;
        SyncRows(force: true);
        _log("Cue deleted");
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        _view.TogglePlayPause();
        UpdatePlayPause();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_view.Subtitles.SaveActive())
        {
            _log("Subtitle saved");
            return;
        }

        _log("Nothing to save");
    }

    private void UpdatePlayPause()
    {
        PlayPauseIcon.Glyph = _view.IsPlaying ? "\uE769" : "\uE768";
        PlayPauseButton.Foreground = (Brush)Application.Current.Resources["GrokAccentBrush"];
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _log("Subtitle settings");

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _stayAbove = !_stayAbove;
        UpdatePinVisual();
        SyncTopmost();
        BindOwner(_stayAbove);
        if (_stayAbove)
        {
            PlaceAbovePlayer();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SetOpen(false);

    private void Style_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            _log("Select a cue");
            return;
        }

        if (_styleWindow is not null)
        {
            _styleWindow.Activate();
            _styleWindow.PlaceAbove();
            return;
        }

        var owner = WindowNative.GetWindowHandle(this);
        var editingRow = _selected;
        var editingTrack = _view.Subtitles.Active;
        _styleWindow = new SubtitleStyleWindow(owner, _playerAlwaysOnTop || _stayAbove, _selected.Cue.Spans);
        _styleWindow.Applied += style =>
        {
            if (_view.Subtitles.Active != editingTrack)
            {
                _log("Select the original subtitle track before applying this style");
                return;
            }

            var cue = editingRow.Cue;
            cue.Spans = [style with { Text = cue.Text }];
            cue.Karaoke = [];
            editingRow.RefreshText();
            FillEdit();
            _view.Subtitles.PersistActive();
            _log("Subtitle style updated");
        };
        _styleWindow.Closed += (_, _) => _styleWindow = null;
        var here = AppWindow.Position;
        _styleWindow.AppWindow.Move(new PointInt32(here.X + 40, here.Y + 80));
        _styleWindow.Activate();
        _styleWindow.PlaceAbove();
    }

    private void CloseStyle()
    {
        if (_styleWindow is null)
        {
            return;
        }

        var dialog = _styleWindow;
        _styleWindow = null;
        try
        {
            dialog.Close();
        }
        catch (Exception)
        {
        }
    }

    private void CloseSync()
    {
        if (_syncWindow is null)
        {
            return;
        }

        var dialog = _syncWindow;
        _syncWindow = null;
        try
        {
            dialog.Close();
        }
        catch (Exception)
        {
        }
    }

    private void UpdatePinVisual()
    {
        PinIcon.Foreground = _stayAbove
            ? (Brush)Application.Current.Resources["GrokAccentBrush"]
            : (Brush)Application.Current.Resources["GrokMutedBrush"];
    }

    private void SyncTopmost()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _playerAlwaysOnTop || _stayAbove;
        }

        _syncWindow?.SyncTopmost(_playerAlwaysOnTop || _stayAbove);
        _styleWindow?.SyncTopmost(_playerAlwaysOnTop || _stayAbove);
    }

    private void BindOwner(bool own)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd == 0)
        {
            return;
        }

        SetWindowLongPtr(hwnd, GwlpHwndParent, own && _playerHwnd != 0 ? _playerHwnd : 0);
    }

    private void PlaceAbovePlayer()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd == 0)
        {
            return;
        }

        SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        _syncWindow?.PlaceAbove();
        _styleWindow?.PlaceAbove();
    }

    private void EmptyArea_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed || IsInteractive(e.OriginalSource))
        {
            return;
        }

        if (!GetCursorPos(out _dragMouse))
        {
            return;
        }

        _dragWindow = AppWindow.Position;
        _dragging = true;
        (sender as UIElement)?.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void EmptyArea_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !GetCursorPos(out var now))
        {
            return;
        }

        AppWindow.Move(new PointInt32(
            _dragWindow.X + now.X - _dragMouse.X,
            _dragWindow.Y + now.Y - _dragMouse.Y));
    }

    private void EmptyArea_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
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

    private static bool IsInteractive(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button or ToggleButton or TextBox or ListView or ListViewItem or ScrollBar or RepeatButton)
            {
                return true;
            }
        }

        return false;
    }

    private const int GwlpHwndParent = -8;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTop = 0;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    public sealed class CueRow : INotifyPropertyChanged
    {
        private string _startText = "";
        private string _endText = "";
        private string _text = "";
        private string _syncText = "";

        public CueRow(SrtCue cue, TimeSpan delay)
        {
            Cue = cue;
            RefreshText();
            ApplyDelay(delay);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public SrtCue Cue { get; }

        public string StartText
        {
            get => _startText;
            private set => Set(ref _startText, value);
        }

        public string EndText
        {
            get => _endText;
            private set => Set(ref _endText, value);
        }

        public string Text
        {
            get => _text;
            private set => Set(ref _text, value);
        }

        public string SyncText
        {
            get => _syncText;
            private set => Set(ref _syncText, value);
        }

        public IReadOnlyList<CaptionSpan> Spans => Cue.Spans;

        public void RefreshText()
        {
            Text = Cue.Text.Replace('\n', ' ');
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Spans)));
        }

        public void ApplyDelay(TimeSpan delay)
        {
            StartText = SrtTime.Format(Cue.Start + delay);
            EndText = SrtTime.Format(Cue.End + delay);
            SyncText = SrtTime.ToMs(Cue.Start + delay).ToString();
        }

        private void Set(ref string field, string value, [CallerMemberName] string? name = null)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
