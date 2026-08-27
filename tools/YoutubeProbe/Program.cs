using Grok.Player.Core.Media;
using Grok.Player.Core.Player;

var url = args.Length > 0 ? args[0] : "https://www.youtube.com/watch?v=WFSdNlLtu7I";
if (args.Length > 0 && args[0] == "langs")
{
    var target = args.Length > 1 ? args[1] : "https://www.youtube.com/watch?v=Qtl8lJwbd4g";
    var audio = args.Length > 2 ? args[2] : "tr";
    var sub = args.Length > 3 ? args[3] : "tr";
    var resolved = YouTubeCatalog.Resolve(target, null, audio, sub);
    if (resolved is null)
    {
        Console.WriteLine("RESOLVE_NULL");
        return 2;
    }

    resolved = YouTubeCatalog.BindHlsRenditions(resolved);
    Console.WriteLine("id=" + resolved.VideoId);
    Console.WriteLine("title=" + resolved.Title);
    Console.WriteLine("kind=" + resolved.Kind);
    Console.WriteLine("audioLang=" + resolved.AudioLang);
    Console.WriteLine("subLang=" + resolved.SubLang);
    Console.WriteLine("captionUrl=" + (resolved.CaptionUrl ?? "none"));
    Console.WriteLine("hlsSubtitles=" + resolved.HlsSubtitles);
    Console.WriteLine("audioUrl=" + (resolved.AudioUrl is null ? "none" : resolved.AudioUrl[..Math.Min(160, resolved.AudioUrl.Length)]));
    Console.WriteLine("media=" + resolved.MediaUrl[..Math.Min(160, resolved.MediaUrl.Length)]);
    var caption = Grok.Player.Core.Subtitles.StreamCaptionLoader.Load(resolved.VideoId, sub, resolved.CaptionUrl);
    Console.WriteLine("captionFile=" + (caption ?? "NONE"));
    if (caption is not null)
    {
        var text = File.ReadAllText(caption);
        Console.WriteLine("captionBytes=" + text.Length);
        Console.WriteLine("captionHead=" + text[..Math.Min(240, text.Length)].Replace('\n', '|'));
    }

    if (resolved.MediaUrl.Contains("m3u8", StringComparison.OrdinalIgnoreCase))
    {
        var master = FetchHls(resolved.MediaUrl, resolved.UserAgent);
        Console.WriteLine("masterBytes=" + (master?.Length ?? 0));
        if (!string.IsNullOrWhiteSpace(master))
        {
            foreach (var line in master.Replace("\r", "").Split('\n'))
            {
                if (line.Contains("TYPE=AUDIO", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("TYPE=SUBTITLES", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(line);
                }
            }
        }
    }

    var audioForOpen = YouTubeCatalog.UsesSeparateAudio(resolved) ? resolved.AudioUrl : null;
    Console.WriteLine("openMedia=" + resolved.MediaUrl[..Math.Min(120, resolved.MediaUrl.Length)]);
    Console.WriteLine("openAudio=" + (audioForOpen is null ? "none" : audioForOpen[..Math.Min(120, audioForOpen.Length)]));
    Console.WriteLine("localMaster=" + (!resolved.MediaUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)));
    using (var host = PlayerHost.CreateHeadless())
    {
        string? error = null;
        host.Error += (_, e) => error = e.Message;
        host.Open(
            resolved.MediaUrl,
            resolved.Kind,
            audioForOpen,
            resolved.Title,
            resolved.UserAgent,
            resolved.AudioLang,
            resolved.SubLang,
            caption);
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until &&
               host.State is not (PlayerState.Playing or PlayerState.Paused or PlayerState.Error))
        {
            host.ProcessPendingEvents();
            Thread.Sleep(40);
        }

        host.ProcessPendingEvents();
        Console.WriteLine("playState=" + host.State);
        Console.WriteLine("playError=" + (host.LastError ?? error ?? "none"));
        Console.WriteLine("playDuration=" + host.Duration);
        Console.WriteLine("aid=" + host.GetMpvString("aid") + " alang=" + host.GetMpvString("current-tracks/audio/lang") + " atitle=" + host.GetMpvString("current-tracks/audio/title"));
        Console.WriteLine("sid=" + host.GetMpvString("sid") + " slang=" + host.GetMpvString("current-tracks/sub/lang") + " stitle=" + host.GetMpvString("current-tracks/sub/title"));
        Console.WriteLine("sub-vis=" + host.GetMpvString("sub-visibility"));
        var tracks = host.GetMpvLong("track-list/count") ?? 0;
        Console.WriteLine("tracks=" + tracks);
        for (var i = 0; i < tracks && i < 40; i++)
        {
            var p = "track-list/" + i + "/";
            Console.WriteLine(
                "  #" + i +
                " type=" + host.GetMpvString(p + "type") +
                " id=" + host.GetMpvLong(p + "id") +
                " lang=" + host.GetMpvString(p + "lang") +
                " title=" + host.GetMpvString(p + "title") +
                " selected=" + host.GetMpvString(p + "selected") +
                " external=" + host.GetMpvString(p + "external") +
                " src=" + (host.GetMpvString(p + "external-filename") ?? ""));
        }
    }

    return 0;
}

static string? FetchHls(string href, string? ua)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        using var req = new HttpRequestMessage(HttpMethod.Get, href);
        req.Headers.TryAddWithoutValidation("User-Agent", ua ?? "Mozilla/5.0");
        req.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
        using var resp = http.Send(req);
        return resp.IsSuccessStatusCode ? resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() : null;
    }
    catch
    {
        return null;
    }
}

if (args.Length > 0 && args[0] == "app")
{
    var target = args.Length > 1 ? args[1] : "https://www.youtube.com/watch?v=WFSdNlLtu7I";
    var resolved = YouTubeCatalog.Resolve(target);
    if (resolved is null)
    {
        Console.WriteLine("RESOLVE_NULL");
        return 2;
    }

    Console.WriteLine("id=" + resolved.VideoId);
    Console.WriteLine("title=" + resolved.Title);
    Console.WriteLine("kind=" + resolved.Kind);
    Console.WriteLine("media=" + resolved.MediaUrl[..Math.Min(120, resolved.MediaUrl.Length)]);
    using var host = PlayerHost.CreateHeadless();
    var error = "";
    host.Error += (_, e) => error = e.Message;
    host.Open(resolved.MediaUrl, resolved.Kind, resolved.AudioUrl, resolved.Title, resolved.UserAgent);
    var until = DateTime.UtcNow.AddSeconds(25);
    while (DateTime.UtcNow < until)
    {
        host.ProcessPendingEvents();
        if (host.State is PlayerState.Playing or PlayerState.Paused or PlayerState.Error)
        {
            break;
        }

        Thread.Sleep(40);
    }

    Console.WriteLine("state=" + host.State);
    Console.WriteLine("error=" + (host.LastError ?? error));
    Console.WriteLine("duration=" + host.Duration);
    Console.WriteLine("format=" + host.FileFormat);
    return host.State is PlayerState.Playing or PlayerState.Paused ? 0 : 1;
}

if (args.Length > 0 && args[0] == "raw")
{
    RawPlay.Run(args.Length > 1 ? args[1] : "https://www.w3schools.com/html/mov_bbb.mp4", args.Length > 2 ? args[2] : null);
    return 0;
}

if (args.Length > 0 && args[0] == "dump")
{
    var tagFile = Path.Combine(Path.GetTempPath(), "yt-itags.txt");
    if (File.Exists(tagFile))
    {
        File.Delete(tagFile);
    }

    Dump.Player(args.Length > 1 ? args[1] : "WFSdNlLtu7I");
    if (File.Exists(tagFile))
    {
        var hls = File.ReadAllLines(tagFile).FirstOrDefault(line => line.StartsWith("hls\t", StringComparison.Ordinal));
        if (hls is not null)
        {
            Console.WriteLine("--- hls raw ---");
            RawPlay.Run(hls.Split('\t', 2)[1],
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15");
        }
    }

    return 0;
}

Console.WriteLine("resolve " + url);
YouTubePlayable? playable = null;
try
{
    playable = YouTubeCatalog.Resolve(url);
}
catch (Exception ex)
{
    Console.WriteLine("resolve threw: " + ex);
}

if (playable is null)
{
    Console.WriteLine("RESOLVE_NULL");
    return 2;
}

Console.WriteLine("id=" + playable.VideoId);
Console.WriteLine("title=" + playable.Title);
Console.WriteLine("kind=" + playable.Kind);
Console.WriteLine("ua=" + playable.UserAgent);
Console.WriteLine("audio=" + (playable.AudioUrl is null ? "none" : playable.AudioUrl[..Math.Min(120, playable.AudioUrl.Length)]));
Console.WriteLine("media=" + playable.MediaUrl[..Math.Min(180, playable.MediaUrl.Length)]);
Console.WriteLine("mediaHost=" + (Uri.TryCreate(playable.MediaUrl, UriKind.Absolute, out var mu) ? mu.Host + mu.AbsolutePath : "?"));

try
{
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(12) };
    using var req = new HttpRequestMessage(HttpMethod.Get, playable.MediaUrl);
    req.Headers.TryAddWithoutValidation("User-Agent", playable.UserAgent ?? "Mozilla/5.0");
    req.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com");
    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 2047);
    using var resp = http.Send(req);
    Console.WriteLine("http=" + (int)resp.StatusCode + " " + resp.StatusCode + " type=" + resp.Content.Headers.ContentType + " len=" + resp.Content.Headers.ContentLength);
    var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    Console.WriteLine("body=" + bytes.Length + " head=" + System.Text.Encoding.ASCII.GetString(bytes.Take(80).ToArray()).Replace('\n', ' ').Replace('\r', ' '));
}
catch (Exception ex)
{
    Console.WriteLine("http fail: " + ex.Message);
}

static int TryMpv(string label, string media, StreamKind kind, string? audio, string? ua)
{
    using var host = PlayerHost.CreateHeadless();
    var error = "";
    host.Error += (_, e) => error = e.Message;
    host.Open(media, kind, audio, "probe", ua);
    var until = DateTime.UtcNow.AddSeconds(18);
    while (DateTime.UtcNow < until)
    {
        host.ProcessPendingEvents();
        if (host.State is PlayerState.Playing or PlayerState.Paused or PlayerState.Error)
        {
            break;
        }

        Thread.Sleep(40);
    }

    Console.WriteLine(label + " state=" + host.State + " err=" + (host.LastError ?? error) + " dur=" + host.Duration + " fmt=" + host.FileFormat);
    return host.State is PlayerState.Playing or PlayerState.Paused ? 0 : 1;
}

var itag18 = playable.MediaUrl;
if (playable.MediaUrl.Contains("itag=", StringComparison.Ordinal))
{
    // probe will also try the catalog URL as-is
}

var videoOnly = TryMpv("video-only", playable.MediaUrl, playable.Kind, null, playable.UserAgent);
var withAudio = playable.AudioUrl is null
    ? -1
    : TryMpv("video+audio", playable.MediaUrl, playable.Kind, playable.AudioUrl, playable.UserAgent);
var chromeUa = TryMpv("chrome-ua", playable.MediaUrl, playable.Kind, null,
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

return videoOnly == 0 || withAudio == 0 || chromeUa == 0 ? 0 : 1;
