using Grok.Player.Core.Media;
using Grok.Player.Core.Player;
using Grok.Player.Core.Preview;
using SkiaSharp;
using System.Diagnostics;

var url = args.Length > 0 ? args[0] : "https://www.youtube.com/watch?v=WFSdNlLtu7I";
if (args.Length > 0 && args[0] == "overlay-check")
{
    using var host = PlayerHost.CreateHeadless();
    host.SetAssOverlay("{\\an2\\bord2.5}METAMFETAMİN BASKINI");
    host.ProcessPendingEvents();
    host.SetAssOverlay(null);
    host.ProcessPendingEvents();
    Console.WriteLine("overlay-check ok");
    return 0;
}
if (args.Length > 0 && args[0] == "transfer")
    return TransferEndurance.Run(args);
if (args.Length > 1 && args[0] == "dump-html")
{
    var page = args[1];
    var referer = args.Length > 2 ? args[2] : page;
    var html = StreamCatalog.GetText(page, StreamCatalog.ChromeUa, referer) ?? "";
    Console.WriteLine("html=" + html.Length);
    var path = Path.Combine(Path.GetTempPath(), "grok-dump.html");
    File.WriteAllText(path, html);
    Console.WriteLine("dump=" + path);
    foreach (var embed in StreamCatalog.PlayerEmbedsIn(html, page).Take(8))
    {
        Console.WriteLine("embed " + embed);
    }
    foreach (var media in StreamCatalog.MediaUrlsIn(html).Take(12))
    {
        Console.WriteLine("media " + media);
    }
    foreach (var cap in StreamCatalog.SidecarCaptionsIn(html))
    {
        Console.WriteLine("cap " + cap.Language + " | " + cap.Name + " | " + cap.Url);
    }
    return 0;
}
if (args.Length > 0 && args[0] == "preview-vod")
{
    return PreviewVodCheck.Run(args);
}

if (args.Length > 0 && args[0] == "dl-check")
{
    return DownloadCheck.Run(args);
}

if (args.Length > 1 && args[0] == "catalog")
{
    var resolved = StreamCatalog.Resolve(args[1]);
    Console.WriteLine(resolved is null ? "RESOLVE_NULL" : $"id={resolved.VideoId}\nkind={resolved.Kind}\ntitle={resolved.Title}\nmedia={resolved.MediaUrl}\nreferer={resolved.Referer}\nformat={resolved.FormatHint}\nheaders={resolved.HttpHeaders}");
    return resolved is null ? 2 : 0;
}

if (args.Length > 1 && args[0] == "catalog-app")
{
    var resolved = StreamCatalog.Resolve(args[1]);
    if (resolved is null)
    {
        Console.WriteLine("RESOLVE_NULL");
        return 2;
    }

    using var host = PlayerHost.CreateHeadless();
    host.Open(resolved.MediaUrl, resolved.Kind, resolved.AudioUrl, resolved.Title, resolved.UserAgent, referer: resolved.Referer, formatHint: resolved.FormatHint, httpHeaders: resolved.HttpHeaders);
    var until = DateTime.UtcNow.AddSeconds(25);
    while (DateTime.UtcNow < until)
    {
        host.ProcessPendingEvents();
        if ((host.State is PlayerState.Playing or PlayerState.Paused) && host.Duration?.TotalSeconds > 0)
        {
            break;
        }
        if (host.State == PlayerState.Error)
        {
            break;
        }
        Thread.Sleep(40);
    }

    Console.WriteLine($"state={host.State} duration={host.Duration} position={host.Position} error={host.LastError}");
    if (host.Duration?.TotalMinutes > 5)
    {
        host.Seek(TimeSpan.FromMinutes(10));
        Thread.Sleep(1200);
        host.ProcessPendingEvents();
        Console.WriteLine($"afterSeek={host.Position} state={host.State}");
    }
    return host.Duration?.TotalMinutes > 5 ? 0 : 3;
}
if (args.Length > 0 && args[0] == "hdfilm-attach")
{
    var page = args.Length > 1 ? args[1] : "https://www.hdfilmcehennemi.now/bolum/breaking-bad-1-sezon-1-bolum-1-izle-16/";
    var resolved = StreamCatalog.Resolve(page);
    if (resolved is null)
    {
        Console.WriteLine("RESOLVE_NULL");
        return 2;
    }

    var harvested = Grok.Player.Core.Media.HlsCaptions.LoadAll(resolved.MediaUrl, resolved.UserAgent, resolved.MediaUrl);
    Console.WriteLine("harvest=" + harvested.Count);
    if (harvested.Count == 0)
    {
        return 3;
    }

    var vtt = harvested[0].File;
    Console.WriteLine("vtt=" + vtt + " bytes=" + new FileInfo(vtt).Length);

    using var host = PlayerHost.CreateHeadless();
    host.Open(
        resolved.MediaUrl,
        resolved.Kind,
        title: resolved.Title,
        userAgent: resolved.UserAgent,
        referer: resolved.Referer,
        formatHint: resolved.FormatHint,
        httpHeaders: resolved.HttpHeaders,
        subFile: vtt);
    var until = DateTime.UtcNow.AddSeconds(35);
    while (DateTime.UtcNow < until &&
           (host.State is not (PlayerState.Playing or PlayerState.Paused) || host.Duration?.TotalSeconds < 300))
    {
        host.ProcessPendingEvents();
        if (host.State == PlayerState.Error)
        {
            break;
        }

        Thread.Sleep(50);
    }

    Console.WriteLine("open-with-sub state=" + host.State + " duration=" + host.Duration + " sid=" + host.GetMpvString("sid") + " ext=" + string.Join(",", host.ListTracks("sub").Select(track => track.Id + ":" + track.External + ":" + track.Title)));
    host.Seek(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(1));
    var wait = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < wait && string.IsNullOrWhiteSpace(host.GetMpvString("sub-text")))
    {
        host.ProcessPendingEvents();
        Thread.Sleep(80);
    }

    Console.WriteLine("pending-text=[" + (host.GetMpvString("sub-text") ?? "").Replace('\n', ' ') + "] sid=" + host.GetMpvString("sid"));

    host.Pause();
    host.ProcessPendingEvents();
    Console.WriteLine("paused-set=" + host.SetSubtitleFile(vtt) + " sid=" + host.GetMpvString("sid") + " suberr=" + host.LastSubtitleError);
    wait = DateTime.UtcNow.AddSeconds(6);
    while (DateTime.UtcNow < wait && string.IsNullOrWhiteSpace(host.GetMpvString("sub-text")))
    {
        host.ProcessPendingEvents();
        Thread.Sleep(80);
    }

    Console.WriteLine("paused-text=[" + (host.GetMpvString("sub-text") ?? "").Replace('\n', ' ') + "] tracks=" + string.Join(",", host.ListTracks("sub").Select(track => track.Id + ":" + track.External + ":" + track.Selected + ":" + track.Title)));

    using var view = new Grok.Player.Core.Presentation.PlaybackViewModel(host);
    view.AddStream(
        Grok.Player.Core.Launch.ExternalOpen.ToProtocol(
            page,
            resolved.Title,
            resolved.Kind,
            referer: resolved.Referer,
            captions:
            [
                new Grok.Player.Core.Launch.ExternalCaption(harvested[0].Language, harvested[0].File, harvested[0].Name),
                harvested.Count > 1
                    ? new Grok.Player.Core.Launch.ExternalCaption(harvested[1].Language, harvested[1].File, harvested[1].Name)
                    : new Grok.Player.Core.Launch.ExternalCaption("en", harvested[0].File, "English")
            ]),
        play: true,
        resolved.Title);
    var waitCaps = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < waitCaps)
    {
        host.ProcessPendingEvents();
        Thread.Sleep(40);
    }

    host.Seek(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(1));
    wait = DateTime.UtcNow.AddSeconds(4);
    while (DateTime.UtcNow < wait)
    {
        host.ProcessPendingEvents();
        Thread.Sleep(40);
    }

    for (var i = 0; i < 6; i++)
    {
        view.CycleSubtitle();
        host.ProcessPendingEvents();
        Console.WriteLine("view " + view.SubtitleTrackLabel + " osd=[" + (view.OnScreenCaption ?? "").Replace('\n', ' ') + "]");
        if ((view.OnScreenCaption ?? "").Length > 0)
        {
            break;
        }
    }

    return 0;
}
if (args.Length > 0 && args[0] == "hdfilm-subs")
{
    var page = args.Length > 1 ? args[1] : "https://www.hdfilmcehennemi.now/bolum/breaking-bad-1-sezon-1-bolum-1-izle-16/";
    Console.WriteLine("page=" + page);
    var resolved = StreamCatalog.Resolve(page);
    if (resolved is null)
    {
        Console.WriteLine("RESOLVE_NULL");
        return 2;
    }

    Console.WriteLine("media=" + resolved.MediaUrl);
    Console.WriteLine("referer=" + resolved.Referer);
    Console.WriteLine("title=" + resolved.Title);
    var caps = StreamCatalog.SidecarCaptionsFromPage(page);
    Console.WriteLine("pageCaps=" + caps.Count);
    foreach (var cap in caps)
    {
        Console.WriteLine("  cap " + cap.Language + " " + cap.Name + " " + cap.Url);
    }

    var harvestWatch = Stopwatch.StartNew();
    var harvested = Grok.Player.Core.Media.HlsCaptions.LoadAll(resolved.MediaUrl, resolved.UserAgent, resolved.MediaUrl);
    Console.WriteLine("harvest=" + harvested.Count + " ms=" + harvestWatch.ElapsedMilliseconds);
    foreach (var item in harvested)
    {
        var body = File.ReadAllText(item.File);
        var cues = Grok.Player.Core.Subtitles.SrtDocument.Parse(body, compact: false).Cues;
        var around = cues.FirstOrDefault(cue => cue.Start >= TimeSpan.FromMinutes(12) && cue.Start < TimeSpan.FromMinutes(13));
        Console.WriteLine(
            "  file " + item.Name + " lang=" + item.Language +
            " cues=" + cues.Count +
            " at12=[" + (around?.Text ?? "").Replace('\n', ' ') + "]" +
            " path=" + item.File);
    }

    using var host = PlayerHost.CreateHeadless();
    using var view = new Grok.Player.Core.Presentation.PlaybackViewModel(host);
    view.AddStream(
        Grok.Player.Core.Launch.ExternalOpen.ToProtocol(
            page,
            resolved.Title,
            resolved.Kind,
            referer: resolved.Referer,
            captions: caps),
        play: true,
        resolved.Title);

    var until = DateTime.UtcNow.AddSeconds(40);
    while (DateTime.UtcNow < until)
    {
        host.ProcessPendingEvents();
        if ((host.State is PlayerState.Playing or PlayerState.Paused) && host.Duration?.TotalSeconds > 300)
        {
            break;
        }

        if (host.State == PlayerState.Error)
        {
            break;
        }

        Thread.Sleep(50);
    }

    Console.WriteLine("state=" + host.State + " duration=" + host.Duration + " error=" + host.LastError);
    var harvestUntil = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < harvestUntil)
    {
        host.ProcessPendingEvents();
        Thread.Sleep(50);
    }

    host.Seek(TimeSpan.FromMinutes(12));
    var seekUntil = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < seekUntil)
    {
        host.ProcessPendingEvents();
        Thread.Sleep(50);
    }

    Console.WriteLine("pos=" + host.Position);
    var audios = host.ListTracks("audio");
    Console.WriteLine("audios=" + audios.Count);
    foreach (var track in audios)
    {
        Console.WriteLine("  audio id=" + track.Id + " lang=" + track.Language + " title=" + track.Title + " sel=" + track.Selected);
    }

    var tracks = host.ListTracks("sub");
    Console.WriteLine("subs=" + tracks.Count);
    foreach (var track in tracks)
    {
        Console.WriteLine("  sub id=" + track.Id + " lang=" + track.Language + " title=" + track.Title + " sel=" + track.Selected + " ext=" + track.External);
    }

    Console.WriteLine("label0=" + view.SubtitleTrackLabel);
    var pickedTurkce = false;
    for (var i = 0; i < 8; i++)
    {
        view.CycleSubtitle();
        host.ProcessPendingEvents();
        Thread.Sleep(400);
        host.ProcessPendingEvents();
        var text = host.GetMpvString("sub-text") ?? "";
        Console.WriteLine(
            "cycle=" + view.SubtitleTrackLabel +
            " sid=" + host.GetMpvString("sid") +
            " stitle=" + host.GetMpvString("current-tracks/sub/title") +
            " slang=" + host.GetMpvString("current-tracks/sub/lang") +
            " vis=" + host.GetMpvString("sub-visibility") +
            " text=[" + text.Replace('\n', ' ') + "]");
        if (view.SubtitleTrackLabel.Contains("Türkçe", StringComparison.OrdinalIgnoreCase) ||
            view.SubtitleTrackLabel.Contains("Turkish", StringComparison.OrdinalIgnoreCase))
        {
            pickedTurkce = true;
            break;
        }
    }

    if (!pickedTurkce)
    {
        Console.WriteLine("NO_TURKCE_CHOICE");
        return 4;
    }

    if (harvested.Count > 0)
    {
        var play = Grok.Player.Core.Subtitles.StreamCaptionLoader.PlaySidecar(harvested[0].File);
        Console.WriteLine("direct-file=" + harvested[0].File + " exists=" + File.Exists(harvested[0].File));
        Console.WriteLine("direct-play=" + play + " exists=" + File.Exists(play));
        Console.WriteLine("direct-srt-set=" + host.SetSubtitleFile(harvested[0].File));
        host.ProcessPendingEvents();
        Thread.Sleep(400);
        host.ProcessPendingEvents();
        Console.WriteLine(
            "srt sid=" + host.GetMpvString("sid") +
            " text=[" + (host.GetMpvString("sub-text") ?? "").Replace('\n', ' ') + "]");
        Console.WriteLine("direct-set=" + host.SetSubtitleFile(play));
        host.ProcessPendingEvents();
        Thread.Sleep(400);
        host.ProcessPendingEvents();
        Console.WriteLine(
            "direct sid=" + host.GetMpvString("sid") +
            " ext=" + string.Join(",", host.ListTracks("sub").Select(track => track.Id + ":" + track.External + ":" + track.Title)) +
            " text=[" + (host.GetMpvString("sub-text") ?? "").Replace('\n', ' ') + "]");
    }

    foreach (var seconds in new[] { 12 * 60 + 1, 12 * 60 + 4, 12 * 60 + 10, 12 * 60 + 20, 12 * 60 + 40 })
    {
        host.Seek(TimeSpan.FromSeconds(seconds));
        var wait = DateTime.UtcNow.AddSeconds(6);
        string sample = "";
        while (DateTime.UtcNow < wait)
        {
            host.ProcessPendingEvents();
            sample = host.GetMpvString("sub-text") ?? "";
            if (!string.IsNullOrWhiteSpace(sample))
            {
                break;
            }

            Thread.Sleep(80);
        }

        Console.WriteLine(
            "at=" + host.Position +
            " sid=" + host.GetMpvString("sid") +
            " stitle=" + host.GetMpvString("current-tracks/sub/title") +
            " text=[" + sample.Replace('\n', ' ') + "]");
    }

    Console.WriteLine("codec=" + host.GetMpvString("current-tracks/sub/codec"));
    Console.WriteLine("decoder=" + host.GetMpvString("current-tracks/sub/decoder-desc"));
    Console.WriteLine("ass=[" + (host.GetMpvString("sub-text/ass") ?? "").Replace('\n', ' ') + "]");
    try
    {
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 GrokPlayer/1.0");
        if (!string.IsNullOrWhiteSpace(resolved.Referer))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", resolved.Referer);
        }

        var master = http.GetStringAsync(resolved.MediaUrl).GetAwaiter().GetResult();
        foreach (var line in master.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Contains("TYPE=SUBTITLES", StringComparison.OrdinalIgnoreCase) ||
                line.Contains(".vtt", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("tur", StringComparison.OrdinalIgnoreCase) && line.StartsWith('#'))
            {
                Console.WriteLine("hls " + line);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("hls-error=" + ex.Message);
    }

    host.Seek(TimeSpan.FromMinutes(12));
    host.Play();
    var playUntil = DateTime.UtcNow.AddSeconds(18);
    var last = "";
    while (DateTime.UtcNow < playUntil)
    {
        host.ProcessPendingEvents();
        var now = host.GetMpvString("sub-text") ?? "";
        if (now != last)
        {
            Console.WriteLine("play " + host.Position + " [" + now.Replace('\n', ' ') + "]");
            last = now;
        }

        Thread.Sleep(80);
    }

    return 0;
}
if (args.Length > 0 && args[0] == "ytdiag")
{
    foreach (var target in args.Skip(1).DefaultIfEmpty("https://www.youtube.com/watch?v=dQw4w9wgBcQ"))
    {
        var watch = Stopwatch.StartNew();
        Console.WriteLine(YouTubeCatalog.Diagnose(target));
        var resolved = YouTubeCatalog.Resolve(target);
        Console.WriteLine(
            (resolved is null ? "NULL" : "OK  ") +
            "  " + target +
            "  ms=" + watch.ElapsedMilliseconds +
            (resolved is null ? "" : " kind=" + resolved.Kind + " media=" + resolved.MediaUrl[..Math.Min(90, resolved.MediaUrl.Length)]));
    }
    return 0;
}
if (args.Length > 0 && args[0] == "cache-preview")
    return await CachePreviewProbe.Run(args.Length > 1 ? args[1] : "https://vs-dash-ww-rd-live.akamaized.net/pl/testcard2020/avc-mobile.m3u8");
if (args.Length > 0 && args[0] == "live-preview")
{
    var target = args.Length > 1 ? args[1] : "https://vs-dash-ww-rd-live.akamaized.net/pl/testcard2020/avc-mobile.m3u8";
    using var host = PlayerHost.CreateHeadless();
    using var buffer = new LivePreviewBuffer();
    host.Open(target, StreamKind.Live);
    var wait = Stopwatch.StartNew();
    while (wait.Elapsed < TimeSpan.FromSeconds(20) &&
           (host.State is not PlayerState.Playing || host.Position.TotalSeconds <= 0))
    {
        if (host.State == PlayerState.Error) break;
        await Task.Delay(100);
    }
    Console.WriteLine($"live state={host.State} position={host.Position} readyMs={wait.ElapsedMilliseconds}");
    var hits = 0;
    for (var i = 0; i < 10; i++)
    {
        var watch = Stopwatch.StartNew();
        var pending = buffer.CaptureAsync(file => host.TryCaptureVideo(file, includeWindow: false), () => host.Position);
        var dispatch = watch.Elapsed.TotalMilliseconds;
        var ok = await pending;
        var elapsed = watch.ElapsedMilliseconds;
        var point = host.Position;
        var lookup = Stopwatch.StartNew();
        string? image = null;
        for (var n = 0; n < 1000; n++) image = buffer.GetFrame(point);
        lookup.Stop();
        using var bitmap = image is null ? null : SKBitmap.Decode(image);
        if (ok && bitmap is not null) hits++;
        Console.WriteLine($"sample={i} ok={ok} dispatchMs={dispatch:F2} captureMs={elapsed} size={bitmap?.Width}x{bitmap?.Height} lookup1000Ms={lookup.Elapsed.TotalMilliseconds:F2} retained={buffer.Count}");
        await Task.Delay(1000);
    }
    Console.WriteLine($"live-preview usable={hits}/10");
    return hits >= 8 ? 0 : 3;
}
if (args.Length > 0 && args[0] == "preview-match")
{
    var target = args.Length > 1 ? args[1] : "https://www.youtube.com/watch?v=BjHswMbm5h4";
    var resolved = YouTubeCatalog.Resolve(target);
    if (resolved is null || StoryboardSpec.Parse(resolved.StoryboardSpec) is not { } spec) return 2;
    resolved = YouTubeCatalog.BindHlsRenditions(resolved);
    var best = spec.BestLevel!;
    var duration = TimeSpan.FromMilliseconds(Math.Max(1, best.IntervalMs) * best.Count);
    using var atlas = new StoryboardAtlas(spec, duration);
    using var decoder = SeekPreviewEngine.Create();
    decoder.Prepare(resolved.MediaUrl);
    foreach (var ratio in new[] { 0.23, 0.51, 0.77 })
    {
        var hover = TimeSpan.FromSeconds(duration.TotalSeconds * ratio);
        atlas.TryGetOrFetchBest(hover, out var board);
        var cell = atlas.FrameTime(hover);
        Console.WriteLine($"hover={hover} cell={cell} interval={atlas.IntervalSeconds:0.###}");
        foreach (var offset in new[] { 0d, atlas.IntervalSeconds * 0.25, atlas.IntervalSeconds * 0.5, atlas.IntervalSeconds * 0.75 })
        {
            var at = cell + TimeSpan.FromSeconds(offset);
            var image = decoder.CaptureExact(at);
            Console.WriteLine($"  at={at} offset={offset:0.###} diff={ImageDifference(board, image):0.0000}");
        }
    }
    return 0;
}
if (args.Length > 0 && args[0] == "preview")
{
    var target = args.Length > 1 ? args[1] : "https://www.youtube.com/watch?v=BjHswMbm5h4";
    var resolved = YouTubeCatalog.Resolve(target);
    if (resolved is null || StoryboardSpec.Parse(resolved.StoryboardSpec) is not { } spec)
    {
        Console.WriteLine("PREVIEW_RESOLVE_NULL");
        return 2;
    }

    resolved = YouTubeCatalog.BindHlsRenditions(resolved);
    Console.WriteLine("id=" + resolved.VideoId);
    Console.WriteLine("title=" + resolved.Title);
    foreach (var level in spec.Levels)
    {
        var sheets = (int)Math.Ceiling(level.Count / (double)level.FramesPerSheet);
        Console.WriteLine($"level={level.Index} size={level.Width}x{level.Height} count={level.Count} intervalMs={level.IntervalMs} sheets={sheets}");
    }

    var best = spec.BestLevel!;
    var duration = TimeSpan.FromMilliseconds(Math.Max(1, best.IntervalMs) * best.Count);
    var points = new[] { 0.17, 0.51, 0.83 }.Select(value =>
        TimeSpan.FromSeconds(duration.TotalSeconds * value)).ToArray();
    using var atlas = new StoryboardAtlas(spec, duration);
    foreach (var point in points)
    {
        var watch = Stopwatch.StartNew();
        var ok = atlas.TryGetOrFetch(point, out var path);
        watch.Stop();
        using var bitmap = ok ? SKBitmap.Decode(path) : null;
        Console.WriteLine($"atlas at={point} ok={ok} ms={watch.ElapsedMilliseconds} size={bitmap?.Width}x{bitmap?.Height}");
        watch.Restart();
        var bestOk = atlas.TryGetOrFetchBest(point, out var bestPath);
        watch.Stop();
        using var bestBitmap = bestOk ? SKBitmap.Decode(bestPath) : null;
        Console.WriteLine($"best  at={point} ok={bestOk} ms={watch.ElapsedMilliseconds} size={bestBitmap?.Width}x{bestBitmap?.Height}");
    }

    using (var pipelineAtlas = new StoryboardAtlas(spec, duration))
    using (var scheduler = new SeekPreviewScheduler(new ProbeRenderer(), atlasUpgradeDelayMs: 100))
    {
        var frames = new List<string>();
        using var highReady = new ManualResetEventSlim(false);
        var pipelineWatch = Stopwatch.StartNew();
        scheduler.FrameReady += (_, path) =>
        {
            using var image = SKBitmap.Decode(path);
            var item = $"{pipelineWatch.ElapsedMilliseconds}ms:{image?.Width}x{image?.Height}";
            lock (frames) frames.Add(item);
            if ((image?.Width ?? 0) >= 320) highReady.Set();
        };
        scheduler.SetMedia(resolved.MediaUrl, duration, prefetch: false);
        scheduler.SetAtlas(pipelineAtlas);
        scheduler.Request(points[1]);
        highReady.Wait(TimeSpan.FromSeconds(3));
        lock (frames) Console.WriteLine("pipeline=" + string.Join(",", frames));
    }

    using var decoder = SeekPreviewEngine.Create();
    var prepare = Stopwatch.StartNew();
    decoder.Prepare(resolved.MediaUrl);
    prepare.Stop();
    Console.WriteLine("decoderPrepareMs=" + prepare.ElapsedMilliseconds);
    foreach (var point in points)
    {
        var watch = Stopwatch.StartNew();
        var path = decoder.Capture(point);
        watch.Stop();
        using var bitmap = path is not null ? SKBitmap.Decode(path) : null;
        Console.WriteLine($"decoder at={point} ok={path is not null} ms={watch.ElapsedMilliseconds} size={bitmap?.Width}x{bitmap?.Height}");
    }
    return 0;
}

if (args.Length > 0 && args[0] == "caps")
{
    return CapDump.Run(args);
}
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

static double ImageDifference(string? leftPath, string? rightPath)
{
    if (string.IsNullOrWhiteSpace(leftPath) || string.IsNullOrWhiteSpace(rightPath)) return 1;
    using var leftSource = SKBitmap.Decode(leftPath);
    using var rightSource = SKBitmap.Decode(rightPath);
    if (leftSource is null || rightSource is null) return 1;
    using var left = leftSource.Resize(new SKImageInfo(64, 36), new SKSamplingOptions(SKFilterMode.Linear));
    using var right = rightSource.Resize(new SKImageInfo(64, 36), new SKSamplingOptions(SKFilterMode.Linear));
    if (left is null || right is null) return 1;
    double sum = 0;
    for (var y = 0; y < left.Height; y++)
    for (var x = 0; x < left.Width; x++)
    {
        var a = left.GetPixel(x, y);
        var b = right.GetPixel(x, y);
        sum += Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
    }
    return sum / (left.Width * left.Height * 3d * 255d);
}

file sealed class ProbeRenderer : ISeekPreviewRenderer
{
    public void Prepare(string path) { }
    public string? Capture(TimeSpan time) => null;
    public void Reset() { }
    public void Dispose() { }
}
