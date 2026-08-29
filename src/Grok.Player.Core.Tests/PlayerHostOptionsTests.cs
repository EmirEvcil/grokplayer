using Grok.Player.Core.Player;
using Grok.Player.Core.Tests.Fakes;

namespace Grok.Player.Core.Tests;

public sealed class PlayerHostOptionsTests
{
    [Fact]
    public void Interactive_profile_sets_nvidia_d3d11_before_initialize()
    {
        var fake = new FakeMpvNative();
        using var host = new PlayerHost(fake, new PlayerHostOptions
        {
            Headless = false,
            HardwareDecode = true,
            WindowHandle = 99,
            UseBackgroundEventLoop = false
        });

        Assert.True(fake.Initialized);
        Assert.True(fake.HasOption("config", "no"));
        Assert.True(fake.HasOption("osc", "no"));
        Assert.True(fake.HasOption("input-default-bindings", "no"));
        Assert.True(fake.HasOption("input-vo-keyboard", "no"));
        Assert.True(fake.HasOption("vo", "gpu"));
        Assert.True(fake.HasOption("gpu-api", "d3d11"));
        Assert.True(fake.HasOption("hwdec", "d3d11va-copy"));
        Assert.True(fake.HasOption("ao", "wasapi"));
        Assert.True(fake.HasOption("wid", "99"));
        Assert.True(fake.HasOption("keep-open", "yes"));
        Assert.True(fake.HasOption("blend-subtitles", "yes"));
        Assert.True(fake.HasOption("sub-visibility", "yes"));
        Assert.True(fake.HasOption("sub-pos", "100"));

        var initializeAt = fake.Lifecycle.IndexOf("initialize");
        Assert.InRange(initializeAt, 1, fake.Lifecycle.Count - 1);
        Assert.True(fake.OptionIndex("hwdec") < initializeAt);
        Assert.True(fake.OptionIndex("wid") < initializeAt);
        Assert.True(fake.OptionIndex("config") < initializeAt);
    }

    [Fact]
    public void Automated_profile_uses_null_outputs_and_no_wid()
    {
        var fake = new FakeMpvNative();
        using var _ = new PlayerHost(fake, PlayerHostOptions.ForAutomatedTests());

        Assert.True(fake.HasOption("vo", "null"));
        Assert.True(fake.HasOption("ao", "null"));
        Assert.True(fake.HasOption("hwdec", "no"));
        Assert.DoesNotContain(fake.Options, item => item.Name == "wid");
    }

    [Fact]
    public void Hardware_decode_can_be_disabled_on_the_interactive_path()
    {
        var fake = new FakeMpvNative();
        using var _ = new PlayerHost(fake, new PlayerHostOptions
        {
            Headless = false,
            HardwareDecode = false,
            UseBackgroundEventLoop = false
        });

        Assert.True(fake.HasOption("hwdec", "no"));
        Assert.DoesNotContain(fake.Options, item => item.Name == "hwdec-codecs");
    }
}
