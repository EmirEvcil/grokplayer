using Grok.Player.Core.Presentation;

namespace Grok.Player.Core.Tests;

public sealed class TimeDisplayTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(65, "1:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3661, "1:01:01")]
    [InlineData(7325, "2:02:05")]
    public void Format_renders_expected_clock(int seconds, string expected)
    {
        Assert.Equal(expected, TimeDisplay.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Format_null_is_placeholder()
    {
        Assert.Equal("--:--", TimeDisplay.Format(null));
    }

    [Fact]
    public void Format_negative_clamps_to_zero()
    {
        Assert.Equal("0:00", TimeDisplay.Format(TimeSpan.FromSeconds(-12)));
    }

    [Fact]
    public void Format_drops_fractional_seconds()
    {
        Assert.Equal("0:01", TimeDisplay.Format(TimeSpan.FromMilliseconds(1999)));
    }

    [Fact]
    public void FormatPair_joins_position_and_duration()
    {
        Assert.Equal("1:05 / 2:00", TimeDisplay.FormatPair(TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(120)));
        Assert.Equal("0:00 / --:--", TimeDisplay.FormatPair(TimeSpan.Zero, null));
    }
}
