using System.Runtime.InteropServices;
using Grok.Player.App.Native;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Grok.Player.App;

public sealed partial class AddStreamWindow : Window
{
    private readonly nint _owner;
    private bool _stayAbove;
    private bool _playerAlwaysOnTop;
    private bool _dragging;
    private Point32 _dragMouse;
    private PointInt32 _dragWindow;

    public AddStreamWindow(nint owner, bool playerAlwaysOnTop)
    {
        _owner = owner;
        _playerAlwaysOnTop = playerAlwaysOnTop;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
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
        AppWindow.Title = "Add stream";
        var sized = WindowNative.GetWindowHandle(this);
        AppWindow.Resize(DialogLayout.Px(sized, 520, 220));
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            SetOpen(false);
        };

        var hwnd = WindowNative.GetWindowHandle(this);
        WindowChrome.ApplyLook(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (owner != 0)
        {
            SetWindowLongPtr(hwnd, GwlpHwndParent, owner);
        }

        UpdatePinVisual();
        UrlBox.Focus(FocusState.Programmatic);
    }

    public event Action<string, bool>? Submitted;

    public void SetOpen(bool open)
    {
        if (open)
        {
            AppWindow.Show();
            SyncTopmost();
            Activate();
            UrlBox.Focus(FocusState.Programmatic);
            return;
        }

        AppWindow.Hide();
    }

    public void SyncPlayerAlwaysOnTop(bool value)
    {
        _playerAlwaysOnTop = value;
        SyncTopmost();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Submit(play: false);

    private void Play_Click(object sender, RoutedEventArgs e) => Submit(play: true);

    private void Submit(bool play)
    {
        var url = UrlBox.Text?.Trim() ?? "";
        if (url.Length == 0)
        {
            return;
        }

        Submitted?.Invoke(url, play);
        UrlBox.Text = "";
        SetOpen(false);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _stayAbove = !_stayAbove;
        UpdatePinVisual();
        SyncTopmost();
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
            if (current is Button or TextBox)
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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }
}
