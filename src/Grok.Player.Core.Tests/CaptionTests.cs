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
    public void Karaoke_two_line_paint_keeps_only_the_current_phrase()
    {
        var document = SrtDocument.Parse(
            """
            WEBVTT

            00:00:02.240 --> 00:00:03.990
            Bu videoyu çok
            insanlar<00:00:02.639><c> çaresiz</c>
            """,
            compact: false);
        Assert.DoesNotContain("Bu videoyu çok", document.Cues[0].Text, StringComparison.Ordinal);
        Assert.Contains("insanlar", document.Cues[0].Text, StringComparison.Ordinal);
        var play = document.ExpandKaraoke();
        Assert.DoesNotContain(play.Cues, cue => cue.Text.Contains("Bu videoyu çok", StringComparison.Ordinal));
        Assert.Contains(play.Cues, cue => cue.Text == "insanlar");
        Assert.Contains(play.Cues, cue => cue.Text.Contains("çaresiz", StringComparison.Ordinal));
    }

    [Fact]
    public void Json3_and_srv3_timedtext_become_vtt()
    {
        const string json3 =
            """
            {"events":[
              {"tStartMs":199,"dDurationMs":3190,"segs":[
                {"utf8":"Hanımlar,"},
                {"tOffsetMs":400,"utf8":" beyler"},
                {"utf8":"\n"},
                {"tOffsetMs":1000,"utf8":"şükür"}
              ]}
            ]}
            """;
        var fromJson = YouTubeTimedText.ToVtt(json3, "de");
        Assert.Contains("WEBVTT", fromJson, StringComparison.Ordinal);
        Assert.Contains("Language: de", fromJson, StringComparison.Ordinal);
        Assert.Contains("Hanımlar", fromJson, StringComparison.Ordinal);
        Assert.Contains("şükür", fromJson, StringComparison.Ordinal);
        var fromAsr = YouTubeTimedText.ToVtt(json3, "de:asr");
        Assert.DoesNotContain("Hanımlar", fromAsr, StringComparison.Ordinal);
        Assert.Contains("şükür", fromAsr, StringComparison.Ordinal);
        const string srv3 =
            """
            <?xml version="1.0" encoding="utf-8" ?>
            <timedtext format="3">
            <body>
            <p t="199" d="3190"><s>Hanımlar,</s><s t="400"> beyler</s></p>
            </body>
            </timedtext>
            """;
        var fromXml = YouTubeTimedText.ToVtt(srv3, "tr");
        Assert.Contains("Hanımlar", fromXml, StringComparison.Ordinal);
        Assert.Contains("beyler", fromXml, StringComparison.Ordinal);
        Assert.Contains("Language: tr", fromXml, StringComparison.Ordinal);
    }

    [Fact]
    public void Authored_qtl_srv3_preserves_both_lines_of_each_event()
    {
        const string srv3 =
            """
            <timedtext format="3"><body>
            <p t="1200" d="4440">Size basit gibi görünen ama cevabı hiç
            de basit olmayan bir soru sorayım.</p>
            </body></timedtext>
            """;
        var vtt = YouTubeTimedText.ToVtt(srv3, "tr");
        Assert.Contains("Size basit gibi görünen ama cevabı hiç", vtt, StringComparison.Ordinal);
        Assert.Contains("de basit olmayan bir soru sorayım.", vtt, StringComparison.Ordinal);
        var cue = Assert.Single(SrtDocument.Parse(vtt!, compact: false).Cues);
        Assert.Equal("Size basit gibi görünen ama cevabı hiç\nde basit olmayan bir soru sorayım.", cue.Text);
    }

    [Fact]
    public void Cache_rejects_a_translation_file_that_is_still_the_source()
    {
        var folder = StreamCaptionLoader.CacheDirectory;
        Directory.CreateDirectory(folder);
        var id = "cachesamexx1";
        File.WriteAllText(
            Path.Combine(folder, id + ".tr-asr.vtt"),
            "WEBVTT\nLanguage: tr\n\n00:00:00.000 --> 00:00:01.000\nMerhaba efendim\n");
        File.WriteAllText(
            Path.Combine(folder, id + ".tr-asr.de.vtt"),
            "WEBVTT\nLanguage: de\n\n00:00:00.000 --> 00:00:01.000\nMerhaba efendim\n");
        File.WriteAllText(
            Path.Combine(folder, id + ".tr-asr.de.srt"),
            "1\n00:00:00,000 --> 00:00:01,000\nMerhaba efendim\n");
        Assert.False(StreamCaptionLoader.CacheMatches(Path.Combine(folder, id + ".tr-asr.de.srt"), "de"));
        Assert.True(StreamCaptionLoader.IsSameAsSource(
            id,
            "https://www.youtube.com/api/timedtext?v=" + id + "&lang=tr&kind=asr&tlang=de&fmt=vtt",
            "WEBVTT\nLanguage: de\n\n00:00:00.000 --> 00:00:01.000\nMerhaba efendim\n"));
        File.Delete(Path.Combine(folder, id + ".tr-asr.vtt"));
        File.Delete(Path.Combine(folder, id + ".tr-asr.de.vtt"));
        File.Delete(Path.Combine(folder, id + ".tr-asr.de.srt"));
    }

    [Fact]
    public void Cache_rejects_a_source_language_file_for_a_translation()
    {
        var folder = StreamCaptionLoader.CacheDirectory;
        Directory.CreateDirectory(folder);
        var id = "cachelangxx1";
        var vtt = Path.Combine(folder, id + ".tr.de.vtt");
        var srt = Path.Combine(folder, id + ".tr.de.srt");
        File.WriteAllText(vtt, "WEBVTT\nLanguage: tr\n\n00:00:00.000 --> 00:00:01.000\nMerhaba\n");
        File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:01,000\nMerhaba\n");
        Assert.False(StreamCaptionLoader.CacheMatches(srt, "de"));
        File.Delete(vtt);
        File.Delete(srt);
    }

    [Fact]
    public void Karaoke_mode_expands_word_times()
    {
        const string vtt =
            """
            WEBVTT

            00:00:00.080 --> 00:00:02.230
            Bu<00:00:00.240><c> videoyu</c><00:00:00.640><c> çok</c>

            00:00:02.230 --> 00:00:02.240
            Bu videoyu çok

            00:00:02.240 --> 00:00:03.990
            Bu videoyu çok
            insanlar<00:00:02.639><c> çaresiz</c>
            """;
        var raw = SrtDocument.Parse(vtt, compact: false);
        Assert.True(raw.HasKaraoke);
        Assert.True(raw.Cues[0].HasKaraoke);
        var play = raw.ExpandKaraoke();
        Assert.Contains(play.Cues, cue => cue.Text == "Bu");
        Assert.Contains(play.Cues, cue => cue.Text == "Bu videoyu");
        Assert.Contains(play.Cues, cue => cue.Text.Contains("insanlar çaresiz", StringComparison.Ordinal));
        var compact = raw.Compacted();
        Assert.DoesNotContain(compact.Cues, cue => (cue.End - cue.Start).TotalMilliseconds <= 20 && cue.Text == "Bu videoyu çok");
    }

    [Fact]
    public void Youtube_asr_commit_twins_leave_the_browser()
    {
        const string vtt =
            """
            WEBVTT

            00:00:03.840 --> 00:00:10.669
            Gaming gaming gaming gaming gentl.

            00:00:10.679 --> 00:00:12.990
            Hey<00:00:11.200><c> efendim</c><00:00:11.800><c> selamlar.</c><00:00:12.400><c> Sesim</c><00:00:12.700><c> yorgun</c>

            00:00:12.990 --> 00:00:13.000
            Hey efendim selamlar. Sesim yorgun

            00:00:13.000 --> 00:00:15.470
            geliyorsa kusuruma bakmayın. Şu anda

            00:00:15.470 --> 00:00:15.480
            geliyorsa kusuruma bakmayın. Şu anda
            """;
        var folder = Path.Combine(Path.GetTempPath(), "GrokPlayer", "caption-tests");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(path, vtt);
        var track = new SubtitleModel().AddFile(path, apply: true);
        Assert.DoesNotContain(
            track.Document.Cues,
            cue => (cue.End - cue.Start).TotalMilliseconds < 50);
        Assert.Equal(3, track.Document.Cues.Count);
        Assert.Equal("Hey efendim selamlar. Sesim yorgun", track.Document.Cues[1].Text);
        Assert.True((track.Document.Cues[1].End - track.Document.Cues[1].Start).TotalMilliseconds > 500);
        File.Delete(path);
        if (File.Exists(track.PlayPath))
        {
            File.Delete(track.PlayPath);
        }
    }

    [Fact]
    public void Youtube_paint_on_and_flash_cues_become_one_styled_line()
    {
        const string vtt =
            """
            WEBVTT

            00:00:36.160 --> 00:00:36.400
            <c.color00BCE7>START</c>

            00:00:36.400 --> 00:00:36.700
            <c.color00BCE7>START</c> THE

            00:00:36.700 --> 00:00:38.910
            <c.color00BCE7><b>START THE TIMER</b></c>

            00:00:36.960 --> 00:00:38.910
            <c.color00BCE7><b>START THE TIMER</b></c>

            00:00:38.910 --> 00:00:38.920
            START THE TIMER
            """;
        var play = SrtDocument.Parse(vtt, compact: false).ForDisplay();
        Assert.Single(play.Cues);
        Assert.Equal("START THE TIMER", play.Cues[0].Text);
        Assert.True(play.Cues[0].Start < TimeSpan.FromSeconds(36.3));
        Assert.True((play.Cues[0].End - play.Cues[0].Start).TotalMilliseconds > 500);
        Assert.True(play.HasStyle);
        var folder = Path.Combine(Path.GetTempPath(), "GrokPlayer", "caption-tests");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(path, vtt);
        var track = new SubtitleModel().AddFile(path, apply: true);
        Assert.Single(track.Document.Cues);
        Assert.EndsWith(".ass", track.PlayPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("START THE TIMER", File.ReadAllText(track.PlayPath), StringComparison.Ordinal);
        File.Delete(path);
        File.Delete(track.PlayPath);
    }

    [Fact]
    public void Duplicate_youtube_paint_cues_play_once()
    {
        const string vtt =
            """
            WEBVTT

            00:00:00.201 --> 00:00:01.834
            BEHIND<00:00:00.551><c> ME</c>

            00:00:00.201 --> 00:00:01.834
            BEHIND<00:00:00.551><c> ME</c>
            """;
        var raw = SrtDocument.Parse(vtt, compact: false);
        Assert.Equal(2, raw.Cues.Count);
        Assert.Single(raw.Deduped().Cues);
        var play = raw.Deduped().ExpandKaraoke().Deduped();
        Assert.Equal(play.Cues.Count, play.Cues.Select(cue => cue.Start + cue.Text).Distinct().Count());
        var folder = Path.Combine(Path.GetTempPath(), "GrokPlayer", "caption-tests");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(path, vtt);
        var track = new SubtitleModel().AddFile(path, apply: true);
        var played = File.ReadAllLines(track.PlayPath).Where(line => line.StartsWith("Dialogue:")).ToArray();
        Assert.Contains("BEHIND", Assert.Single(played));
        File.Delete(path);
    }

    [Fact]
    public void Youtube_blank_line_after_timing_keeps_the_payload()
    {
        var document = SrtDocument.Parse(
            """
            WEBVTT

            00:00:00.199 --> 00:00:03.389 align:start position:0%

            Hanımlar,<00:00:00.599><c> beyler</c><00:00:01.199><c> şükür</c>

            00:00:03.389 --> 00:00:03.399 align:start position:0%
            Hanımlar, beyler şükür

            """,
            compact: false);
        Assert.True(document.Cues[0].Start < TimeSpan.FromSeconds(1));
        Assert.True(document.Cues[0].HasKaraoke);
        Assert.Contains("Hanımlar", document.Cues[0].Text, StringComparison.Ordinal);
        var play = document.Deduped().ExpandKaraoke().Deduped();
        Assert.Contains(play.Cues, cue => cue.Text == "Hanımlar,");
        Assert.Contains(play.Cues, cue => cue.Text.Contains("beyler", StringComparison.Ordinal));
        Assert.True(play.Cues[0].Start < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Real_asr_vtt_play_file_has_cues_from_the_first_second()
    {
        var vtt = Path.Combine(Path.GetTempPath(), "GrokPlayer", "captions", "fFxbSyTAmBs.tr.vtt");
        if (!File.Exists(vtt))
        {
            return;
        }

        var raw = SrtDocument.Parse(File.ReadAllText(vtt), compact: false);
        if (raw.Cues.Count < 10)
        {
            return;
        }

        Assert.True(raw.Cues[0].Start < TimeSpan.FromSeconds(1),
            "raw0=" + raw.Cues[0].Start + " karaoke=" + raw.Cues[0].Karaoke.Count + " text=" + raw.Cues[0].Text);
        if (!raw.HasKaraoke)
        {
            return;
        }
        var play = raw.Deduped().ExpandKaraoke().Deduped();
        Assert.True(play.Cues.Count > 10, "play cues=" + play.Cues.Count);
        Assert.True(play.Cues[0].Start < TimeSpan.FromSeconds(1),
            "play0=" + play.Cues[0].Start + " text=" + play.Cues[0].Text);
        var track = new SubtitleModel().AddFile(vtt, apply: true);
        var played = SrtDocument.Load(track.PlayPath);
        Assert.True(played.Cues[0].Start < TimeSpan.FromSeconds(1));
        Assert.True(played.Cues.Count > 10, "written cues=" + played.Cues.Count);
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
