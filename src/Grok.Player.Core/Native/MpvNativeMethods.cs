using System.Runtime.InteropServices;

namespace Grok.Player.Core.Native;

internal static partial class MpvNativeMethods
{
    private const string Dll = "libmpv-2";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong mpv_client_api_version();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint mpv_create();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(nint ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(nint ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(nint ctx, nint name, nint data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option(nint ctx, nint name, MpvFormat format, nint data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(nint ctx, nint args);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(nint ctx, nint name, nint data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property(nint ctx, nint name, MpvFormat format, nint data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint mpv_get_property_string(nint ctx, nint name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(nint ctx, nint name, MpvFormat format, nint data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_observe_property(nint ctx, ulong userdata, nint name, MpvFormat format);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint mpv_wait_event(nint ctx, double timeout);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_wakeup(nint ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(nint data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint mpv_error_string(int error);
}
