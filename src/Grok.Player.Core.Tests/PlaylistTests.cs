using Grok.Player.Core.Player;
using Grok.Player.Core.Playlist;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Tests.Fakes;
using Grok.Player.Core.Tests.Support;

namespace Grok.Player.Core.Tests;

public sealed class PlaylistTests
{
    [Fact]
    public void Duplicate_paths_are_ignored()
    {
        var list = new MediaPlaylist();
        var path = TestMedia.CreateTempFile("once.mp4");
        Assert.True(list.TryAdd(path));
        Assert.False(list.TryAdd(path));
        Assert.False(list.TryAdd(path.ToUpperInvariant()));
        Assert.Equal(1, list.Count);
    }

    [Fact]
    public void Unsupported_extensions_are_rejected()
    {
        var list = new MediaPlaylist();
        Assert.False(list.TryAdd(@"C:\notes.txt"));
        Assert.True(MediaFiles.IsSupported(@"C:\a.mp3"));
        Assert.True(MediaFiles.IsAudio(@"D:\track.mp3"));
    }

    [Fact]
    public void Next_respects_loop_modes()
    {
        var list = new MediaPlaylist();
        var a = TestMedia.CreateTempFile("a.mp4");
        var b = TestMedia.CreateTempFile("b.mp4");
        list.TryAdd(a);
        list.TryAdd(b);
        list.SetCurrent(a);
        Assert.Equal(b, list.Next(LoopMode.Off));
        Assert.Null(list.Next(LoopMode.Off));
        list.SetCurrent(b);
        Assert.Equal(a, list.Next(LoopMode.Playlist));
        list.SetCurrent(b);
        Assert.Equal(b, list.Next(LoopMode.One));
    }

    [Fact]
    public void Drop_plays_when_idle_and_enqueues_when_playing()
    {
        Assert.Equal(DropAction.PlayFirstEnqueueRest, DropPolicy.ForState(PlayerState.Idle));
        Assert.Equal(DropAction.EnqueueAll, DropPolicy.ForState(PlayerState.Playing));
        Assert.Equal(DropAction.EnqueueAll, DropPolicy.ForState(PlayerState.Paused));
    }

    [Fact]
    public void ViewModel_drop_does_not_replace_current_when_playing()
    {
        var first = TestMedia.CreateTempFile("one.mp4");
        var second = TestMedia.CreateTempFile("two.mp4");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.AcceptPaths([first]);
        host.ProcessPendingEvents();
        var current = host.MediaPath;
        view.AcceptPaths([second]);
        host.ProcessPendingEvents();
        Assert.Equal(2, view.Playlist.Count);
        Assert.Equal(current, host.MediaPath);
    }

    [Fact]
    public void Youtube_watch_urls_are_the_same_playlist_item()
    {
        var list = new MediaPlaylist();
        Assert.True(list.TryAdd("https://www.youtube.com/watch?v=dQw4w9wgBcQ", "Song"));
        Assert.False(list.TryAdd("https://youtu.be/dQw4w9wgBcQ", "Song"));
        Assert.Equal(1, list.Count);
        Assert.Equal("Song", list.Items[0].Title);
        Assert.Equal("youtube|dQw4w9wgBcQ", MediaPlaylist.Identity("https://www.youtube.com/watch?v=dQw4w9wgBcQ"));
    }

    [Fact]
    public void TitleLine_includes_format_and_playlist_index()
    {
        var first = TestMedia.CreateTempFile("C1456 (1).mp4");
        var second = TestMedia.CreateTempFile("other.mp4");
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.AcceptPaths([first, second]);
        host.ProcessPendingEvents();
        Assert.Equal("mp4", view.TitleFormat);
        Assert.Equal("[1/2]", view.TitleIndex);
        Assert.Equal("C1456 (1).mp4", view.TitleName);
    }

    [Fact]
    public void Loop_and_time_mode_cycle()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        using var view = new PlaybackViewModel(host);
        view.CycleLoop();
        Assert.Equal(LoopMode.Playlist, view.Loop);
        view.CycleLoop();
        Assert.Equal(LoopMode.One, view.Loop);
        view.CycleLoop();
        Assert.Equal(LoopMode.Off, view.Loop);
        Assert.False(view.LoopIsActive);
        Assert.Equal("\uE8EE", view.LoopGlyph);
        view.CycleLoop();
        Assert.True(view.LoopIsActive);
        view.CycleLoop();
        Assert.Equal("\uE8ED", view.LoopGlyph);
        view.ToggleTimeMode();
        Assert.True(view.ShowRemaining);
    }

    [Fact]
    public void Clock_is_always_hhmmss()
    {
        Assert.Equal("00:38", TimeDisplay.FormatSeek(TimeSpan.FromSeconds(38.5)));
        Assert.Equal("1:01:05", TimeDisplay.FormatSeek(TimeSpan.FromSeconds(3665)));
        Assert.Equal("00:00:05", TimeDisplay.FormatClock(TimeSpan.FromSeconds(5)));
        Assert.Equal("00:01:05", TimeDisplay.FormatClock(TimeSpan.FromSeconds(65), remaining: true));
        Assert.Equal("00:01:05 / 00:02:00", TimeDisplay.FormatClockPair(TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(120), false));
        Assert.Equal("00:00:55 / 00:02:00", TimeDisplay.FormatClockPair(TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(120), true));
    }
}
