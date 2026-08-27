using System.Net.Http.Headers;

namespace Grok.Player.Core.Media;

public interface IStreamInspector
{
    StreamKind Inspect(string url);
}

public sealed class HttpStreamInspector : IStreamInspector
{
    public StreamKind Inspect(string url)
    {
        var fromUrl = StreamProbe.ClassifyUrl(url);
        if (fromUrl != StreamKind.Unknown)
        {
            return fromUrl;
        }

        try
        {
            var text = ReadPrefix(url);
            if (string.IsNullOrWhiteSpace(text))
            {
                return StreamKind.Unknown;
            }

            if (StreamProbe.LooksLikeDrm(text))
            {
                return StreamKind.Unknown;
            }

            var kind = StreamProbe.ClassifyManifest(text);
            if (kind != StreamKind.Unknown)
            {
                return kind;
            }

            var variant = StreamProbe.FirstVariantUri(text, url);
            if (variant is null)
            {
                return StreamKind.Unknown;
            }

            var child = ReadPrefix(variant);
            return StreamProbe.ClassifyManifest(child);
        }
        catch (Exception)
        {
            return StreamProbe.ClassifyUrl(url);
        }
    }

    private static string ReadPrefix(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 65535);
        request.Headers.UserAgent.ParseAdd("GrokPlayer/1.0");
        using var response = http.Send(request);
        using var stream = response.Content.ReadAsStream();
        var buffer = new byte[65536];
        var read = stream.Read(buffer, 0, buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Max(0, read));
    }
}
