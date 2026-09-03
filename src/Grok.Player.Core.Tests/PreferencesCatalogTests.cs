using Grok.Player.Core.Preferences;

namespace Grok.Player.Core.Tests;

public sealed class PreferencesCatalogTests
{
    [Fact]
    public void Video_tabs_are_the_child_sections()
    {
        var deinterlace = PreferencesCatalog.Find("video-deinterlace");
        Assert.NotNull(deinterlace);
        Assert.Equal("Deinterlacing", deinterlace.Title);
        var tabs = deinterlace.Tabs;
        Assert.Equal(12, tabs.Count);
        Assert.Contains(tabs, page => page.Id == "video-crop");
        Assert.Contains(tabs, page => page.Id == "video-resize");
        Assert.Contains(tabs, page => page.Id == "video-hdr");
        Assert.Contains(tabs, page => page.Id == "video-super-resolution");
        Assert.Same(tabs, PreferencesCatalog.Find("video")!.Tabs);
    }

    [Fact]
    public void Leaf_root_has_itself_as_the_only_tab()
    {
        var general = PreferencesCatalog.Find("general");
        Assert.NotNull(general);
        Assert.Equal("general", Assert.Single(general.Tabs).Id);
        Assert.NotNull(PreferencesCatalog.Find("downloads"));
    }

    [Fact]
    public void Search_finds_nested_titles()
    {
        var hits = PreferencesCatalog.Search("crop");
        Assert.Contains(hits, page => page.Id == "video-crop");
        Assert.Contains(PreferencesCatalog.Search("hdr"), page => page.Id == "video-hdr");
        Assert.DoesNotContain(hits, page => page.Id == "audio");
        Assert.True(PreferencesCatalog.Matches(PreferencesCatalog.Find("video")!, "crop"));
    }
}
