namespace Grok.Player.Core.Subtitles;

public sealed class SrtCue
{
    public SrtCue(int index, TimeSpan start, TimeSpan end, string text, IReadOnlyList<CaptionSpan>? spans = null)
    {
        Index = index;
        Start = start;
        End = end < start ? start : end;
        Text = text ?? "";
        Spans = spans is { Count: > 0 } ? spans : [new CaptionSpan(Text, null)];
    }

    public int Index { get; set; }

    public TimeSpan Start { get; set; }

    public TimeSpan End { get; set; }

    public string Text { get; set; }

    public IReadOnlyList<CaptionSpan> Spans { get; set; }

    public IReadOnlyList<(TimeSpan At, string Text)> Karaoke { get; set; } = [];

    public bool HasKaraoke => Karaoke.Count > 1;

    public long StartMs => (long)Math.Round(Start.TotalMilliseconds);

    public SrtCue WithRange(TimeSpan start, TimeSpan end) =>
        new(Index, start, end < start ? start : end, Text, Spans) { Karaoke = Karaoke };
}
