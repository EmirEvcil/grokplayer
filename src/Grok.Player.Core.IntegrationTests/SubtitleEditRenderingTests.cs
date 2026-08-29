using Grok.Player.Core.IntegrationTests.Support;
using Grok.Player.Core.Native;
using Grok.Player.Core.Player;
using Grok.Player.Core.Subtitles;

namespace Grok.Player.Core.IntegrationTests;

public sealed class SubtitleEditRenderingTests
{
    [LibMpvFact]
    public void Paused_player_reloads_edited_text_and_inline_styles_with_real_libmpv()
    {
        var sample = GeneratedMedia.TryCreateSample(5);
        Assert.True(sample is not null, GeneratedMedia.LastError);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vtt");
        File.WriteAllText(path, "WEBVTT\n\n00:00:00.000 --> 00:00:05.000\n<c.color00BCE7>Hello world</c>\n");
        var native = new MpvNative();
        using var host = new PlayerHost(native, new PlayerHostOptions { Headless = true, UseBackgroundEventLoop = true });
        try
        {
            host.Open(sample!);
            EventWait.Until(() => host.Duration is not null, TimeSpan.FromSeconds(10), "video opened");
            host.Pause();
            host.Seek(TimeSpan.FromSeconds(1));
            var model = new SubtitleModel();
            model.Changed += _ => host.SetSubtitleFile(model.Applied?.PlayPath);
            var track = model.AddFile(path, true);
            EventWait.Until(() => (host.GetMpvString("sub-text") ?? "").Contains("Hello"), TimeSpan.FromSeconds(5), "initial subtitle");
            Assert.Contains("E7BC00", host.GetMpvString("sub-text/ass") ?? "");
            track.Document.Cues[0].Text = "Updated while paused";
            track.Document.Cues[0].Spans = [new CaptionSpan("Updated while paused", "#00FF00", Bold: true, Italic: true, Underline: true)];
            model.PersistActive();
            EventWait.Until(() => (host.GetMpvString("sub-text") ?? "").Contains("Updated while paused"), TimeSpan.FromSeconds(5), "edited subtitle without reopening");
            var rendered = host.GetMpvString("sub-text/ass") ?? "";
            Assert.Contains("00FF00", rendered);
            Assert.Contains("\\u1", rendered);
            Assert.Contains("\\b1", rendered);
            host.SetSubFont("Arial");
            host.SetSubFontSize(42);
            host.SetSubShiftX(0);
            Assert.Equal("yes", host.GetMpvString("sub-ass-override"));
            Assert.Equal(42, host.GetMpvDouble("sub-font-size"));
            native.SetPropertyString("screenshot-format", "png");
            var picture = Path.Combine(Path.GetTempPath(), "grok-codex-verification", "edited-subtitle-render.png");
            Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
            native.Command("screenshot-to-file", picture, "subtitles");
            Assert.True(File.Exists(picture) && new FileInfo(picture).Length > 100);
        }
        finally { File.Delete(path); File.Delete(path + ".edited.srt"); }
    }


    [LibMpvFact]
    public void Libmpv_renders_arabic_and_authored_color_in_the_same_ass_track()
    {
        var sample = GeneratedMedia.TryCreateSample(5);
        Assert.True(sample is not null, GeneratedMedia.LastError);
        var document = new SrtDocument([
            new SrtCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(5), "خلفي 100 شرطي،",
                [new CaptionSpan("خلفي 100 شرطي،", "#00BCE7", Bold: true)])
        ]);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ass");
        File.WriteAllText(path, document.ToAss());
        using var host = PlayerHost.CreateHeadless();
        try
        {
            host.Open(sample!);
            EventWait.Until(() => host.Duration is not null, TimeSpan.FromSeconds(10), "video opened");
            host.Pause();
            host.Seek(TimeSpan.FromSeconds(1));
            Assert.True(host.SetSubtitleFile(path));
            EventWait.Until(() => (host.GetMpvString("sub-text") ?? "").Contains("خلفي"), TimeSpan.FromSeconds(5), "Arabic subtitle");
            var ass = host.GetMpvString("sub-text/ass") ?? "";
            Assert.Contains("خلفي", ass);
            Assert.Contains("E7BC00", ass);
        }
        finally { File.Delete(path); }
    }
}
