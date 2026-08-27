namespace Grok.Player.Core.Preferences;

public sealed class PreferencesPage
{
    public PreferencesPage(string id, string title, params PreferencesPage[] children)
    {
        Id = id;
        Title = title;
        Children = children;
        foreach (var child in children)
        {
            child.Parent = this;
        }
    }

    public string Id { get; }

    public string Title { get; }

    public PreferencesPage? Parent { get; private set; }

    public IReadOnlyList<PreferencesPage> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public IReadOnlyList<PreferencesPage> Tabs
    {
        get
        {
            if (HasChildren)
            {
                return Children;
            }

            return Parent is { HasChildren: true } parent ? parent.Children : [this];
        }
    }
}
