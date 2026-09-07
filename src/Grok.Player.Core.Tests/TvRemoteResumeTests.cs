using Grok.Player.Core.Media;
using Grok.Player.Core.Player;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Tests.Fakes;
using Grok.Player.Core.Tests.Support;

namespace Grok.Player.Core.Tests;

public sealed class TvRemoteResumeTests
{
    [Fact]
    public void Local_open_offers_continue_watching_on_pc()
    {
        using var box = Harness.WithResume(30);
        ResumeRecord? offered = null;
        box.View.ResumeOffered += record => offered = record;
        box.View.Open(box.Path);
        box.Host.ProcessPendingEvents();
        Assert.NotNull(offered);
        Assert.Equal(30, offered!.Seconds, 0.1);
        Assert.True(box.View.Player.State == PlayerState.Paused);
        Assert.Null(box.View.TvResumeOffer);
    }

    [Fact]
    public void Send_start_over_skips_every_prompt_and_plays_from_zero()
    {
        using var box = Harness.WithResume(40);
        var offered = 0;
        box.View.ResumeOffered += _ => offered++;
        box.View.EnqueueOrPlay(box.Path, play: true, title: "Movie", startOver: true);
        box.Host.ProcessPendingEvents();
        Assert.Equal(0, offered);
        Assert.Null(box.View.TvResumeOffer);
        Assert.Equal(PlayerState.Playing, box.View.Player.State);
        Assert.True(box.View.Player.Position.TotalSeconds < 1);
    }

    [Fact]
    public void Send_continue_seeks_without_any_prompt()
    {
        using var box = Harness.WithResume(45);
        var offered = 0;
        box.View.ResumeOffered += _ => offered++;
        box.View.EnqueueOrPlay(box.Path, play: true, title: "Movie", startOver: false);
        box.Host.ProcessPendingEvents();
        Assert.Equal(0, offered);
        Assert.Null(box.View.TvResumeOffer);
        Assert.Equal(PlayerState.Playing, box.View.Player.State);
        Assert.InRange(box.View.Player.Position.TotalSeconds, 44, 46);
    }

    [Fact]
    public void Tv_playlist_switch_asks_tv_not_pc()
    {
        using var box = Harness.WithResume(25);
        var offered = 0;
        box.View.ResumeOffered += _ => offered++;
        box.View.EnqueueOrPlay(box.Path, play: false, title: "Movie");
        box.View.PlayLocalIndex(0);
        box.Host.ProcessPendingEvents();
        Assert.Equal(0, offered);
        Assert.NotNull(box.View.TvResumeOffer);
        Assert.Equal(25, box.View.TvResumeOffer!.Seconds, 0.1);
        Assert.Equal(PlayerState.Paused, box.View.Player.State);
    }

    [Fact]
    public void Tv_resume_continue_plays_from_saved_position()
    {
        using var box = Harness.WithResume(33);
        box.View.EnqueueOrPlay(box.Path, play: false, title: "Movie");
        box.View.PlayLocalIndex(0);
        box.Host.ProcessPendingEvents();
        box.View.ContinueResume();
        box.Host.ProcessPendingEvents();
        Assert.Null(box.View.TvResumeOffer);
        Assert.Equal(PlayerState.Playing, box.View.Player.State);
        Assert.InRange(box.View.Player.Position.TotalSeconds, 32, 34);
    }

    [Fact]
    public void Tv_resume_start_over_plays_from_zero()
    {
        using var box = Harness.WithResume(33);
        box.View.EnqueueOrPlay(box.Path, play: false, title: "Movie");
        box.View.PlayLocalIndex(0);
        box.Host.ProcessPendingEvents();
        box.View.DeclineResume();
        box.Host.ProcessPendingEvents();
        Assert.Null(box.View.TvResumeOffer);
        Assert.Equal(PlayerState.Playing, box.View.Player.State);
        Assert.True(box.View.Player.Position.TotalSeconds < 1);
    }

    [Fact]
    public void Pc_playlist_click_still_uses_pc_popup_after_a_tv_send()
    {
        using var box = Harness.WithTwoFiles(30);
        var offered = 0;
        box.View.ResumeOffered += _ => offered++;
        box.View.EnqueueOrPlay(box.Path, play: true, title: "First", startOver: true);
        box.Host.ProcessPendingEvents();
        Assert.Equal(0, offered);
        box.View.PlayIndex(1);
        box.Host.ProcessPendingEvents();
        Assert.Equal(1, offered);
        Assert.Null(box.View.TvResumeOffer);
        Assert.Equal(PlayerState.Paused, box.View.Player.State);
    }

    [Fact]
    public void Local_open_after_tv_session_uses_pc_popup()
    {
        using var box = Harness.WithTwoFiles(28);
        var offered = 0;
        box.View.ResumeOffered += _ => offered++;
        box.View.PlayLocalIndex(0);
        box.Host.ProcessPendingEvents();
        Assert.Equal(0, offered);
        box.View.Open(box.SecondPath);
        box.Host.ProcessPendingEvents();
        Assert.Equal(1, offered);
        Assert.Null(box.View.TvResumeOffer);
    }

    [Fact]
    public void Restore_without_playing_does_not_steal_next_local_resume()
    {
        using var box = Harness.WithResume(30);
        var offered = 0;
        box.View.ResumeOffered += _ => offered++;
        box.View.EnqueueOrPlay(box.Path, play: false, title: "Movie");
        box.View.Open(box.Path);
        box.Host.ProcessPendingEvents();
        Assert.Equal(1, offered);
    }

    private sealed class Harness : IDisposable
    {
        private Harness(string path, string? second, string resumeFile, PlayerHost host, PlaybackViewModel view)
        {
            Path = path;
            SecondPath = second ?? path;
            _resumeFile = resumeFile;
            Host = host;
            View = view;
        }

        public string Path { get; }
        public string SecondPath { get; }
        public PlayerHost Host { get; }
        public PlaybackViewModel View { get; }
        private readonly string _resumeFile;

        public static Harness WithResume(double seconds)
        {
            var path = TestMedia.CreateTempFile($"tv-resume-{Guid.NewGuid():N}.mp4");
            return Build(path, null, seconds);
        }

        public static Harness WithTwoFiles(double seconds)
        {
            var first = TestMedia.CreateTempFile($"tv-resume-a-{Guid.NewGuid():N}.mp4");
            var second = TestMedia.CreateTempFile($"tv-resume-b-{Guid.NewGuid():N}.mp4");
            return Build(first, second, seconds);
        }

        private static Harness Build(string first, string? second, double seconds)
        {
            var resumeFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tv-resume-{Guid.NewGuid():N}.json");
            var store = new ResumeStore(resumeFile);
            store.Save(ContentFingerprint.ForLocalFile(first), "First", seconds, 120);
            if (second is not null)
            {
                store.Save(ContentFingerprint.ForLocalFile(second), "Second", seconds, 120);
            }

            var fake = new FakeMpvNative();
            var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
            var view = new PlaybackViewModel(host, resume: store);
            view.EnqueueOrPlay(first, play: false, "First");
            if (second is not null)
            {
                view.EnqueueOrPlay(second, play: false, "Second");
            }

            return new Harness(first, second, resumeFile, host, view);
        }

        public void Dispose()
        {
            View.Dispose();
            Host.Dispose();
            try { File.Delete(Path); } catch { }
            if (SecondPath != Path)
            {
                try { File.Delete(SecondPath); } catch { }
            }

            try { File.Delete(_resumeFile); } catch { }
        }
    }
}
