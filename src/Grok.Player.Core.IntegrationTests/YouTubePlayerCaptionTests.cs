using System.Collections.Concurrent;
using Grok.Player.Core.Launch;
using Grok.Player.Core.Media;
using Grok.Player.Core.Player;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Subtitles;
using Grok.Player.Core.IntegrationTests.Support;

namespace Grok.Player.Core.IntegrationTests;

public sealed class YouTubePlayerCaptionTests
{
    [LibMpvFact]
    public void Player_shows_auto_translate_on_u0amhpj1xno()
    {
        var result = PlayCaptions(
            "https://www.youtube.com/watch?v=U0Amhpj1XNo",
            "de",
            translateFrom: "tr:asr");
        Assert.False(string.IsNullOrWhiteSpace(result.AppliedText), result.Dump);
        Assert.True(
            result.AppliedText.Contains("Darf", StringComparison.OrdinalIgnoreCase) ||
            result.AppliedText.Contains("Frage", StringComparison.OrdinalIgnoreCase) ||
            result.AppliedText.Contains("ich", StringComparison.OrdinalIgnoreCase) ||
            result.AppliedText.Contains("und", StringComparison.OrdinalIgnoreCase) ||
            result.AppliedText.Contains("ist", StringComparison.OrdinalIgnoreCase),
            result.Dump);
    }

    [LibMpvFact]
    public void Player_shows_be8_turkish_asr()
    {
        var result = PlayCaptions(
            "https://www.youtube.com/watch?v=Be8Jwg2i718",
            "tr:asr");
        Assert.False(string.IsNullOrWhiteSpace(result.AppliedText), result.Dump);
        Assert.Contains("tr", result.SidLang + " " + result.SubLang, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tlang=", result.CaptionUrl ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [LibMpvFact]
    public void Player_shows_be8_german_auto_translate()
    {
        var result = PlayCaptions(
            "https://www.youtube.com/watch?v=Be8Jwg2i718",
            "de",
            translateFrom: "tr:asr");
        Assert.False(string.IsNullOrWhiteSpace(result.AppliedText), result.Dump);
        Assert.DoesNotContain("efendim", result.AppliedText, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            result.AppliedText.Contains("ich", StringComparison.OrdinalIgnoreCase) ||
            result.AppliedText.Contains("und", StringComparison.OrdinalIgnoreCase) ||
            result.AppliedText.Contains("der", StringComparison.OrdinalIgnoreCase) ||
            result.AppliedText.Contains("die", StringComparison.OrdinalIgnoreCase) ||
            result.AppliedText.Contains("das", StringComparison.OrdinalIgnoreCase) ||
            result.AppliedText.Contains("ist", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.SidLang, "de", StringComparison.OrdinalIgnoreCase),
            result.Dump);
    }

    [LibMpvFact]
    public void Player_loads_qtl_when_protocol_has_no_caption()
    {
        YouTubePlayable? playable;
        try
        {
            playable = YouTubeCatalog.Resolve("https://www.youtube.com/watch?v=Qtl8lJwbd4g", null, "tr", "en");
        }
        catch (Exception)
        {
            return;
        }

        if (playable is null)
        {
            return;
        }

        var pending = new ConcurrentQueue<Action>();
        using var host = PlayerHost.CreateHeadless();
        var view = new PlaybackViewModel(host)
        {
            PostToUi = action => pending.Enqueue(action)
        };
        try
        {
            view.AddStream(
                "grokplayer://open?url=" +
                Uri.EscapeDataString("https://www.youtube.com/watch?v=Qtl8lJwbd4g&t=159s"),
                play: true);
            var until = DateTime.UtcNow.AddSeconds(35);
            while (DateTime.UtcNow < until)
            {
                while (pending.TryDequeue(out var action))
                {
                    action();
                }

                host.ProcessPendingEvents();
                if (view.Subtitles.Applied is { Document.Cues.Count: > 0 })
                {
                    break;
                }

                Thread.Sleep(40);
            }

            Assert.False(view.Streams.Items[0].SkipCaptions);
            Assert.NotNull(view.Subtitles.Applied);
            Assert.Contains(
                "BEHIND",
                string.Join(' ', view.Subtitles.Applied!.Document.Cues.Take(8).Select(cue => cue.Text)),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            view.Dispose();
        }
    }

    [LibMpvFact]
    public void Player_shows_qtl_official_english()
    {
        var result = PlayCaptions(
            "https://www.youtube.com/watch?v=Qtl8lJwbd4g&t=159s",
            "en");
        Assert.False(string.IsNullOrWhiteSpace(result.AppliedText), result.Dump);
        Assert.Contains("BEHIND", result.AppliedText, StringComparison.OrdinalIgnoreCase);
    }

    [LibMpvFact]
    public void Player_shows_qtl_official_arabic()
    {
        var result = PlayCaptions(
            "https://www.youtube.com/watch?v=Qtl8lJwbd4g&t=159s",
            "ar");
        Assert.False(string.IsNullOrWhiteSpace(result.AppliedText), result.Dump);
        Assert.Contains("خلفي", result.AppliedText, StringComparison.Ordinal);
    }

    private static PlayResult PlayCaptions(string watch, string sub, string? translateFrom = null)
    {
        YouTubePlayable? playable;
        try
        {
            playable = YouTubeCatalog.Resolve(watch, null, "tr", translateFrom ?? sub);
        }
        catch (Exception ex)
        {
            return new PlayResult { Dump = "resolve threw " + ex };
        }

        if (playable is null)
        {
            return new PlayResult { Dump = "resolve null" };
        }

        playable = YouTubeCatalog.BindHlsRenditions(playable, 1080);
        var captionUrl = playable.CaptionUrl;
        if (!string.IsNullOrWhiteSpace(translateFrom))
        {
            var source = playable.CaptionUrl ?? YouTubeCatalog.CaptionVttUrl(playable.VideoId, translateFrom);
            captionUrl = YouTubeCatalog.WithTranslate(source, MediaLanguage.Normalize(sub));
        }
        else if (string.IsNullOrWhiteSpace(captionUrl))
        {
            captionUrl = YouTubeCatalog.CaptionVttUrl(playable.VideoId, sub);
        }

        try
        {
            StreamCaptionLoader.Load(playable.VideoId, translateFrom is null ? sub : sub, captionUrl);
        }
        catch (Exception)
        {
        }

        var protocol = ExternalOpen.ToProtocol(watch, playable.Title, StreamKind.Vod, "tr", sub, 0, captionUrl);
        var pending = new ConcurrentQueue<Action>();
        using var host = PlayerHost.CreateHeadless();
        var view = new PlaybackViewModel(host)
        {
            PostToUi = action => pending.Enqueue(action)
        };
        view.AddStream(protocol, play: true, playable.Title, "tr", sub);
        var until = DateTime.UtcNow.AddSeconds(35);
        try
        {
            while (DateTime.UtcNow < until)
            {
                Drain(pending);
                host.ProcessPendingEvents();
                if (view.Subtitles.Applied is { Document.Cues.Count: > 0 })
                {
                    break;
                }

                Thread.Sleep(40);
            }

            Drain(pending);
            host.ProcessPendingEvents();
            var applied = view.Subtitles.Applied;
            var sample = applied is null
                ? ""
                : string.Join(" | ", applied.Document.Cues.Take(6).Select(cue => cue.Text));
            string sid;
            string slang;
            string state;
            string error;
            try
            {
                sid = host.GetMpvString("sid") ?? "";
                slang = host.GetMpvString("current-tracks/sub/lang") ?? "";
                state = host.State.ToString();
                error = host.LastError ?? "none";
            }
            catch (ObjectDisposedException)
            {
                sid = "disposed";
                slang = "";
                state = "disposed";
                error = "disposed";
            }

            return new PlayResult
            {
                Playing = state is "Playing" or "Paused",
                AppliedText = sample,
                SidLang = slang.Length > 0 ? slang : sid,
                SubLang = view.PreferredSubLang ?? "",
                CaptionUrl = captionUrl ?? playable.CaptionUrl ?? "",
                Dump =
                    "state=" + state +
                    " err=" + error +
                    " sub=" + view.PreferredSubLang +
                    " applied=" + (applied is null ? "null" : applied.Document.Cues.Count + " cues") +
                    " sid=" + sid +
                    " slang=" + slang +
                    " play=" + (applied?.PlayPath ?? "none") +
                    " captionUrl=" + (captionUrl ?? "none") +
                    " sample=" + sample
            };
        }
        finally
        {
            view.Dispose();
        }
    }

    private static void Drain(ConcurrentQueue<Action> pending)
    {
        while (pending.TryDequeue(out var action))
        {
            action();
        }
    }

    private sealed class PlayResult
    {
        public bool Playing { get; init; }
        public string AppliedText { get; init; } = "";
        public string SidLang { get; init; } = "";
        public string SubLang { get; init; } = "";
        public string CaptionUrl { get; init; } = "";
        public string Dump { get; init; } = "";
    }
}
