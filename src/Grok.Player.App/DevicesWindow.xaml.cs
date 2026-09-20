using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Grok.Player.App.Link;
using Grok.Player.App.Native;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Grok.Player.App;

public sealed partial class DevicesWindow : Window
{
    public const int WidthPx = 580;
    public const int HeightPx = 680;

    private readonly nint _playerHwnd;
    private readonly LinkServer _server;
    private bool _stayAbove;
    private bool _playerAlwaysOnTop;
    private bool _placed;
    private bool _dragging;
    private Point32 _dragMouse;
    private PointInt32 _dragWindow;
    private readonly DispatcherTimer _tick;
    private string _lastPaint = "";
    private string _tvBrowsePath = "";
    private string? _tvBrowseParent;
    private readonly List<string> _tvGrants = [];
    private bool _browseBusy;

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
            presenter.IsMaximizable = false;
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
        ShowHome();
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
            ShowHome();
            PairCard.Visibility = Visibility.Visible;
            PairTitle.Text = name + " · enter the code shown on the TV";
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

        ThisPcMeta.Text = $"{_server.Name}  ·  {_server.Host}:{_server.Port}";
        if (!_server.PairPending && PairCard is not null)
        {
            PairCard.Visibility = Visibility.Collapsed;
        }

        var key = PaintKey();
        if (key == _lastPaint)
        {
            return;
        }

        _lastPaint = key;
        PaintHome();
    }

    private string PaintKey()
    {
        var session = _server.SessionTvId;
        var tvs = string.Join("|", _server.Televisions().Select(tv =>
            $"{tv.Id};{tv.Name};{tv.Id == session};{_server.IsTvOnline(tv.Id)}"));
        var folders = string.Join("|", SharedFolders.List());
        var jobs = string.Join("|", _server.Jobs.Select(job => $"{job.Id};{job.Status};{job.Done};{job.Total}"));
        return tvs + "\n" + folders + "\n" + jobs + "\n" + _server.PairPending;
    }

    private void PaintHome()
    {
        var tvs = _server.Televisions();
        var sessionId = _server.SessionTvId;
        TvList?.Children.Clear();
        if (EmptyTvs is not null)
        {
            EmptyTvs.Visibility = tvs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var tv in tvs)
        {
            TvList?.Children.Add(TvRow(tv, connected: tv.Id == sessionId, online: _server.IsTvOnline(tv.Id)));
        }

        FolderList?.Children.Clear();
        var folders = SharedFolders.List();
        if (EmptyFolders is not null)
        {
            EmptyFolders.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var folder in folders)
        {
            FolderList?.Children.Add(FolderRow(folder));
        }

        JobList?.Children.Clear();
        var jobs = _server.Jobs.ToList();
        if (TransfersSection is not null)
        {
            TransfersSection.Visibility = jobs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        foreach (var job in jobs)
        {
            JobList?.Children.Add(JobRow(job));
        }
    }

    private Border TvRow(TrustedTv tv, bool connected, bool online)
    {
        var status = connected ? "Connected" : online ? "Online" : "Offline";
        var dot = connected
            ? Windows.UI.Color.FromArgb(255, 61, 206, 106)
            : online
                ? Windows.UI.Color.FromArgb(255, 240, 201, 58)
                : Windows.UI.Color.FromArgb(255, 90, 90, 98);

        var body = new StackPanel { Spacing = 8 };
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(dot),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        });
        var labels = new StackPanel { Spacing = 1 };
        labels.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(tv.Name) ? "Television" : tv.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        labels.Children.Add(new TextBlock
        {
            Text = status,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["GrokMutedBrush"],
        });
        Grid.SetColumn(labels, 1);
        head.Children.Add(labels);
        body.Children.Add(head);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (connected)
        {
            var browse = new Button { Content = "Browse folders", Style = ButtonStyle("AccentActionButton") };
            browse.Click += async (_, _) => await OpenBrowse();
            actions.Children.Add(browse);
            var disconnect = new Button { Content = "Disconnect", Style = ButtonStyle("SubBarButton") };
            disconnect.Click += (_, _) =>
            {
                _server.DisconnectSession();
                _lastPaint = "";
                Refresh();
            };
            actions.Children.Add(disconnect);
        }

        var forget = new Button { Content = "Forget", Style = ButtonStyle("SubBarButton") };
        forget.Click += (_, _) =>
        {
            _server.Forget(tv.Id);
            if (connected)
            {
                ShowHome();
            }

            _lastPaint = "";
            Refresh();
        };
        actions.Children.Add(forget);
        body.Children.Add(actions);

        return Card(body, 12);
    }

    private Border FolderRow(string path)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = FolderName(path),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = path,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["GrokMutedBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Children.Add(text);
        var remove = new Button { Content = "Remove", Style = ButtonStyle("SubBarButton") };
        remove.Click += (_, _) =>
        {
            SharedFolders.Remove(path);
            _lastPaint = "";
            Refresh();
        };
        Grid.SetColumn(remove, 1);
        row.Children.Add(remove);
        return Card(row, 10);
    }

    private Border JobRow(LinkJobDto job)
    {
        var box = new StackPanel { Spacing = 6 };
        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(job.Title) ? "Transfer" : job.Title,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var meta = new TextBlock
        {
            Text = JobLabel(job),
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["GrokMutedBrush"],
        };
        Grid.SetColumn(meta, 1);
        line.Children.Add(meta);
        box.Children.Add(line);
        if (job.Total > 0)
        {
            box.Children.Add(new ProgressBar
            {
                Minimum = 0,
                Maximum = 1,
                Value = Math.Clamp(job.Done / (double)job.Total, 0, 1),
                Height = 3,
                Foreground = (Brush)Application.Current.Resources["GrokAccentBrush"],
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 42, 42, 48)),
            });
        }

        return Card(box, 10);
    }

    private async Task OpenBrowse()
    {
        if (string.IsNullOrWhiteSpace(_server.SessionTvId))
        {
            return;
        }

        HomeView.Visibility = Visibility.Collapsed;
        BrowseView.Visibility = Visibility.Visible;
        await LoadTvBrowse("");
    }

    private void ShowHome()
    {
        BrowseView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        _tvBrowsePath = "";
        _tvBrowseParent = null;
    }

    private async void BrowseBack_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_tvBrowsePath))
        {
            ShowHome();
            return;
        }

        await LoadTvBrowse(_tvBrowseParent ?? "");
    }

    private void BrowseClose_Click(object sender, RoutedEventArgs e) => ShowHome();

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
        _lastPaint = "";
        Refresh();
    }

    private async Task LoadTvBrowse(string path)
    {
        if (BrowseList is null || string.IsNullOrWhiteSpace(_server.SessionTvId))
        {
            return;
        }

        if (_browseBusy)
        {
            return;
        }

        _browseBusy = true;
        BrowseList.Children.Clear();
        EmptyBrowse.Visibility = Visibility.Collapsed;
        try
        {
            var json = await _server.AskTvBrowse(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new HttpRequestException("tv-browse");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            _tvBrowsePath = root.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? path : path;
            _tvBrowseParent = root.TryGetProperty("parent", out var parentEl) && parentEl.ValueKind != JsonValueKind.Null
                ? parentEl.GetString()
                : null;
            if (root.TryGetProperty("granted", out var granted) && granted.ValueKind == JsonValueKind.Array)
            {
                _tvGrants.Clear();
                foreach (var item in granted.EnumerateArray())
                {
                    var grant = item.GetString();
                    if (!string.IsNullOrWhiteSpace(grant))
                    {
                        _tvGrants.Add(grant);
                    }
                }
            }

            PaintCrumbs();
            BrowseBack.IsEnabled = true;
            BrowseTitle.Text = string.IsNullOrWhiteSpace(_tvBrowsePath) ? "TV folders" : FolderName(_tvBrowsePath);

            var added = 0;
            if (root.TryGetProperty("dirs", out var dirs))
            {
                foreach (var dir in dirs.EnumerateArray())
                {
                    var name = dir.GetProperty("name").GetString() ?? "Folder";
                    var dirPath = dir.GetProperty("path").GetString() ?? "";
                    BrowseList.Children.Add(BrowseItem(name, "Folder", isFolder: true, () => LoadTvBrowse(dirPath)));
                    added++;
                }
            }

            if (root.TryGetProperty("videos", out var videos))
            {
                foreach (var video in videos.EnumerateArray())
                {
                    var title = video.GetProperty("title").GetString() ?? "Video";
                    var videoPath = video.GetProperty("path").GetString() ?? "";
                    BrowseList.Children.Add(BrowseItem(title, "Play on this PC", isFolder: false, () =>
                    {
                        App.Main?.PlayTvFolder(videoPath, title);
                        return Task.CompletedTask;
                    }));
                    added++;
                }
            }

            if (added == 0)
            {
                EmptyBrowse.Text = string.IsNullOrWhiteSpace(_tvBrowsePath)
                    ? "The TV has not shared any folders yet. On the TV open Cihazlar → TV klasörlerini paylaş."
                    : "This folder is empty.";
                EmptyBrowse.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            EmptyBrowse.Text = "The TV did not answer in time. Keep the TV on this PC, then try again.";
            EmptyBrowse.Visibility = Visibility.Visible;
            BrowseTitle.Text = "TV folders";
            BrowseCrumbs.Children.Clear();
        }
        finally
        {
            _browseBusy = false;
        }
    }

    private void PaintCrumbs()
    {
        BrowseCrumbs.Children.Clear();
        var crumbs = BuildCrumbs(_tvBrowsePath);
        for (var i = 0; i < crumbs.Count; i++)
        {
            var crumb = crumbs[i];
            if (i > 0)
            {
                BrowseCrumbs.Children.Add(new TextBlock
                {
                    Text = "/",
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["GrokMutedBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var last = i == crumbs.Count - 1;
            if (last)
            {
                BrowseCrumbs.Children.Add(new TextBlock
                {
                    Text = crumb.Name,
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["GrokMutedBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            else
            {
                var link = new HyperlinkButton
                {
                    Content = crumb.Name,
                    Padding = new Thickness(0),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["GrokAccentBrush"],
                };
                var target = crumb.Path;
                link.Click += async (_, _) => await LoadTvBrowse(target);
                BrowseCrumbs.Children.Add(link);
            }
        }
    }

    private List<(string Name, string Path)> BuildCrumbs(string path)
    {
        var crumbs = new List<(string, string)> { ("TV", "") };
        if (string.IsNullOrWhiteSpace(path))
        {
            return crumbs;
        }

        var grant = _tvGrants.FirstOrDefault(item =>
            path.Equals(item, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(item.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(item.TrimEnd('\\', '/') + "/", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(grant))
        {
            crumbs.Add((FolderName(path), path));
            return crumbs;
        }

        crumbs.Add((FolderName(grant), grant));
        if (path.Equals(grant, StringComparison.OrdinalIgnoreCase))
        {
            return crumbs;
        }

        var relative = path[grant.Length..].Trim('\\', '/');
        var acc = grant;
        foreach (var part in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            acc = Path.Combine(acc, part);
            crumbs.Add((part, acc));
        }

        return crumbs;
    }

    private Border BrowseItem(string title, string meta, bool isFolder, Func<Task> activate)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon
        {
            Glyph = isFolder ? "\uE8B7" : "\uE714",
            FontSize = 14,
            Foreground = (Brush)Application.Current.Resources[isFolder ? "GrokMutedBrush" : "GrokAccentBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = meta,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["GrokMutedBrush"],
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var card = Card(row, 10);
        card.Tag = "hit";
        card.PointerEntered += (_, _) => card.BorderBrush = (Brush)Application.Current.Resources["GrokAccentBrush"];
        card.PointerExited += (_, _) => card.BorderBrush = (Brush)Application.Current.Resources["GrokLineBrush"];
        card.Tapped += async (_, e) =>
        {
            e.Handled = true;
            await activate();
        };
        return card;
    }

    private static Border Card(UIElement child, double pad) =>
        new()
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 22, 22, 24)),
            BorderBrush = (Brush)Application.Current.Resources["GrokLineBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(pad, 10, pad, 10),
            Child = child,
        };

    private static string FolderName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string JobLabel(LinkJobDto job)
    {
        var status = job.Status switch
        {
            "receiving" or "sending" => "Copying",
            "ready" or "done" or "queued" => "Done",
            "starting" => "Starting",
            _ => string.IsNullOrWhiteSpace(job.Status) ? "Working" : job.Status,
        };
        if (job.Total > 0)
        {
            return $"{status}  ·  {Percent(job.Done, job.Total)}";
        }

        return status;
    }

    private static string Percent(long done, long total)
    {
        if (total <= 0)
        {
            return "";
        }

        return Math.Clamp((int)Math.Round(100.0 * done / total), 0, 100) + "%";
    }

    private Style? ButtonStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    private void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (_server.TryAcceptPin(PinBox.Text))
        {
            PairCard.Visibility = Visibility.Collapsed;
            PinBox.Text = "";
            _lastPaint = "";
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
            if (current is Button or TextBox or HyperlinkButton or RepeatButton or ScrollBar)
            {
                return true;
            }

            if (current is FrameworkElement element && element.Tag as string == "hit")
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
