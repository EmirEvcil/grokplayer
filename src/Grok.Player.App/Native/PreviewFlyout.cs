using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Grok.Player.App.Native;

internal sealed class PreviewFlyout : IDisposable
{
    internal const int DipWidth = 268;
    internal const int DipHeight = 176;
    private readonly Window _window;
    private readonly Grid _frame;
    private readonly Image _image;
    private readonly TextBlock _time;
    private readonly WndProc _wndProc;
    private string? _path;
    private bool _visible;
    private int _loadGeneration;
    private nint _originalWndProc;
    private nint _hookedHwnd;

    public PreviewFlyout()
    {
        _wndProc = WindowProc;
        _image = new Image
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.UniformToFill
        };
        _time = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 201, 58))
        };
        _frame = new Grid
        {
            Width = 256,
            Height = 144,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0)),
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 256, 144) }
        };
        _frame.Children.Add(_image);
        _frame.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(8, 2, 8, 2),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 16, 16, 18)),
            Child = _time
        });

        _window = new Window
        {
            Content = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(245, 16, 16, 18)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 58, 66)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                Child = _frame
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
        _window.AppWindow.Resize(new SizeInt32(DipWidth, DipHeight));
    }

    public void AttachOwner(nint owner)
    {
        if (owner == 0)
        {
            return;
        }

        SetWindowLongPtr(WindowNative.GetWindowHandle(_window), -8, owner);
        ApplyInputStyles();
    }

    public void Show(string timeText, string? imagePath, int screenX, int screenY, double scale) =>
        Show(timeText, imagePath, screenX, screenY, scale, holdPreviousImage: false);

    public void Show(string timeText, string? imagePath, int screenX, int screenY, double scale, bool holdPreviousImage)
    {
        _time.Text = string.IsNullOrWhiteSpace(timeText) ? "00:00" : timeText;
        var missing = string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath);
        if (missing)
        {
            if (!holdPreviousImage)
            {
                _path = null;
                _loadGeneration++;
                _image.Source = null;
                _image.Visibility = Visibility.Collapsed;
            }
        }
        else if (imagePath is not null && imagePath != _path)
        {
            _path = imagePath;
            var generation = ++_loadGeneration;
            if (!holdPreviousImage)
            {
                _image.Source = null;
                _image.Visibility = Visibility.Collapsed;
            }

            _ = LoadImageAsync(imagePath, generation);
        }
        else if (_image.Source is not null)
        {
            _image.Visibility = Visibility.Visible;
        }

        var pixelW = Math.Max(1, (int)Math.Round(DipWidth * scale));
        var pixelH = Math.Max(1, (int)Math.Round(DipHeight * scale));
        if (_window.AppWindow.Size.Width != pixelW || _window.AppWindow.Size.Height != pixelH)
        {
            _window.AppWindow.Resize(new SizeInt32(pixelW, pixelH));
        }

        var x = Math.Max(0, screenX);
        var y = Math.Max(0, screenY);
        _window.AppWindow.Move(new PointInt32(x, y));
        if (!_visible)
        {
            _window.AppWindow.Show(false);
            _visible = true;
            ApplyInputStyles();
        }
    }

    public void Clear()
    {
        _path = null;
        _loadGeneration++;
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
        Hide();
    }

    public void Hide()
    {
        if (!_visible)
        {
            return;
        }

        _window.AppWindow.Hide();
        _visible = false;
    }

    public void Dispose()
    {
        Hide();
        if (_hookedHwnd != 0 && _originalWndProc != 0)
        {
            SetWindowLongPtr(_hookedHwnd, GwlpWndProc, _originalWndProc);
            _hookedHwnd = 0;
            _originalWndProc = 0;
        }

        _window.Close();
    }

    private async Task LoadImageAsync(string path, int generation)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            if (bytes.Length == 0 || generation != _loadGeneration)
            {
                return;
            }

            var ras = new InMemoryRandomAccessStream();
            await ras.WriteAsync(bytes.AsBuffer());
            ras.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(ras);
            if (generation == _loadGeneration)
            {
                _image.Source = bitmap;
                _image.Visibility = Visibility.Visible;
            }
        }
        catch (Exception)
        {
            if (generation == _loadGeneration)
            {
                _path = null;
                _image.Source = null;
                _image.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ApplyInputStyles()
    {
        var hwnd = WindowNative.GetWindowHandle(_window);
        if (hwnd == 0)
        {
            return;
        }

        var style = GetWindowLongPtr(hwnd, GwlExStyle);
        style = (nint)((long)style | WsExTransparent | WsExNoActivate | WsExToolWindow);
        SetWindowLongPtr(hwnd, GwlExStyle, style);
        if (_hookedHwnd == 0)
        {
            _originalWndProc = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_wndProc));
            _hookedHwnd = hwnd;
        }
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam)
    {
        if (message == WmNcHitTest)
        {
            return HtTransparent;
        }

        return CallWindowProc(_originalWndProc, hwnd, message, wParam, lParam);
    }

    private const int GwlExStyle = -20;
    private const int GwlpWndProc = -4;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private delegate nint WndProc(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint previous, nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}
