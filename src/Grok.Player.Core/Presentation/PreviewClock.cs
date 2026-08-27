namespace Grok.Player.Core.Presentation;

public static class PreviewClock
{
    public static string Text(bool live, TimeSpan time) =>
        live ? TimeDisplay.FormatClock(time) : TimeDisplay.FormatSeek(time);
}
