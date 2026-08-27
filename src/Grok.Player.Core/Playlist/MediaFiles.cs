using Grok.Player.Core.Media;

namespace Grok.Player.Core.Playlist;

public static class MediaFiles
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ts", ".m2ts", ".wmv", ".flv", ".mpeg", ".mpg",
        ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma", ".aiff",
        ".m3u8", ".m3u", ".mpd"
    };

    public static bool IsSubtitle(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSupported(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return UrlSanitizer.IsUrl(path) || Extensions.Contains(ExtensionOf(path));
    }

    public static string ExtensionOf(string path)
    {
        var value = path;
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri && !uri.IsFile)
        {
            value = uri.AbsolutePath;
        }

        return Path.GetExtension(value);
    }

    public static bool IsAudio(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aac", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".opus", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wma", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aiff", StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatLabel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        if (UrlSanitizer.IsUrl(path))
        {
            return StreamProbe.FormatLabel(path);
        }

        var ext = ExtensionOf(path).TrimStart('.');
        return string.IsNullOrWhiteSpace(ext) ? "media" : ext.ToLowerInvariant();
    }

    public static string DisplayName(string path) =>
        UrlSanitizer.IsUrl(path) ? UrlSanitizer.DisplayName(path) : Path.GetFileName(path);
}
