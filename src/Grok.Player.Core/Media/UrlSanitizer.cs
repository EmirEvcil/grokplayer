namespace Grok.Player.Core.Media;

public static class UrlSanitizer
{
    private static readonly HashSet<string> Secrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "access_token", "auth", "authorization", "signature", "sig", "key", "api_key",
        "apikey", "pwd", "password", "pass", "jwt", "expires", "expiry", "expire", "hash",
        "session", "sid", "secret", "code", "id_token", "refresh_token", "hdnts", "hdnea"
    };

    public static bool IsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("srt://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase);
    }

    public static string Identity(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if (string.IsNullOrEmpty(uri.Query))
        {
            builder.Query = string.Empty;
            return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        var kept = new List<string>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = pair.Split('=', 2)[0];
            if (!Secrets.Contains(name))
            {
                kept.Add(pair);
            }
        }

        builder.Query = kept.Count == 0 ? string.Empty : string.Join('&', kept);
        return builder.Uri.ToString().TrimEnd('/');
    }

    public static string Redact(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !IsUrl(url))
        {
            return url ?? "";
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if (string.IsNullOrEmpty(uri.Query))
        {
            return builder.Uri.GetLeftPart(UriPartial.Path);
        }

        var kept = new List<string>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            kept.Add(Secrets.Contains(parts[0]) ? parts[0] + "=***" : pair);
        }

        builder.Query = string.Join('&', kept);
        return builder.Uri.ToString();
    }

    public static string DisplayName(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }

        var name = Path.GetFileName(uri.AbsolutePath.TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(name) || name is "/" or ".")
        {
            return uri.Host;
        }

        return Uri.UnescapeDataString(name);
    }
}
