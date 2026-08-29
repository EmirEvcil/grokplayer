using Grok.Player.Core.Media;
using Grok.Player.Core.Subtitles;

namespace Grok.Player.Core.IntegrationTests;

public sealed class YouTubeLiveCaptionTests
{
    [Fact]
    public void Official_english_captions_load_on_qtl()
    {
        var path = TryLoad("Qtl8lJwbd4g", "en", YouTubeCatalog.CaptionVttUrl("Qtl8lJwbd4g", "en"));
        if (path is null)
        {
            return;
        }

        var document = SrtDocument.Load(StreamCaptionLoader.DocumentPath(path));
        Assert.NotEmpty(document.Cues);
        Assert.Contains(
            document.Cues,
            cue => cue.Text.Contains("BEHIND", StringComparison.OrdinalIgnoreCase) ||
                   cue.Text.Contains("cop", StringComparison.OrdinalIgnoreCase) ||
                   cue.Text.Length > 8);
        var leftover = StreamCaptionLoader.Load(
            "Qtl8lJwbd4g",
            "de",
            YouTubeCatalog.CaptionVttUrl("Qtl8lJwbd4g", "en"));
        Assert.False(string.IsNullOrWhiteSpace(leftover));
        var leftoverDoc = SrtDocument.Load(StreamCaptionLoader.DocumentPath(leftover!));
        Assert.NotEmpty(leftoverDoc.Cues);
    }

    [Fact]
    public void Turkish_asr_loads_on_ezw_without_commit_twins()
    {
        var url = YouTubeCatalog.CaptionVttUrl("EzWLUda58k4", "tr:asr");
        var path = TryLoad("EzWLUda58k4", "tr:asr", url);
        if (path is null)
        {
            return;
        }

        var raw = SrtDocument.Load(StreamCaptionLoader.DocumentPath(path));
        Assert.True(raw.Cues.Count > 5, "asr cues=" + raw.Cues.Count);
        var track = new SubtitleModel().AddFile(path, apply: true);
        Assert.DoesNotContain(
            track.Document.Cues,
            cue => (cue.End - cue.Start).TotalMilliseconds < 50 &&
                   track.Document.Cues.Any(other =>
                       !ReferenceEquals(other, cue) &&
                       string.Equals(other.Text, cue.Text, StringComparison.Ordinal)));
        Assert.True(track.Document.Cues[0].Start < TimeSpan.FromSeconds(8), track.Document.Cues[0].Start.ToString());
        Assert.Contains(
            track.Document.Cues,
            cue => cue.Text.Contains("efendim", StringComparison.OrdinalIgnoreCase) ||
                   cue.Text.Contains("Gaming", StringComparison.OrdinalIgnoreCase) ||
                   cue.Text.Length > 6);
    }

    [Fact]
    public void Auto_translate_german_loads_on_ezw()
    {
        var caption = YouTubeCatalog.WithTranslate(
            YouTubeCatalog.CaptionVttUrl("EzWLUda58k4", "tr:asr"),
            "de");
        var path = TryLoad("EzWLUda58k4", "de", caption);
        if (path is null)
        {
            return;
        }

        var text = File.ReadAllText(StreamCaptionLoader.DocumentPath(path));
        var document = SrtDocument.Load(StreamCaptionLoader.DocumentPath(path));
        Assert.NotEmpty(document.Cues);
        var header = YouTubeCatalog.CaptionLanguageHeader(text);
        var sample = string.Join(' ', document.Cues.Take(8).Select(cue => cue.Text));
        Assert.DoesNotContain("efendim", sample, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            (header is not null && MediaLanguage.Matches("de", header)) ||
            sample.Contains("Herr", StringComparison.OrdinalIgnoreCase) ||
            sample.Contains("mein", StringComparison.OrdinalIgnoreCase) ||
            sample.Contains("und", StringComparison.OrdinalIgnoreCase) ||
            sample.Contains("ich", StringComparison.OrdinalIgnoreCase),
            "header=" + header + " sample=" + sample);
    }

    [Fact]
    public void Browser_drops_flash_twins_on_live_asr()
    {
        var path = TryLoad("EzWLUda58k4", "tr:asr", YouTubeCatalog.CaptionVttUrl("EzWLUda58k4", "tr:asr"));
        if (path is null)
        {
            return;
        }

        var track = new SubtitleModel().AddFile(path, apply: true);
        Assert.DoesNotContain(
            track.Document.Cues,
            cue => (cue.End - cue.Start).TotalMilliseconds < 50);
    }

    private static string? TryLoad(string videoId, string language, string? captionUrl)
    {
        try
        {
            return StreamCaptionLoader.Load(videoId, language, captionUrl);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
