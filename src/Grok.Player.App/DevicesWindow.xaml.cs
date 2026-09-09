using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Grok.Player.App.Link;
using Grok.Player.App.Native;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Grok.Player.App;

public sealed partial class DevicesWindow : Window
{
    public const int WidthPx = 520;
    public const int HeightPx = 560;

    private readonly nint _playerHwnd;
    private readonly LinkServer _server;
    private bool _stayAbove;
    private bool _playerAlwaysOnTop;
    private bool _placed;
    private bool _dragging;
    private Point32 _dragMouse;
    private PointInt32 _dragWindow;
    private readonly DispatcherTimer _tick;

    public DevicesWindow(nint playerHwnd, bool playerAlwaysOnTop, LinkServer server)
    {
        _playerHwnd = playerHwnd;
        _playerAlwaysOnTop = playerAlwaysOnTop;
        _server = server;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsAlwaysOnTop = playerAlwaysOnTop;
        }

        SetTitleBar(TitleDrag);
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.Title = "Devices";
        AppWindow.Resize(new SizeInt32(WidthPx, HeightPx));
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            SetOpen(false);
        };

        var hwnd = WindowNative.GetWindowHandle(this);
        WindowChrome.ApplyLook(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _tick.Tick += (_, _) => Refresh();
        _server.PairOffered += (_, name) => DispatcherQueue.TryEnqueue(() => OfferPair(name));
        _server.Changed += () => DispatcherQueue.TryEnqueue(Refresh);
        UpdatePinVisual();
        Refresh();
    }

    public bool IsOpen => AppWindow.IsVisible;

    public void SetOpen(bool open)
    {
        if (open)
        {
            if (!_placed)
            {
                PlaceBesidePlayer();
                _placed = true;
            }
            AppWindow.Show();
            SyncTopmost();
            Activate();
            _tick.Start();
            Refresh();
            return;
        }

        _tick.Stop();
        AppWindow.Hide();
    }

    public void SyncPlayerAlwaysOnTop(bool value)
    {
        _playerAlwaysOnTop = value;
        SyncTopmost();
    }

    public void PlaceAbovePlayerIfPinned()
    {
        if (_stayAbove && IsOpen)
        {
            AppWindow.MoveInZOrderAtTop();
        }
    }

    public void OfferPair(string name)
    {
        try
        {
            SetOpen(true);
            PairCard.Visibility = Visibility.Visible;
            PairTitle.Text = name + " · enter the code on the TV";
            if (string.IsNullOrWhiteSpace(PinBox.Text))
            {
                PinBox.Focus(FocusState.Programmatic);
            }
        }
        catch (Exception)
        {
        }
    }

    private void Refresh()
    {
        if (ThisPcMeta is null)
        {
            return;
        }

        ThisPcMeta.Text = $"{_server.Name} · {_server.Host}:{_server.Port} · visible on LAN";
        if (!_server.PairPending && PairCard is not null)
        {
            PairCard.Visibility = Visibility.Collapsed;
        }

        var tvs = _server.Televisions();
        var sessionId = _server.SessionTvId;
        ConnectedList?.Children.Clear();
        TrustList?.Children.Clear();
        var connected = tvs.Where(tv => tv.Id == sessionId).ToList();
        var paired = tvs.Where(tv => tv.Id != sessionId).ToList();
        if (EmptyConnected is not null)
        {
            EmptyConnected.Visibility = connected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (EmptyTrust is not null)
        {
            EmptyTrust.Visibility = tvs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var tv in connected)
        {
            ConnectedList?.Children.Add(TvRow(tv, connected: true, online: true));
        }

        foreach (var tv in paired)
        {
            TrustList?.Children.Add(TvRow(tv, connected: false, online: _server.IsTvOnline(tv.Id)));
        }

        FolderList?.Children.Clear();
        foreach (var folder in SharedFolders.List())
        {
            FolderList?.Children.Add(FolderRow(folder));
        }

        if (BrowseTvButton is not null)
        {
            BrowseTvButton.Visibility = sessionId is null ? Visibility.Collapsed : Visibility.Visible;
        }

        JobList?.Children.Clear();
        foreach (var job in _server.Jobs)
        {
            JobList.Children.Add(new TextBlock
            {
                Text = $"{job.Title}  ·  {job.Status}  {job.Done}/{job.Total}",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["GrokMutedBrush"],
            });
        }
    }

    private Border TvRow(TrustedTv tv, bool connected, bool online)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (connected)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var status = connected ? "Connected" : online ? "Paired · online" : "Paired · offline";
        var text = new TextBlock
        {
            Text = tv.Name + "  ·  " + status,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        row.Children.Add(text);
        if (connected)
        {
            var disconnect = new Button { Content = "Disconnect", Padding = new Thickness(10, 4, 10, 4) };
            disconnect.Click += (_, _) =>
            {
                _server.DisconnectSession();
                Refresh();
            };
            Grid.SetColumn(disconnect, 1);
            row.Children.Add(disconnect);
        }

        var forget = new Button { Content = "Forget pairing", Padding = new Thickness(10, 4, 10, 4) };
        forget.Click += (_, _) =>
        {
            _server.Forget(tv.Id);
            Refresh();
        };
        Grid.SetColumn(forget, connected ? 2 : 1);
        row.Children.Add(forget);
        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 24, 28)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Child = row,
        };
    }

    private Border FolderRow(string path)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = path,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        var remove = new Button { Content = "Remove", Padding = new Thickness(10, 4, 10, 4) };
        remove.Click += (_, _) =>
        {
            SharedFolders.Remove(path);
            Refresh();
        };
        Grid.SetColumn(remove, 1);
        row.Children.Add(remove);
        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 24, 28)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = row,
        };
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        SharedFolders.Add(folder.Path);
        Refresh();
    }

    private string? _tvBrowsePath = "";

    private async void BrowseTv_Click(object sender, RoutedEventArgs e)
    {
        await LoadTvBrowse(_tvBrowsePath ?? "");
    }

    private async Task LoadTvBrowse(string path)
    {
        if (TvBrowseList is null || string.IsNullOrWhiteSpace(_server.TvHost) || string.IsNullOrWhiteSpace(_server.SessionToken))
        {
            return;
        }

        TvBrowseList.Children.Clear();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            client.DefaultRequestHeaders.TryAddWithoutValidation(LinkProtocol.TokenHeader, _server.SessionToken);
            var url = $"http://{_server.TvHost}:{_server.TvPort}/v1/browse?path={Uri.EscapeDataString(path)}";
            var json = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            _tvBrowsePath = root.GetProperty("path").GetString() ?? path;
            var parent = root.TryGetProperty("parent", out var parentEl) ? parentEl.GetString() : null;
            if (parent is not null)
            {
                var up = new Button { Content = "Up", HorizontalAlignment = HorizontalAlignment.Left };
                var upPath = parent;
                up.Click += async (_, _) => await LoadTvBrowse(upPath ?? "");
                TvBrowseList.Children.Add(up);
            }

            if (root.TryGetProperty("dirs", out var dirs))
            {
                foreach (var dir in dirs.EnumerateArray())
                {
                    var name = dir.GetProperty("name").GetString() ?? "folder";
                    var dirPath = dir.GetProperty("path").GetString() ?? "";
                    var button = new Button { Content = "📁 " + name, HorizontalAlignment = HorizontalAlignment.Stretch };
                    button.Click += async (_, _) => await LoadTvBrowse(dirPath);
                    TvBrowseList.Children.Add(button);
                }
            }

            if (root.TryGetProperty("videos", out var videos))
            {
                foreach (var video in videos.EnumerateArray())
                {
                    var title = video.GetProperty("title").GetString() ?? "video";
                    var videoPath = video.GetProperty("path").GetString() ?? "";
                    var button = new Button { Content = "▶ " + title, HorizontalAlignment = HorizontalAlignment.Stretch };
                    button.Click += (_, _) =>
                    {
                        var playUrl = $"http://{_server.TvHost}:{_server.TvPort}/v1/file?path={Uri.EscapeDataString(videoPath)}&token={Uri.EscapeDataString(_server.SessionToken ?? "")}";
                        App.Main?.PlayFromTv(playUrl, title);
                    };
                    TvBrowseList.Children.Add(button);
                }
            }
        }
        catch
        {
            TvBrowseList.Children.Add(new TextBlock
            {
                Text = "TV folders need permission on the TV (Cihazlar → TV klasörlerini paylaş).",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["GrokMutedBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (_server.TryAcceptPin(PinBox.Text))
        {
            PairCard.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }

    private void PinBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PinBox.Text.Length == 6)
        {
            Pair_Click(sender, e);
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _stayAbove = !_stayAbove;
        UpdatePinVisual();
        SyncTopmost();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SetOpen(false);

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
            try { element.ReleasePointerCapture(e.Pointer); }
            catch (Exception) { }
        }
    }

    private static bool IsInteractive(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button or TextBox)
            {
                return true;
            }
        }

        return false;
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
    }

    private void PlaceBesidePlayer()
    {
        if (_playerHwnd == 0) return;
        GetWindowRect(_playerHwnd, out var rect);
        AppWindow.Move(new PointInt32(rect.Left + 56, rect.Top + 72));
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }
}
