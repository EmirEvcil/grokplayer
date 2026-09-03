using Grok.Player.Core.Presentation;

namespace Grok.Player.Core.Tests;

public sealed class ActionFeedbackTests
{
    [Fact]
    public void Skip_is_the_skip_amount_without_percent()
    {
        Assert.Equal("Skipping 00:05", ActionFeedback.Skip(TimeSpan.FromSeconds(5)));
        Assert.Equal("Skipping 00:05", ActionFeedback.Skip(TimeSpan.FromSeconds(-5)));
        Assert.Equal("Skipping 0.5s", ActionFeedback.Skip(TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public void Skip_uses_hours_when_needed()
    {
        Assert.Equal("Skipping 1:00:00", ActionFeedback.Skip(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Volume_is_a_whole_number()
    {
        Assert.Equal("Volume 75", ActionFeedback.Volume(75.4));
    }

    [Fact]
    public void Equalizer_logs_preset_and_signed_gain()
    {
        Assert.Equal("Equalizer on", ActionFeedback.EqualizerEnabled(true));
        Assert.Equal("EQ Rock", ActionFeedback.EqualizerPreset("Rock"));
        Assert.Equal("EQ 1k +24", ActionFeedback.EqualizerBand("1k", 24.2));
        Assert.Equal("EQ 60 -12", ActionFeedback.EqualizerBand("60", -12));
    }

    [Fact]
    public void Video_logs_picture_and_filters()
    {
        Assert.Equal("Brightness 50", ActionFeedback.VideoPicture("Brightness", 50.4));
        Assert.Equal("Color 0", ActionFeedback.VideoPicture("Color", -3));
        Assert.Equal("Softer on", ActionFeedback.VideoFilter("Softer", true));
        Assert.Equal("HDR Native", ActionFeedback.HdrMode("Native"));
        Assert.Equal("Sharpen off", ActionFeedback.VideoFilter("Sharpen", false));
        Assert.Equal("Captured frame", ActionFeedback.CapturedFrame());
        Assert.Equal("Subtitle film.srt", ActionFeedback.SubtitleLoaded("film.srt"));
        Assert.Equal("Added film.srt", ActionFeedback.SubtitleAdded("film.srt"));
        Assert.Equal("Subtitles off", ActionFeedback.SubtitlesOff());
        Assert.Equal("Sync +0.5s", ActionFeedback.SubtitleSync(0.5));
        Assert.Equal("Sync -1.25s", ActionFeedback.SubtitleSync(-1.25));
        Assert.Equal("Speed 1.2x", ActionFeedback.Speed(1.2));
        Assert.Equal("A-B off", ActionFeedback.LoopCleared());
        Assert.Equal("Scaling Cinema", ActionFeedback.ScalingPreset("Cinema"));
        Assert.Equal("Upscaling Lanczos", ActionFeedback.ScaleKernel("Upscaling", "Lanczos"));
        Assert.Equal("Aspect 4:3", ActionFeedback.ResizeAspect("4:3"));
        Assert.Equal("Width 102%", ActionFeedback.ImageWidth(1.02));
        Assert.Equal("Height 98%", ActionFeedback.ImageHeight(0.98));
        Assert.Equal("Image reset", ActionFeedback.ImageReset());
        Assert.Equal("Step 2%", ActionFeedback.ShortcutStep(2));
        Assert.Equal("Size 5120×1440", ActionFeedback.ResizeSize(5120, 1440));
    }

    [Fact]
    public void Opened_includes_playlist_index()
    {
        Assert.Equal("[2/5] clip.mp4", ActionFeedback.Opened(2, 5, "clip.mp4"));
        Assert.Equal("clip.mp4", ActionFeedback.Opened(1, 0, "clip.mp4"));
    }

    [Fact]
    public void Added_counts_files()
    {
        Assert.Equal("Added 1 file", ActionFeedback.Added(1));
        Assert.Equal("Added 3 files", ActionFeedback.Added(3));
    }
}
