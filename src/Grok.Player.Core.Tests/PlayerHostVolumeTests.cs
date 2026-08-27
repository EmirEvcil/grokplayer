using Grok.Player.Core.Player;
using Grok.Player.Core.Tests.Fakes;

namespace Grok.Player.Core.Tests;

public sealed class PlayerHostVolumeTests
{
    [Fact]
    public void Default_volume_is_100()
    {
        using var host = new PlayerHost(new FakeMpvNative(), PlayerHostOptions.ForAutomatedTests());
        Assert.Equal(100, host.Volume);
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(0, 0)]
    [InlineData(37, 37)]
    [InlineData(100, 100)]
    [InlineData(250, 100)]
    public void SetVolume_clamps_and_writes_property(double input, double expected)
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        host.SetVolume(input);
        host.ProcessPendingEvents();
        Assert.Equal(expected, host.Volume);
        Assert.Contains(fake.Lifecycle, item => item == $"property:volume={expected}");
    }

    [Fact]
    public void Volume_can_be_changed_without_media()
    {
        using var host = new PlayerHost(new FakeMpvNative(), PlayerHostOptions.ForAutomatedTests());
        host.SetVolume(25);
        Assert.Equal(25, host.Volume);
    }

    [Fact]
    public void Mute_is_applied_before_unpause()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());
        var path = Grok.Player.Core.Tests.Support.TestMedia.CreateTempFile();
        try
        {
            host.Open(path);
            host.ProcessPendingEvents();
            host.Pause();
            host.SetMuted(true);
            host.Play();
            var mute = fake.Lifecycle.FindLastIndex(item => item == "property:mute=True");
            var ao = fake.Lifecycle.FindLastIndex(item => item == "property:ao-volume=0");
            var flush = fake.Commands.FindLastIndex(item => item.Length >= 2 && item[0] == "seek" && item[1] == "0");
            var pauseOff = fake.Lifecycle.FindLastIndex(item => item == "property:pause=False");
            Assert.True(mute >= 0 && pauseOff > mute, "mute must be set before unpause");
            Assert.True(ao >= 0 && pauseOff > ao, "ao-volume 0 must be set before unpause");
            Assert.True(flush >= 0, "paused mute must flush the audio buffer");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Custom_initial_volume_is_applied_at_construction()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, new PlayerHostOptions
        {
            Headless = true,
            UseBackgroundEventLoop = false,
            InitialVolume = 40
        });
        Assert.Equal(40, host.Volume);
        Assert.Contains(fake.Lifecycle, item => item == "property:volume=40");
    }
}
