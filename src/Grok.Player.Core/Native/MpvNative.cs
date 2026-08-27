using System.Reflection;
using System.Runtime.InteropServices;

namespace Grok.Player.Core.Native;

public sealed class MpvNative : IMpvNative
{
    private nint _handle;
    private bool _terminated;

    static MpvNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, ResolveLibMpv);
    }

    public MpvNative()
    {
        _handle = MpvNativeMethods.mpv_create();
        if (_handle == 0)
        {
            throw new MpvException("mpv_create returned null. Is libmpv-2.dll next to the executable?");
        }
    }

    public bool IsTerminated => _terminated;

    public static ulong GetClientApiVersion() => MpvNativeMethods.mpv_client_api_version();

    public static bool TryFindLibrary(out string path)
    {
        path = FindLibraryPath() ?? string.Empty;
        return path.Length > 0 && File.Exists(path);
    }

    public void SetOption(string name, string value)
    {
        EnsureAlive();
        var n = Utf8Marshal.Alloc(name);
        var v = Utf8Marshal.Alloc(value);
        try
        {
            MpvException.ThrowIfError(MpvNativeMethods.mpv_set_option_string(_handle, n, v), $"set option {name}");
        }
        finally
        {
            Utf8Marshal.Free(n);
            Utf8Marshal.Free(v);
        }
    }

    public void SetOptionLong(string name, long value)
    {
        EnsureAlive();
        var n = Utf8Marshal.Alloc(name);
        var data = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            Marshal.WriteInt64(data, value);
            MpvException.ThrowIfError(
                MpvNativeMethods.mpv_set_option(_handle, n, MpvFormat.Int64, data),
                $"set option {name}");
        }
        finally
        {
            Utf8Marshal.Free(n);
            Marshal.FreeHGlobal(data);
        }
    }

    public void Initialize()
    {
        EnsureAlive();
        MpvException.ThrowIfError(MpvNativeMethods.mpv_initialize(_handle), "initialize");
    }

    public void Command(params string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            throw new ArgumentException("A command requires at least one argument.", nameof(args));
        }

        EnsureAlive();
        var pointers = new nint[args.Length + 1];
        var block = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                pointers[i] = Utf8Marshal.Alloc(args[i]);
                Marshal.WriteIntPtr(block, i * IntPtr.Size, pointers[i]);
            }

            Marshal.WriteIntPtr(block, args.Length * IntPtr.Size, 0);
            MpvException.ThrowIfError(MpvNativeMethods.mpv_command(_handle, block), $"command {args[0]}");
        }
        finally
        {
            for (var i = 0; i < args.Length; i++)
            {
                Utf8Marshal.Free(pointers[i]);
            }

            Marshal.FreeHGlobal(block);
        }
    }

    public void SetPropertyString(string name, string value)
    {
        EnsureAlive();
        var n = Utf8Marshal.Alloc(name);
        var v = Utf8Marshal.Alloc(value);
        try
        {
            MpvException.ThrowIfError(MpvNativeMethods.mpv_set_property_string(_handle, n, v), $"set {name}");
        }
        finally
        {
            Utf8Marshal.Free(n);
            Utf8Marshal.Free(v);
        }
    }

    public void SetPropertyFlag(string name, bool value)
    {
        SetScalar(name, MpvFormat.Flag, value ? 1 : 0);
    }

    public void SetPropertyDouble(string name, double value)
    {
        EnsureAlive();
        var n = Utf8Marshal.Alloc(name);
        var data = Marshal.AllocHGlobal(sizeof(double));
        try
        {
            Marshal.Copy(new[] { value }, 0, data, 1);
            MpvException.ThrowIfError(
                MpvNativeMethods.mpv_set_property(_handle, n, MpvFormat.Double, data),
                $"set {name}");
        }
        finally
        {
            Utf8Marshal.Free(n);
            Marshal.FreeHGlobal(data);
        }
    }

    public void SetPropertyLong(string name, long value)
    {
        EnsureAlive();
        var n = Utf8Marshal.Alloc(name);
        var data = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            Marshal.WriteInt64(data, value);
            MpvException.ThrowIfError(
                MpvNativeMethods.mpv_set_property(_handle, n, MpvFormat.Int64, data),
                $"set {name}");
        }
        finally
        {
            Utf8Marshal.Free(n);
            Marshal.FreeHGlobal(data);
        }
    }

    public string? GetPropertyString(string name)
    {
        EnsureAlive();
        var n = Utf8Marshal.Alloc(name);
        try
        {
            var result = MpvNativeMethods.mpv_get_property_string(_handle, n);
            if (result == 0)
            {
                return null;
            }

            try
            {
                return Utf8Marshal.PtrToString(result);
            }
            finally
            {
                MpvNativeMethods.mpv_free(result);
            }
        }
        finally
        {
            Utf8Marshal.Free(n);
        }
    }

    public bool? GetPropertyFlag(string name)
    {
        var value = GetInt32(name, MpvFormat.Flag);
        return value is null ? null : value.Value != 0;
    }

    public double? GetPropertyDouble(string name)
    {
        EnsureAlive();
        var n = Utf8Marshal.Alloc(name);
        var data = Marshal.AllocHGlobal(sizeof(double));
        try
        {
            var code = MpvNativeMethods.mpv_get_property(_handle, n, MpvFormat.Double, data);
            if (code < 0)
            {
                return null;
            }

            var buffer = new double[1];
            Marshal.Copy(data, buffer, 0, 1);
            return buffer[0];
        }
        finally
        {
            Utf8Marshal.Free(n);
            Marshal.FreeHGlobal(data);
        }
    }

    public long? GetPropertyLong(string name)
    {
        EnsureAlive();
        var n = Utf8Marshal.Alloc(name);
        var data = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            var code = MpvNativeMethods.mpv_get_property(_handle, n, MpvFormat.Int64, data);
            if (code < 0)
            {
                return null;
            }

            return Marshal.ReadInt64(data);
        }
        finally
        {
            Utf8Marshal.Free(n);
            Marshal.FreeHGlobal(data);
        }
    }

    public void ObserveProperty(string name, MpvFormat format)
    {
        EnsureAlive();
        var n = Utf8Marshal.Alloc(name);
        try
        {
            MpvException.ThrowIfError(
                MpvNativeMethods.mpv_observe_property(_handle, 0, n, format),
                $"observe {name}");
        }
        finally
        {
            Utf8Marshal.Free(n);
        }
    }

    public MpvEvent WaitEvent(double timeoutSeconds)
    {
        EnsureAlive();
        var ptr = MpvNativeMethods.mpv_wait_event(_handle, timeoutSeconds);
        if (ptr == 0)
        {
            return MpvEvent.None;
        }

        var raw = Marshal.PtrToStructure<NativeEvent>(ptr);
        return Convert(raw);
    }

    public void Wakeup()
    {
        if (_handle != 0 && !_terminated)
        {
            MpvNativeMethods.mpv_wakeup(_handle);
        }
    }

    public void TerminateDestroy()
    {
        if (_terminated || _handle == 0)
        {
            _terminated = true;
            return;
        }

        MpvNativeMethods.mpv_terminate_destroy(_handle);
        _handle = 0;
        _terminated = true;
    }

    public void Dispose()
    {
        TerminateDestroy();
    }

    private void SetScalar(string name, MpvFormat format, int value)
    {
        EnsureAlive();
        var n = Utf8Marshal.Alloc(name);
        var data = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(data, value);
            MpvException.ThrowIfError(
                MpvNativeMethods.mpv_set_property(_handle, n, format, data),
                $"set {name}");
        }
        finally
        {
            Utf8Marshal.Free(n);
            Marshal.FreeHGlobal(data);
        }
    }

    private int? GetInt32(string name, MpvFormat format)
    {
        EnsureAlive();
        var n = Utf8Marshal.Alloc(name);
        var data = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            var code = MpvNativeMethods.mpv_get_property(_handle, n, format, data);
            if (code < 0)
            {
                return null;
            }

            return Marshal.ReadInt32(data);
        }
        finally
        {
            Utf8Marshal.Free(n);
            Marshal.FreeHGlobal(data);
        }
    }

    private void EnsureAlive()
    {
        ObjectDisposedException.ThrowIf(_terminated || _handle == 0, this);
    }

    private static MpvEvent Convert(NativeEvent raw)
    {
        if (raw.EventId == MpvEventId.None)
        {
            return MpvEvent.None;
        }

        if (raw.EventId == MpvEventId.EndFile && raw.Data != 0)
        {
            var end = Marshal.PtrToStructure<NativeEndFile>(raw.Data);
            return MpvEvent.EndFile(end.Reason, end.Error);
        }

        if ((raw.EventId == MpvEventId.PropertyChange || raw.EventId == MpvEventId.GetPropertyReply) && raw.Data != 0)
        {
            var prop = Marshal.PtrToStructure<NativeProperty>(raw.Data);
            return new MpvEvent
            {
                Id = raw.EventId,
                Error = raw.Error,
                PropertyName = Utf8Marshal.PtrToString(prop.Name),
                PropertyFormat = prop.Format,
                PropertyValue = ReadPropertyValue(prop)
            };
        }

        return new MpvEvent { Id = raw.EventId, Error = raw.Error };
    }

    private static object? ReadPropertyValue(NativeProperty prop)
    {
        if (prop.Data == 0 || prop.Format == MpvFormat.None)
        {
            return null;
        }

        return prop.Format switch
        {
            MpvFormat.Flag => Marshal.ReadInt32(prop.Data) != 0,
            MpvFormat.Int64 => Marshal.ReadInt64(prop.Data),
            MpvFormat.Double => ReadDouble(prop.Data),
            MpvFormat.String or MpvFormat.OsdString => Utf8Marshal.PtrToString(Marshal.ReadIntPtr(prop.Data)),
            _ => null
        };
    }

    private static double ReadDouble(nint ptr)
    {
        var buffer = new double[1];
        Marshal.Copy(ptr, buffer, 0, 1);
        return buffer[0];
    }

    private static nint ResolveLibMpv(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("libmpv-2", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var path = FindLibraryPath();
        return path is not null && NativeLibrary.TryLoad(path, out var handle) ? handle : 0;
    }

    private static string? FindLibraryPath()
    {
        var fileName = OperatingSystem.IsWindows() ? "libmpv-2.dll" : "libmpv-2.so";
        foreach (var candidate in EnumerateSearchPaths(fileName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSearchPaths(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, fileName);
        yield return Path.Combine(baseDir, "libmpv", fileName);

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "native", "libmpv", fileName);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEvent
    {
        public MpvEventId EventId;
        public int Error;
        public ulong ReplyUserdata;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProperty
    {
        public nint Name;
        public MpvFormat Format;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEndFile
    {
        public MpvEndFileReason Reason;
        public int Error;
        public long PlaylistEntryId;
        public long PlaylistInsertId;
        public int PlaylistInsertNumEntries;
    }
}
