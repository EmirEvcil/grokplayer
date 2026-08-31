using Grok.Player.Core.Download;
using Grok.Player.Core.Launch;
using Grok.Player.Core.Media;
using Grok.Player.Core.Subtitles;

static class DownloadCheck
{
    public static int Run(string[] args)
    {
        var target = args.Length > 1 ? args[1] : "all";
        if (target is "all" or "youtube")
        {
            YouTube();
        }

        if (target is "all" or "hdfilm")
        {
            Hdfilm();
        }

        if (target is "all" or "daily")
        {
            Daily();
        }

        return 0;
    }

    private static void YouTube()
    {
        const string page = "https://www.youtube.com/watch?v=EmkUwzOG8HU";
        Console.WriteLine("=== youtube ===");
        var playable = YouTubeCatalog.Resolve(page, null, null, "de");
        Console.WriteLine("resolveCaption=" + (playable?.CaptionUrl ?? "none"));
        Console.WriteLine("resolveSub=" + (playable?.SubLang ?? "none"));
        var listed = YouTubeCatalog.ListCaptions(page);
        Console.WriteLine("listed=" + listed.Count);
        foreach (var cap in listed)
        {
            Console.WriteLine("  track " + cap.Language + " | " + cap.Name + " | translate=" +
                              YouTubeCatalog.CaptionUrlIsTranslate(cap.Url));
        }

        var translated = playable?.CaptionUrl;
        if (string.IsNullOrWhiteSpace(translated) || !YouTubeCatalog.CaptionUrlIsTranslate(translated))
        {
            var source = listed.FirstOrDefault(item => item.Language.Contains("asr", StringComparison.OrdinalIgnoreCase)).Url
                         ?? listed.FirstOrDefault()?.Url;
            if (!string.IsNullOrWhiteSpace(source))
            {
                translated = YouTubeCatalog.WithTranslate(source, "de");
            }
        }

        Console.WriteLine("deUrl=" + translated);
        var loaded = StreamCaptionLoader.Load("EmkUwzOG8HU", "de", translated);
        Console.WriteLine("deFile=" + (loaded ?? "FAIL"));
        if (!string.IsNullOrWhiteSpace(loaded) && File.Exists(loaded))
        {
            var text = File.ReadAllText(StreamCaptionLoader.DocumentPath(loaded));
            Console.WriteLine("deHeader=" + (YouTubeCatalog.CaptionLanguageHeader(text) ?? "none"));
            Console.WriteLine("deSample=" + FirstCue(text));
        }

        var folder = Path.Combine(Path.GetTempPath(), "grok-dl-yt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, "yt-de-check.mp4");
        File.WriteAllBytes(output, [0, 0, 0, 1, 2, 3, 4, 5]);
        var job = new DownloadJob(page, "yt-de-check", output)
        {
            SubLang = "de",
            CaptionUrl = translated
        };
        foreach (var cap in listed)
        {
            DownloadManager.AddCaption(job, cap);
        }

        DownloadManager.AddSelectedCaption(job);
        DownloadManager.AttachCaptions(job);
        Console.WriteLine("ytJobCaps=" + job.Captions.Count);
        foreach (var cap in job.Captions)
        {
            Console.WriteLine("  jobCap " + cap.Language + " | " + cap.Name);
        }

        DumpSidecars(output);
        TryDeleteTree(folder);
    }

    private static void Hdfilm()
    {
        const string page = "https://www.hdfilmcehennemi.now/bolum/breaking-bad-1-sezon-1-bolum-1-izle-16/";
        Console.WriteLine("=== hdfilm ===");
        var playable = StreamCatalog.Resolve(page);
        Console.WriteLine(playable is null
            ? "RESOLVE_NULL"
            : "title=" + playable.Title + "\nmedia=" + playable.MediaUrl + "\nreferer=" + playable.Referer +
              "\nformat=" + playable.FormatHint);
        if (playable is null)
        {
            return;
        }

        var fromPage = StreamCatalog.SidecarCaptionsFromPage(page);
        Console.WriteLine("pageCaps=" + fromPage.Count);
        foreach (var cap in fromPage)
        {
            Console.WriteLine("  page " + cap.Language + " | " + cap.Name);
        }

        var master = StreamCatalog.GetText(playable.MediaUrl, playable.UserAgent, playable.Referer ?? playable.MediaUrl);
        Console.WriteLine("masterLen=" + (master?.Length ?? 0) + " isMaster=" +
                          (master is not null && HlsPlaylist.IsMaster(master)));
        if (!string.IsNullOrWhiteSpace(master))
        {
            foreach (var sub in HlsPlaylist.Subtitles(master, playable.MediaUrl))
            {
                Console.WriteLine("  hls " + sub.Language + " | " + sub.Name + " forced=" + sub.Forced + " | " + sub.Url);
            }

            var loaded = HlsCaptions.LoadAll(playable.MediaUrl, playable.UserAgent, playable.MediaUrl);
            Console.WriteLine("loadAll=" + loaded.Count);
            foreach (var item in loaded)
            {
                Console.WriteLine("  loaded " + item.Language + " | " + item.Name + " cues=" +
                                  SrtDocument.Parse(File.ReadAllText(item.File), compact: false).Cues.Count);
            }
        }

        var folder = Path.Combine(Path.GetTempPath(), "grok-dl-bb-" + Guid.NewGuid().ToString("N")[..8]);
        using var manager = new DownloadManager(new DownloadSettings { Folder = folder, MaxParallel = 1, MaxHeight = 360 });
        var job = manager.Enqueue(page, playable.Title, start: true, captions: fromPage);
        Wait(job, 25);
        Console.WriteLine("bbState=" + job.State + " err=" + (job.Error ?? "") + " bytes=" + job.Bytes +
                          " jobCaps=" + job.Captions.Count);
        foreach (var cap in job.Captions)
        {
            Console.WriteLine("  jobCap " + cap.Language + " | " + cap.Name);
        }

        if (job.Captions.Count > 0)
        {
            var dummy = Path.ChangeExtension(job.OutputPath, ".mp4");
            if (!File.Exists(dummy))
            {
                File.WriteAllBytes(dummy, [0, 0, 0, 1]);
            }

            job.OutputPath = dummy;
            DownloadManager.AttachCaptions(job);
        }

        DumpSidecars(job.OutputPath);
        if (job.State == DownloadState.Running)
        {
            manager.Cancel(job.Id);
        }

        TryDeleteTree(folder);
    }

    private static void Daily()
    {
        const string page = "https://www.dailymotion.com/video/xap6qz2";
        Console.WriteLine("=== dailymotion ===");
        var playable = StreamCatalog.Resolve(page);
        Console.WriteLine(playable is null
            ? "RESOLVE_NULL"
            : "title=" + playable.Title + "\nmedia=" + playable.MediaUrl + "\nreferer=" + playable.Referer);
        if (playable is null)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, playable.MediaUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", playable.UserAgent ?? StreamCatalog.ChromeUa);
        request.Headers.TryAddWithoutValidation("Referer", playable.Referer ?? "https://www.dailymotion.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.dailymotion.com");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var response = http.Send(request);
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Console.WriteLine("httpStatus=" + (int)response.StatusCode + " len=" + body.Length + " head=" +
                          body[..Math.Min(80, body.Length)].Replace('\n', ' '));

        var viaCatalog = StreamCatalog.GetText(playable.MediaUrl, playable.UserAgent, playable.Referer);
        Console.WriteLine("catalogGet=" + (viaCatalog is null ? "null" : viaCatalog.Length + " " + viaCatalog[..Math.Min(20, viaCatalog.Length)]));

        var folder = Path.Combine(Path.GetTempPath(), "grok-dl-dm-" + Guid.NewGuid().ToString("N")[..8]);
        using var manager = new DownloadManager(new DownloadSettings { Folder = folder, MaxParallel = 1, MaxHeight = 360 });
        var job = manager.Enqueue(page, playable.Title, start: true);
        Wait(job, 45);
        Console.WriteLine("dmState=" + job.State + " err=" + (job.Error ?? "") + " bytes=" + job.Bytes);
        if (job.State == DownloadState.Running)
        {
            manager.Cancel(job.Id);
            Console.WriteLine("dmCanceled after first progress");
        }

        TryDeleteTree(folder);
    }

    private static void Wait(DownloadJob job, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && job.State is DownloadState.Queued or DownloadState.Running)
        {
            Thread.Sleep(200);
            if (job.Bytes > 250_000 && job.State == DownloadState.Running)
            {
                break;
            }
        }
    }

    private static void DumpSidecars(string output)
    {
        var dir = Path.GetDirectoryName(output);
        var stem = Path.GetFileNameWithoutExtension(output);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            Console.WriteLine("sidecars=none");
            return;
        }

        foreach (var file in Directory.GetFiles(dir, stem + "*"))
        {
            var info = new FileInfo(file);
            Console.WriteLine("file " + info.Name + " " + info.Length);
        }
    }

    private static string FirstCue(string text)
    {
        var parsed = SrtDocument.Parse(text, compact: false);
        return parsed.Cues.Count == 0 ? "" : parsed.Cues[0].Text.Replace('\n', ' ');
    }

    private static void TryDeleteTree(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
        catch (Exception)
        {
        }
    }
}
