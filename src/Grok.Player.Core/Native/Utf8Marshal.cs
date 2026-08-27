using System.Runtime.InteropServices;
using System.Text;

namespace Grok.Player.Core.Native;

internal static class Utf8Marshal
{
    public static nint Alloc(string? value)
    {
        if (value is null)
        {
            return 0;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }

    public static string? PtrToString(nint ptr)
    {
        if (ptr == 0)
        {
            return null;
        }

        var length = 0;
        while (Marshal.ReadByte(ptr, length) != 0)
        {
            length++;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    public static void Free(nint ptr)
    {
        if (ptr != 0)
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
