namespace Grok.Player.Core.Native;

public readonly struct MpvEvent
{
    public static MpvEvent None { get; } = new() { Id = MpvEventId.None };

    public MpvEventId Id { get; init; }
    public int Error { get; init; }
    public string? PropertyName { get; init; }
    public MpvFormat PropertyFormat { get; init; }
    public object? PropertyValue { get; init; }
    public MpvEndFileReason EndFileReason { get; init; }
    public int EndFileError { get; init; }

    public static MpvEvent FileLoaded() => new() { Id = MpvEventId.FileLoaded };

    public static MpvEvent EndFile(MpvEndFileReason reason, int error = 0) =>
        new() { Id = MpvEventId.EndFile, EndFileReason = reason, EndFileError = error, Error = error };

    public static MpvEvent Property(string name, object? value, MpvFormat format) =>
        new()
        {
            Id = MpvEventId.PropertyChange,
            PropertyName = name,
            PropertyValue = value,
            PropertyFormat = format
        };
}
