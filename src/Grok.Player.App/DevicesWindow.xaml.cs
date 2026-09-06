using System.Runtime.InteropServices;
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

        _server.PairOffered += (id, name) => DispatcherQueue.TryEnqueue(() => ShowPair(name));
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
            Refresh();
            return;
        }

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

    private void ShowPair(string name)
    {
        var already = PairCard.Visibility == Visibility.Visible && IsOpen;
        PairCard.Visibility = Visibility.Visible;
        PairTitle.Text = name + " · enter the code on the TV";
        if (!already)
        {
            PinBox.Text = "";
            SetOpen(true);
            PinBox.Focus(FocusState.Programmatic);
        }
    }

    private void Refresh()
    {
        ThisPcMeta.Text = $"{_server.Name} · {_server.Host}:{_server.Port} · visible";
        if (PinBox.FocusState != FocusState.Unfocused)
        {
            return;
        }
        TrustList.Children.Clear();
        EmptyTrust.Visibility = _server.Tokens.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var pair in _server.Tokens)
        {
            var id = pair.Key;
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock
            {
                Text = "TV  " + id[..Math.Min(8, id.Length)],
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var remove = new Button { Content = "Remove", Padding = new Thickness(10, 4, 10, 4) };
            remove.Click += (_, _) =>
            {
                _server.Forget(id);
                Refresh();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(text);
            row.Children.Add(remove);
            TrustList.Children.Add(new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 24, 28)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Child = row,
            });
        }

        JobList.Children.Clear();
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
