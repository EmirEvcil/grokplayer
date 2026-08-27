namespace Grok.Player.Core.Native;

public enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9
}

public enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25
}

public enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5
}

public enum MpvError
{
    Success = 0,
    EventQueueFull = -1,
    NoMem = -2,
    Uninitialized = -3,
    InvalidParameter = -4,
    OptionNotFound = -5,
    OptionFormat = -6,
    OptionError = -7,
    PropertyNotFound = -8,
    PropertyFormat = -9,
    PropertyUnavailable = -10,
    PropertyError = -11,
    Command = -12,
    LoadingFailed = -13,
    AoInitFailed = -14,
    VoInitFailed = -15,
    NothingToPlay = -16,
    UnknownFormat = -17,
    Unsupported = -18,
    NotImplemented = -19,
    Generic = -20
}
