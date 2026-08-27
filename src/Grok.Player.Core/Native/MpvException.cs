namespace Grok.Player.Core.Native;

public sealed class MpvException : Exception
{
    public int ErrorCode { get; }

    public MpvException(string message)
        : base(message)
    {
    }

    public MpvException(int errorCode, string operation)
        : base($"{operation} failed: {Describe(errorCode)} ({errorCode}).")
    {
        ErrorCode = errorCode;
    }

    public static void ThrowIfError(int errorCode, string operation)
    {
        if (errorCode < 0)
        {
            throw new MpvException(errorCode, operation);
        }
    }

    public static string Describe(int errorCode) => errorCode switch
    {
        0 => "success",
        -1 => "event queue full",
        -2 => "out of memory",
        -3 => "not initialized",
        -4 => "invalid parameter",
        -5 => "option not found",
        -6 => "option format",
        -7 => "option error",
        -8 => "property not found",
        -9 => "property format",
        -10 => "property unavailable",
        -11 => "property error",
        -12 => "command error",
        -13 => "loading failed",
        -14 => "audio init failed",
        -15 => "video init failed",
        -16 => "nothing to play",
        -17 => "unknown format",
        -18 => "unsupported",
        -19 => "not implemented",
        -20 => "generic error",
        _ => "mpv error"
    };
}
