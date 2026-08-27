using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Grok.Player.App.Native;

internal static class DialogLayout
{
    public static SizeInt32 Px(nint hwnd, int dipWidth, int dipHeight, double raster = 0)
    {
        var scale = raster;
        if (scale <= 0)
        {
            scale = Scale(hwnd);
        }

        return new SizeInt32(
            Math.Max(200, (int)Math.Round(dipWidth * scale)),
            Math.Max(160, (int)Math.Round(dipHeight * scale)));
    }

    public static double Scale(nint hwnd)
    {
        if (hwnd != 0)
        {
            var monitor = MonitorFromWindow(hwnd, 2);
            if (monitor != 0 && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }

            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0)
            {
                return dpi / 96.0;
            }
        }

        return 1;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
