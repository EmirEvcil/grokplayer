using Grok.Player.Core.Player;

namespace Grok.Player.Core.Tests;

public sealed class PlaybackMathTests
{
    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(140, 100)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(double.NegativeInfinity, 0)]
    public void ClampVolume_covers_range_and_non_finite(double input, double expected)
    {
        Assert.Equal(expected, PlaybackMath.ClampVolume(input));
    }

    [Fact]
    public void ClampPosition_rejects_negative_and_past_end()
    {
        Assert.Equal(TimeSpan.Zero, PlaybackMath.ClampPosition(TimeSpan.FromSeconds(-4), TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(10), PlaybackMath.ClampPosition(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(4), PlaybackMath.ClampPosition(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ClampSeek_stays_inside_ab_range()
    {
        var duration = TimeSpan.FromSeconds(100);
        var a = TimeSpan.FromSeconds(20);
        var b = TimeSpan.FromSeconds(80);
        Assert.Equal(20, PlaybackMath.ClampSeek(5, duration, a, null));
        Assert.Equal(80, PlaybackMath.ClampSeek(95, duration, null, b));
        Assert.Equal(20, PlaybackMath.ClampSeek(0, duration, a, b));
        Assert.Equal(80, PlaybackMath.ClampSeek(100, duration, a, b));
        Assert.Equal(40, PlaybackMath.ClampSeek(40, duration, a, b));
    }

    [Fact]
    public void ClampPosition_without_duration_only_rejects_negative()
    {
        Assert.Equal(TimeSpan.FromHours(3), PlaybackMath.ClampPosition(TimeSpan.FromHours(3), null));
        Assert.Equal(TimeSpan.Zero, PlaybackMath.ClampPosition(TimeSpan.FromSeconds(-1), null));
    }

    [Theory]
    [InlineData(@"C:\videos\a.mkv", true)]
    [InlineData(@"D:\İstanbul\film.mp4", true)]
    [InlineData("http://example.com/a.m3u8", false)]
    [InlineData("https://cdn/x.mp4", false)]
    [InlineData("rtsp://cam/stream", false)]
    [InlineData("file://server/share", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void LooksLikeLocalPath_classifies_sources(string path, bool expected)
    {
        Assert.Equal(expected, PlaybackMath.LooksLikeLocalPath(path));
    }
}
