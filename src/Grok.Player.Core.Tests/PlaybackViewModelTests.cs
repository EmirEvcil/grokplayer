using Grok.Player.Core.Media;
using Grok.Player.Core.Player;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Subtitles;
using Grok.Player.Core.Tests.Fakes;
using Grok.Player.Core.Tests.Support;

namespace Grok.Player.Core.Tests;

public sealed class PlaybackViewModelTests
{
    [Fact]
    public void Empty_state_before_open()
    {
        using var session = Create(open: false);
        Assert.True(session.View.ShowEmptyState);
        Assert.Equal("Open a video to begin", session.View.StatusText);
        Assert.Equal("00:00:00", session.View.PositionText);
        Assert.Equal("00:00:00", session.View.DurationText);
        Assert.False(session.View.CanSeek);
        Assert.False(session.View.CanStop);
        Assert.False(session.View.CanTogglePlayback);
        Assert.Equal("\uE768", session.View.PlayPauseGlyph);
    }

    [Fact]
    public void Open_updates_title_duration_and_playing_glyph()
    {
        using var session = Create();
        Assert.False(session.View.ShowEmptyState);
        Assert.True(session.View.IsPlaying);
        Assert.Equal("\uE769", session.View.PlayPauseGlyph);
        Assert.Equal("00:02:00", session.View.DurationText);
        Assert.True(session.View.CanSeek);
        Assert.Contains("d3d11va", session.View.HwdecLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Toggle_and_stop_refresh_command_availability()
    {
        using var session = Create();
        session.View.TogglePlayPause();
        session.Host.ProcessPendingEvents();
        Assert.False(session.View.IsPlaying);
        Assert.Equal("Paused", session.View.StatusText);

        session.View.Stop();
        session.Host.ProcessPendingEvents();
        Assert.True(session.View.ShowEmptyState);
        Assert.Equal("00:00:00", session.View.PositionText);
        Assert.False(session.View.CanTogglePlayback);
        Assert.False(session.View.HasMedia);
    }

    [Fact]
    public void Seek_drag_does_not_fight_time_updates()
    {
        using var session = Create();
        var seekNotifications = 0;
        session.View.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackViewModel.SeekValue))
            {
                seekNotifications++;
            }
        };

        session.View.BeginSeek();
        session.View.UpdateSeekPreview(40);
        seekNotifications = 0;
        session.Host.Seek(TimeSpan.FromSeconds(10));
        session.Host.ProcessPendingEvents();

        Assert.True(session.View.IsSeeking);
        Assert.Equal(40, session.View.SeekValue);
        Assert.Equal("00:00:40", session.View.PositionText);
        Assert.Equal(0, seekNotifications);

        session.View.EndSeek(40);
        session.Host.ProcessPendingEvents();
        Assert.False(session.View.IsSeeking);
        Assert.Equal(40, session.View.SeekValue);
        Assert.Equal("00:00:40", session.View.PositionText);
        Assert.Contains(session.Fake.Commands, c => c is ["seek", "40", "absolute+exact"] or ["seek", "40", "absolute"]);
    }

    [Fact]
    public void Pause_during_seek_does_not_reset_thumb()
    {
        using var session = Create();
        session.View.BeginSeek();
        session.View.UpdateSeekPreview(55);
        session.Host.Pause();
        session.Host.ProcessPendingEvents();
        Assert.True(session.View.IsSeeking);
        Assert.Equal(55, session.View.SeekValue);
    }

    [Fact]
    public void CancelSeek_restores_player_position()
    {
        using var session = Create();
        session.View.BeginSeek();
        session.View.UpdateSeekPreview(90);
        session.View.CancelSeek();
        Assert.False(session.View.IsSeeking);
        Assert.Equal(0, session.View.SeekValue);
        Assert.Equal("00:00:00", session.View.PositionText);
    }

    [Fact]
    public void UpdateSeekPreview_ignored_when_not_dragging()
    {
        using var session = Create();
        session.View.UpdateSeekPreview(30);
        Assert.Equal(0, session.View.SeekValue);
    }

    [Fact]
    public void Volume_setter_clamps_and_updates_glyph()
    {
        using var session = Create();
        session.View.Volume = 0;
        Assert.Equal(0, session.View.Volume);
        Assert.Equal("\uE74F", session.View.VolumeGlyph);
        session.View.Volume = 80;
        Assert.Equal("\uE767", session.View.VolumeGlyph);
    }

    [Fact]
    public void Toggle_without_media_is_ignored()
    {
        using var session = Create(open: false);
        session.View.TogglePlayPause();
        Assert.Equal(PlayerState.Idle, session.Host.State);
    }

    [Fact]
    public void Stop_clears_playback_and_keeps_playlist()
    {
        using var session = Create();
        var path = session.View.Playlist.CurrentPath;
        Assert.Equal(1, session.View.Playlist.Count);

        session.View.Stop();
        session.Host.ProcessPendingEvents();

        Assert.True(session.View.ShowEmptyState);
        Assert.False(session.View.HasMedia);
        Assert.Equal(1, session.View.Playlist.Count);
        Assert.Equal(path, session.View.Playlist.CurrentPath);
    }

    [Fact]
    public void Youtube_dub_and_caption_survive_player_rebinds()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00:00.000 --> 00:00:01.500\nMerhaba\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "dQw4w9wgBcQ",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Song",
            StreamKind.Vod,
            userAgent: "TestTube/1.0",
            audioLang: "tr.3",
            subLang: "tr",
            captionUrl: vtt);
        view.AddStream("grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=dQw4w9wgBcQ") + "&audio=tr.3&sub=tr", play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
        }

        host.ProcessPendingEvents();
        Assert.Equal("tr", view.PreferredAudioLang);
        Assert.Equal("tr", view.PreferredSubLang);
        Assert.NotNull(view.Subtitles.Applied);
        Assert.Contains(fake.Lifecycle, item => item.Contains("alang=", StringComparison.Ordinal) && item.Contains("tr", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("slang=", StringComparison.Ordinal) && item.Contains("no", StringComparison.Ordinal));
        Assert.Contains(fake.Commands, command => command.Length >= 2 && command[0] == "sub-add");
        var addedAt = fake.Commands.FindLastIndex(command => command.Length >= 1 && command[0] == "sub-add");
        Assert.True(addedAt >= 0);
        Assert.DoesNotContain(
            fake.Commands.Skip(addedAt + 1),
            command => command.Length >= 1 && command[0] == "sub-remove");
        File.Delete(vtt);
    }

    [Fact]
    public void Colored_youtube_captions_play_ass_once_with_spaces()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(
            vtt,
            """
            WEBVTT

            00:00:00.000 --> 00:00:02.000
            <c.color00FF00>Hello world</c>
            """);
        var written = StreamCaptionLoader.WriteSrt(
            Path.Combine(Path.GetTempPath(), "GrokPlayer", "captions", "colorvid.en.vtt"),
            File.ReadAllText(vtt));
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "colorvidxx1",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Song",
            StreamKind.Vod,
            captionUrl: vtt);
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=colorvidxx1") + "&audio=en&sub=en",
            play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
        }

        host.ProcessPendingEvents();
        Assert.NotNull(view.Subtitles.Applied);
        Assert.Equal("Hello world", view.Subtitles.Applied!.Document.Cues[0].Text);
        Assert.Contains("Hello world", File.ReadAllText(view.Subtitles.Applied.PlayPath), StringComparison.Ordinal);
        var added = fake.Commands.Last(command => command.Length >= 2 && command[0] == "sub-add");
        Assert.Contains("Hello world", File.ReadAllText(added[1]), StringComparison.Ordinal);
        Assert.DoesNotContain(
            fake.Commands.Skip(fake.Commands.FindLastIndex(command => command.Length >= 1 && command[0] == "sub-add") + 1),
            command => command.Length >= 1 && command[0] == "sub-add");
        File.Delete(vtt);
        if (File.Exists(written))
        {
            File.Delete(written);
        }

        var ass = StreamCaptionLoader.PlayPath(written);
        if (ass.Length > 0 && File.Exists(ass) && ass != written)
        {
            File.Delete(ass);
        }
    }

    [Fact]
    public void External_caption_file_does_not_also_enable_stream_slang()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello world\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "dQw4w9wgBcQ",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Song",
            StreamKind.Vod,
            captionUrl: vtt);
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=dQw4w9wgBcQ") + "&audio=en&sub=en",
            play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
        }

        host.ProcessPendingEvents();
        Assert.DoesNotContain(fake.Lifecycle, item => item.Contains("slang=en", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("slang=no", StringComparison.Ordinal));
        File.Delete(vtt);
    }

    [Fact]
    public void Open_clears_a_stuck_seek_preview()
    {
        var first = TestMedia.CreateTempFile("one.mp4");
        var second = TestMedia.CreateTempFile("two.mp4");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.Open(first);
        host.ProcessPendingEvents();
        view.BeginSeek();
        view.UpdateSeekPreview(12);
        Assert.True(view.IsSeeking);
        view.Open(second);
        host.ProcessPendingEvents();
        Assert.False(view.IsSeeking);
        Assert.Equal(second, host.MediaPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Local_after_stream_clears_extra_audio_and_stream_subs()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nStream line\n");
        var local = TestMedia.CreateTempFile("movie.mp4");
        var sidecar = Path.ChangeExtension(local, ".srt");
        File.WriteAllText(sidecar, "1\n00:00:00,000 --> 00:00:01,000\nLocal line\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "dQw4w9wgBcQ",
            "https://manifest.googlevideo.com/api/manifest/hls_playlist/video.m3u8",
            "Song",
            StreamKind.Vod,
            audioUrl: "https://manifest.googlevideo.com/api/manifest/hls_playlist/tr.m3u8",
            audioLang: "tr",
            captionUrl: vtt);
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=dQw4w9wgBcQ") + "&audio=tr&sub=tr",
            play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
        }

        host.ProcessPendingEvents();
        view.Open(local);
        host.ProcessPendingEvents();
        Assert.Contains(fake.Lifecycle, item => item == "property:audio-file=");
        Assert.Contains(fake.Commands, command => command.Length >= 1 && command[0] == "audio-remove");
        Assert.False(view.StreamTab);
        Assert.Equal("Local line", view.Subtitles.Applied?.Document.Cues[0].Text);
        Assert.DoesNotContain(
            view.Subtitles.Tracks.Where(track => view.Subtitles.IsVisible(track)),
            track => track.AttachedMedia is not null &&
                     track.AttachedMedia.StartsWith("youtube|", StringComparison.OrdinalIgnoreCase));
        var lastAdd = fake.Commands.LastOrDefault(command => command.Length >= 2 && command[0] == "sub-add");
        Assert.NotNull(lastAdd);
        Assert.Equal(view.Subtitles.Applied?.PlayPath, lastAdd![1]);
        Assert.Contains("Local line", File.ReadAllText(lastAdd[1]), StringComparison.Ordinal);
        File.Delete(vtt);
        File.Delete(sidecar);
    }

    [Fact]
    public void Browser_edit_rewrites_a_new_play_file()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".srt");
        File.WriteAllText(path, "1\n00:00:00,000 --> 00:00:01,000\nHello\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        var track = view.Subtitles.AddFile(path, apply: true);
        var first = track.PlayPath;
        track.Document.Cues[0].Text = "Hello world";
        track.Document.Cues[0].Spans = [new CaptionSpan("Hello world", "#00FF00")];
        view.Subtitles.PersistActive();
        Assert.NotEqual(first, view.Subtitles.Applied!.PlayPath);
        Assert.Contains("Hello world", File.ReadAllText(view.Subtitles.Applied.PlayPath), StringComparison.Ordinal);
        File.Delete(path);
    }

    [Fact]
    public void Opening_the_next_stream_pauses_the_one_that_is_playing()
    {
        var local = TestMedia.CreateTempFile("hold.mp4");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.Open(local);
        host.ProcessPendingEvents();
        view.ResolveYouTube = _ => new YouTubePlayable(
            "aaaaaaaaaaa",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/next.m3u8",
            "Next",
            StreamKind.Vod);
        view.AddStream("https://www.youtube.com/watch?v=aaaaaaaaaaa", play: true);
        Assert.Contains(fake.Lifecycle, item => item.Contains("pause=True", StringComparison.OrdinalIgnoreCase));
        var load = fake.Lifecycle.FindLastIndex(item => item.Contains("loadfile", StringComparison.Ordinal));
        var play = fake.Lifecycle.FindLastIndex(item => item == "property:pause=False");
        Assert.True(play > load, "the replacement stream must start playing");
    }

    [Fact]
    public void Switching_back_to_a_resolved_stream_uses_the_cached_playable()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        var firstUrl = "https://www.youtube.com/watch?v=aaaaaaaaaaa";
        var secondUrl = "https://www.youtube.com/watch?v=bbbbbbbbbbb";
        var resolves = 0;
        view.ResolveYouTube = url =>
        {
            if (url.Contains("aaaaaaaaaaa", StringComparison.Ordinal) && Interlocked.Increment(ref resolves) == 1)
            {
                return new YouTubePlayable(
                    "aaaaaaaaaaa",
                    "https://manifest.googlevideo.com/api/manifest/hls_variant/one.m3u8",
                    "One",
                    StreamKind.Vod);
            }

            return null;
        };
        view.AddStream(firstUrl, play: true, "One");
        Assert.Equal("https://manifest.googlevideo.com/api/manifest/hls_variant/one.m3u8", view.Streams.Items[0].MediaUrl);
        view.AddStream(secondUrl, play: true, "Two");
        view.PlayFrom(view.Streams, 0);
        Assert.Equal(1, resolves);
        Assert.Contains(
            fake.Commands,
            command => command.Length >= 2 &&
                       command[0] == "loadfile" &&
                       command[1].Contains("one.m3u8", StringComparison.Ordinal));
    }

    [Fact]
    public void Storyboard_spec_does_not_leak_onto_the_next_stream()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = url =>
        {
            if (url.Contains("aaaaaaaaaaa", StringComparison.Ordinal))
            {
                return new YouTubePlayable(
                    "aaaaaaaaaaa",
                    "https://manifest.googlevideo.com/api/manifest/hls_variant/one.m3u8",
                    "One",
                    StreamKind.Vod,
                    storyboardSpec: "https://i.ytimg.com/sb/aaaaaaaaaaa/storyboard3_L$L/$N.jpg|160#90#20#5#5#5000#M$M#rs$one");
            }

            return new YouTubePlayable(
                "bbbbbbbbbbb",
                "https://manifest.googlevideo.com/api/manifest/hls_variant/two.m3u8",
                "Two",
                StreamKind.Live);
        };
        view.AddStream("https://www.youtube.com/watch?v=aaaaaaaaaaa", play: true, "One");
        Assert.Contains("aaaaaaaaaaa", view.StoryboardSpec, StringComparison.Ordinal);
        view.AddStream("https://www.youtube.com/watch?v=bbbbbbbbbbb", play: true, "Two");
        Assert.True(string.IsNullOrWhiteSpace(view.StoryboardSpec));
    }

    [Fact]
    public void Changing_audio_on_a_cached_stream_resolves_again()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        var watch = "https://www.youtube.com/watch?v=aaaaaaaaaaa";
        var resolves = 0;
        view.ResolveYouTube = _ =>
        {
            Interlocked.Increment(ref resolves);
            return new YouTubePlayable(
                "aaaaaaaaaaa",
                "https://manifest.googlevideo.com/api/manifest/hls_variant/" + resolves + ".m3u8",
                "One",
                StreamKind.Vod,
                audioLang: resolves == 1 ? "en" : "ko");
        };
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString(watch) + "&audio=en",
            play: true,
            "One");
        Assert.Equal(1, resolves);
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString(watch) + "&audio=ko",
            play: true,
            "One");
        Assert.Equal(2, resolves);
        Assert.Contains(
            fake.Commands,
            command => command.Length >= 2 &&
                       command[0] == "loadfile" &&
                       command[1].Contains("2.m3u8", StringComparison.Ordinal));
    }

    [Fact]
    public void Stream_items_keep_their_own_height()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "aaaaaaaaaaa",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "One",
            StreamKind.Vod);
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=aaaaaaaaaaa") + "&height=144",
            play: false,
            "One");
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=bbbbbbbbbbb") + "&height=1440",
            play: false,
            "Two");
        Assert.Equal(144, view.Streams.Items[0].VideoHeight);
        Assert.Equal(1440, view.Streams.Items[1].VideoHeight);
    }

    [Fact]
    public void Protocol_without_sub_still_loads_captions()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(vtt, "WEBVTT\nLanguage: English\n\n00:00:00.000 --> 00:00:01.000\nBEHIND ME\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "randomvidxx",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Talk",
            StreamKind.Vod,
            captionUrl: vtt);
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=randomvidxx"),
            play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.False(view.Streams.Items[0].SkipCaptions);
        Assert.NotNull(view.Subtitles.Applied);
        Assert.Contains("BEHIND ME", view.Subtitles.Applied!.Document.Cues[0].Text, StringComparison.Ordinal);
        File.Delete(vtt);
    }

    [Fact]
    public void Stream_without_protocol_langs_does_not_invent_english()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(
            host,
            streamSubtitles: new StreamSubtitleSettings { Mode = StreamSubtitleMode.On });
        view.ResolveYouTube = _ => new YouTubePlayable(
            "dQw4w9wgBcQ",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Song",
            StreamKind.Vod);
        view.AddStream("https://www.youtube.com/watch?v=dQw4w9wgBcQ", play: false);
        Assert.Null(view.PreferredAudioLang);
        Assert.Null(view.PreferredSubLang);
    }

    [Fact]
    public void Youtube_bound_variant_attaches_selected_audio_file()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "Qtl8lJwbd4g",
            "https://manifest.googlevideo.com/api/manifest/hls_playlist/video.m3u8",
            "Escape",
            StreamKind.Vod,
            audioUrl: "https://manifest.googlevideo.com/api/manifest/hls_playlist/tr.m3u8",
            audioLang: "tr",
            subLang: "tr");
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=Qtl8lJwbd4g") + "&audio=tr&sub=tr",
            play: true);
        Assert.Contains(
            fake.Lifecycle,
            item => item.Contains("audio-file=", StringComparison.Ordinal) && item.Contains("tr.m3u8", StringComparison.Ordinal));
        Assert.Contains(
            fake.Commands,
            command => command.Length >= 2 &&
                       command[0] == "loadfile" &&
                       command[1].Contains("hls_playlist", StringComparison.Ordinal));
        Assert.DoesNotContain(
            fake.Commands,
            command => command.Length >= 2 &&
                       command[0] == "loadfile" &&
                       command[1].EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) &&
                       !command[1].StartsWith("http", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Protocol_langs_replace_remembered_turkish()
    {
        var store = Path.Combine(Path.GetTempPath(), "stream-subs-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new StreamSubtitleSettings { Mode = StreamSubtitleMode.On, Store = store, LastAudio = "tr", LastSub = "tr" };
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host, streamSubtitles: settings);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "Qtl8lJwbd4g",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Escape",
            StreamKind.Vod);
        Assert.Equal("tr", view.PreferredAudioLang);
        Assert.Equal("tr", view.PreferredSubLang);
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=Qtl8lJwbd4g") +
            "&audio=bn&sub=ru",
            play: false);
        Assert.Equal("bn", view.PreferredAudioLang);
        Assert.Equal("ru", view.PreferredSubLang);
        Assert.Equal("bn", view.Streams.Items[0].AudioLang);
        Assert.Equal("ru", view.Streams.Items[0].SubLang);
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=Qtl8lJwbd4g") +
            "&audio=zh-Hans&sub=off",
            play: false);
        Assert.Equal("zh-Hans", view.PreferredAudioLang);
        Assert.Null(view.PreferredSubLang);
        Assert.True(view.Streams.Items[0].SkipCaptions);
        File.Delete(store);
    }

    [Fact]
    public void Stream_items_keep_their_own_languages()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = path =>
        {
            var id = path.Contains("aaaaaaaaaaa", StringComparison.Ordinal) ? "aaaaaaaaaaa" : "bbbbbbbbbbb";
            return new YouTubePlayable(id, "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8", id, StreamKind.Vod);
        };
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=aaaaaaaaaaa") +
            "&audio=bn&sub=bn",
            play: false,
            "One");
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=bbbbbbbbbbb") +
            "&audio=ru&sub=zh-Hans",
            play: true,
            "Two");
        Assert.Equal("bn", view.Streams.Items[0].AudioLang);
        Assert.Equal("bn", view.Streams.Items[0].SubLang);
        Assert.Equal("ru", view.Streams.Items[1].AudioLang);
        Assert.Equal("zh-Hans", view.Streams.Items[1].SubLang);
        view.PlayFrom(view.Streams, 0);
        Assert.Equal("bn", view.PreferredAudioLang);
        Assert.Equal("bn", view.PreferredSubLang);
        view.PlayFrom(view.Streams, 1);
        Assert.Equal("ru", view.PreferredAudioLang);
        Assert.Equal("zh-Hans", view.PreferredSubLang);
    }

    [Fact]
    public void Switching_stream_vods_reapplies_each_items_captions()
    {
        var first = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        var second = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(first, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nFirst vod\n");
        File.WriteAllText(second, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nSecond vod\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = path =>
        {
            var one = path.Contains("aaaaaaaaaaa", StringComparison.Ordinal);
            return new YouTubePlayable(
                one ? "aaaaaaaaaaa" : "bbbbbbbbbbb",
                "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
                one ? "One" : "Two",
                StreamKind.Vod,
                captionUrl: one ? first : second);
        };
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=aaaaaaaaaaa") +
            "&sub=en&caption=" + Uri.EscapeDataString(first),
            play: true,
            "One");
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied?.Document.Cues[0].Text != "First vod")
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.Equal("First vod", view.Subtitles.Applied?.Document.Cues[0].Text);
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=bbbbbbbbbbb") +
            "&sub=en&caption=" + Uri.EscapeDataString(second),
            play: true,
            "Two");
        until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied?.Document.Cues[0].Text != "Second vod")
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.Equal("Second vod", view.Subtitles.Applied?.Document.Cues[0].Text);
        view.PlayFrom(view.Streams, 0);
        host.ProcessPendingEvents();
        until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied?.Document.Cues[0].Text != "First vod")
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.Equal("en", view.PreferredSubLang);
        Assert.False(view.Streams.Items[0].SkipCaptions);
        Assert.Equal("First vod", view.Subtitles.Applied?.Document.Cues[0].Text);
        File.Delete(first);
        File.Delete(second);
    }

    [Fact]
    public void Switching_captioned_youtube_to_direct_live_clears_old_caption_immediately()
    {
        var caption = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(caption, "WEBVTT\n\n00:00:00.000 --> 00:02:00.000\nOld vod caption\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "aaaaaaaaaaa",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Captioned VOD",
            StreamKind.Vod,
            captionUrl: caption);

        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=aaaaaaaaaaa") +
            "&sub=en&caption=" + Uri.EscapeDataString(caption),
            play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.Equal("Old vod caption", view.Subtitles.Applied?.Document.Cues[0].Text);
        var commandCount = fake.Commands.Count;

        view.AddStream("https://example.com/live.m3u8", play: true, "Live");

        Assert.Null(view.Subtitles.Applied);
        Assert.Null(view.OnScreenCaption);
        host.ProcessPendingEvents();
        Assert.Null(view.Subtitles.Applied);
        Assert.Null(view.OnScreenCaption);
        Assert.Contains(fake.Commands.Skip(commandCount), command => command.Length >= 1 && command[0] == "sub-remove");
        File.Delete(caption);
    }

    [Fact]
    public void Reopen_same_video_with_translate_replaces_captions()
    {
        var turkish = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        var german = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(turkish, "WEBVTT\nLanguage: tr\n\n00:00:00.000 --> 00:00:01.000\nMerhaba\n");
        File.WriteAllText(german, "WEBVTT\nLanguage: de\n\n00:00:00.000 --> 00:00:01.000\nHallo\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "EzWLUda58k4",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "GTA",
            StreamKind.Vod);
        var watch = "https://www.youtube.com/watch?v=EzWLUda58k4";
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString(watch) +
            "&sub=tr:asr&caption=" + Uri.EscapeDataString(turkish),
            play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied?.Document.Cues[0].Text != "Merhaba")
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.Equal("Merhaba", view.Subtitles.Applied?.Document.Cues[0].Text);
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString(watch) +
            "&sub=de&caption=" + Uri.EscapeDataString(german),
            play: true);
        until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied?.Document.Cues[0].Text != "Hallo")
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.Equal("de", view.PreferredSubLang);
        Assert.Equal("Hallo", view.Subtitles.Applied?.Document.Cues[0].Text);
        File.Delete(turkish);
        File.Delete(german);
    }

    [Fact]
    public void Reopen_with_captions_off_clears_previous_subs()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00:00.000 --> 00:00:01.500\nMerhaba\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "Qtl8lJwbd4g",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Escape",
            StreamKind.Vod,
            captionUrl: vtt);
        var watch = "https://www.youtube.com/watch?v=Qtl8lJwbd4g";
        view.AddStream("grokplayer://open?url=" + Uri.EscapeDataString(watch) + "&audio=tr&sub=off", play: true);
        host.ProcessPendingEvents();
        Assert.Null(view.Subtitles.Applied);
        view.AddStream("grokplayer://open?url=" + Uri.EscapeDataString(watch) + "&audio=tr&sub=tr", play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
        }

        host.ProcessPendingEvents();
        Assert.NotNull(view.Subtitles.Applied);
        view.AddStream("grokplayer://open?url=" + Uri.EscapeDataString(watch) + "&audio=tr&sub=off", play: true);
        Assert.Null(view.Subtitles.Applied);
        host.ProcessPendingEvents();
        Assert.Null(view.Subtitles.Applied);
        Assert.Null(view.OnScreenCaption);
        Assert.Contains(fake.Commands, command => command.Length >= 1 && command[0] == "sub-remove");
        File.Delete(vtt);
    }

    [Fact]
    public void Leftover_translate_lang_still_applies_official_captions()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(
            vtt,
            "WEBVTT\nLanguage: English\n\n00:00:00.000 --> 00:00:01.500\nBEHIND ME are ONE HUNDRED cops\n");
        var store = Path.Combine(Path.GetTempPath(), "stream-subs-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new StreamSubtitleSettings { Mode = StreamSubtitleMode.On, Store = store, LastSub = "de" };
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host, streamSubtitles: settings);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "qtlcaptionx",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Escape",
            StreamKind.Vod,
            captionUrl: vtt);
        view.AddStream("https://www.youtube.com/watch?v=qtlcaptionx&t=159s", play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.NotNull(view.Subtitles.Applied);
        Assert.Contains(
            "BEHIND ME",
            view.Subtitles.Applied!.Document.Cues[0].Text,
            StringComparison.Ordinal);
        File.Delete(vtt);
        File.Delete(store);
    }

    [Fact]
    public void Official_captions_apply_when_opening_a_new_youtube_video()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(
            vtt,
            "WEBVTT\nLanguage: English\n\n00:00:00.000 --> 00:00:01.500\nBEHIND ME are ONE HUNDRED cops\n");
        var store = Path.Combine(Path.GetTempPath(), "stream-subs-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new StreamSubtitleSettings { Mode = StreamSubtitleMode.On, Store = store, LastSub = "de" };
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host, streamSubtitles: settings);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "Qtl8lJwbd4g",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Escape",
            StreamKind.Vod,
            captionUrl: vtt);
        view.AddStream(
            "grokplayer://open?url=" +
            Uri.EscapeDataString("https://www.youtube.com/watch?v=Qtl8lJwbd4g&t=159s") +
            "&sub=en&caption=" + Uri.EscapeDataString(vtt),
            play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.Equal("en", view.PreferredSubLang);
        Assert.NotNull(view.Subtitles.Applied);
        Assert.Contains(
            "BEHIND ME",
            view.Subtitles.Applied!.Document.Cues[0].Text,
            StringComparison.Ordinal);
        File.Delete(vtt);
        File.Delete(store);
    }

    [Fact]
    public void Auto_asr_caption_url_from_protocol_loads()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nAuto generated\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "asrvidxxxx1",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Talk",
            StreamKind.Vod);
        view.AddStream(
            "grokplayer://open?url=" +
            Uri.EscapeDataString("https://www.youtube.com/watch?v=asrvidxxxx1") +
            "&sub=en:asr&caption=" + Uri.EscapeDataString(vtt),
            play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.Equal("en:asr", view.PreferredSubLang);
        Assert.False(view.Streams.Items[0].SkipCaptions);
        Assert.NotNull(view.Subtitles.Applied);
        Assert.Equal("Auto generated", view.Subtitles.Applied!.Document.Cues[0].Text);
        File.Delete(vtt);
    }

    [Fact]
    public void Default_subtitle_position_matches_three_down_nudges_from_the_old_default()
    {
        var session = Create(open: false);
        Assert.Equal(100, session.View.SubPos);
        session.View.NudgeSubPos(-4);
        Assert.Equal(96, session.View.SubPos);
        session.View.NudgeSubPos(4);
        Assert.Equal(100, session.View.SubPos);
        session.Dispose();
    }

    private static Session Create(bool open = true)
    {
        var fake = new FakeMpvNative();
        var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        var view = new PlaybackViewModel(host);
        if (open)
        {
            var path = TestMedia.CreateTempFile("movie.mp4");
            view.Open(path);
            host.ProcessPendingEvents();
        }

        return new Session(host, view, fake);
    }

    private sealed class Session : IDisposable
    {
        public Session(PlayerHost host, PlaybackViewModel view, FakeMpvNative fake)
        {
            Host = host;
            View = view;
            Fake = fake;
        }

        public PlayerHost Host { get; }
        public PlaybackViewModel View { get; }
        public FakeMpvNative Fake { get; }

        public void Dispose()
        {
            View.Dispose();
            Host.Dispose();
        }
    }
}
