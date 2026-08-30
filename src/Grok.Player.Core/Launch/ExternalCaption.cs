namespace Grok.Player.Core.Launch;

public sealed record ExternalCaption(string Language, string Url, string Name)
{
    public static ExternalCaption? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        if (LooksLikeCaptionUrl(text))
        {
            return new ExternalCaption("", LocalPath(text) ?? text, "Subtitle");
        }

        var first = text.IndexOf('|');
        if (first <= 0 || first == text.Length - 1)
        {
            return null;
        }

        var language = text[..first].Trim();
        var rest = text[(first + 1)..];
        var second = rest.IndexOf('|');
        var url = (second < 0 ? rest : rest[..second]).Trim();
        var name = second < 0 ? "" : rest[(second + 1)..].Trim();
        url = LocalPath(url) ?? url;
        if (!LooksLikeCaptionUrl(url))
        {
            return null;
        }

        return new ExternalCaption(language, url, string.IsNullOrWhiteSpace(name) ? language : name);
    }

    public string ToToken() =>
        (string.IsNullOrWhiteSpace(Language) ? "und" : Language) + "|" + Url +
        (string.IsNullOrWhiteSpace(Name) ? "" : "|" + Name);

    private static bool LooksLikeCaptionUrl(string value) =>
        value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
        File.Exists(value);

    private static string? LocalPath(string value)
    {
        if (File.Exists(value))
        {
            return value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.IsFile &&
               File.Exists(uri.LocalPath)
            ? uri.LocalPath
            : null;
    }
}