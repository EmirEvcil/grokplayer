using Grok.Player.Core.Launch;
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
    public void External_short_prerolls_fall_back_to_the_real_player_page()
    {
        var dizipal = "https://dizipal2121.com/bolum/test";
        Assert.Equal(
            dizipal,
            PlaybackViewModel.PreferredExternalPath("https://cdn.example/clip.mp4", dizipal, 2));

        var kick = "https://kick.com/channel/videos/01a044ef-8900-7c3d-9539-696d32367f14";
        Assert.Equal(
            kick,
            PlaybackViewModel.PreferredExternalPath("https://cdn.example/advert.mp4", kick, null));

        Assert.Equal(
            "https://cdn.example/movie.m3u8",
            PlaybackViewModel.PreferredExternalPath(
                "https://cdn.example/movie.m3u8",
                "https://movies.example/watch/full-film",
                5400));

        var protectedManifest = "https://fastplay.mom/manifests/episode/master.txt?verify=123-proof";
        var protectedPlayer = "https://fastplay.mom/video/episode";
        Assert.Equal(
            protectedPlayer,
            PlaybackViewModel.PreferredExternalPath(protectedManifest, protectedPlayer, null));

        Assert.Equal(
            "https://www.dailymotion.com/video/xap6qz2",
            PlaybackViewModel.PreferredExternalPath(
                "https://cdndirector.dailymotion.com/cdn/manifest/video/xap6qz2.m3u8?sec=token",
                "https://www.dailymotion.com/video/xap6qz2",
                2614));

        Assert.Equal(
            "https://hdfilmcehennemi.mobi/video/embed/xnZQ9xsXLfb/?rapidrame_id=gr2rb77x3mpm",
            PlaybackViewModel.PreferredExternalPath(
                "https://hls8.playmix.uno/hls/film.mp4/master.txt",
                "https://hdfilmcehennemi.mobi/video/embed/xnZQ9xsXLfb/?rapidrame_id=gr2rb77x3mpm",
                5400));
    }

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
    public void Cycling_a_sidecar_file_fills_on_screen_caption()
    {
        var turkish = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-tr.vtt");
        File.WriteAllText(turkish, "WEBVTT\n\n00:00:00.000 --> 00:00:05.000\nWalt, beynin Wisconsin kadar büyük...\n");
        try
        {
            using var session = Create(open: false);
            Assert.True(session.View.AddStream(
                ExternalOpen.ToProtocol(
                    "https://cdn.example/bb.m3u8",
                    "BB",
                    StreamKind.Vod,
                    captions: [new ExternalCaption("tr", turkish, "Turkish")]),
                play: true));
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until)
            {
                session.Host.ProcessPendingEvents();
                session.View.CycleSubtitle();
                if (session.View.SubtitleTrackLabel.Contains("Türkçe", StringComparison.Ordinal))
                {
                    break;
                }

                Thread.Sleep(20);
            }

            session.Host.ProcessPendingEvents();
            Assert.Contains("Wisconsin", session.View.OnScreenCaption);
        }
        finally
        {
            File.Delete(turkish);
        }
    }

    [Fact]
    public void Selecting_a_network_sidecar_writes_an_ass_overlay_even_when_vf_add_succeeds()
    {
        var english = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-en.vtt");
        var turkish = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-tr.vtt");
        File.WriteAllText(english, "WEBVTT\n\n00:00:00.000 --> 00:00:05.000\nI am the one who knocks\n");
        File.WriteAllText(turkish, "WEBVTT\n\n00:00:00.000 --> 00:00:05.000\nKapıyı çalan benim\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        Assert.True(view.AddStream(
            ExternalOpen.ToProtocol(
                "https://cdn.example/bb.m3u8",
                "BB",
                StreamKind.Vod,
                captions:
                [
                    new ExternalCaption("en", english, "English"),
                    new ExternalCaption("tr", turkish, "Turkish")
                ]),
            play: true));
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until)
        {
            host.ProcessPendingEvents();
            view.CycleSubtitle();
            if ((view.OnScreenCaption ?? "").Contains("knocks", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            Thread.Sleep(20);
        }

        Assert.Contains("knocks", view.OnScreenCaption, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            fake.Commands,
            command => command.Length >= 3 &&
                       command[0] == "osd-overlay" &&
                       command.Contains("ass-events") &&
                       command.Any(arg => arg.Contains("\\bord3", StringComparison.Ordinal)) &&
                       command.Any(arg => arg.Contains("knocks", StringComparison.OrdinalIgnoreCase)));

        until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
        {
            view.CycleSubtitle();
            if ((view.OnScreenCaption ?? "").Contains("çalan", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            Thread.Sleep(20);
        }

        Assert.Contains("çalan", view.OnScreenCaption, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            fake.Commands,
            command => command.Length >= 3 &&
                       command[0] == "osd-overlay" &&
                       command.Contains("ass-events") &&
                       command.Any(arg => arg.Contains("çalan", StringComparison.OrdinalIgnoreCase)));

        view.Subtitles.Active!.Document.Cues[0].Spans =
            [new CaptionSpan("Kapıyı çalan benim", "#00FF00", Bold: true)];
        view.Subtitles.PersistActive();
        host.ProcessPendingEvents();
        var styledOverlay = fake.Commands.Last(command =>
            command.Length >= 4 && command[0] == "osd-overlay" && command[2] == "ass-events");
        Assert.Contains("\\c&H0000FF00&", styledOverlay[3], StringComparison.Ordinal);
        Assert.Contains("\\b1", styledOverlay[3], StringComparison.Ordinal);
        File.Delete(english);
        File.Delete(turkish);
    }

    [Fact]
    public void Network_sidecar_overlay_is_hidden_before_its_first_cue_and_between_cues()
    {
        var caption = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-tr.vtt");
        File.WriteAllText(
            caption,
            "WEBVTT\n\n00:01:08.151 --> 00:01:10.000\nİlk altyazı\n\n00:01:15.000 --> 00:01:17.000\nİkinci altyazı\n");
        try
        {
            var fake = new FakeMpvNative();
            using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
            using var view = new PlaybackViewModel(host);
            Assert.True(view.AddStream(
                ExternalOpen.ToProtocol(
                    "https://cdn.example/movie.m3u8",
                    "Movie",
                    StreamKind.Vod,
                    captions: [new ExternalCaption("tr", caption, "Turkish")]),
                play: true));
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
            {
                host.ProcessPendingEvents();
                Thread.Sleep(20);
            }

            Assert.NotNull(view.Subtitles.Applied);
            host.Seek(TimeSpan.Zero);
            host.ProcessPendingEvents();
            Assert.Null(view.OnScreenCaption);
            Assert.Contains(fake.Commands, command =>
                command.Length >= 3 && command[0] == "osd-overlay" && command[2] == "none");

            host.Seek(TimeSpan.FromSeconds(69));
            host.ProcessPendingEvents();
            Assert.Equal("İlk altyazı", view.OnScreenCaption);

            host.Seek(TimeSpan.FromSeconds(12));
            host.ProcessPendingEvents();
            Assert.Null(view.OnScreenCaption);
        }
        finally
        {
            File.Delete(caption);
        }
    }

    [Fact]
    public void Cycling_youtube_captions_keeps_the_karaoke_play_file()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-yt.vtt");
        File.WriteAllText(
            vtt,
            "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nHello<00:00:00.400><c> world</c>\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "karaokexxx1",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Song",
            StreamKind.Vod,
            captionUrl: vtt);
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=karaokexxx1") + "&sub=en&caption=" + Uri.EscapeDataString(vtt),
            play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.NotNull(view.Subtitles.Applied);
        Assert.True(view.Subtitles.Applied!.Document.HasKaraoke);
        var firstPlay = view.Subtitles.Applied.PlayPath;
        Assert.Contains("\\t(", File.ReadAllText(firstPlay), StringComparison.Ordinal);
        view.CycleSubtitle();
        host.ProcessPendingEvents();
        view.CycleSubtitle();
        host.ProcessPendingEvents();
        var lastAdd = fake.Commands.Last(command => command.Length >= 2 && command[0] == "sub-add");
        Assert.Equal(view.Subtitles.Applied!.PlayPath, lastAdd[1]);
        Assert.Contains("\\t(", File.ReadAllText(lastAdd[1]), StringComparison.Ordinal);
        Assert.NotEqual(vtt, lastAdd[1]);
        File.Delete(vtt);
    }

    [Fact]
    public void Mute_does_not_turn_youtube_captions_off()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-yt.vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nHello world\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "mutecapxxx1",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Song",
            StreamKind.Vod,
            captionUrl: vtt);
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=mutecapxxx1") + "&sub=en&caption=" + Uri.EscapeDataString(vtt),
            play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.NotNull(view.Subtitles.Applied);
        host.Pause();
        host.ProcessPendingEvents();
        view.ToggleMute();
        host.ProcessPendingEvents();
        Assert.NotNull(view.Subtitles.Applied);
        Assert.NotEqual("Subs Off", view.SubtitleTrackLabel);
        var lastSid = fake.Lifecycle.FindLastIndex(item => item == "property:sid=no");
        var lastAdd = fake.Lifecycle.FindLastIndex(item => item.StartsWith("command:sub-add", StringComparison.Ordinal));
        Assert.True(lastAdd > lastSid, "mute must not leave captions detached");
        File.Delete(vtt);
    }

    [Fact]
    public void Switching_youtube_playlist_items_keeps_captions()
    {
        var first = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-a.vtt");
        var second = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-b.vtt");
        File.WriteAllText(first, "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nFirst video\n");
        File.WriteAllText(second, "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nSecond video\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = path =>
            path.Contains("aaaaaaaaaaa", StringComparison.Ordinal)
                ? new YouTubePlayable(
                    "aaaaaaaaaaa",
                    "https://manifest.googlevideo.com/api/manifest/hls_variant/one.m3u8",
                    "One",
                    StreamKind.Vod,
                    captionUrl: first)
                : new YouTubePlayable(
                    "bbbbbbbbbbb",
                    "https://manifest.googlevideo.com/api/manifest/hls_variant/two.m3u8",
                    "Two",
                    StreamKind.Vod,
                    captionUrl: second);
        Assert.True(view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=aaaaaaaaaaa") + "&sub=en&caption=" + Uri.EscapeDataString(first),
            play: true));
        host.ProcessPendingEvents();
        Assert.True(view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=bbbbbbbbbbb") + "&sub=en&caption=" + Uri.EscapeDataString(second),
            play: true));
        host.ProcessPendingEvents();
        view.PlayFrom(view.Streams, 0);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until)
        {
            host.ProcessPendingEvents();
            if (view.Subtitles.Applied?.Document.Cues.FirstOrDefault()?.Text.Contains("First", StringComparison.Ordinal) == true)
            {
                break;
            }

            Thread.Sleep(20);
        }

        Assert.Contains("First", view.Subtitles.Applied?.Document.Cues[0].Text ?? "", StringComparison.Ordinal);
        File.Delete(first);
        File.Delete(second);
    }

    [Fact]
    public void Browser_edit_of_a_sidecar_vtt_reapplies_the_play_file()
    {
        var turkish = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-tr.vtt");
        File.WriteAllText(turkish, "WEBVTT\n\n00:00:00.000 --> 00:00:05.000\nWalt, beynin Wisconsin kadar büyük...\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        Assert.True(view.AddStream(
            ExternalOpen.ToProtocol(
                "https://cdn.example/somebody.m3u8",
                "Somebody",
                StreamKind.Vod,
                captions: [new ExternalCaption("tr", turkish, "Turkish")]),
            play: true));
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            host.ProcessPendingEvents();
            Thread.Sleep(20);
        }

        Assert.NotNull(view.Subtitles.Applied);
        view.Subtitles.Active!.Document.Cues[0].Text = "Edited Wisconsin";
        view.Subtitles.Active.Document.Cues[0].Spans = [new CaptionSpan("Edited Wisconsin", null)];
        view.Subtitles.PersistActive();
        host.ProcessPendingEvents();
        var lastAdd = fake.Commands.Last(command => command.Length >= 2 && command[0] == "sub-add");
        Assert.Equal(view.Subtitles.Applied!.PlayPath, lastAdd[1]);
        Assert.Contains("Edited Wisconsin", File.ReadAllText(lastAdd[1]), StringComparison.Ordinal);
        File.Delete(turkish);
        var edited = turkish + ".edited.srt";
        if (File.Exists(edited))
        {
            File.Delete(edited);
        }
    }

    [Fact]
    public void Youtube_asr_and_official_turkish_are_separate_cycle_choices()
    {
        var official = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-tr.vtt");
        var asr = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-asr.vtt");
        File.WriteAllText(official, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nMerhaba\n");
        File.WriteAllText(asr, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nmerhaba\n");
        try
        {
            using var session = Create(open: false);
            Assert.True(session.View.AddStream(
                ExternalOpen.ToProtocol(
                    "https://cdn.example/master.m3u8",
                    "Talk",
                    StreamKind.Vod,
                    captions:
                    [
                        new ExternalCaption("en", official, "English"),
                        new ExternalCaption("tr", official, "Turkish"),
                        new ExternalCaption("tr:asr", asr, "Turkish")
                    ]),
                play: true));
            var until = DateTime.UtcNow.AddSeconds(3);
            var labels = new List<string>();
            while (DateTime.UtcNow < until)
            {
                session.Host.ProcessPendingEvents();
                session.View.CycleSubtitle();
                labels.Add(session.View.SubtitleTrackLabel);
                if (labels.Contains("Subs Off") &&
                    labels.Contains("Subs English") &&
                    labels.Contains("Subs Türkçe") &&
                    labels.Contains("Subs Türkçe (auto)"))
                {
                    break;
                }

                Thread.Sleep(20);
            }

            Assert.Contains("Subs English", labels);
            Assert.Contains("Subs Türkçe", labels);
            Assert.Contains("Subs Türkçe (auto)", labels);
            Assert.DoesNotContain("Subs tr:asr", labels);
        }
        finally
        {
            File.Delete(official);
            File.Delete(asr);
        }
    }

    [Fact]
    public void Stream_off_still_lists_named_sidecar_choices()
    {
        var english = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-en.vtt");
        var turkish = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-tr.vtt");
        File.WriteAllText(english, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello\n");
        File.WriteAllText(turkish, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nMerhaba\n");
        try
        {
            using var session = Create(open: false);
            session.View.SetStreamSubtitleMode(StreamSubtitleMode.Off);
            Assert.True(session.View.AddStream(
                ExternalOpen.ToProtocol(
                    "https://cdn.example/somebody.m3u8",
                    "Somebody",
                    StreamKind.Vod,
                    captions:
                    [
                        new ExternalCaption("en", english, "English"),
                        new ExternalCaption("tr", turkish, "Turkish")
                    ]),
                play: true));
            var until = DateTime.UtcNow.AddSeconds(3);
            var labels = new List<string>();
            while (DateTime.UtcNow < until)
            {
                session.Host.ProcessPendingEvents();
                session.View.CycleSubtitle();
                labels.Add(session.View.SubtitleTrackLabel);
                if (labels.Contains("Subs English") && labels.Contains("Subs Türkçe"))
                {
                    break;
                }

                Thread.Sleep(20);
            }

            Assert.Contains("Subs English", labels);
            Assert.Contains("Subs Türkçe", labels);
        }
        finally
        {
            File.Delete(english);
            File.Delete(turkish);
        }
    }

    [Fact]
    public void Leftover_youtube_asr_lang_does_not_hide_hdfilm_sidecars()
    {
        var english = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-en.vtt");
        var turkish = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-tr.vtt");
        File.WriteAllText(english, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello\n");
        File.WriteAllText(turkish, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nMerhaba\n");
        try
        {
            var fake = new FakeMpvNative();
            using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
            using var view = new PlaybackViewModel(
                host,
                streamSubtitles: new StreamSubtitleSettings { Mode = StreamSubtitleMode.On, LastSub = "tr:asr" });
            fake.SeedTrack(0, "sub", 1, "en", "English", false);
            fake.SeedTrack(1, "sub", 2, "tr", "Turkish", false);
            Assert.True(view.AddStream(
                ExternalOpen.ToProtocol(
                    "https://cdn.example/somebody.m3u8",
                    "Somebody",
                    StreamKind.Vod,
                    captions:
                    [
                        new ExternalCaption("en", english, "English"),
                        new ExternalCaption("tr", turkish, "Turkish")
                    ]),
                play: true));
            var until = DateTime.UtcNow.AddSeconds(3);
            var labels = new List<string>();
            while (DateTime.UtcNow < until)
            {
                host.ProcessPendingEvents();
                view.CycleSubtitle();
                labels.Add(view.SubtitleTrackLabel);
                if (labels.Contains("Subs English") && labels.Contains("Subs Türkçe"))
                {
                    break;
                }

                Thread.Sleep(20);
            }

            Assert.DoesNotContain(labels, item => item.Contains("tr:asr", StringComparison.Ordinal));
            Assert.Contains("Subs English", labels);
            Assert.Contains("Subs Türkçe", labels);
        }
        finally
        {
            File.Delete(english);
            File.Delete(turkish);
        }
    }

    [Fact]
    public void Sidecars_select_turkish_instead_of_staying_off()
    {
        var english = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-en.vtt");
        var turkish = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-tr.vtt");
        File.WriteAllText(english, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello\n");
        File.WriteAllText(turkish, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nMerhaba\n");
        try
        {
            var fake = new FakeMpvNative();
            using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
            using var view = new PlaybackViewModel(
                host,
                streamSubtitles: new StreamSubtitleSettings { Mode = StreamSubtitleMode.On, LastSub = "de" });
            Assert.True(view.AddStream(
                ExternalOpen.ToProtocol(
                    "https://cdn.example/somebody.m3u8",
                    "Somebody",
                    StreamKind.Vod,
                    subLang: "de",
                    captions:
                    [
                        new ExternalCaption("en", english, "English"),
                        new ExternalCaption("tr", turkish, "Turkish")
                    ]),
                play: true));
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until)
            {
                host.ProcessPendingEvents();
                if (view.SubtitleTrackLabel.Contains("Türkçe", StringComparison.Ordinal))
                {
                    break;
                }

                Thread.Sleep(20);
            }

            Assert.Equal("Subs Türkçe", view.SubtitleTrackLabel);
            Assert.DoesNotContain("de", view.SubtitleTrackLabel, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(english);
            File.Delete(turkish);
        }
    }

    [Fact]
    public void Stream_subtitle_off_clears_youtube_captions_and_keeps_them_off()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nOn screen\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(
            host,
            streamSubtitles: new StreamSubtitleSettings { Mode = StreamSubtitleMode.On });
        view.ResolveYouTube = _ => new YouTubePlayable(
            "n9izgGYxxx1",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Talk",
            StreamKind.Vod,
            captionUrl: vtt);
        view.AddStream("https://www.youtube.com/watch?v=n_9izgGY-_E", play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.NotNull(view.Subtitles.Applied);
        host.Seek(TimeSpan.FromSeconds(1));
        host.ProcessPendingEvents();
        Assert.Equal("On screen", view.OnScreenCaption);
        var commandsBeforeOff = fake.Commands.Count;

        view.SetStreamSubtitleMode(StreamSubtitleMode.Off);
        host.ProcessPendingEvents();
        view.ApplySubtitleTrack();
        host.ProcessPendingEvents();

        Assert.Equal(StreamSubtitleMode.Off, view.StreamSubtitles.Mode);
        Assert.Null(view.Subtitles.Applied);
        Assert.Null(view.OnScreenCaption);
        Assert.Equal("Subs Off", view.SubtitleTrackLabel);
        Assert.DoesNotContain(
            fake.Commands.Skip(commandsBeforeOff),
            command => command.Length >= 1 && command[0] == "sub-add");
        File.Delete(vtt);
    }

    [Fact]
    public void Youtube_cycle_off_clears_applied_stream_caption()
    {
        var vtt = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nOn screen\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "n9izgGYxxx2",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Talk",
            StreamKind.Vod,
            captionUrl: vtt);
        view.AddStream(
            "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=n_9izgGY-_E") + "&sub=en",
            play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.NotNull(view.Subtitles.Applied);
        for (var i = 0; i < 4 && view.SubtitleTrackLabel != "Subs Off"; i++)
        {
            view.CycleSubtitle();
            host.ProcessPendingEvents();
        }

        Assert.Equal("Subs Off", view.SubtitleTrackLabel);
        Assert.Null(view.Subtitles.Applied);
        Assert.Null(view.OnScreenCaption);
        view.ApplySubtitleTrack();
        host.ProcessPendingEvents();
        Assert.Null(view.Subtitles.Applied);
        Assert.Null(view.OnScreenCaption);
        File.Delete(vtt);
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
    public void Protocol_vod_caption_file_applies_to_direct_media()
    {
        var caption = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(caption, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello vod\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.AddStream(
            ExternalOpen.ToProtocol(
                "https://cdn.example/movie.m3u8",
                "Movie",
                StreamKind.Vod,
                subLang: "en",
                captionUrl: caption),
            play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied is null)
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        try
        {
            Assert.Equal(StreamKind.Vod, view.Streams.Items[0].StreamKind);
            Assert.False(view.IsLive);
            Assert.Equal("Hello vod", view.Subtitles.Applied?.Document.Cues[0].Text);
        }
        finally
        {
            File.Delete(caption);
        }
    }

    [Fact]
    public void Protocol_duration_without_kind_is_vod_not_live()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.AddStream(
            ExternalOpen.ToProtocol(
                "https://cdn.example/rapidrame/master.m3u8",
                "Film",
                StreamKind.Unknown,
                durationSeconds: 6120),
            play: true);
        Assert.Equal(StreamKind.Vod, view.Streams.Items[0].StreamKind);
        Assert.False(view.IsLive);
        Assert.DoesNotContain(fake.Lifecycle, item => item.Contains("live_start_index=-1", StringComparison.Ordinal));
    }

    [Fact]
    public void Protocol_vod_hls_does_not_use_live_demuxer()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.AddStream(
            ExternalOpen.ToProtocol(
                "https://rumble.com/hls-vod/abc/playlist.m3u8",
                "Rumble",
                StreamKind.Vod),
            play: true);
        Assert.Equal(StreamKind.Vod, view.Streams.Items[0].StreamKind);
        Assert.False(view.IsLive);
        Assert.DoesNotContain(fake.Lifecycle, item => item.Contains("live_start_index=-1", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("force-seekable=yes", StringComparison.Ordinal));
    }

    [Fact]
    public void Protocol_sound_attaches_sidecar_audio()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        const string video = "https://scontent.cdninstagram.com/o1/v/t16/v1?mime_type=video_mp4";
        const string sound = "https://scontent.cdninstagram.com/o1/v/t16/a1?mime_type=audio_mp4";
        view.AddStream(
            ExternalOpen.ToProtocol(
                video,
                "Reel",
                StreamKind.Vod,
                referer: "https://www.instagram.com/",
                soundtrack: sound),
            play: true);
        Assert.Equal(sound, view.Streams.Items[0].AudioUrl);
        Assert.Contains(fake.Lifecycle, item => item.Contains("audio-files=", StringComparison.Ordinal) &&
                                               item.Contains("audio_mp4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Instagram_cdn_url_uses_the_long_lavf_probe()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.AddStream(
            ExternalOpen.ToProtocol(
                "https://scontent.cdninstagram.com/o1/v/t16/f2/m86/foo",
                "Reel",
                StreamKind.Vod,
                referer: "https://www.instagram.com/"),
            play: true);
        Assert.Contains(fake.Lifecycle, item => item.Contains("demuxer-lavf-analyzeduration=10", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("instagram.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tiktok_cdn_url_opens_as_direct_media()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        const string cdn = "https://v16-webapp.tiktokcdn.com/video/tos/foo?mime_type=video_mp4";
        view.AddStream(
            ExternalOpen.ToProtocol(cdn, "TikTok", StreamKind.Vod, referer: "https://www.tiktok.com/"),
            play: true);
        Assert.Contains(fake.Commands, command =>
            command.Length >= 2 &&
            command[0] == "loadfile" &&
            command[1].Contains("tiktokcdn", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fake.Lifecycle, item => item.Contains("live_start_index=-1", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("demuxer-lavf-analyzeduration=10", StringComparison.Ordinal));
    }

    [Fact]
    public void Protocol_duration_is_not_applied_before_playback()
    {
        var fake = new FakeMpvNative { AutoLoad = false };
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.AddStream(
            ExternalOpen.ToProtocol(
                "https://scontent.cdninstagram.com/o1/v/t16/f2/m86/foo",
                "Reel",
                StreamKind.Vod,
                referer: "https://www.instagram.com/",
                durationSeconds: 60),
            play: true);
        host.ProcessPendingEvents();
        Assert.Null(host.Duration);
        Assert.Equal(TimeSpan.Zero, host.Position);
    }

    [Fact]
    public void Protocol_duration_is_used_when_mpv_has_none()
    {
        var fake = new FakeMpvNative();
        fake.AutoDurationSeconds = 0;
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.AddStream(
            ExternalOpen.ToProtocol(
                "https://v16-webapp.tiktokcdn.com/video/tos/foo?mime_type=video_mp4",
                "TikTok",
                StreamKind.Vod,
                referer: "https://www.tiktok.com/",
                durationSeconds: 5),
            play: true);
        host.ProcessPendingEvents();
        Assert.Equal(TimeSpan.FromSeconds(5), host.Duration);
    }

    [Fact]
    public void Protocol_live_keeps_live_kind_and_skips_search()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(
            host,
            streamSubtitles: new StreamSubtitleSettings { Mode = StreamSubtitleMode.On, LastSub = "tr:asr" });
        view.AddStream(
            ExternalOpen.ToProtocol("https://cdn.example/live.m3u8", "Live", StreamKind.Live),
            play: true);
        Assert.Equal(StreamKind.Live, view.Streams.Items[0].StreamKind);
        Assert.True(view.IsLive);
        Assert.True(view.Streams.Items[0].SkipCaptions || view.PreferredSubLang is null);
        Assert.Null(view.Subtitles.Applied);
        Assert.Contains(fake.Lifecycle, item => item.Contains("slang=no", StringComparison.Ordinal));
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
    public void Protocol_caption_list_is_stored_and_tracks_cycle_in_the_player()
    {
        var english = "https://cdn.example/en.vtt";
        var turkish = "https://cdn.example/tr.vtt";
        using var session = Create(open: false);
        session.Fake.SeedTrack(0, "audio", 1, "ger", "German", true);
        session.Fake.SeedTrack(1, "audio", 2, "tur", "Turkish", false);
        session.Fake.SeedTrack(2, "sub", 1, "en", "English", false);
        session.Fake.SeedTrack(3, "sub", 2, "tr", "Turkish", false);
        var protocol = ExternalOpen.ToProtocol(
            "https://cdn.example/master.m3u8",
            "Episode",
            StreamKind.Vod,
            captions:
            [
                new ExternalCaption("en", english, "English"),
                new ExternalCaption("tr", turkish, "Turkish")
            ]);
        Assert.True(session.View.AddStream(protocol, play: false, "Episode"));
        var item = session.View.Streams.Items[0];
        Assert.Equal(2, item.CaptionTracks.Count);
        Assert.Equal(english, item.CaptionTracks[0].Url);
        Assert.Equal(turkish, item.CaptionTracks[1].Url);
        Assert.Null(item.AudioLang);
        Assert.Null(item.SubLang);

        session.View.Open(TestMedia.CreateTempFile("movie.mp4"));
        session.Host.ProcessPendingEvents();
        Assert.Equal("Audio German", session.View.AudioTrackLabel);
        Assert.Equal("Subs Off", session.View.SubtitleTrackLabel);

        session.View.SelectPlayingAudio(1);
        Assert.Equal("Audio Turkish", session.View.AudioTrackLabel);
        Assert.Contains(session.View.PlayingAudioChoices(), item => item.Selected && item.Label.Contains("Turkish", StringComparison.OrdinalIgnoreCase));
        session.View.SelectPlayingSubtitle(0);
        Assert.Equal("Subs English", session.View.SubtitleTrackLabel);
        session.View.SelectPlayingSubtitle(-1);
        Assert.Equal("Subs Off", session.View.SubtitleTrackLabel);
        session.View.CycleAudio();
        Assert.Equal("Audio German", session.View.AudioTrackLabel);

        session.View.CycleSubtitle();
        Assert.Equal("Subs English", session.View.SubtitleTrackLabel);
        session.View.CycleSubtitle();
        Assert.Equal("Subs Türkçe", session.View.SubtitleTrackLabel);
        session.View.CycleSubtitle();
        Assert.Equal("Subs Off", session.View.SubtitleTrackLabel);
    }

    [Fact]
    public void Forced_hls_turkish_is_not_a_cycle_choice()
    {
        using var session = Create(open: false);
        session.Fake.SeedTrack(0, "sub", 1, "tur", "Türkçe", false);
        session.Fake.SeedTrack(1, "sub", 2, "eng", "İngilizce", false);
        session.Fake.SeedTrack(2, "sub", 3, "tur", "Türkçe (Zorunlu)", false);
        session.View.Open(TestMedia.CreateTempFile("movie.mp4"));
        session.Host.ProcessPendingEvents();
        var labels = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            session.View.CycleSubtitle();
            labels.Add(session.View.SubtitleTrackLabel);
        }

        Assert.Equal(["Subs Türkçe", "Subs English", "Subs Off"], labels);
    }

    [Fact]
    public void Unnamed_hls_stub_does_not_appear_as_subs_subtitle()
    {
        var english = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-en.vtt");
        var turkish = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-tr.vtt");
        File.WriteAllText(english, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello\n");
        File.WriteAllText(turkish, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nMerhaba\n");
        try
        {
            using var session = Create(open: false);
            session.Fake.SeedTrack(0, "sub", 1, "en", "English", false);
            session.Fake.SeedTrack(1, "sub", 2, "tr", "Turkish", false);
            session.Fake.SeedTrack(2, "sub", 3, "", "Subtitle", false);
            session.Fake.SeedTrack(3, "sub", 4, "und", "Original", false);
            Assert.True(session.View.AddStream(
                ExternalOpen.ToProtocol(
                    "https://cdn.example/master.m3u8",
                    "Episode",
                    StreamKind.Vod,
                    captions:
                    [
                        new ExternalCaption("en", english, "English"),
                        new ExternalCaption("tr", turkish, "Turkish")
                    ]),
                play: true));
            var labels = new List<string>();
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until)
            {
                session.Host.ProcessPendingEvents();
                session.View.CycleSubtitle();
                labels.Add(session.View.SubtitleTrackLabel);
                if (labels.Contains("Subs Off") && labels.Contains("Subs English") && labels.Contains("Subs Türkçe"))
                {
                    break;
                }

                Thread.Sleep(20);
            }

            Assert.Contains("Subs English", labels);
            Assert.Contains("Subs Türkçe", labels);
            Assert.DoesNotContain("Subs subtitle", labels);
            Assert.DoesNotContain("Subs Subtitle", labels);
            Assert.DoesNotContain("Subs original", labels);
            Assert.DoesNotContain("Subs Original", labels);
        }
        finally
        {
            File.Delete(english);
            File.Delete(turkish);
        }
    }

    [Fact]
    public void Tr_sidecar_file_is_used_instead_of_empty_hls_turkish()
    {
        var english = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-en.vtt");
        var turkish = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-tr.vtt");
        File.WriteAllText(english, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello\n");
        File.WriteAllText(turkish, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nMerhaba\n");
        try
        {
            using var session = Create(open: false);
            session.Fake.SeedTrack(0, "sub", 1, "en", "English", false);
            session.Fake.SeedTrack(1, "sub", 2, "tr", "Turkish", false);
            session.Fake.SeedTrack(2, "sub", 3, "", "subtitle", false);
            Assert.True(session.View.AddStream(
                ExternalOpen.ToProtocol(
                    "https://cdn.example/master.m3u8",
                    "Episode",
                    StreamKind.Vod,
                    captions:
                    [
                        new ExternalCaption("en", english, "English"),
                        new ExternalCaption("tr", turkish, "tr")
                    ]),
                play: true));
            var labels = new List<string>();
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until)
            {
                session.Host.ProcessPendingEvents();
                session.View.CycleSubtitle();
                labels.Add(session.View.SubtitleTrackLabel);
                if (labels.Contains("Subs Off") && labels.Contains("Subs English") && labels.Contains("Subs Türkçe"))
                {
                    break;
                }

                Thread.Sleep(20);
            }

            Assert.Contains("Subs English", labels);
            Assert.Contains("Subs Türkçe", labels);
            Assert.DoesNotContain("Subs tr", labels);
            Assert.DoesNotContain("Subs subtitle", labels);
        }
        finally
        {
            File.Delete(english);
            File.Delete(turkish);
        }
    }

    [Fact]
    public void Weak_tr_sidecar_does_not_hide_english_and_turkish_tracks()
    {
        var leftover = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-tr.vtt");
        File.WriteAllText(leftover, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nMerhaba\n");
        try
        {
            using var session = Create(open: false);
            session.Fake.SeedTrack(0, "sub", 1, "en", "English", false);
            session.Fake.SeedTrack(1, "sub", 2, "tr", "Turkish", false);
            Assert.True(session.View.AddStream(
                ExternalOpen.ToProtocol(
                    "https://cdn.example/master.m3u8",
                    "Episode",
                    StreamKind.Vod,
                    captions: [new ExternalCaption("tr", leftover, "tr")]),
                play: true));
            var until = DateTime.UtcNow.AddSeconds(3);
            var labels = new List<string>();
            while (DateTime.UtcNow < until)
            {
                session.Host.ProcessPendingEvents();
                session.View.CycleSubtitle();
                labels.Add(session.View.SubtitleTrackLabel);
                if (labels.Contains("Subs Off") && labels.Contains("Subs English") && labels.Contains("Subs Türkçe"))
                {
                    break;
                }

                Thread.Sleep(20);
            }

            Assert.Contains("Subs English", labels);
            Assert.Contains("Subs Türkçe", labels);
            Assert.DoesNotContain("Subs tr", labels);
        }
        finally
        {
            File.Delete(leftover);
        }
    }

    [Fact]
    public void Named_english_and_turkish_sidecars_are_applied_instead_of_empty_hls_stubs()
    {
        var english = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-en.vtt");
        var turkish = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-engtr.vtt");
        File.WriteAllText(english, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello\n");
        File.WriteAllText(turkish, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nMerhaba\n");
        try
        {
            using var session = Create(open: false);
            session.Fake.SeedTrack(0, "sub", 1, "en", "English", false);
            session.Fake.SeedTrack(1, "sub", 2, "tr", "Turkish", false);
            Assert.True(session.View.AddStream(
                ExternalOpen.ToProtocol(
                    "https://cdn.example/master.m3u8",
                    "Episode",
                    StreamKind.Vod,
                    captions:
                    [
                        new ExternalCaption("en", english, "English"),
                        new ExternalCaption("tr", turkish, "Turkish")
                    ]),
                play: true));
            string? label = null;
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until)
            {
                session.Host.ProcessPendingEvents();
                session.View.CycleSubtitle();
                label = session.View.SubtitleTrackLabel;
                if (label == "Subs English")
                {
                    break;
                }

                Thread.Sleep(20);
            }

            Assert.Equal("Subs English", label);
            session.View.CycleSubtitle();
            Assert.Equal("Subs Türkçe", session.View.SubtitleTrackLabel);
            session.View.CycleSubtitle();
            Assert.Equal("Subs Off", session.View.SubtitleTrackLabel);
        }
        finally
        {
            File.Delete(english);
            File.Delete(turkish);
        }
    }

    [Fact]
    public void Sidecar_captions_cycle_even_when_mpv_has_no_sub_tracks()
    {
        var english = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-en.vtt");
        var turkish = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-tr.vtt");
        File.WriteAllText(english, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello\n");
        File.WriteAllText(turkish, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nMerhaba\n");
        try
        {
            using var session = Create(open: false);
            Assert.True(session.View.AddStream(
                ExternalOpen.ToProtocol(
                    "https://cdn.example/master.m3u8",
                    "Episode",
                    StreamKind.Vod,
                    captions:
                    [
                        new ExternalCaption("en", english, "English"),
                        new ExternalCaption("tr", turkish, "Turkish")
                    ]),
                play: true));
            string? label = null;
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until)
            {
                session.Host.ProcessPendingEvents();
                session.View.CycleSubtitle();
                label = session.View.SubtitleTrackLabel;
                if (label == "Subs English")
                {
                    break;
                }

                Thread.Sleep(20);
            }

            Assert.Equal("Subs English", label);
            session.View.CycleSubtitle();
            Assert.Equal("Subs Türkçe", session.View.SubtitleTrackLabel);
            session.View.TogglePlayPause();
            session.Host.ProcessPendingEvents();
            Assert.Equal("Subs Türkçe", session.View.SubtitleTrackLabel);
            session.View.TogglePlayPause();
            session.Host.ProcessPendingEvents();
            Assert.Equal("Subs Türkçe", session.View.SubtitleTrackLabel);
            session.View.CycleSubtitle();
            Assert.Equal("Subs Off", session.View.SubtitleTrackLabel);
            session.View.TogglePlayPause();
            session.Host.ProcessPendingEvents();
            Assert.Equal("Subs Off", session.View.SubtitleTrackLabel);
        }
        finally
        {
            File.Delete(english);
            File.Delete(turkish);
        }
    }

    [Fact]
    public void Youtube_protocol_without_cap_list_still_applies_the_selected_caption()
    {
        var caption = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(caption, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello\n");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "dQw4w9wgBcQ",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Song",
            StreamKind.Vod,
            captionUrl: caption);
        view.AddStream(
            ExternalOpen.ToProtocol(
                "https://www.youtube.com/watch?v=dQw4w9wgBcQ",
                "Song",
                StreamKind.Vod,
                "tr",
                "en",
                captionUrl: caption),
            play: true);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && view.Subtitles.Applied?.Document.Cues[0].Text != "Hello")
        {
            Thread.Sleep(20);
            host.ProcessPendingEvents();
        }

        Assert.Equal("tr", view.PreferredAudioLang);
        Assert.Equal("en", view.PreferredSubLang);
        Assert.Equal("Hello", view.Subtitles.Applied?.Document.Cues[0].Text);
        File.Delete(caption);
    }

    [Fact]
    public void Local_open_renders_the_sidecar_the_button_names()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grok-local-subs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var video = Path.Combine(dir, "clip.mp4");
        File.WriteAllBytes(video, [1, 2, 3, 4]);
        File.WriteAllText(
            Path.Combine(dir, "clip.af.srt"),
            "1\n00:00:00,000 --> 00:00:01,000\nOndersteuning voor die aanvang\n");
        File.WriteAllText(
            Path.Combine(dir, "clip.tr-asr.srt"),
            "1\n00:00:00,000 --> 00:00:01,000\nNumaraya baslamadan once\n");
        try
        {
            var fake = new FakeMpvNative();
            using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
            using var view = new PlaybackViewModel(
                host,
                streamSubtitles: new StreamSubtitleSettings { Mode = StreamSubtitleMode.On, LastSub = "af" });
            view.Open(video);
            host.ProcessPendingEvents();

            Assert.Contains("Afrikaans", view.SubtitleTrackLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Ondersteuning", view.Subtitles.Applied?.Document.Cues[0].Text ?? "", StringComparison.Ordinal);
            var added = fake.Commands.Last(command => command.Length >= 2 && command[0] == "sub-add");
            Assert.Contains(".af.", added[1], StringComparison.OrdinalIgnoreCase);

            view.CycleSubtitle();
            Assert.Contains("Türkçe", view.SubtitleTrackLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Numaraya", view.Subtitles.Applied?.Document.Cues[0].Text ?? "", StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Pause_keeps_the_cycled_local_sidecar()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grok-local-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var video = Path.Combine(dir, "clip.mp4");
        File.WriteAllBytes(video, [1, 2, 3, 4]);
        File.WriteAllText(
            Path.Combine(dir, "clip.af.srt"),
            "1\n00:00:00,000 --> 00:00:01,000\nOndersteuning voor die aanvang\n");
        File.WriteAllText(
            Path.Combine(dir, "clip.tr-asr.srt"),
            "1\n00:00:00,000 --> 00:00:01,000\nNumaraya baslamadan once\n");
        try
        {
            var fake = new FakeMpvNative();
            using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
            using var view = new PlaybackViewModel(
                host,
                streamSubtitles: new StreamSubtitleSettings { Mode = StreamSubtitleMode.On, LastSub = "tr:asr" });
            view.Open(video);
            host.ProcessPendingEvents();
            Assert.Contains("Türkçe", view.SubtitleTrackLabel, StringComparison.OrdinalIgnoreCase);

            view.CycleSubtitle();
            host.ProcessPendingEvents();
            Assert.Contains("Off", view.SubtitleTrackLabel, StringComparison.OrdinalIgnoreCase);

            view.CycleSubtitle();
            host.ProcessPendingEvents();
            Assert.Contains("Afrikaans", view.SubtitleTrackLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Ondersteuning", view.Subtitles.Applied?.Document.Cues[0].Text ?? "", StringComparison.Ordinal);
            var afterPick = fake.Lifecycle.Count;

            view.TogglePlayPause();
            host.ProcessPendingEvents();
            view.TogglePlayPause();
            host.ProcessPendingEvents();

            Assert.Contains("Afrikaans", view.SubtitleTrackLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Ondersteuning", view.Subtitles.Applied?.Document.Cues[0].Text ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(fake.Lifecycle.Skip(afterPick), item => item == "property:sid=no");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Youtube_storyboard_does_not_attach_to_hdfilm()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.ResolveYouTube = _ => new YouTubePlayable(
            "dQw4w9wgBcQ",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            "Song",
            StreamKind.Vod,
            storyboardSpec: "https://i.ytimg.com/sb/dQw4w9wgBcQ/storyboard3_L$L/$N.jpg|48#27#100#10#10#0#default#rs$0");
        view.AddStream("https://www.youtube.com/watch?v=dQw4w9wgBcQ", play: true);
        host.ProcessPendingEvents();
        Assert.False(string.IsNullOrWhiteSpace(view.StoryboardSpec));

        view.AddStream(
            "https://www.hdfilmcehennemi.now/bolum/breaking-bad-1-sezon-1-bolum-1-izle-16/",
            play: true);
        host.ProcessPendingEvents();
        Assert.True(string.IsNullOrWhiteSpace(view.StoryboardSpec));
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
