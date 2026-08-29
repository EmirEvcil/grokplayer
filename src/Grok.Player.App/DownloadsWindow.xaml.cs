using System.Runtime.InteropServices;
using Grok.Player.App.Native;
using Grok.Player.Core.Download;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Grok.Player.App;

public sealed partial class DownloadsWindow : Window
{
    private readonly nint _owner;
    private readonly DownloadManager _downloads;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly Dictionary<string, JobRow> _rows = new(StringComparer.Ordinal);
    private bool _stayAbove;
    private bool _playerAlwaysOnTop;
    private bool _dragging;
    private int _dirty = 1;
    private Point32 _dragMouse;
    private PointInt32 _dragWindow;

    public DownloadsWindow(nint owner, bool playerAlwaysOnTop, DownloadManager downloads)
    {
        _owner = owner;
        _playerAlwaysOnTop = playerAlwaysOnTop;
        _downloads = downloads;
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
        AppWindow.Title = "Downloads";
        var hwnd = WindowNative.GetWindowHandle(this);
        Root.Loaded += (_, _) => FitOnce();
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            SetOpen(false);
        };
        WindowChrome.ApplyLook(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        BindOwner(false);

        _downloads.Changed += DownloadsChanged;
        Closed += (_, _) => { _clock.Stop(); _downloads.Changed -= DownloadsChanged; };
        _clock.Tick += (_, _) =>
        {
            if (Interlocked.Exchange(ref _dirty, 0) != 0) Paint();
        };
        UpdatePinVisual();
        Paint();
    }

    private bool _fitted;

    private void DownloadsChanged() => Interlocked.Exchange(ref _dirty, 1);

    private void FitOnce()
    {
        if (_fitted)
        {
            return;
        }

        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var width = Math.Min(560, Math.Max(420, area.Width - 40));
            var height = Math.Min(460, Math.Max(320, area.Height - 40));
            if (width >= 200 && height >= 160)
            {
                AppWindow.Resize(new SizeInt32(width, height));
            }
            _fitted = true;
        }
        catch (Exception)
        {
        }
    }

    public void SetOpen(bool open)
    {
        if (open)
        {
            AppWindow.Show();
            BindOwner(_stayAbove);
            SyncTopmost();
            if (_stayAbove)
            {
                PlaceAbove();
            }

            Activate();
            _clock.Start();
            Paint();
            return;
        }

        _clock.Stop();
        AppWindow.Hide();
    }

    public void SyncPlayerAlwaysOnTop(bool value)
    {
        _playerAlwaysOnTop = value;
        SyncTopmost();
    }

    private void Paint()
    {
        var jobs = _downloads.Jobs;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            seen.Add(job.Id);
            if (!_rows.TryGetValue(job.Id, out var row))
            {
                row = CreateRow(job);
                _rows[job.Id] = row;
                JobHost.Children.Add(row.Root);
            }

            row.Status.Text = job.StatusText + (string.IsNullOrWhiteSpace(job.SizeText) ? "" : "  ·  " + job.SizeText);
            row.Bar.Value = job.Progress;
            if (row.State != job.State)
            {
                row.State = job.State;
                FillButtons(row, job);
            }
        }

        foreach (var id in _rows.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            JobHost.Children.Remove(_rows[id].Root);
            _rows.Remove(id);
        }

        EmptyState.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var running = jobs.Count(item => item.State == DownloadState.Running);
        var waiting = jobs.Count(item => item.State == DownloadState.Queued);
        var done = jobs.Count(item => item.State == DownloadState.Completed);
        SummaryText.Text = jobs.Count == 0
            ? "No downloads"
            : running + " downloading · " + waiting + " waiting · " + done + " done";
        CountText.Text = jobs.Count == 0 ? "" : jobs.Count + (jobs.Count == 1 ? " item" : " items");
    }

    private JobRow CreateRow(DownloadJob job)
    {
        var accent = (Brush)Application.Current.Resources["GrokAccentBrush"];
        var muted = (Brush)Application.Current.Resources["GrokMutedBrush"];
        var line = (Brush)Application.Current.Resources["GrokLineBrush"];
        var row = new JobRow
        {
            State = job.State,
            Glyph = new FontIcon { FontSize = 14, Glyph = "\uE896", Foreground = muted, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) },
            Title = new TextBlock
            {
                Text = job.Title,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 230, 230, 234)),
                TextTrimming = TextTrimming.CharacterEllipsis
            },
            Status = new TextBlock { FontSize = 11, Foreground = muted },
            Bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 1,
                Value = job.Progress,
                Height = 4,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = accent,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 36, 40))
            },
            Buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top }
        };
        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(row.Title);
        Grid.SetColumn(row.Buttons, 1);
        heading.Children.Add(row.Buttons);
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(heading);
        text.Children.Add(row.Status);
        text.Children.Add(row.Bar);
        var body = new Grid { ColumnSpacing = 10 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(row.Glyph);
        Grid.SetColumn(text, 1);
        body.Children.Add(text);
        row.Root = new Border
        {
            Background = (Brush)Application.Current.Resources["GrokPanelBrush"],
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 10, 10),
            Child = body
        };
        FillButtons(row, job);
        return row;
    }

    private void FillButtons(JobRow row, DownloadJob job)
    {
        row.Buttons.Children.Clear();
        row.Glyph.Glyph = job.State switch
        {
            DownloadState.Running => "\uE896",
            DownloadState.Paused => "\uE769",
            DownloadState.Completed => "\uE73E",
            DownloadState.Failed => "\uE783",
            DownloadState.Canceled => "\uE711",
            _ => "\uE121"
        };
        row.Glyph.Foreground = job.State switch
        {
            DownloadState.Running => (Brush)Application.Current.Resources["GrokAccentBrush"],
            DownloadState.Failed => (Brush)Application.Current.Resources["GrokPinkBrush"],
            DownloadState.Completed => (Brush)Application.Current.Resources["GrokAccentBrush"],
            _ => (Brush)Application.Current.Resources["GrokMutedBrush"]
        };
        if (job.State is DownloadState.Queued or DownloadState.Paused or DownloadState.Failed)
        {
            row.Buttons.Children.Add(IconButton("\uE768", "Start download", () => _downloads.Start(job.Id)));
        }

        if (job.State == DownloadState.Running)
        {
            row.Buttons.Children.Add(IconButton("\uE769", "Pause", () => _downloads.Pause(job.Id)));
        }

        if (job.State is DownloadState.Running or DownloadState.Queued or DownloadState.Paused)
        {
            row.Buttons.Children.Add(IconButton("\uE711", "Cancel", () => _downloads.Cancel(job.Id)));
        }

        row.Buttons.Children.Add(IconButton("\uE74D", "Delete", () => _downloads.Delete(job.Id)));
    }

    private sealed class JobRow
    {
        public Border Root { get; set; } = null!;
        public FontIcon Glyph { get; set; } = null!;
        public TextBlock Title { get; set; } = null!;
        public TextBlock Status { get; set; } = null!;
        public ProgressBar Bar { get; set; } = null!;
        public StackPanel Buttons { get; set; } = null!;
        public DownloadState State { get; set; }
    }

    private Button IconButton(string glyph, string tip, Action action)
    {
        var button = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = (Brush)Application.Current.Resources["GrokChromeBrush"],
            BorderBrush = (Brush)Application.Current.Resources["GrokLineBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Content = new FontIcon
            {
                FontSize = 12,
                Glyph = glyph,
                Foreground = (Brush)Application.Current.Resources["GrokMutedBrush"]
            }
        };
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _stayAbove = !_stayAbove;
        UpdatePinVisual();
        BindOwner(_stayAbove);
        SyncTopmost();
        if (_stayAbove)
        {
            PlaceAbove();
        }
    }

    private void BindOwner(bool own)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(hwnd, -8, own && _owner != 0 ? _owner : 0);
    }

    private void PlaceAbove()
    {
        if (_owner == 0)
        {
            return;
        }

        SetWindowPos(WindowNative.GetWindowHandle(this), _owner, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SetOpen(false);

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
    }

    private void EmptyArea_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
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
    }

    private void EmptyArea_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !GetCursorPos(out var now))
        {
            return;
        }

        AppWindow.Move(new PointInt32(_dragWindow.X + now.X - _dragMouse.X, _dragWindow.Y + now.Y - _dragMouse.Y));
    }

    private void EmptyArea_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
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

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }
}
