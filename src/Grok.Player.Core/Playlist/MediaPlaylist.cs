using System.Collections.ObjectModel;
using Grok.Player.Core.Launch;
using Grok.Player.Core.Media;

namespace Grok.Player.Core.Playlist;

public enum PlaylistKind
{
    Local,
    Stream
}

public sealed class PlaylistItem
{
    public PlaylistItem(string path, PlaylistKind kind = PlaylistKind.Local, string? title = null)
    {
        Path = path;
        Kind = kind;
        Name = MediaFiles.DisplayName(path);
        Title = string.IsNullOrWhiteSpace(title) ? TitleFrom(path, kind) : title.Trim();
        Format = MediaFiles.FormatLabel(path);
        StreamKind = kind == PlaylistKind.Stream ? StreamKind.Unknown : StreamKind.Unknown;
    }

    public string Path { get; internal set; }
    public string Name { get; private set; }
    public string Title { get; private set; }

    public void SetTitle(string title)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            Title = title.Trim();
        }
    }
    public string Format { get; private set; }
    public PlaylistKind Kind { get; }
    public StreamKind StreamKind { get; set; }

    public int VideoHeight { get; set; }

    public string? AudioLang { get; set; }

    public string? SubLang { get; set; }

    public bool SkipCaptions { get; set; }

    public string? MediaUrl { get; set; }

    public string? AudioUrl { get; set; }

    public string? UserAgent { get; set; }

    public string? Referer { get; set; }

    public string? CaptionUrl { get; set; }

    public List<ExternalCaption> CaptionTracks { get; } = [];

    public int CachedHeight { get; set; }

    public string? CachedAudioLang { get; set; }

    public string? CachedSubLang { get; set; }

    public DateTime? PlayableAt { get; set; }

    public string? StoryboardSpec { get; set; }

    public void RememberPlayable(YouTubePlayable playable, int height = 0, string? requestAudio = null, string? requestSub = null)
    {
        ArgumentNullException.ThrowIfNull(playable);
        MediaUrl = playable.MediaUrl;
        AudioUrl = playable.AudioUrl;
        UserAgent = playable.UserAgent;
        Referer = playable.Referer;
        CaptionUrl = playable.CaptionUrl;
        StreamKind = playable.Kind;
        CachedHeight = height;
        CachedAudioLang = requestAudio ?? playable.AudioLang;
        CachedSubLang = requestSub ?? playable.SubLang;
        PlayableAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(playable.StoryboardSpec))
        {
            StoryboardSpec = playable.StoryboardSpec;
        }
        if (!string.IsNullOrWhiteSpace(playable.AudioLang))
        {
            AudioLang = playable.AudioLang;
        }

        if (!string.IsNullOrWhiteSpace(playable.SubLang))
        {
            SubLang = playable.SubLang;
        }

        SetTitle(playable.Title);
    }

    public void ForgetPlayable()
    {
        MediaUrl = null;
        AudioUrl = null;
        UserAgent = null;
        Referer = null;
        CaptionUrl = null;
        CachedHeight = 0;
        CachedAudioLang = null;
        CachedSubLang = null;
        PlayableAt = null;
    }

    public void UpdatePath(string path)
    {
        Path = path;
        Name = MediaFiles.DisplayName(path);
        Format = MediaFiles.FormatLabel(path);
    }

    public override string ToString() => Title;

    private static string TitleFrom(string path, PlaylistKind kind)
    {
        if (kind == PlaylistKind.Stream)
        {
            return MediaFiles.DisplayName(path);
        }

        var title = System.IO.Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(title) ? MediaFiles.DisplayName(path) : title;
    }
}

public sealed class MediaPlaylist
{
    private readonly ObservableCollection<PlaylistItem> _items = [];
    private readonly HashSet<string> _unique = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<PlaylistItem> Items => _items;

    public int CurrentIndex { get; private set; } = -1;

    public int Count => _items.Count;

    public string? CurrentPath => CurrentIndex >= 0 && CurrentIndex < _items.Count ? _items[CurrentIndex].Path : null;

    public bool TryAdd(string path) => TryAdd(path, null);

    public bool TryAdd(string path, string? title)
    {
        var full = Normalize(path);
        if (string.IsNullOrWhiteSpace(full) || !MediaFiles.IsSupported(full))
        {
            return false;
        }

        var key = Identity(full);
        var kind = UrlSanitizer.IsUrl(full) || YouTubeCatalog.IsWatchUrl(full)
            ? PlaylistKind.Stream
            : PlaylistKind.Local;
        if (!_unique.Add(key))
        {
            foreach (var item in _items)
            {
                if (Identity(item.Path) != key)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(title))
                {
                    item.SetTitle(title);
                }

                if (kind == PlaylistKind.Stream && item.Path != full && !YouTubeCatalog.IsWatchUrl(item.Path))
                {
                    item.UpdatePath(full);
                }
            }

            return false;
        }

        _items.Add(new PlaylistItem(full, kind, title));
        return true;
    }

    public IReadOnlyList<string> AddMany(IEnumerable<string> paths)
    {
        var added = new List<string>();
        foreach (var path in paths)
        {
            if (TryAdd(path))
            {
                added.Add(Normalize(path));
            }
        }

        return added;
    }

    public bool SetCurrent(string path)
    {
        var full = Normalize(path);
        var key = Identity(full);
        var index = -1;
        for (var i = 0; i < _items.Count; i++)
        {
            if (Identity(_items[i].Path).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                _items[i].UpdatePath(full);
                break;
            }
        }

        if (index < 0)
        {
            if (!TryAdd(full))
            {
                return false;
            }

            index = _items.Count - 1;
        }

        CurrentIndex = index;
        return true;
    }

    public string? Next(LoopMode loop)
    {
        if (_items.Count == 0)
        {
            return null;
        }

        if (loop == LoopMode.One && CurrentIndex >= 0)
        {
            return _items[CurrentIndex].Path;
        }

        if (CurrentIndex + 1 < _items.Count)
        {
            CurrentIndex++;
            return _items[CurrentIndex].Path;
        }

        if (loop == LoopMode.Playlist)
        {
            CurrentIndex = 0;
            return _items[0].Path;
        }

        return null;
    }

    public static string Identity(string path)
    {
        if (YouTubeCatalog.TryReadVideoId(path, out var videoId))
        {
            return "youtube|" + videoId;
        }

        return UrlSanitizer.IsUrl(path) ? UrlSanitizer.Identity(path) : Normalize(path);
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var trimmed = path.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return trimmed;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception)
        {
            return trimmed;
        }
    }
}
