using Grok.Player.Core.Media;
using Grok.Player.Core.Player;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Subtitles;
using Grok.Player.Core.Tests.Fakes;
using Grok.Player.Core.Tests.Support;

namespace Grok.Player.Core.Tests;

public sealed class SubtitleTests
{
    private const string Sample = """
        1
        00:00:01,000 --> 00:00:03,000
        Hello

        2
        00:00:04,500 --> 00:00:06,000
        World
        line two
        """;

    [Fact]
    public void Parse_reads_cues_and_dot_or_comma()
    {
        var document = SrtDocument.Parse(Sample);
        Assert.Equal(2, document.Cues.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), document.Cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(3), document.Cues[0].End);
        Assert.Equal("Hello", document.Cues[0].Text);
        Assert.Equal("World\nline two", document.Cues[1].Text);
        Assert.Equal(1000, document.Cues[0].StartMs);

        var dotted = SrtDocument.Parse("00:00:01.250 --> 00:00:02.000\nHi\n");
        Assert.Equal(1250, dotted.Cues[0].StartMs);
        Assert.Equal("00:00:01.250", SrtTime.Format(dotted.Cues[0].Start));
        Assert.Equal("-00:00:01.500", SrtTime.Format(TimeSpan.FromSeconds(-1.5)));
        Assert.Equal(-250, SrtTime.ToMs(TimeSpan.FromMilliseconds(-250)));
        Assert.True(SrtTime.TryParse("-00:00:00.500", out var parsed) && parsed == TimeSpan.FromMilliseconds(-500));
        Assert.True(SrtTime.TryParse("00:06.666", out var shortCue));
        Assert.Equal(TimeSpan.FromMilliseconds(6666), shortCue);
        Assert.True(SrtTime.TryParse("1:00:02.208", out var hourCue));
        Assert.Equal(TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(2208), hourCue);
        Assert.True(SrtTime.TryParseRange("00:06.666 --> 00:09.291", out var start, out var end));
        Assert.Equal(TimeSpan.FromMilliseconds(6666), start);
        Assert.Equal(TimeSpan.FromMilliseconds(9291), end);
        var lotr = SrtDocument.Parse("WEBVTT\n\n00:06.666 --> 00:09.291\nSummon the legions.\n\n1:00:02.208 --> 1:00:03.750\nno idhui.\n", compact: false);
        Assert.Equal(2, lotr.Cues.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(6666), lotr.Cues[0].Start);
        Assert.Equal("Summon the legions.", lotr.Cues[0].Text);
    }

    [Fact]
    public void Youtube_vtt_color_tags_are_stripped()
    {
        var document = SrtDocument.Parse(
            """
            WEBVTT

            00:00:00.080 --> 00:00:03.869 align:start position:0%
            <c.color00FF00>Hello</c> <c.colorE5E5E5>world</c>
            """);
        Assert.Equal("Hello world", document.Cues[0].Text);
        Assert.DoesNotContain("<c", document.Cues[0].Text, StringComparison.Ordinal);
        Assert.Equal("14 Mart 2011", SrtDocument.CleanMarkup("14<00:00:00.480><c> Mart</c><00:00:01.079><c> 2011</c>"));
    }

    [Fact]
    public void Merge_stacks_matching_times_and_appends_the_rest()
    {
        var left = SrtDocument.Parse(Sample);
        var right = SrtDocument.Parse("""
            1
            00:00:01,000 --> 00:00:03,000
            Merhaba

            2
            00:00:10,000 --> 00:00:11,000
            Extra
            """);

        var merged = left.Merge(right);
        Assert.Equal(3, merged.Cues.Count);
        Assert.Equal("Hello\nMerhaba", merged.Cues[0].Text);
        Assert.Equal("World\nline two", merged.Cues[1].Text);
        Assert.Equal("Extra", merged.Cues[2].Text);
        Assert.Equal(TimeSpan.FromSeconds(10), merged.Cues[2].Start);

        var overlap = left.Merge(SrtDocument.Parse("""
            1
            00:00:01,200 --> 00:00:02,800
            Over
            """));
        Assert.Contains("Over", overlap.Cues[0].Text);
    }

    [Fact]
    public void CueAt_uses_active_range_then_nearest()
    {
        var document = SrtDocument.Parse(Sample);
        Assert.Equal("Hello", document.CueAt(TimeSpan.FromSeconds(2))?.Text);
        Assert.Equal("World\nline two", document.CueAt(TimeSpan.FromSeconds(5))?.Text);
        Assert.Equal("Hello", document.CueAt(TimeSpan.FromMilliseconds(200))?.Text);
    }

    [Fact]
    public void Model_load_apply_disable_and_sync()
    {
        var path = WriteTemp(Sample);
        try
        {
            var model = new SubtitleModel();
            model.AddFile(path, apply: true);
            Assert.True(model.Enabled);
            Assert.Equal(0, model.AppliedIndex);
            model.NudgeDelay(0.5);
            Assert.Equal(0.5, model.DelaySeconds, 3);
            model.SyncSelectedToPosition(model.Active!.Document.Cues[0], TimeSpan.FromSeconds(3));
            Assert.Equal(2, model.DelaySeconds, 3);
            model.ResetDelay();
            Assert.Equal(0, model.DelaySeconds);
            model.SetDelay(double.NaN);
            Assert.Equal(0, model.DelaySeconds);
            model.Disable();
            Assert.False(model.Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddFile_replaces_the_same_source_instead_of_stacking()
    {
        var path = WriteTemp(Sample);
        try
        {
            var model = new SubtitleModel();
            model.AddFile(path, apply: true);
            model.AddFile(path, apply: true);
            Assert.Equal(1, model.Tracks.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sidecar_is_applied_after_the_file_has_loaded()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"grok-sub-open-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var video = Path.Combine(dir, "clip.mp4");
        var srt = Path.Combine(dir, "clip.srt");
        File.WriteAllBytes(video, [1]);
        File.WriteAllText(srt, Sample);
        try
        {
            var fake = new FakeMpvNative();
            using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
            using var view = new PlaybackViewModel(host);
            view.Open(video);
            Assert.DoesNotContain(fake.Commands, command => command.Length >= 1 && command[0] == "sub-add");
            host.ProcessPendingEvents();
            Assert.Contains(
                fake.Commands,
                command => command.Length >= 2 &&
                           command[0] == "sub-add" &&
                           command[1] == view.Subtitles.Applied?.PlayPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Browser_hides_tracks_from_other_videos()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"grok-sub-vis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var video = Path.Combine(dir, "clip.mp4");
        var other = Path.Combine(dir, "other.mp4");
        var sidecar = Path.Combine(dir, "clip.srt");
        var extra = Path.Combine(dir, "other.srt");
        File.WriteAllBytes(video, [1]);
        File.WriteAllBytes(other, [1]);
        File.WriteAllText(sidecar, Sample);
        File.WriteAllText(extra, Sample);
        try
        {
            var model = new SubtitleModel();
            model.AddFile(sidecar, apply: true, attachTo: video);
            model.AddFile(extra, apply: false, attachTo: other);
            var stream = Path.Combine(dir, "stream.en.srt");
            File.WriteAllText(stream, Sample);
            model.AddFile(stream, apply: false, attachTo: "youtube|dQw4w9wgBcQ");
            model.BindForMedia(video);
            Assert.True(model.IsVisible(model.Tracks[0]));
            Assert.False(model.IsVisible(model.Tracks[1]));
            Assert.False(model.IsVisible(model.Tracks[2]));
            Assert.True(SubtitleModel.BelongsTo(model.Tracks[0], video));
            Assert.False(SubtitleModel.BelongsTo(model.Tracks[2], video));
            model.SelectTab(2);
            Assert.Equal(2, model.ActiveIndex);
            model.BindForMedia(other);
            Assert.Equal(1, model.ActiveIndex);
            Assert.Equal(1, model.AppliedIndex);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Returning_to_a_local_video_reapplies_its_sidecar()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"grok-sub-return-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var first = Path.Combine(dir, "one.mp4");
        var second = Path.Combine(dir, "two.mp4");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [1]);
        File.WriteAllText(Path.ChangeExtension(first, ".srt"), "1\n00:00:00,000 --> 00:00:01,000\nFirst\n");
        File.WriteAllText(Path.ChangeExtension(second, ".srt"), "1\n00:00:00,000 --> 00:00:01,000\nSecond\n");
        try
        {
            var fake = new FakeMpvNative();
            using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
            using var view = new PlaybackViewModel(host);
            view.Open(first);
            host.ProcessPendingEvents();
            Assert.Equal("First", view.Subtitles.Applied?.Document.Cues[0].Text);
            view.Open(second);
            host.ProcessPendingEvents();
            Assert.Equal("Second", view.Subtitles.Applied?.Document.Cues[0].Text);
            view.Open(first);
            host.ProcessPendingEvents();
            Assert.Equal("First", view.Subtitles.Applied?.Document.Cues[0].Text);
            var lastAdd = fake.Commands.Last(command => command.Length >= 2 && command[0] == "sub-add");
            Assert.Equal(view.Subtitles.Applied?.PlayPath, lastAdd[1]);
            Assert.Contains("First", File.ReadAllText(lastAdd[1]), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Browser_edit_reapplies_the_updated_play_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"grok-sub-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var video = Path.Combine(dir, "clip.mp4");
        var srt = Path.Combine(dir, "clip.srt");
        File.WriteAllBytes(video, [1]);
        File.WriteAllText(srt, Sample);
        try
        {
            var fake = new FakeMpvNative();
            using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
            using var view = new PlaybackViewModel(host);
            view.Open(video);
            host.ProcessPendingEvents();
            Assert.NotNull(view.Subtitles.Applied);
            var before = view.Subtitles.Applied!.PlayPath;
            view.Subtitles.Active!.Document.Cues[0].Text = "Edited";
            view.Subtitles.Active.Document.Cues[0].Spans =
                [new CaptionSpan("Edited", "#00FF00", Bold: true, Italic: true, Underline: true)];
            view.Subtitles.PersistActive();
            Assert.NotEqual(before, view.Subtitles.Applied.PlayPath);
            Assert.Equal("Edited", view.Subtitles.Applied.Document.Cues[0].Text);
            var lastAdd = fake.Commands.Last(command => command.Length >= 2 && command[0] == "sub-add");
            Assert.Equal(view.Subtitles.Applied.PlayPath, lastAdd[1]);
            var after = fake.Lifecycle.FindLastIndex(item => item.StartsWith("command:sub-add", StringComparison.Ordinal));
            Assert.DoesNotContain(
                fake.Lifecycle.Skip(after + 1),
                item => item.StartsWith("property:sid=auto", StringComparison.Ordinal));
            Assert.Contains("Edited", File.ReadAllText(lastAdd[1]), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Persist_keeps_edits_when_the_same_source_is_added_again()
    {
        var path = WriteTemp(Sample);
        try
        {
            var model = new SubtitleModel();
            model.AddFile(path, apply: true, attachTo: "youtube|abc");
            model.BindForMedia("youtube|abc");
            model.Active!.Document.Cues[0].Text = "Edited line";
            model.Active.Document.Cues[0].Spans =
            [
                new CaptionSpan("Edited line", "#00FF00", Bold: true, Italic: true, Underline: true, Strike: true, Pre: true, Quote: true)
            ];
            model.PersistActive();
            Assert.Contains("Edited line", File.ReadAllText(path), StringComparison.Ordinal);
            model.AddFile(path, apply: true, attachTo: "youtube|abc");
            Assert.Equal("Edited line", model.Applied!.Document.Cues[0].Text);
            Assert.True(model.Applied.Document.Cues[0].Spans[0].Bold);
            Assert.True(model.Applied.Document.Cues[0].Spans[0].Pre);
            Assert.True(model.Applied.Document.Cues[0].Spans[0].Quote);
            Assert.Contains("Edited line", File.ReadAllText(model.Applied.PlayPath), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Player_adds_and_clears_subtitle()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        var path = WriteTemp(Sample);
        try
        {
            using var view = new PlaybackViewModel(host);
            view.Subtitles.AddFile(path, apply: true);
            Assert.Contains(fake.Commands, command => command.Length >= 2 && command[0] == "sub-add" &&
                command[1] == view.Subtitles.Applied?.PlayPath);
            view.Subtitles.NudgeDelay(-0.5);
            Assert.Contains(fake.Lifecycle, item => item.StartsWith("property:sub-delay=", StringComparison.Ordinal));
            view.Subtitles.Disable();
            Assert.Contains(fake.Lifecycle, item => item == "property:sid=no");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sidecar_and_name_match_attach_to_the_video()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"grok-sub-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var video = Path.Combine(dir, "clip.mp4");
        var other = Path.Combine(dir, "other.mp4");
        var sidecar = Path.Combine(dir, "clip.srt");
        var extra = Path.Combine(dir, "notes.srt");
        File.WriteAllBytes(video, [1]);
        File.WriteAllBytes(other, [1]);
        File.WriteAllText(sidecar, Sample);
        File.WriteAllText(extra, Sample);
        try
        {
            var model = new SubtitleModel();
            model.DiscoverSidecar(video);
            Assert.Single(model.Tracks);
            Assert.Equal(video, model.Tracks[0].AttachedMedia, StringComparer.OrdinalIgnoreCase);

            model.BindForMedia(video);
            Assert.True(model.Enabled);
            Assert.Equal(0, model.AppliedIndex);

            model.BindForMedia(other);
            Assert.False(model.Enabled);

            var dropped = model.IngestDropped(extra, [video, other]);
            Assert.Equal(other, dropped!.AttachedMedia, StringComparer.OrdinalIgnoreCase);
            Assert.True(model.Enabled);
            Assert.Equal(1, model.AppliedIndex);

            model.Disable();
            model.BindForMedia(other);
            Assert.False(model.Enabled);

            model.BindForMedia(video);
            Assert.True(model.Enabled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Insert_and_delete_cues()
    {
        var path = WriteTemp(Sample);
        try
        {
            var model = new SubtitleModel();
            model.AddFile(path, apply: true);
            var inserted = model.InsertCue(0);
            Assert.NotNull(inserted);
            Assert.Equal(3, model.Active!.Document.Cues.Count);
            Assert.True(model.DeleteCue(inserted!));
            Assert.Equal(2, model.Active.Document.Cues.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Karaoke_play_file_is_used_by_default()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(vtt,
            "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nHello<00:00:00.400><c> world</c>\n");
        var model = new SubtitleModel();
        var track = model.AddFile(vtt, apply: true);
        Assert.True(track.Document.HasKaraoke);
        var karaokeText = File.ReadAllText(track.PlayPath);
        Assert.Contains("Hello", karaokeText, StringComparison.Ordinal);
        Assert.Contains("world", karaokeText, StringComparison.Ordinal);
        File.Delete(vtt);
    }

    [Fact]
    public void Stream_subtitle_settings_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "stream-subs-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new StreamSubtitleSettings { Mode = StreamSubtitleMode.Browser, Store = path };
        settings.Save();
        var loaded = StreamSubtitleSettings.Load(path);
        Assert.Equal(StreamSubtitleMode.Browser, loaded.Mode);
        File.Delete(path);
    }

    [Fact]
    public void Caption_loader_writes_clean_srt()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        var srt = StreamCaptionLoader.WriteSrt(
            vtt,
            "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\n<c.color00000>Hello</c>\n");
        Assert.EndsWith(".srt", srt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hello", File.ReadAllText(srt), StringComparison.Ordinal);
        Assert.DoesNotContain("<c", File.ReadAllText(srt), StringComparison.Ordinal);
        File.Delete(vtt);
        File.Delete(srt);
    }

    [Fact]
    public void Auto_stream_subs_load_vtt_even_when_hls_has_captions()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHi\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host, streamSubtitles: new StreamSubtitleSettings { Mode = StreamSubtitleMode.On });
        view.ResolveYouTube = _ => new YouTubePlayable(
            "dQw4w9wgBcQ",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Song",
            StreamKind.Vod,
            audioLang: "tr",
            subLang: "tr",
            captionUrl: vtt,
            hlsSubtitles: true);
        view.AddStream("https://www.youtube.com/watch?v=dQw4w9wgBcQ", play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
        }

        Assert.NotNull(view.Subtitles.Applied);
        File.Delete(vtt);
    }

    [Fact]
    public void On_screen_caption_follows_applied_cue()
    {
        var path = WriteTemp(Sample);
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        var video = TestMedia.CreateTempFile();
        view.Open(video);
        host.ProcessPendingEvents();
        view.Subtitles.AddFile(path, apply: true, attachTo: video);
        host.Seek(TimeSpan.FromSeconds(2));
        host.ProcessPendingEvents();
        Assert.Equal("Hello", view.OnScreenCaption);
        File.Delete(path);
    }

    [Fact]
    public void Remembered_stream_language_survives_reopen()
    {
        var store = Path.Combine(Path.GetTempPath(), "stream-subs-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new StreamSubtitleSettings { Mode = StreamSubtitleMode.On, Store = store };
        var fake = new FakeMpvNative();
        using (var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests()))
        using (var view = new PlaybackViewModel(host, streamSubtitles: settings))
        {
            view.ResolveYouTube = _ => new YouTubePlayable(
                "dQw4w9wgBcQ",
                "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
                "Song",
                StreamKind.Vod,
                audioLang: "tr",
                subLang: "tr");
            view.AddStream(
                "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=dQw4w9wgBcQ") + "&audio=tr&sub=tr",
                play: true);
            Assert.Equal("tr", view.PreferredSubLang);
            Assert.Equal("tr", view.PreferredAudioLang);
        }

        var loaded = StreamSubtitleSettings.Load(store);
        var fake2 = new FakeMpvNative();
        using var host2 = new PlayerHost(fake2, PlayerHostOptions.ForAutomatedTests());
        using var view2 = new PlaybackViewModel(host2, streamSubtitles: loaded);
        Assert.Equal("tr", view2.PreferredSubLang);
        Assert.Equal("tr", view2.PreferredAudioLang);
        File.Delete(store);
    }

    [Fact]
    public void Off_stream_subs_do_not_load_vtt()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHi\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(
            host,
            streamSubtitles: new StreamSubtitleSettings { Mode = StreamSubtitleMode.Off });
        view.ResolveYouTube = _ => new YouTubePlayable(
            "dQw4w9wgBcQ",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Song",
            StreamKind.Vod,
            subLang: "tr",
            captionUrl: vtt,
            hlsSubtitles: true);
        view.AddStream("https://www.youtube.com/watch?v=dQw4w9wgBcQ", play: true);
        Thread.Sleep(80);
        Assert.Null(view.Subtitles.Applied);
        File.Delete(vtt);
    }

    private static string WriteTemp(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"grok-sub-{Guid.NewGuid():N}.srt");
        File.WriteAllText(path, text);
        return path;
    }
}
