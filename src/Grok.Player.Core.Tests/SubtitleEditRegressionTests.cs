using Grok.Player.Core.Subtitles;

namespace Grok.Player.Core.Tests;

public sealed class SubtitleEditRegressionTests
{
    private const string Source = "WEBVTT\nLanguage: tr\n\n00:00:00.000 --> 00:00:04.000\nHello<00:00:01.000> world\n";

    [Fact]
    public void Edited_karaoke_uses_new_color_and_tags_without_revealing_old_text()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vtt");
        File.WriteAllText(path, Source);
        try
        {
            var model = new SubtitleModel();
            var track = model.AddFile(path, true, "youtube|test");
            var cue = track.Document.Cues[0];
            cue.Text = "Updated";
            cue.Spans = [new CaptionSpan(cue.Text, "#12AB34", Bold: true, Italic: true, Underline: true)];
            model.PersistActive();
            var ass = File.ReadAllText(track.PlayPath);
            Assert.Contains("\\c&H0034AB12&", ass);
            Assert.Contains("\\b1\\i1\\u1", ass);
            Assert.Contains("Updated", ass);
            Assert.DoesNotContain("Hello", ass);
            Assert.False(cue.HasKaraoke);
            Assert.Equal(Source, File.ReadAllText(path)); // Raw cache is not overwritten with SRT.
            model.BindForMedia("youtube|other");
            File.WriteAllText(path, Source); // A fresh network fetch must not erase user edits.
            model.AddFile(path, true, "youtube|test");
            Assert.Equal("Updated", model.Applied!.Document.Cues[0].Text);
            var reopened = new SubtitleModel().AddFile(path, true, "youtube|test");
            Assert.Equal("Updated", reopened.Document.Cues[0].Text);
            Assert.True(reopened.Document.Cues[0].Spans[0].Underline);
        }
        finally { File.Delete(path); File.Delete(path + ".edited.srt"); }
    }

    [Fact]
    public void Karaoke_reserves_full_phrase_layout_in_a_single_dialogue()
    {
        var document = SrtDocument.Parse(Source);
        var ass = document.ToAss(revealWords: true);
        var line = Assert.Single(ass.Split('\n').Where(line => line.StartsWith("Dialogue:")));
        Assert.Contains("Hello", line);
        Assert.Contains("world", line);
        Assert.Contains("\\alpha&HFF&\\t(1000,1001,\\alpha&H00&)", line);
        Assert.DoesNotContain("\\fad", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Styled_source_does_not_reuse_a_stale_ass_sibling()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".srt");
        File.WriteAllText(path, "1\n00:00:00,000 --> 00:00:04,000\n<font color=\"#00FF00\"><u>Current</u></font>\n");
        File.WriteAllText(Path.ChangeExtension(path, ".ass"), "stale");
        try
        {
            var model = new SubtitleModel();
            var track = model.AddFile(path, true);
            Assert.Contains("\\c&H0000FF00&", File.ReadAllText(track.PlayPath));
            Assert.Contains("\\u1", File.ReadAllText(track.PlayPath));
            model.SaveActive();
            Assert.DoesNotContain("stale", File.ReadAllText(track.PlayPath));
        }
        finally { File.Delete(path); File.Delete(Path.ChangeExtension(path, ".ass")); }
    }

    [Fact]
    public void Network_cache_refresh_does_not_erase_an_edited_srt()
    {
        Directory.CreateDirectory(StreamCaptionLoader.CacheDirectory);
        var path = Path.Combine(StreamCaptionLoader.CacheDirectory, Guid.NewGuid() + ".en.srt");
        File.WriteAllText(path, "1\n00:00:00,000 --> 00:00:04,000\nOriginal\n");
        try
        {
            var first = new SubtitleModel();
            var cue = first.AddFile(path, true, "youtube|test").Document.Cues[0];
            cue.Text = "Edited";
            cue.Spans = [new CaptionSpan("Edited", "#00FF00", Underline: true)];
            first.PersistActive();
            File.WriteAllText(path, "1\n00:00:00,000 --> 00:00:04,000\nFresh network source\n");
            var reopened = new SubtitleModel().AddFile(path, true, "youtube|test");
            Assert.Equal("Edited", reopened.Document.Cues[0].Text);
            Assert.True(reopened.IsEdited);
        }
        finally { File.Delete(path); File.Delete(path + ".edited.srt"); }
    }

    [Fact]
    public void Playback_switches_overlapping_cues_in_sequence()
    {
        var doc = new SrtDocument([
            new SrtCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(2), "First"),
            new SrtCue(2, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3), "Second"),
            new SrtCue(3, TimeSpan.FromSeconds(2.8), TimeSpan.FromSeconds(4), "Third")
        ]).ForReadablePlayback();
        Assert.Equal(["First", "Second", "Third"], doc.Cues.Select(cue => cue.Text));
        Assert.Equal(TimeSpan.FromSeconds(1.5), doc.Cues[0].End);
        Assert.Equal(TimeSpan.FromSeconds(2.8), doc.Cues[1].End);
        Assert.DoesNotContain(doc.Cues.Zip(doc.Cues.Skip(1)), pair => pair.First.End > pair.Second.Start);
    }

    [Fact]
    public void Playback_does_not_merge_three_simultaneous_cues_into_unreadable_lines()
    {
        var doc = new SrtDocument([
            new SrtCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(4), "First"),
            new SrtCue(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "Second"),
            new SrtCue(3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.5), "Third")
        ]).ForReadablePlayback();
        Assert.Equal(["First", "Second", "Third"], doc.Cues.Select(cue => cue.Text));
        Assert.DoesNotContain(doc.Cues, cue => cue.Text.Contains('\n'));
    }

    [Fact]
    public void Playback_switches_overlapping_karaoke_without_losing_word_timing()
    {
        var first = new SrtCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(3), "First phrase")
        {
            Karaoke = [(TimeSpan.Zero, "First "), (TimeSpan.FromSeconds(1), "phrase")]
        };
        var second = new SrtCue(2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), "Second phrase")
        {
            Karaoke = [(TimeSpan.FromSeconds(2), "Second "), (TimeSpan.FromSeconds(3), "phrase")]
        };

        var doc = new SrtDocument([first, second]).ForReadablePlayback();
        Assert.True(doc.Cues[0].HasKaraoke);
        Assert.True(doc.Cues[1].HasKaraoke);
        Assert.Equal("First phrase\nSecond phrase", doc.Cues[1].Text);
        Assert.Equal("First phrase\n", doc.Cues[1].Karaoke[0].Text);
        Assert.DoesNotContain(doc.Cues.Zip(doc.Cues.Skip(1)), pair => pair.First.End > pair.Second.Start);
    }

    [Fact]
    public void Rolling_playback_moves_the_completed_line_without_a_fade()
    {
        var first = new SrtCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(2), "First line")
        {
            Karaoke = [(TimeSpan.Zero, "First "), (TimeSpan.FromSeconds(1), "line")]
        };
        var second = new SrtCue(2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), "Second line")
        {
            Karaoke = [(TimeSpan.FromSeconds(2), "Second "), (TimeSpan.FromSeconds(3), "line")]
        };

        var ass = new SrtDocument([first, second]).ForReadablePlayback().ToAss(revealWords: true);
        var secondCue = ass.Split('\n').Where(line => line.Contains("0:00:02.00,0:00:04.00", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, secondCue.Length);
        Assert.Contains(secondCue, line => line.Contains("\\move(960,1032,960,968,0,150)", StringComparison.Ordinal));
        Assert.Contains(secondCue, line => line.Contains("\\pos(960,1032)", StringComparison.Ordinal));
        Assert.DoesNotContain(secondCue, line => line.Contains("\\fad", StringComparison.Ordinal));
    }

    [Fact]
    public void Playback_turns_authored_multi_space_separator_into_a_second_line()
    {
        var cue = new SrtCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(3),
            "First sentence.   Second sentence.",
            [new CaptionSpan("First sentence.   Second sentence.", "#00BCE7", Bold: true)]);
        var result = new SrtDocument([cue]).ForReadablePlayback().Cues[0];
        Assert.Equal("First sentence.\nSecond sentence.", result.Text);
        Assert.Equal("First sentence.\nSecond sentence.", result.Spans[0].Text);
        Assert.Equal("#00BCE7", result.Spans[0].Color);
    }
}
