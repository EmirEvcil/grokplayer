namespace Grok.Player.Core.Preview;

public readonly record struct SeekPreviewState(
    bool IsVisible,
    TimeSpan Time,
    string TimeText,
    string? ImagePath,
    double NormalizedPosition);
