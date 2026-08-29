using Grok.Player.Core.Media;
using Grok.Player.Core.Subtitles;

static class CapDump
{
    public static int Run(string[] args)
    {
        var id = args.Length > 1 ? args[1] : "fFxbSyTAmBs";
        var want = args.Length > 2 ? args[2] : "de";
        var playable = YouTubeCatalog.Resolve("https://www.youtube.com/watch?v=" + id, null, "tr", want);
        Console.WriteLine("resolveCaption=" + (playable?.CaptionUrl ?? "none"));
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(playable?.CaptionUrl))
        {
            urls.Add(YouTubeCatalog.EnsureVtt(playable.CaptionUrl));
            urls.Add(YouTubeCatalog.WithTranslate(playable.CaptionUrl, want));
        }

        if (!string.IsNullOrWhiteSpace(playable?.CaptionUrl))
        {
            var raw = playable.CaptionUrl;
            urls.Add(raw);
            urls.Add(raw.Replace("&tlang=" + want, "").Replace("tlang=" + want + "&", ""));
            urls.Add(raw.Replace("fmt=vtt", "fmt=srv3"));
            urls.Add(raw.Replace("fmt=vtt", "fmt=json3"));
        }

        urls.Add(YouTubeCatalog.CaptionVttUrl(id, "tr:asr") + "&tlang=" + want);
        urls.Add(YouTubeCatalog.CaptionVttUrl(id, "tr:asr"));
        urls.Add(YouTubeCatalog.CaptionVttUrl(id, want));
        Console.WriteLine("urlCount=" + urls.Count);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
        foreach (var item in urls.Distinct())
        {
            try
            {
                using var response = http.GetAsync(item).GetAwaiter().GetResult();
                var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Console.WriteLine(
                    "status=" + (int)response.StatusCode +
                    " bytes=" + text.Length +
                    " header=" + (YouTubeCatalog.CaptionLanguageHeader(text) ?? "-") +
                    " head=" + text[..Math.Min(70, text.Length)].Replace('\n', '|') +
                    " url=" + item[..Math.Min(140, item.Length)]);
            }
            catch (Exception ex)
            {
                Console.WriteLine("err=" + ex.GetType().Name + " " + item[..Math.Min(80, item.Length)]);
            }
        }

        var loaded = StreamCaptionLoader.Load(id, want, playable?.CaptionUrl);
        Console.WriteLine("loaded=" + (loaded ?? "NONE"));
        if (loaded is not null)
        {
            var doc = SrtDocument.Load(StreamCaptionLoader.DocumentPath(loaded));
            Console.WriteLine("cues=" + doc.Cues.Count);
            if (doc.Cues.Count > 0)
            {
                Console.WriteLine("first=" + doc.Cues[0].Text.Replace('\n', '|'));
            }
        }

        if (!string.IsNullOrWhiteSpace(playable?.CaptionUrl))
        {
            foreach (var fmt in new[] { "srv3", "json3" })
            {
                var href = YouTubeCatalog.WithCaptionFormat(playable.CaptionUrl, fmt);
                var raw = YouTubeCatalog.DownloadCaption(href);
                var body = raw is null ? "" : System.Text.Encoding.UTF8.GetString(raw);
                Console.WriteLine(fmt + "bytes=" + body.Length);
                Console.WriteLine(fmt + "hasEfendim=" + body.Contains("efendim", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine(fmt + "hasHerr=" + (body.Contains("Herr", StringComparison.Ordinal) || body.Contains("mein", StringComparison.OrdinalIgnoreCase)));
                var sampleAt = body.IndexOf("<p ", StringComparison.OrdinalIgnoreCase);
                if (sampleAt < 0)
                {
                    sampleAt = body.IndexOf("\"utf8\"", StringComparison.Ordinal);
                }

                if (sampleAt >= 0)
                {
                    Console.WriteLine(fmt + "sample=" + body.Substring(sampleAt, Math.Min(280, body.Length - sampleAt)).Replace('\n', '|'));
                }
            }
        }

        return 0;
    }
}
