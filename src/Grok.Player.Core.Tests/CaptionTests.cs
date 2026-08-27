using Grok.Player.Core.Subtitles;

namespace Grok.Player.Core.Tests;

public sealed class CaptionTests
{
    [Fact]
    public void Single_color_tag_keeps_the_whole_phrase_and_its_spaces()
    {
        var spans = CaptionMarkup.Parse("<c.color00FF00>Hello world again</c>");
        Assert.Equal(new[] { "Hello world again" }, spans.Select(span => span.Text));
        Assert.Equal("#00FF00", spans[0].Color);
        Assert.Equal("Hello world again", CaptionMarkup.Plain(spans));
        Assert.Equal("{\\b0\\i0\\u0\\c&H0000FF00&}Hello world again", CaptionMarkup.ToAssText(spans));
    }

    [Fact]
    public void Adjacent_colors_keep_the_space_between_words()
    {
        var spans = CaptionMarkup.Parse("<c.colorFF0000>Hello</c> <c.color00FF00>world</c>");
        Assert.Equal("Hello world", CaptionMarkup.Plain(spans));
        Assert.Contains(spans, span => span.Text.Contains(' '));
        var ass = CaptionMarkup.ToAssText(spans);
        Assert.Contains("Hello ", ass, StringComparison.Ordinal);
        Assert.Contains("world", ass, StringComparison.Ordinal);
        Assert.Contains("&H000000FF&", ass, StringComparison.Ordinal);
        Assert.Contains("&H0000FF00&", ass, StringComparison.Ordinal);
        Assert.DoesNotContain("Helloworld", ass, StringComparison.Ordinal);
    }

    [Fact]
    public void Karaoke_timestamps_do_not_eat_spaces()
    {
        var spans = CaptionMarkup.Parse("14<00:00:00.480><c> Mart</c><00:00:01.079><c> 2011</c>");
        Assert.Equal("14 Mart 2011", CaptionMarkup.Plain(spans));
        Assert.DoesNotContain("<c", CaptionMarkup.Plain(spans), StringComparison.Ordinal);
    }

    [Fact]
    public void Bare_c_tag_inherits_the_current_color()
    {
        var spans = CaptionMarkup.Parse("<c.colorE5E5E5>one<c> two</c> three</c>");
        Assert.Equal("one two three", CaptionMarkup.Plain(spans));
        Assert.All(spans, span => Assert.Equal("#E5E5E5", span.Color));
        Assert.Equal("{\\b0\\i0\\u0\\c&H00E5E5E5&}one two three", CaptionMarkup.ToAssText(spans));
    }

    [Fact]
    public void Nested_colors_restore_the_outer_color()
    {
        var spans = CaptionMarkup.Parse("<c.colorFF0000>red <c.color0000FF>blue</c> red</c>");
        Assert.Equal("red blue red", CaptionMarkup.Plain(spans));
        Assert.Equal("#FF0000", spans[0].Color);
        Assert.Equal("#0000FF", spans[1].Color);
        Assert.Equal("#FF0000", spans[2].Color);
        Assert.StartsWith("red ", spans[0].Text);
        Assert.Equal("blue", spans[1].Text);
        Assert.Equal(" red", spans[2].Text);
    }

    [Fact]
    public void Font_and_named_classes_resolve()
    {
        Assert.Equal("#FFFF00", CaptionMarkup.Parse("<c.yellow>Hi</c>")[0].Color);
        Assert.Equal("#FF0000", CaptionMarkup.Parse("<font color=\"#FF0000\">Hi</font>")[0].Color);
        Assert.Equal("#0000FF", CaptionMarkup.Parse("<font color='blue'>Hi</font>")[0].Color);
        Assert.Equal("#E5E5E5", CaptionMarkup.Parse("<c.colorE5E5E5.bg_transparent>Hi</c>")[0].Color);
    }

    [Fact]
    public void Bold_italic_underline_are_styles_not_literal_tags()
    {
        var spans = CaptionMarkup.Parse("<b> Drive, Jimmy! DRIIIVE! </b>");
        Assert.Equal(" Drive, Jimmy! DRIIIVE! ", CaptionMarkup.Plain(spans));
        Assert.DoesNotContain("<b>", CaptionMarkup.Plain(spans), StringComparison.Ordinal);
        Assert.True(spans[0].Bold);
        Assert.False(spans[0].Italic);
        var ass = CaptionMarkup.ToAssText(spans);
        Assert.Contains("\\b1", ass, StringComparison.Ordinal);
        Assert.Contains("Drive, Jimmy! DRIIIVE!", ass, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", ass, StringComparison.Ordinal);

        var mixed = CaptionMarkup.Parse("<i>Hello</i> <u>world</u>");
        Assert.Equal("Hello world", CaptionMarkup.Plain(mixed));
        Assert.True(mixed[0].Italic);
        Assert.True(mixed[^1].Underline);
        Assert.Contains("Hello ", CaptionMarkup.ToAssText(mixed), StringComparison.Ordinal);

        var both = CaptionMarkup.Parse("<b><i>Hey</i></b>");
        Assert.Equal("Hey", CaptionMarkup.Plain(both));
        Assert.True(both[0].Bold);
        Assert.True(both[0].Italic);
        Assert.Contains("\\b1", CaptionMarkup.ToAssText(both), StringComparison.Ordinal);
        Assert.Contains("\\i1", CaptionMarkup.ToAssText(both), StringComparison.Ordinal);
    }

    [Fact]
    public void Uncolored_text_stays_uncolored_and_spaced()
    {
        var spans = CaptionMarkup.Parse("plain words here");
        Assert.Equal("plain words here", CaptionMarkup.Plain(spans));
        Assert.False(CaptionMarkup.HasColor(spans));
        Assert.Equal("{\\b0\\i0\\u0\\c&H00FFFFFF&}plain words here", CaptionMarkup.ToAssText(spans));
    }

    [Fact]
    public void Document_writes_ass_without_losing_spaces_or_splitting_a_phrase()
    {
        var document = SrtDocument.Parse(
            """
            WEBVTT

            00:00:00.080 --> 00:00:03.869 align:start position:0%
            <c.color00FF00>Hello world</c>

            00:00:04.000 --> 00:00:05.000
            <c.colorFF0000>One</c> <c.color0000FF>two</c>
            """);
        Assert.Equal("Hello world", document.Cues[0].Text);
        Assert.Equal("#00FF00", document.Cues[0].Spans[0].Color);
        Assert.Equal("One two", document.Cues[1].Text);
        Assert.True(document.HasColors);
        Assert.True(document.HasStyle);
        var ass = document.ToAss();
        Assert.Contains("Hello world", ass, StringComparison.Ordinal);
        Assert.Contains("One ", ass, StringComparison.Ordinal);
        Assert.DoesNotContain("Onetwo", ass, StringComparison.Ordinal);
        Assert.Contains("&H0000FF00&", ass, StringComparison.Ordinal);
        Assert.Contains("&H000000FF&", ass, StringComparison.Ordinal);
        Assert.Contains("&H00FF0000&", ass, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_plays_ass_and_keeps_srt_for_the_document()
    {
        var folder = Path.Combine(Path.GetTempPath(), "GrokPlayer", "caption-tests");
        Directory.CreateDirectory(folder);
        var vtt = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".vtt");
        var srt = StreamCaptionLoader.WriteSrt(
            vtt,
            """
            WEBVTT

            00:00:00.000 --> 00:00:01.000
            <c.color00FF00>Hello world</c>
            """);
        Assert.EndsWith(".srt", srt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Hello world", SrtDocument.Load(srt).Cues[0].Text);
        var ass = StreamCaptionLoader.PlayPath(srt);
        Assert.EndsWith(".ass", ass, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(ass));
        Assert.Contains("Hello world", File.ReadAllText(ass), StringComparison.Ordinal);
        Assert.Equal(srt, StreamCaptionLoader.DocumentPath(ass));
        var model = new SubtitleModel();
        var track = model.AddFile(srt, apply: true);
        Assert.EndsWith(".ass", track.PlayPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hello world", File.ReadAllText(track.PlayPath), StringComparison.Ordinal);
        Assert.Equal("Hello world", track.Document.Cues[0].Text);
        File.Delete(vtt);
        File.Delete(srt);
        File.Delete(ass);
    }

    [Fact]
    public void Rolling_youtube_cues_collapse_to_one_final_line()
    {
        var document = SrtDocument.Parse(
            """
            WEBVTT

            00:00:01.000 --> 00:00:04.000
            <c.colorE5E5E5>Drive</c>

            00:00:01.000 --> 00:00:04.000
            <c.colorE5E5E5>Drive</c>

            00:00:01.200 --> 00:00:04.000
            <b>Drive, Jimmy! DRIIIVE!</b>

            00:00:01.200 --> 00:00:04.000
            <b>Drive, Jimmy! DRIIIVE!</b>
            """);
        Assert.Single(document.Cues);
        Assert.Equal("Drive, Jimmy! DRIIIVE!", document.Cues[0].Text);
        Assert.True(document.Cues[0].Spans[0].Bold);
        Assert.DoesNotContain("<b>", document.Cues[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Four_copy_partial_and_full_color_collapses_to_the_full_line()
    {
        var document = SrtDocument.Parse(
            """
            WEBVTT

            00:00:08.000 --> 00:00:12.000
            <c.color00FF00>Hello</c> wo

            00:00:08.000 --> 00:00:12.000
            <c.color00FF00>Hello</c> wo

            00:00:08.240 --> 00:00:12.000
            <c.color00FF00>Hello</c> <c.colorE5E5E5>world</c>

            00:00:08.240 --> 00:00:12.000
            <c.color00FF00>Hello</c> <c.colorE5E5E5>world</c>
            """);
        Assert.Single(document.Cues);
        Assert.Equal("Hello world", document.Cues[0].Text);
        Assert.Contains(document.Cues[0].Spans, span => span.Color == "#00FF00");
        Assert.Contains(document.Cues[0].Spans, span => span.Color == "#E5E5E5");
    }

    [Fact]
    public void Ass_override_tags_do_not_set_font_or_size()
    {
        var spans = CaptionMarkup.Parse("<font color=\"#00FF00\"><b><i><u>Hi</u></i></b></font>");
        var ass = CaptionMarkup.ToAssText(spans);
        Assert.Contains("\\c&H0000FF00&", ass, StringComparison.Ordinal);
        Assert.Contains("\\b1", ass, StringComparison.Ordinal);
        Assert.Contains("\\i1", ass, StringComparison.Ordinal);
        Assert.Contains("\\u1", ass, StringComparison.Ordinal);
        Assert.DoesNotContain("\\fn", ass, StringComparison.Ordinal);
        Assert.DoesNotContain("\\fs", ass, StringComparison.Ordinal);
    }

    [Fact]
    public void Extended_tags_parse_roundtrip_and_stay_off_the_font_size_controls()
    {
        var spans = CaptionMarkup.Parse("<pre><q><s>Hello</s></q></pre>");
        Assert.Equal("Hello", CaptionMarkup.Plain(spans));
        Assert.True(spans[0].Pre);
        Assert.True(spans[0].Quote);
        Assert.True(spans[0].Strike);
        var marked = CaptionMarkup.ToMarked(spans);
        Assert.Contains("<pre>", marked, StringComparison.Ordinal);
        Assert.Contains("<q>", marked, StringComparison.Ordinal);
        Assert.Contains("<s>", marked, StringComparison.Ordinal);
        var again = CaptionMarkup.Parse(marked);
        Assert.True(again[0].Pre);
        Assert.True(again[0].Quote);
        Assert.True(again[0].Strike);
        var ass = CaptionMarkup.ToAssText(spans);
        Assert.Contains("\\s1", ass, StringComparison.Ordinal);
        Assert.Contains("“Hello”", ass, StringComparison.Ordinal);
        Assert.DoesNotContain("\\fn", ass, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\\fs\d", ass);

        var many = CaptionMarkup.WithTags("Hi", "#F0C93A", ["b", "i", "u", "code", "mark", "small", "sup"]);
        Assert.True(many.Bold && many.Italic && many.Underline && many.Code && many.Mark && many.Small && many.Super);
        Assert.Contains("code", CaptionMarkup.SelectedTags(many));
        Assert.Equal(7, CaptionMarkup.SelectedTags(many).Count);
        var every = CaptionMarkup.WithTags("All", "#F0C93A", CaptionMarkup.TagOptions.Select(option => option.Id));
        var everyAss = CaptionMarkup.ToAssText([every]);
        Assert.Contains("All", everyAss, StringComparison.Ordinal);
        Assert.DoesNotContain("\\fn", everyAss, StringComparison.Ordinal);
        Assert.Equal(CaptionMarkup.TagOptions.Length, CaptionMarkup.SelectedTags(every).Count);
        Assert.DoesNotContain("\\fn", CaptionMarkup.ToAssText([many]), StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\\fs\d", CaptionMarkup.ToAssText([many]));
    }

    [Fact]
    public void Alias_tags_map_onto_the_same_styles()
    {
        var spans = CaptionMarkup.Parse("<strong><em><strike>Go</strike></em></strong>");
        Assert.True(spans[0].Bold);
        Assert.True(spans[0].Italic);
        Assert.True(spans[0].Strike);
        Assert.Equal("Go", CaptionMarkup.Plain(spans));
        var code = CaptionMarkup.Parse("<code>x</code>");
        Assert.True(code[0].Code);
        var cite = CaptionMarkup.Parse("<cite>said</cite>");
        Assert.True(cite[0].Italic);
    }

    [Fact]
    public void Loader_does_not_emit_ass_when_there_is_no_color()
    {
        var folder = Path.Combine(Path.GetTempPath(), "GrokPlayer", "caption-tests");
        Directory.CreateDirectory(folder);
        var vtt = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".vtt");
        var srt = StreamCaptionLoader.WriteSrt(
            vtt,
            """
            WEBVTT

            00:00:00.000 --> 00:00:01.000
            Hello world
            """);
        Assert.Equal(srt, StreamCaptionLoader.PlayPath(srt));
        Assert.False(File.Exists(Path.ChangeExtension(vtt, ".ass")));
        File.Delete(vtt);
        File.Delete(srt);
    }
}
