using Grok.Player.Core.Player;

namespace Grok.Player.Core.Tests;

public sealed class LivePlaybackTests
{
    [Fact]
    public void Snap_target_is_the_newest_tip()
    {
        Assert.Equal(98, LivePlayback.SnapTargetSeconds(80, 100, 95), 3);
        Assert.Equal(0, LivePlayback.SnapTargetSeconds(0, 0, 0), 3);
    }

    [Fact]
    public void Long_live_windows_keep_only_the_last_three_minutes()
    {
        Assert.False(LivePlayback.ShouldCapDvr(80));
        Assert.True(LivePlayback.ShouldCapDvr(900));
        Assert.Equal(0, LivePlayback.WindowStart(80));
        Assert.Equal(720, LivePlayback.WindowStart(900));
        Assert.Equal(720, LivePlayback.ClampToWindow(40, 900));
        Assert.Equal(900, LivePlayback.ClampToWindow(980, 900));
        Assert.Equal(850, LivePlayback.ClampToWindow(850, 900));
    }

    [Fact]
    public void Catch_up_is_needed_until_the_playhead_reaches_the_tip()
    {
        Assert.True(LivePlayback.NeedsCatchUp(10, 16));
        Assert.False(LivePlayback.NeedsCatchUp(14, 16));
    }

    [Fact]
    public void At_live_uses_a_tight_slack()
    {
        Assert.True(LivePlayback.IsAtLive(97.5, 100));
        Assert.False(LivePlayback.IsAtLive(96, 100));
    }

    [Fact]
    public void Follow_live_tolerates_normal_segment_latency_but_not_a_real_dvr_seek()
    {
        Assert.True(LivePlayback.CanKeepFollowing(91, 100));
        Assert.False(LivePlayback.CanKeepFollowing(80, 100));
    }

    [Fact]
    public void Usable_still_rejects_missing_and_tiny_files()
    {
        Assert.False(LivePlayback.IsUsableStill(null));
        Assert.False(LivePlayback.IsUsableStill(@"C:\missing-preview.jpg"));
        var tiny = Path.Combine(Path.GetTempPath(), $"tiny-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(tiny, [0xFF, 0xD8, 0xFF, 0xD9]);
        try
        {
            Assert.False(LivePlayback.IsUsableStill(tiny));
        }
        finally
        {
            File.Delete(tiny);
        }
    }

    [Fact]
    public void Usable_still_accepts_a_jpeg_header_with_payload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"still-{Guid.NewGuid():N}.jpg");
        var bytes = new byte[5000];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        File.WriteAllBytes(path, bytes);
        try
        {
            Assert.True(LivePlayback.IsUsableStill(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
