using System.Runtime.InteropServices;

namespace Grok.Player.App.Native;

internal static class DisplayMonitor
{
    private const int DefaultToNearest = 2;

    public static (int W, int H) SizeFromWindow(nint hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, DefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
        {
            return (1920, 1080);
        }

        return (Math.Max(1, info.Monitor.Right - info.Monitor.Left),
            Math.Max(1, info.Monitor.Bottom - info.Monitor.Top));
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public int Flags;
    }
}
