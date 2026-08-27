using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Grok.Player.App.Native;

internal sealed class ActionOsd : IDisposable
{
    private readonly Window _window;
    private readonly TextBlock _text;
    private nint _owner;
    private bool _visible;

    public bool IsVisible => _visible;

    public ActionOsd()
    {
        _text = new TextBlock
        {
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 201, 58)),
            TextWrapping = TextWrapping.NoWrap
        };

        _window = new Window
        {
            Content = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 1, 1, 1)),
                Padding = new Thickness(14, 10, 16, 8),
                Child = _text
            }
        };

        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = false;
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.IsShownInSwitchers = false;
        _window.AppWindow.Resize(new SizeInt32(640, 36));
    }

    public void AttachOwner(nint owner)
    {
        _owner = owner;
        if (owner == 0)
        {
            return;
        }

        SetWindowLongPtr(WindowNative.GetWindowHandle(_window), GwlpHwndParent, owner);
        ApplyStyles();
    }

    public void Show(string message, int screenX, int screenY, double scale)
    {
        var app = _window.AppWindow;
        if (app is null)
        {
            return;
        }

        _text.Text = message ?? "";
        var width = Math.Max(160, Math.Min(900, (int)Math.Round((_text.Text.Length * 8 + 40) * scale)));
        var height = Math.Max(28, (int)Math.Round(34 * scale));
        try
        {
            app.Resize(new SizeInt32(width, height));
            app.Move(new PointInt32(Math.Max(0, screenX), Math.Max(0, screenY)));
            if (!_visible)
            {
                app.Show(false);
                _visible = true;
                ApplyStyles();
                RestackAboveOwner();
            }
        }
        catch (Exception)
        {
            _visible = false;
        }
    }

    public void RestackAboveOwner()
    {
        if (!_visible || _owner == 0)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(_window);
        if (hwnd == 0)
        {
            return;
        }

        SetWindowPos(
            hwnd,
            OwnerIsTopmost() ? HwndTopmost : HwndNoTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);

        var above = GetWindow(_owner, GwHwndPrev);
        if (above == hwnd)
        {
            return;
        }

        SetWindowPos(
            hwnd,
            above == 0 ? HwndTop : above,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    public void Hide()
    {
        if (!_visible)
        {
            return;
        }

        try
        {
            _window.AppWindow?.Hide();
        }
        catch (Exception)
        {
        }

        _visible = false;
    }

    public void Dispose()
    {
        Hide();
        _window.Close();
    }

    private void ApplyStyles()
    {
        var hwnd = WindowNative.GetWindowHandle(_window);
        if (hwnd == 0)
        {
            return;
        }

        var ex = GetWindowLongPtr(hwnd, GwlExStyle);
        ex = (nint)((long)ex | WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow);
        SetWindowLongPtr(hwnd, GwlExStyle, ex);
        SetLayeredWindowAttributes(hwnd, 0x00010101, 0, LwaColorKey);
    }

    private bool OwnerIsTopmost()
    {
        return _owner != 0 && ((long)GetWindowLongPtr(_owner, GwlExStyle) & WsExTopmost) != 0;
    }

    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const int LwaColorKey = 0x00000001;
    private const uint GwHwndPrev = 3;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly nint HwndTop = 0;
    private static readonly nint HwndTopmost = -1;
    private static readonly nint HwndNoTopmost = -2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint uCmd);
}
