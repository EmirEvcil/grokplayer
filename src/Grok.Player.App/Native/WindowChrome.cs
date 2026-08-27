using System.Runtime.InteropServices;

namespace Grok.Player.App.Native;

internal static class WindowChrome
{
    public static Func<int, int, bool>? TryHandleContextMenu;
    public static Action? AfterPlayerRaised;

    public static void Apply(nint hwnd, string iconPath, int minWidth, int minHeight)
    {
        ApplyLook(hwnd, iconPath);
        SetMinSize(hwnd, minWidth, minHeight);
    }

    public static void LimitSize(nint hwnd, int minWidth, int minHeight)
    {
        if (hwnd == 0)
        {
            return;
        }

        if (_limits.TryGetValue(hwnd, out var existing))
        {
            existing.W = minWidth;
            existing.H = minHeight;
            return;
        }

        var limit = new SizeLimit { W = minWidth, H = minHeight };
        limit.Proc = (h, msg, wParam, lParam) => LimitHook(limit, h, msg, wParam, lParam);
        limit.Original = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(limit.Proc));
        _limits[hwnd] = limit;
    }

    public static void ApplyLook(nint hwnd, string iconPath)
    {
        var preference = DwmWcpDoNotRound;
        DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref preference, sizeof(int));
        if (File.Exists(iconPath))
        {
            var big = LoadImage(0, iconPath, ImageIcon, 256, 256, LrLoadFromFile);
            var small = LoadImage(0, iconPath, ImageIcon, 16, 16, LrLoadFromFile);
            if (big != 0)
            {
                SendMessage(hwnd, WmSetIcon, 1, big);
            }

            if (small != 0)
            {
                SendMessage(hwnd, WmSetIcon, 0, small);
            }
        }
    }

    private static void SetMinSize(nint hwnd, int minWidth, int minHeight)
    {
        _minW = minWidth;
        _minH = minHeight;
        if (_hooked)
        {
            return;
        }

        _original = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_proc));
        _hookedHwnd = hwnd;
        _hooked = true;
    }

    private static nint Hook(nint hwnd, int msg, nint wParam, nint lParam)
    {
        if (msg == WmGetMinMaxInfo)
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.ptMinTrackSize = new Point32(_minW, _minH);
            Marshal.StructureToPtr(info, lParam, false);
        }

        if (msg == WmContextMenu && lParam != unchecked((nint)(-1)))
        {
            var x = (short)(lParam & 0xFFFF);
            var y = (short)((lParam >> 16) & 0xFFFF);
            if (TryHandleContextMenu?.Invoke(x, y) == true)
            {
                return 0;
            }
        }

        var result = CallWindowProc(_original, hwnd, msg, wParam, lParam);
        if (msg == WmActivate && (wParam & 0xFFFF) != 0)
        {
            AfterPlayerRaised?.Invoke();
        }

        if (msg == WmWindowPosChanged)
        {
            AfterPlayerRaised?.Invoke();
        }

        return result;
    }

    private static nint LimitHook(SizeLimit limit, nint hwnd, int msg, nint wParam, nint lParam)
    {
        if (msg == WmGetMinMaxInfo)
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.ptMinTrackSize = new Point32(limit.W, limit.H);
            Marshal.StructureToPtr(info, lParam, false);
        }

        return CallWindowProc(limit.Original, hwnd, msg, wParam, lParam);
    }

    private sealed class SizeLimit
    {
        public nint Original;
        public int W;
        public int H;
        public WndProc Proc = null!;
    }

    private static readonly Dictionary<nint, SizeLimit> _limits = [];
    private static nint _original;
    private static nint _hookedHwnd;
    private static bool _hooked;
    private static int _minW = 800;
    private static int _minH = 500;
    private static readonly WndProc _proc = Hook;

    private const int DwmWindowCornerPreference = 33;
    private const int DwmWcpDoNotRound = 1;
    private const int GwlpWndProc = -4;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmContextMenu = 0x007B;
    private const int WmNcHitTest = 0x0084;
    private const int HtCaption = 2;
    private const int WmSetIcon = 0x0080;
    private const int WmActivate = 0x0006;
    private const int WmWindowPosChanged = 0x0047;
    private const int ImageIcon = 1;
    private const int LrLoadFromFile = 0x0010;

    private delegate nint WndProc(nint hWnd, int msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
        public Point32(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point32 ptReserved;
        public Point32 ptMaxSize;
        public Point32 ptMaxPosition;
        public Point32 ptMinTrackSize;
        public Point32 ptMaxTrackSize;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint hInst, string name, int type, int cx, int cy, int fuLoad);
}
