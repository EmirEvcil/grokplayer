using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Grok.Player.Core.Media;

internal static class ProtectedStreamProxy
{
    private sealed record Session(string Secret, long StartTime, string Referer, string ProtectedHost, DateTime CreatedAt);

    private static readonly ConcurrentDictionary<string, Session> Sessions = new(StringComparer.Ordinal);
    private static readonly HttpClient Http = CreateClient();
    private static readonly TcpListener Listener = StartListener();
    private static readonly int Port = ((IPEndPoint)Listener.LocalEndpoint).Port;

    static ProtectedStreamProxy()
    {
        _ = Task.Run(AcceptLoop);
    }

    internal static bool TryUnwrap(string? url, out string target)
    {
        target = "";
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Host is not ("127.0.0.1" or "localhost" or "[::1]"))
        {
            return false;
        }

        return TryTarget(uri.AbsolutePath, out _, out target);
    }

    internal static string Register(string manifestUrl, string referer, string secret, long startTime)
    {
        var token = Guid.NewGuid().ToString("N");
        var host = new Uri(manifestUrl).Host;
        Sessions[token] = new Session(secret, startTime, referer, host, DateTime.UtcNow);
        if (Sessions.Count > 64)
        {
            foreach (var stale in Sessions.OrderBy(entry => entry.Value.CreatedAt).Take(Sessions.Count - 48))
            {
                Sessions.TryRemove(stale.Key, out _);
            }
        }

        return ProxyUrl(token, manifestUrl);
    }

    internal static string RewriteHlsManifest(string manifest, string sourceUrl, Func<string, string> proxyUrl)
    {
        var source = new Uri(sourceUrl);
        var lines = manifest.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith('#'))
            {
                lines[i] = Regex.Replace(lines[i], "URI=\"([^\"]+)\"", match =>
                    "URI=\"" + proxyUrl(new Uri(source, match.Groups[1].Value).AbsoluteUri) + "\"",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            else if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                lines[i] = proxyUrl(new Uri(source, lines[i].Trim()).AbsoluteUri);
            }
        }

        return string.Join("\n", lines);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private static TcpListener StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(24);
        return listener;
    }

    private static async Task AcceptLoop()
    {
        while (true)
        {
            try
            {
                var client = await Listener.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(() => Handle(client));
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
            }
        }
    }

    private static async Task Handle(TcpClient client)
    {
        using (client)
        using (var network = client.GetStream())
        using (var reader = new StreamReader(network, Encoding.ASCII, false, 4096, leaveOpen: true))
        {
            try
            {
                var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                var requestParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                string? range = null;
                while (true)
                {
                    var header = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(header))
                    {
                        break;
                    }
                    if (header.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    {
                        range = header[6..].Trim();
                    }
                }

                if (requestParts.Length < 2 || requestParts[0] != "GET" || !TryTarget(requestParts[1], out var token, out var target) ||
                    !Sessions.TryGetValue(token, out var session))
                {
                    await Respond(network, 404, "text/plain", "Not found"u8.ToArray()).ConfigureAwait(false);
                    return;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.TryAddWithoutValidation("User-Agent", StreamCatalog.ChromeUa);
                request.Headers.TryAddWithoutValidation("Referer", session.Referer);
                request.Headers.TryAddWithoutValidation("Origin", new Uri(session.Referer).GetLeftPart(UriPartial.Authority));
                if (string.Equals(new Uri(target).Host, session.ProtectedHost, StringComparison.OrdinalIgnoreCase))
                {
                    var elapsed = Math.Max(0L, (long)(DateTime.UtcNow - session.CreatedAt).TotalSeconds);
                    var random = Base36(Random.Shared.Next(1, int.MaxValue));
                    request.Headers.TryAddWithoutValidation("X-Sp", StreamCatalog.BuildSpProof(session.Secret, session.StartTime + elapsed, random));
                }
                if (!string.IsNullOrWhiteSpace(range))
                {
                    request.Headers.TryAddWithoutValidation("Range", range);
                }

                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                if (response.IsSuccessStatusCode && LooksManifest(bytes))
                {
                    var manifest = Encoding.UTF8.GetString(bytes);
                    bytes = Encoding.UTF8.GetBytes(RewriteHlsManifest(manifest, target, url => ProxyUrl(token, url)));
                    contentType = "application/vnd.apple.mpegurl";
                }

                await Respond(network, (int)response.StatusCode, contentType, bytes).ConfigureAwait(false);
            }
            catch (Exception)
            {
                try { await Respond(network, 502, "text/plain", "Upstream failed"u8.ToArray()).ConfigureAwait(false); }
                catch (Exception) { }
            }
        }
    }

    private static bool LooksManifest(byte[] bytes) =>
        bytes.Length >= 7 && Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 16)).TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);

    private static async Task Respond(NetworkStream stream, int status, string contentType, byte[] body)
    {
        var reason = status is >= 200 and < 300 ? "OK" : status == 404 ? "Not Found" : "Bad Gateway";
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    private static string ProxyUrl(string token, string target) =>
        $"http://127.0.0.1:{Port}/stream/{token}/{Encode(target)}";

    private static bool TryTarget(string path, out string token, out string target)
    {
        token = "";
        target = "";
        var clean = path.Split('?', 2)[0];
        var parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "stream")
        {
            return false;
        }

        token = parts[1];
        try
        {
            target = Decode(parts[2]);
            return Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base36(int value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> buffer = stackalloc char[7];
        var cursor = buffer.Length;
        do
        {
            buffer[--cursor] = digits[value % 36];
            value /= 36;
        } while (value > 0);
        return new string(buffer[cursor..]);
    }

    private static string Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
