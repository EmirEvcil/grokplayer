using System.Security.Cryptography;

namespace Grok.Player.Core.Media;

public static class ContentFingerprint
{
    public static string ForLocalFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return "file|missing|" + path;
        }

        return $"file|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{PrefixHash(path)}";
    }

    public static string ForVod(string url, double durationSeconds)
    {
        if (YouTubeCatalog.TryReadVideoId(url, out var videoId))
        {
            return "youtube|" + videoId;
        }

        var id = UrlSanitizer.Identity(url);
        return durationSeconds > 1
            ? $"vod|{id}|{durationSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}"
            : $"vod|{id}";
    }

    private static string PrefixHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var take = (int)Math.Min(65536, stream.Length);
            var buffer = new byte[take];
            var read = stream.Read(buffer, 0, take);
            var hash = SHA256.HashData(buffer.AsSpan(0, read));
            return Convert.ToHexString(hash.AsSpan(0, 8));
        }
        catch (Exception)
        {
            return "0";
        }
    }
}
