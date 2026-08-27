using Grok.Player.Core.Presentation;

namespace Grok.Player.Core.Tests;

public sealed class SeekBarMathTests
{
    [Fact]
    public void TimeAt_maps_edges_and_midpoint()
    {
        var duration = TimeSpan.FromSeconds(100);
        Assert.Equal(TimeSpan.Zero, SeekBarMath.TimeAt(0, 200, duration));
        Assert.Equal(TimeSpan.FromSeconds(50), SeekBarMath.TimeAt(100, 200, duration));
        Assert.Equal(duration, SeekBarMath.TimeAt(200, 200, duration));
    }

    [Fact]
    public void TimeAt_clamps_out_of_range()
    {
        var duration = TimeSpan.FromSeconds(10);
        Assert.Equal(TimeSpan.Zero, SeekBarMath.TimeAt(-20, 100, duration));
        Assert.Equal(duration, SeekBarMath.TimeAt(500, 100, duration));
    }

    [Fact]
    public void TimeAt_zero_width_or_duration_is_zero()
    {
        Assert.Equal(TimeSpan.Zero, SeekBarMath.TimeAt(10, 0, TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.Zero, SeekBarMath.TimeAt(10, 100, TimeSpan.Zero));
    }

    [Fact]
    public void OffsetForTime_is_inverse_of_TimeAt()
    {
        var duration = TimeSpan.FromSeconds(80);
        var x = SeekBarMath.OffsetForTime(TimeSpan.FromSeconds(20), duration, 400);
        Assert.Equal(100, x);
        Assert.Equal(TimeSpan.FromSeconds(20), SeekBarMath.TimeAt(x, 400, duration));
    }
}
