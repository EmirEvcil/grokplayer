using Grok.Player.Core.Media;
using Grok.Player.Core.Subtitles;

namespace Grok.Player.Core.Tests;

public sealed class CaptionRegressionTests
{
    // YouTube uses separate edge/shadow passes for the same visible sentence.
    private const string StyledSrv3 = """
        <timedtext format="3"><head>
        <pen id="2" fc="#A0AAB4" fo="0"/>
        <pen id="3" b="1" fc="#00BCE7" fo="254" et="4"/>
        <pen id="4" b="1" fc="#00BCE7" fo="254" et="3"/>
        </head><body>
        <p t="67" d="67"><s p="3">BEHIND </s><s p="2">ME are ONE HUNDRED cops,</s></p>
        <p t="67" d="67"><s p="4">BEHIND </s><s p="2">ME are ONE HUNDRED cops,</s></p>
        <p t="134" d="1700"><s p="3">BEHIND </s><s p="3" t="350">ME are ONE HUNDRED cops,</s></p>
        <p t="134" d="1700"><s p="4">BEHIND </s><s p="4" t="350">ME are ONE HUNDRED cops,</s></p>
        </body></timedtext>
        """;

    [Fact]
    public void Styled_srv3_preserves_color_and_deduplicates_browser_and_playback()
    {
        var vtt = YouTubeTimedText.ToVtt(StyledSrv3, "en")!;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vtt");
        File.WriteAllText(path, vtt);
        try
        {
            var track = new SubtitleModel().AddFile(path, apply: true);
            var cue = Assert.Single(track.Document.Cues);
            Assert.Equal("BEHIND ME are ONE HUNDRED cops,", cue.Text);
            Assert.All(cue.Spans, span => { Assert.Equal("#00BCE7", span.Color); Assert.True(span.Bold); });
            var rendered = Assert.Single(File.ReadAllLines(track.PlayPath).Where(line => line.StartsWith("Dialogue:")));
            Assert.Contains("\\c&H00E7BC00&", rendered);
            Assert.Contains("\\b1", rendered);
            Assert.Contains(cue.Text, rendered);
            File.Delete(track.PlayPath);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Styled_json3_preserves_pen_color_and_ignores_invisible_placeholder()
    {
        var vtt = YouTubeTimedText.ToVtt("""
        {"pens":[{}, {"fcForeColor":48359,"bAttr":1}, {"foForeAlpha":0}],
         "events":[{"tStartMs":0,"dDurationMs":2000,"segs":[
           {"utf8":"Hello ","pPenId":1},{"utf8":"world","pPenId":1,"tOffsetMs":500},
           {"utf8":"HIDDEN","pPenId":2}]}]}
        """);
        var cue = Assert.Single(SrtDocument.Parse(vtt!).Cues);
        Assert.Equal("Hello world", cue.Text);
        Assert.True(CaptionMarkup.HasColor(cue.Spans));
        Assert.All(cue.Spans, span => Assert.Equal("#00BCE7", span.Color));
    }

    [Fact]
    public void Styled_multiline_vtt_does_not_drop_the_first_line_or_lose_colors()
    {
        var cue = Assert.Single(SrtDocument.Parse("""
        WEBVTT

        00:00:01.000 --> 00:00:04.000
        <c.color00BCE7><b>Hello
        <00:00:02.000>world</b></c>
        """).Cues);
        Assert.Equal("Hello\nworld", cue.Text);
        Assert.False(cue.HasKaraoke);
        Assert.True(CaptionMarkup.HasColor(cue.Spans));
    }

    [Fact]
    public void Same_words_later_in_video_are_not_merged_across_the_gap()
    {
        var doc = SrtDocument.Parse("1\n00:00:00,000 --> 00:00:01,000\nYes\n\n2\n00:01:00,000 --> 00:01:01,000\nYes\n");
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), doc.Cues[0].End);
    }

    [Fact]
    public void Translation_never_requests_an_untranslated_source_fallback()
    {
        var url = "https://www.youtube.com/api/timedtext?v=abc&lang=tr&kind=asr&tlang=de";
        Assert.All(StreamCaptionLoader.Urls("abc", "de", url), candidate => Assert.Contains("tlang=de", candidate));
        Assert.Equal("de", StreamCaptionLoader.EffectiveLanguage("de", url));
        Assert.NotEqual(StreamCaptionLoader.CacheTag("tr", null), StreamCaptionLoader.CacheTag("tr:asr", null));
        Assert.Null(StreamCaptionLoader.Load("abc", "off", url));
    }

    [Fact]
    public void Prefer_high_resolution_page_storyboard_when_media_uses_another_client()
    {
        const string low = "https://test.example/$L/$N.jpg|160#90#100#5#5#10000#M$M";
        const string high = "https://test.example/$L/$N.jpg|320#180#100#5#5#10000#M$M";
        Assert.Equal(high, YouTubeCatalog.BetterStoryboard(high, low));
        Assert.Equal(high, YouTubeCatalog.BetterStoryboard(low, high));
    }
}
