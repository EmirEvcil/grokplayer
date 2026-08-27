using System.Runtime.InteropServices;
using Grok.Player.App.Native;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRT.Interop;

namespace Grok.Player.App;

public sealed partial class ResumeWindow : Window
{
    private readonly nint _owner;
    private bool _dragging;
    private Point32 _dragMouse;
    private PointInt32 _dragWindow;
    private bool _decided;

    public ResumeWindow(nint owner, string message)
    {
        _owner = owner;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        SetTitleBar(TitleDrag);
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.Title = "Resume playback";
        var hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.Resize(DialogLayout.Px(hwnd, 460, 260));
        MessageText.Text = message;
        AppWindow.Closing += (_, args) =>
        {
            if (!_decided)
            {
                Declined?.Invoke();
            }
        };

        WindowChrome.ApplyLook(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (owner != 0)
        {
            SetWindowLongPtr(hwnd, GwlpHwndParent, owner);
        }
    }

    public event Action? Continued;
    public event Action? Declined;

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        _decided = true;
        Continued?.Invoke();
        Close();
    }

    private void StartOver_Click(object sender, RoutedEventArgs e)
    {
        _decided = true;
        Declined?.Invoke();
        Close();
    }

    private void EmptyArea_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed || e.OriginalSource is Button)
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

        AppWindow.Move(new PointInt32(
            _dragWindow.X + now.X - _dragMouse.X,
            _dragWindow.Y + now.Y - _dragMouse.Y));
    }

    private void EmptyArea_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
    }

    private const int GwlpHwndParent = -8;

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
