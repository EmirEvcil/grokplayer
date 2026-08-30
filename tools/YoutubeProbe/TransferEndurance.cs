using System.Diagnostics;
using System.Text.Json;
using Grok.Player.Core.Launch;
using Grok.Player.Core.Media;
using Grok.Player.Core.Player;

internal static class TransferEndurance
{
    public static int Run(string[] args)
    {
        var urls = ReadUrls(args);
        if (urls.Count == 0)
        {
            Console.WriteLine("NO_URLS");
            return 2;
        }

        var rows = new List<Row>();
        var failed = 0;
        foreach (var url in urls)
        {
            var row = Probe(url);
            rows.Add(row);
            if (row.Status != "ok")
            {
                failed++;
            }

            Console.WriteLine((row.Status == "ok" ? "OK  " : "FAIL") + "  " + row.Label + "  " + row.Detail);
        }

        var outPath = Path.Combine(Path.GetTempPath(), "grok-transfer-endurance.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("wrote " + outPath);
        Console.WriteLine("summary passed=" + (urls.Count - failed) + " failed=" + failed + " total=" + urls.Count);
        return failed == 0 ? 0 : 1;
    }

    private sealed record Row(
        string Url,
        string Title,
        string Label,
        string Status,
        string Detail,
        string Kind,
        string Media,
        string State,
        string? Format,
        double? Duration,
        string Error);

    private static Row Probe(string raw)
    {
        var started = Stopwatch.StartNew();
        try
        {
            if (!ExternalOpen.TryParse(raw, out var open) || string.IsNullOrWhiteSpace(open.Url))
            {
                return Fail(raw, "parse", started, "not a url");
            }

            var url = open.Url;
            var playable = StreamCatalog.Resolve(url) ?? YouTubeCatalog.Resolve(url);
            if (playable is null || string.IsNullOrWhiteSpace(playable.MediaUrl))
            {
                return Fail(url, "resolve", started, "catalog returned nothing");
            }

            if (playable.Kind != StreamKind.Live &&
                (playable.MediaUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                 playable.MediaUrl.Contains("hls", StringComparison.OrdinalIgnoreCase)))
            {
                playable = YouTubeCatalog.BindHlsRenditions(playable);
            }

            using var host = PlayerHost.CreateHeadless();
            string error = "";
            host.Error += (_, e) => error = e.Message;
            host.Open(
                playable.MediaUrl,
                playable.Kind,
                playable.AudioUrl,
                playable.Title,
                playable.UserAgent,
                referer: playable.Referer);
            var until = DateTime.UtcNow.AddSeconds(playable.Kind == StreamKind.Live ? 22 : 18);
            while (DateTime.UtcNow < until)
            {
                host.ProcessPendingEvents();
                if (host.State is PlayerState.Playing or PlayerState.Paused)
                {
                    if (playable.Kind == StreamKind.Live ||
                        host.Position.TotalSeconds > 0.15 ||
                        (host.Duration is { } d && d.TotalSeconds > 1))
                    {
                        break;
                    }
                }

                if (host.State == PlayerState.Error)
                {
                    break;
                }

                Thread.Sleep(40);
            }

            var ok = host.State is PlayerState.Playing or PlayerState.Paused &&
                     string.IsNullOrWhiteSpace(host.LastError ?? error);
            var detail = "kind=" + playable.Kind +
                         " state=" + host.State +
                         " fmt=" + (host.FileFormat ?? "?") +
                         " dur=" + host.Duration +
                         " pos=" + host.Position.TotalSeconds.ToString("0.##") +
                         " live=" + host.LiveWindow +
                         " ms=" + started.ElapsedMilliseconds +
                         " media=" + Trim(playable.MediaUrl);
            if (!ok)
            {
                detail += " err=" + (host.LastError ?? error);
            }

            return new Row(
                url,
                playable.Title,
                Label(url),
                ok ? "ok" : "fail",
                detail,
                playable.Kind.ToString(),
                playable.MediaUrl,
                host.State.ToString(),
                host.FileFormat,
                host.Duration?.TotalSeconds,
                host.LastError ?? error);
        }
        catch (Exception ex)
        {
            return Fail(raw, "exception", started, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static Row Fail(string url, string stage, Stopwatch started, string detail) =>
        new(
            url,
            "",
            Label(url),
            "fail",
            stage + " " + detail + " ms=" + started.ElapsedMilliseconds,
            "",
            "",
            "",
            null,
            null,
            detail);

    private static string Label(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase) + uri.AbsolutePath.TrimEnd('/');
        }
        catch
        {
            return url;
        }
    }

    private static string Trim(string url) =>
        url.Length <= 90 ? url : url[..90] + "…";

    private static List<string> ReadUrls(string[] args)
    {
        var list = new List<string>();
        foreach (var arg in args.Skip(1))
        {
            if (File.Exists(arg))
            {
                foreach (var line in File.ReadAllLines(arg))
                {
                    var text = line.Trim();
                    if (text.Length > 0 && !text.StartsWith('#') && !text.StartsWith("//"))
                    {
                        list.Add(text);
                    }
                }
            }
            else if (arg.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                     arg.StartsWith("grokplayer:", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(arg);
            }
        }

        return list;
    }
}
