using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Grok.Player.App.Native;

internal sealed class NativeVideoSurface : IDisposable
{
    private const string ClassName = "GrokPlayerVideoHost";
    private static bool _classRegistered;
    private static bool _oleReady;
    private static readonly object RegisterLock = new();
    private static readonly Dictionary<nint, NativeVideoSurface> Instances = [];

    private nint _hwnd;
    private readonly nint _parent;
    private readonly DropTarget _dropTarget;
    private bool _dropRegistered;

    public NativeVideoSurface(nint parent)
    {
        if (parent == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parent));
        }

        _parent = parent;
        _dropTarget = new DropTarget(this);
        RegisterWindowClass();
        EnsureOle();
        _hwnd = CreateWindowExW(
            0,
            ClassName,
            string.Empty,
            WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0, 0, 16, 16,
            parent,
            0,
            GetModuleHandleW(null),
            0);

        if (_hwnd == 0)
        {
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
        }

        Instances[_hwnd] = this;
        _dropRegistered = RegisterDragDrop(_hwnd, _dropTarget) == 0;
    }

    public nint Handle => _hwnd;

    public event Action<IReadOnlyList<string>>? FilesDropped;
    public event Action<int, int>? MouseMoved;
    public event Action<int, int>? RightClicked;
    public event Action? MouseLeft;
    public event Action<int>? ControlDigit;
    public Func<bool>? AllowDrag { get; set; }
    public Func<bool>? ClientHitsOnly { get; set; }
    public bool HideCursor { get; set; }

    private bool _trackingLeave;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private int _lastW;
    private int _lastH;
    private bool _hidden;
    private bool _hasRegion;
    private int _cutTop;
    private int _cutBottom;

    public void Move(int x, int y, int width, int height)
    {
        if (_hwnd == 0)
        {
            return;
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var samePlace = x == _lastX && y == _lastY && width == _lastW && height == _lastH;
        if (samePlace)
        {
            return;
        }

        _lastX = x;
        _lastY = y;
        _lastW = width;
        _lastH = height;
        var flags = SwpNoActivate | SwpNoZOrder | SwpNoCopyBits;
        if (_hidden)
        {
            return;
        }

        SetWindowPos(_hwnd, 0, x, y, width, height, flags);
        if (_cutTop > 0 || _cutBottom > 0)
        {
            ApplyRegion();
        }
    }

    public void Show()
    {
        if (_hwnd == 0 || !_hidden)
        {
            return;
        }

        ShowWindow(_hwnd, SwShowNa);
        _hidden = false;
        _lastX = int.MinValue;
    }

    public void Hide()
    {
        if (_hwnd == 0 || _hidden)
        {
            return;
        }

        ShowWindow(_hwnd, SwHide);
        _hidden = true;
        _lastX = int.MinValue;
    }

    public bool TryCaptureStill(string path)
    {
        if (_hwnd == 0 || _hidden || _lastW < 16 || _lastH < 16)
        {
            return false;
        }

        var hdcWnd = GetDC(_hwnd);
        if (hdcWnd == 0)
        {
            return false;
        }

        var hdcMem = CreateCompatibleDC(hdcWnd);
        var destW = Math.Min(320, _lastW);
        var destH = Math.Max(1, _lastH * destW / Math.Max(1, _lastW));
        var bmp = CreateCompatibleBitmap(hdcWnd, destW, destH);
        var old = SelectObject(hdcMem, bmp);
        try
        {
            if (!PrintWindow(_hwnd, hdcMem, 2) &&
                !StretchBlt(hdcMem, 0, 0, destW, destH, hdcWnd, 0, 0, _lastW, _lastH, SrcCopy))
            {
                return false;
            }

            return WriteBmp(path, hdcMem, bmp, destW, destH);
        }
        finally
        {
            SelectObject(hdcMem, old);
            DeleteObject(bmp);
            DeleteDC(hdcMem);
            ReleaseDC(_hwnd, hdcWnd);
        }
    }

    public void Dispose()
    {
        if (_hwnd == 0)
        {
            return;
        }

        if (_dropRegistered)
        {
            RevokeDragDrop(_hwnd);
            _dropRegistered = false;
        }

        Instances.Remove(_hwnd);
        if (IsWindow(_hwnd))
        {
            DestroyWindow(_hwnd);
        }

        _hwnd = 0;
    }

    public void SetOverlayCutouts(int top, int bottom)
    {
        _cutTop = Math.Max(0, top);
        _cutBottom = Math.Max(0, bottom);
        ApplyRegion();
    }

    private void ApplyRegion()
    {
        if (_hwnd == 0 || _lastW <= 0 || _lastH <= 0)
        {
            return;
        }

        if (_cutTop <= 0 && _cutBottom <= 0)
        {
            if (_hasRegion)
            {
                SetWindowRgn(_hwnd, 0, true);
                _hasRegion = false;
            }

            return;
        }

        var full = CreateRectRgn(0, 0, _lastW, _lastH);
        var holeTop = _cutTop > 0 ? CreateRectRgn(0, 0, _lastW, Math.Min(_cutTop, _lastH)) : 0;
        var holeBottom = _cutBottom > 0 ? CreateRectRgn(0, Math.Max(0, _lastH - _cutBottom), _lastW, _lastH) : 0;
        if (holeTop != 0)
        {
            CombineRgn(full, full, holeTop, 4);
            DeleteObject(holeTop);
        }

        if (holeBottom != 0)
        {
            CombineRgn(full, full, holeBottom, 4);
            DeleteObject(holeBottom);
        }

        SetWindowRgn(_hwnd, full, true);
        _hasRegion = true;
    }

    private static void RegisterWindowClass()
    {
        lock (RegisterLock)
        {
            if (_classRegistered)
            {
                return;
            }

            var wnd = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProc),
                hInstance = GetModuleHandleW(null),
                hCursor = LoadCursor(0, IdcArrow),
                hbrBackground = GetStockObject(BlackBrush),
                lpszClassName = ClassName
            };

            if (RegisterClassExW(ref wnd) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorClassAlreadyExists)
                {
                    throw new InvalidOperationException($"RegisterClassEx failed ({error}).");
                }
            }

            _classRegistered = true;
        }
    }

    private static void EnsureOle()
    {
        if (_oleReady)
        {
            return;
        }

        OleInitialize(0);
        _oleReady = true;
    }

    private static nint DefaultWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (Instances.TryGetValue(hwnd, out var surface))
        {
            if (msg is WmKeyDown or WmSysKeyDown)
            {
                var key = (int)wParam;
                if ((GetKeyState(VkControl) & 0x8000) != 0 &&
                    key is >= 0x31 and <= 0x35 or >= 0x61 and <= 0x65)
                {
                    surface.ControlDigit?.Invoke(key);
                    return 0;
                }
            }

            if (msg == WmNcHitTest)
            {
                return surface.HitTest(lParam);
            }

            if (msg == WmLButtonDown)
            {
                if (surface.AllowDrag?.Invoke() != false)
                {
                    if (GetCursorPos(out var cursor))
                    {
                        ReleaseCapture();
                        SendMessage(surface._parent, WmNcLButtonDown, HtCaption, PackPoint(cursor.X, cursor.Y));
                    }
                }

                return 0;
            }

            if (msg == WmMouseMove)
            {
                surface.TrackLeave();
                if (surface.HideCursor)
                {
                    surface.HideCursor = false;
                    SetCursor(LoadCursor(0, IdcArrow));
                }

                var x = (short)(lParam & 0xFFFF);
                var y = (short)((lParam >> 16) & 0xFFFF);
                surface.MouseMoved?.Invoke(x, y);
            }

            if (msg == WmMouseLeave)
            {
                surface._trackingLeave = false;
                surface.HideCursor = false;
                SetCursor(LoadCursor(0, IdcArrow));
                surface.MouseLeft?.Invoke();
                return 0;
            }

            if (msg == WmRButtonUp)
            {
                if (GetCursorPos(out var cursor))
                {
                    surface.RightClicked?.Invoke(cursor.X, cursor.Y);
                }

                return 0;
            }

            if (msg == WmContextMenu)
            {
                return 0;
            }
        }

        if (msg == WmSetCursor)
        {
            if (Instances.TryGetValue(hwnd, out var cursorSurface) && cursorSurface.HideCursor)
            {
                SetCursor(0);
            }
            else
            {
                SetCursor(LoadCursor(0, IdcArrow));
            }

            return 1;
        }

        if (msg == WmEraseBkgnd)
        {
            return 1;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private nint HitTest(nint lParam)
    {
        if (ClientHitsOnly?.Invoke() == true)
        {
            return HtClient;
        }

        var screenX = (short)(lParam & 0xFFFF);
        var screenY = (short)((lParam >> 16) & 0xFFFF);
        if (!GetWindowRect(_parent, out var parent))
        {
            return HtClient;
        }

        const int grip = 8;
        if (screenX <= parent.Left + grip ||
            screenX >= parent.Right - grip ||
            screenY <= parent.Top + grip ||
            screenY >= parent.Bottom - grip)
        {
            return HtTransparent;
        }

        return HtClient;
    }

    public void ApplyCursor()
    {
        SetCursor(HideCursor ? 0 : LoadCursor(0, IdcArrow));
    }

    private void TrackLeave()
    {
        if (_trackingLeave || _hwnd == 0)
        {
            return;
        }

        var track = new TrackMouseEventNative
        {
            Size = TrackMouseEventNative.SizeInBytes,
            Flags = TmeLeave,
            HwndTrack = _hwnd
        };
        if (TrackMouseEvent(ref track))
        {
            _trackingLeave = true;
        }
    }

    private void RaiseFiles(IReadOnlyList<string> files)
    {
        if (files.Count > 0)
        {
            FilesDropped?.Invoke(files);
        }
    }

    private static readonly WndProcDelegate WndProc = DefaultWndProc;

    private static nint PackPoint(int x, int y) =>
        unchecked((nint)(int)((uint)(ushort)x | ((uint)(ushort)y << 16)));

    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int BlackBrush = 4;
    private const int SwHide = 0;
    private const int SwShowNa = 8;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoCopyBits = 0x0100;
    private const uint SwpShowWindow = 0x0040;
    private const int WmSetCursor = 0x0020;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkControl = 0x11;
    private const int WmEraseBkgnd = 0x0014;
    private const int WmNcHitTest = 0x0084;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseLeave = 0x02A3;
    private const uint TmeLeave = 0x00000002;
    private const int IdcArrow = 32512;
    private const int ErrorClassAlreadyExists = 1410;
    private const int HtTransparent = -1;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int WmNcLButtonDown = 0x00A1;
    private const int DropEffectMove = 2;
    private const short CfHdrop = 15;

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [ComImport]
    [Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleDropTarget
    {
        [PreserveSig]
        int DragEnter([MarshalAs(UnmanagedType.Interface)] object pDataObj, int grfKeyState, PointL pt, ref int pdwEffect);

        [PreserveSig]
        int DragOver(int grfKeyState, PointL pt, ref int pdwEffect);

        [PreserveSig]
        int DragLeave();

        [PreserveSig]
        int Drop([MarshalAs(UnmanagedType.Interface)] object pDataObj, int grfKeyState, PointL pt, ref int pdwEffect);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class DropTarget : IOleDropTarget
    {
        private readonly NativeVideoSurface _owner;

        public DropTarget(NativeVideoSurface owner) => _owner = owner;

        public int DragEnter(object pDataObj, int grfKeyState, PointL pt, ref int pdwEffect)
        {
            pdwEffect = DropEffectMove;
            return 0;
        }

        public int DragOver(int grfKeyState, PointL pt, ref int pdwEffect)
        {
            pdwEffect = DropEffectMove;
            return 0;
        }

        public int DragLeave()
        {
            return 0;
        }

        public int Drop(object pDataObj, int grfKeyState, PointL pt, ref int pdwEffect)
        {
            pdwEffect = DropEffectMove;
            _owner.RaiseFiles(ReadHdrop(pDataObj));
            return 0;
        }
    }

    private static List<string> ReadHdrop(object data)
    {
        var files = new List<string>();
        if (data is not IDataObject ole)
        {
            return files;
        }

        var format = HdropFormat();
        ole.GetData(ref format, out var medium);
        try
        {
            if (medium.unionmember == 0)
            {
                return files;
            }

            var count = DragQueryFile(medium.unionmember, 0xFFFFFFFF, null, 0);
            var buffer = new char[1024];
            for (uint i = 0; i < count; i++)
            {
                var len = DragQueryFile(medium.unionmember, i, buffer, (uint)buffer.Length);
                if (len > 0)
                {
                    files.Add(new string(buffer, 0, (int)len));
                }
            }
        }
        catch (COMException)
        {
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }

        return files;
    }

    private static FORMATETC HdropFormat() => new()
    {
        cfFormat = CfHdrop,
        ptd = 0,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = TYMED.TYMED_HGLOBAL
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent,
        nint hWndMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int i);

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint hInstance, nint lpCursorName);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint hCursor);

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TrackMouseEventNative lpEventTrack);

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventNative
    {
        public static readonly uint SizeInBytes = (uint)Marshal.SizeOf<TrackMouseEventNative>();
        public uint Size;
        public uint Flags;
        public nint HwndTrack;
        public uint HoverTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out ScreenPoint lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(nint hDrop, uint iFile, char[]? lpszFile, uint cch);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(nint dest, nint src1, nint src2, int combineMode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hWnd, nint hRgn, bool bRedraw);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(nint pvReserved);

    [DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(nint hwnd, IOleDropTarget pDropTarget);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(nint hwnd);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(nint hdcDest, int xDest, int yDest, int wDest, int hDest, nint hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);

    private const uint SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    private static bool WriteBmp(string path, nint hdc, nint hbm, int width, int height)
    {
        var stride = ((width * 3 + 3) / 4) * 4;
        var pixels = new byte[stride * height];
        var info = new BitmapInfo
        {
            Size = 40,
            Width = width,
            Height = height,
            Planes = 1,
            BitCount = 24
        };
        if (GetDIBits(hdc, hbm, 0, (uint)height, pixels, ref info, 0) == 0)
        {
            return false;
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54 + pixels.Length);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(pixels.Length);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(pixels);
        return true;
    }
}
