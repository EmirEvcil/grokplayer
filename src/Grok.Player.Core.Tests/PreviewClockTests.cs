using Grok.Player.Core.Presentation;

namespace Grok.Player.Core.Tests;

public sealed class PreviewClockTests
{
    [Fact]
    public void Live_preview_always_uses_the_elapsed_clock()
    {
        Assert.Equal("00:14:40", PreviewClock.Text(true, TimeSpan.FromSeconds(880)));
        Assert.Equal("00:02:40", PreviewClock.Text(true, TimeSpan.FromSeconds(160)));
    }

    [Fact]
    public void Vod_preview_uses_the_seek_label()
    {
        Assert.Equal("02:40", PreviewClock.Text(false, TimeSpan.FromSeconds(160)));
        Assert.Equal("14:40", PreviewClock.Text(false, TimeSpan.FromSeconds(880)));
    }
}
