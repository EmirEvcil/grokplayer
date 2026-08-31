using Grok.Player.Core.Launch;
using Grok.Player.Core.Media;
using Grok.Player.Core.Playlist;
using Grok.Player.Core.Subtitles;

namespace Grok.Player.Core.Download;

public sealed class DownloadManager : IDisposable
{
    private readonly object _gate = new();
    private readonly List<DownloadJob> _jobs = [];
    private readonly Dictionary<string, CancellationTokenSource> _tokens = [];
    private readonly HttpClient _http;
    private bool _disposed;
    private long _lastProgress;

    public DownloadManager(DownloadSettings? settings = null, HttpMessageHandler? handler = null)
    {
        Settings = settings ?? DownloadSettings.Load();
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromMinutes(5) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
    }

    public DownloadSettings Settings { get; }

    public event Action? Changed;

    public IReadOnlyList<DownloadJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return _jobs.ToArray();
            }
        }
    }

    public static bool IsVod(PlaylistItem item)
    {
        if (item.Kind != PlaylistKind.Stream)
        {
            return false;
        }

        if (item.StreamKind == StreamKind.Live)
        {
            return false;
        }

        if (item.StreamKind == StreamKind.Vod || YouTubeCatalog.IsWatchUrl(item.Path))
        {
            return true;
        }

        var ext = StreamProbe.Extension(item.Path);
        return ext is ".mp4" or ".mkv" or ".webm" or ".mov" or ".m4v" or ".avi";
    }

    public DownloadJob Enqueue(
        string sourceUrl,
        string title,
        bool start,
        string? audioLang = null,
        int maxHeight = 0,
        string? subLang = null,
        string? captionUrl = null,
        IEnumerable<ExternalCaption>? captions = null)
    {
        return EnqueueCore(sourceUrl, title, start, audioLang, maxHeight, subLang, captionUrl, captions);
    }

    public DownloadJob EnqueueCore(
        string sourceUrl,
        string title,
        bool start,
        string? audioLang,
        int maxHeight = 0,
        string? subLang = null,
        string? captionUrl = null,
        IEnumerable<ExternalCaption>? captions = null)
    {
        Directory.CreateDirectory(Settings.Folder);
        title = StreamCatalog.UsableTitle(title, sourceUrl);
        var name = DownloadJob.SafeFileName(title);
        var path = UniquePath(Path.Combine(Settings.Folder, name + Settings.ContainerExtension));
        var job = new DownloadJob(sourceUrl, title, path)
        {
            AudioLang = audioLang,
            MaxHeight = maxHeight > 0 ? maxHeight : Settings.MaxHeight,
            SubLang = subLang,
            CaptionUrl = captionUrl
        };
        if (captions is not null)
        {
            job.Captions.AddRange(captions);
        }
        lock (_gate)
        {
            _jobs.Add(job);
        }

        Raise();
        if (start)
        {
            Start(job.Id, manual: true);
        }
        else
        {
            Pump();
        }

        return job;
    }

    public int EnqueueAll(IEnumerable<(string Url, string Title)> items, string? audioLang = null, int maxHeight = 0)
    {
        var count = 0;
        foreach (var item in items)
        {
            Enqueue(item.Url, item.Title, start: false, audioLang, maxHeight);
            count++;
        }

        Pump();
        return count;
    }

    public void Start(string id, bool manual = true)
    {
        DownloadJob? job;
        lock (_gate)
        {
            job = _jobs.FirstOrDefault(item => item.Id == id);
            if (job is null || job.State is DownloadState.Running or DownloadState.Completed)
            {
                return;
            }

            job.ManualStart = manual;
            job.State = DownloadState.Running;
            job.Error = null;
        }

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_tokens.TryGetValue(id, out var old))
            {
                old.Cancel();
                old.Dispose();
            }

            _tokens[id] = cts;
        }

        Raise();
        new Thread(() => Run(job, cts.Token))
        {
            IsBackground = true,
            Name = "vod-download",
            Priority = ThreadPriority.BelowNormal
        }.Start();
    }

    public void Pause(string id)
    {
        lock (_gate)
        {
            if (_jobs.FirstOrDefault(item => item.Id == id) is { State: DownloadState.Running } job)
            {
                job.State = DownloadState.Paused;
            }

            if (_tokens.TryGetValue(id, out var cts))
            {
                cts.Cancel();
            }
        }

        Raise();
    }

    public void Cancel(string id)
    {
        lock (_gate)
        {
            if (_jobs.FirstOrDefault(item => item.Id == id) is { } job &&
                job.State is not DownloadState.Completed)
            {
                job.State = DownloadState.Canceled;
            }

            if (_tokens.TryGetValue(id, out var cts))
            {
                cts.Cancel();
            }
        }

        Raise();
        Pump();
    }

    public void Delete(string id)
    {
        DownloadJob? job;
        var running = false;
        lock (_gate)
        {
            job = _jobs.FirstOrDefault(item => item.Id == id);
            if (job is null)
            {
                return;
            }

            job.DeleteRequested = true;
            running = job.State == DownloadState.Running &&
                      _tokens.TryGetValue(id, out var cts);
            if (running)
            {
                _tokens[id].Cancel();
            }
            else
            {
                if (_tokens.Remove(id, out var leftover))
                {
                    leftover.Dispose();
                }

                _jobs.Remove(job);
            }
        }

        if (!running)
        {
            TryDeleteOutputs(job.OutputPath);
        }

        Raise();
        Pump();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            foreach (var cts in _tokens.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _tokens.Clear();
        }

        _http.Dispose();
    }

    private void Pump()
    {
        List<DownloadJob> start = [];
        lock (_gate)
        {
            var running = _jobs.Count(item => item.State == DownloadState.Running);
            var room = Math.Max(0, Settings.MaxParallel - running);
            foreach (var job in _jobs)
            {
                if (room <= 0)
                {
                    break;
                }

                if (job.State == DownloadState.Queued)
                {
                    start.Add(job);
                    room--;
                }
            }
        }

        foreach (var job in start)
        {
            Start(job.Id, manual: false);
        }
    }

    private void Run(DownloadJob job, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);
            var source = job.SourceUrl;
            string? userAgent = null;
            string? formatHint = null;
            if (YouTubeCatalog.IsWatchUrl(source) || StreamCatalog.LooksResolvable(source))
            {
                var playable = YouTubeCatalog.IsWatchUrl(source)
                    ? YouTubeCatalog.Resolve(source, null, job.AudioLang, job.SubLang)
                    : StreamCatalog.Resolve(source, job.AudioLang, job.SubLang);
                if (playable is null || playable.Kind == StreamKind.Live)
                {
                    Fail(job, playable is null ? "Stream unavailable" : "Live streams cannot be downloaded");
                    return;
                }

                HarvestCaptions(job, source);
                source = playable.MediaUrl;
                userAgent = playable.UserAgent;
                formatHint = playable.FormatHint;
                job.Referer = PageReferer(playable.Referer, job.SourceUrl);
                job.AudioLang ??= playable.AudioLang;
                job.CaptionUrl ??= playable.CaptionUrl;
                if (string.IsNullOrWhiteSpace(job.SubLang))
                {
                    job.SubLang = playable.SubLang;
                }
            }

            job.Referer = PageReferer(job.Referer, job.SourceUrl);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var hls = LooksLikeHls(source) ||
                      string.Equals(formatHint, "hls", StringComparison.OrdinalIgnoreCase);
            if (hls)
            {
                DownloadHls(job, source, userAgent, token);
            }
            else
            {
                DownloadFile(job, source, userAgent, token);
                if (job.State == DownloadState.Running && LooksLikePlaylistFile(job.OutputPath))
                {
                    TryDelete(job.OutputPath);
                    DownloadHls(job, source, userAgent, token);
                }
            }

            if (job.State == DownloadState.Running && LooksLikeStubFile(job.OutputPath))
            {
                Fail(job, "Download did not produce a video file");
                TryDeleteOutputs(job.OutputPath);
                return;
            }

            if (job.State == DownloadState.Running)
            {
                AttachCaptions(job);
            }

            lock (_gate)
            {
                if (job.State == DownloadState.Running)
                {
                    job.State = DownloadState.Completed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (job.State == DownloadState.Running)
                {
                    job.State = DownloadState.Paused;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(job, ex.Message);
        }
        finally
        {
            Finish(job);
        }
    }

    private void Finish(DownloadJob job)
    {
        lock (_gate)
        {
            if (_tokens.Remove(job.Id, out var cts))
            {
                cts.Dispose();
            }

            if (job.DeleteRequested)
            {
                _jobs.Remove(job);
            }
        }

        if (job.DeleteRequested)
        {
            TryDeleteOutputs(job.OutputPath);
        }

        Raise();
        Pump();
    }

    private void DownloadHls(DownloadJob job, string url, string? userAgent, CancellationToken token)
    {
        var master = GetString(url, userAgent, job.Referer, token);
        var mediaUrl = url;
        string? audioUrl = null;
        if (HlsPlaylist.IsMaster(master))
        {
            var cap = job.MaxHeight > 0 ? job.MaxHeight : Settings.MaxHeight;
            var pick = HlsPlaylist.Pick(HlsPlaylist.Variants(master, url), cap, preferVideoOnly: true)
                       ?? HlsPlaylist.Pick(HlsPlaylist.Variants(master, url), cap);
            if (pick is null)
            {
                throw new InvalidOperationException("No HLS variant.");
            }

            job.Height = pick.Height;
            audioUrl = HlsPlaylist.AudioUri(master, url, job.AudioLang, pick.Audio)
                       ?? HlsPlaylist.AudioUri(master, url, job.AudioLang);
            CollectHlsTracks(job, master, url, userAgent);
            Raise();
            mediaUrl = pick.Url;
            master = GetString(mediaUrl, userAgent, job.Referer, token);
        }

        if (HlsPlaylist.IsLive(master))
        {
            throw new InvalidOperationException("Live streams cannot be downloaded");
        }

        var videoPath = Path.ChangeExtension(job.OutputPath, ".video.bin");
        var audioPath = string.IsNullOrWhiteSpace(audioUrl)
            ? null
            : Path.ChangeExtension(job.OutputPath, ".audio.bin");
        var dest = Path.ChangeExtension(job.OutputPath, Settings.ContainerExtension);

        if (!(File.Exists(videoPath) && new FileInfo(videoPath).Length > 1024))
        {
            WriteMediaPlaylist(job, master, mediaUrl, videoPath, userAgent, token, updateProgress: true);
        }

        if (audioPath is not null &&
            !string.IsNullOrWhiteSpace(audioUrl) &&
            !(File.Exists(audioPath) && new FileInfo(audioPath).Length > 1024))
        {
            var audioList = GetString(audioUrl, userAgent, job.Referer, token);
            WriteMediaPlaylist(job, audioList, audioUrl, audioPath, userAgent, token, updateProgress: false);
        }

        var extraAudio = DownloadExtraAudio(job, audioUrl, userAgent, token);
        var labeled = LabeledAudio(job, audioPath, audioUrl, extraAudio);
        if (FfmpegMux.TryRemux(videoPath, labeled, dest) ||
            FfmpegMux.TryRemux(videoPath, audioPath, dest, extraAudio.Select(item => item.Path).ToList()) ||
            StreamDump.TryRemux(videoPath, audioPath, dest, token))
        {
            job.OutputPath = dest;
            TryDelete(videoPath);
            if (audioPath is not null)
            {
                TryDelete(audioPath);
            }

            foreach (var extra in extraAudio)
            {
                TryDelete(extra.Path);
            }
        }
        else if (audioPath is not null)
        {
            throw new InvalidOperationException(
                "Could not mux audio into the download" +
                (FfmpegMux.LastError is { } err ? " (" + err + ")" : "") +
                ". Video and audio files were kept so you can retry.");
        }
        else
        {
            File.Move(videoPath, dest, overwrite: true);
            job.OutputPath = dest;
        }

        if (File.Exists(job.OutputPath))
        {
            job.Bytes = new FileInfo(job.OutputPath).Length;
            job.TotalBytes = job.Bytes;
        }
    }

    private void WriteMediaPlaylist(
        DownloadJob job,
        string playlist,
        string mediaUrl,
        string path,
        string? userAgent,
        CancellationToken token,
        bool updateProgress)
    {
        var (map, segments) = HlsPlaylist.Media(playlist, mediaUrl);
        if (updateProgress)
        {
            job.SegmentsTotal = segments.Count + (map is null ? 0 : 1);
            job.SegmentsDone = 0;
            job.Bytes = 0;
        }

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        if (map is not null)
        {
            CopySegment(output, map, userAgent, job.Referer, token);
            if (updateProgress)
            {
                job.SegmentsDone++;
                RaiseProgress();
            }
        }

        foreach (var segment in segments)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfPaused(job);
            var bytes = CopySegment(output, segment.Url, userAgent, job.Referer, token, segment.RangeStart, segment.RangeLength);
            if (updateProgress)
            {
                job.Bytes += bytes;
                job.SegmentsDone++;
                RaiseProgress();
            }
        }
    }

    private void DownloadFile(DownloadJob job, string url, string? userAgent, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, userAgent, job.Referer);
        using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            var viaCurl = StreamCatalog.CurlBytes(url, job.Referer, userAgent);
            if (viaCurl is { Length: > 0 })
            {
                File.WriteAllBytes(job.OutputPath, viaCurl);
                job.Bytes = viaCurl.Length;
                job.TotalBytes = viaCurl.Length;
                return;
            }

            response.EnsureSuccessStatusCode();
        }

        job.TotalBytes = response.Content.Headers.ContentLength ?? 0;
        job.Bytes = 0;
        using var input = response.Content.ReadAsStream(token);
        using var output = new FileStream(job.OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfPaused(job);
            output.Write(buffer, 0, read);
            job.Bytes += read;
            if (job.Bytes % (512 * 1024) < read)
            {
                RaiseProgress();
            }
        }
    }

    private string GetString(string url, string? userAgent, string? referer, CancellationToken token) =>
        System.Text.Encoding.UTF8.GetString(GetBytes(url, userAgent, referer, token));

    private byte[] GetBytes(string url, string? userAgent, string? referer, CancellationToken token, long? rangeStart = null, int? rangeLength = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(request, userAgent, referer);
            if (rangeStart is { } start && rangeLength is { } length)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, start + length - 1);
            }

            using var response = _http.Send(request, token);
            if (response.IsSuccessStatusCode)
            {
                return response.Content.ReadAsByteArrayAsync(token).GetAwaiter().GetResult();
            }

            if ((int)response.StatusCode is 401 or 403 or 412)
            {
                var viaCurl = StreamCatalog.CurlBytes(url, referer, userAgent, rangeStart, rangeLength);
                if (viaCurl is { Length: > 0 })
                {
                    return viaCurl;
                }
            }

            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            var viaCurl = StreamCatalog.CurlBytes(url, referer, userAgent, rangeStart, rangeLength);
            if (viaCurl is { Length: > 0 })
            {
                return viaCurl;
            }

            throw;
        }

        throw new InvalidOperationException("Download failed.");
    }

    private static void ApplyHeaders(HttpRequestMessage request, string? userAgent, string? referer)
    {
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            string.IsNullOrWhiteSpace(userAgent) ? StreamCatalog.ChromeUa : userAgent);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        var url = request.RequestUri?.ToString() ?? "";
        var page = LooksLikeYouTubeMedia(url)
            ? "https://www.youtube.com"
            : StreamCatalog.SiteReferer(url, referer);
        if (string.IsNullOrWhiteSpace(page))
        {
            return;
        }

        var origin = StreamCatalog.PageOrigin(page) ?? page;
        request.Headers.Remove("Referer");
        request.Headers.Remove("Origin");
        request.Headers.TryAddWithoutValidation("Referer", page);
        request.Headers.TryAddWithoutValidation("Origin", origin.TrimEnd('/'));
    }

    private static void Write(Stream output, byte[] bytes) => output.Write(bytes, 0, bytes.Length);

    private long CopySegment(Stream output, string url, string? userAgent, string? referer, CancellationToken token,
        long? rangeStart = null, int? rangeLength = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, userAgent, referer);
        if (rangeStart is { } start && rangeLength is { } length)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, start + length - 1);
        using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            var viaCurl = StreamCatalog.CurlBytes(url, referer, userAgent, rangeStart, rangeLength);
            if (viaCurl is { Length: > 0 })
            {
                output.Write(viaCurl, 0, viaCurl.Length);
                return viaCurl.Length;
            }

            response.EnsureSuccessStatusCode();
        }

        using var input = response.Content.ReadAsStream(token);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                total += read;
            }
            return total;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void RaiseProgress()
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastProgress);
        if (now - previous >= 250 && Interlocked.CompareExchange(ref _lastProgress, now, previous) == previous)
            Raise();
    }

    private static void ThrowIfPaused(DownloadJob job)
    {
        if (job.State == DownloadState.Paused)
        {
            throw new OperationCanceledException();
        }
    }

    private void Fail(DownloadJob job, string message)
    {
        lock (_gate)
        {
            if (job.State is DownloadState.Canceled or DownloadState.Paused)
            {
                return;
            }

            job.State = DownloadState.Failed;
            job.Error = message;
        }
    }

    private void Raise() => Changed?.Invoke();

    internal static bool LooksLikeHls(string url)
    {
        if (ProtectedStreamProxy.TryUnwrap(url, out var real))
        {
            url = real;
        }

        return url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("hls_variant", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("master.txt", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("playlist.txt", StringComparison.OrdinalIgnoreCase) ||
               StreamCatalog.LooksPackedHls(url);
    }

    private static bool LooksLikeYouTubeMedia(string url) =>
        url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

    private static void HarvestCaptions(DownloadJob job, string source)
    {
        AddSelectedCaption(job);
        IReadOnlyList<ExternalCaption> found = [];
        try
        {
            if (YouTubeCatalog.IsWatchUrl(source))
            {
                found = YouTubeCatalog.ListCaptions(source);
            }
            else if (StreamCatalog.TryReadDailymotion(source, out var dailyId))
            {
                found = StreamCatalog.DailyCaptions(dailyId);
            }
            else if (job.Captions.Count == 0)
            {
                found = StreamCatalog.SidecarCaptionsFromPage(source);
            }
        }
        catch (Exception)
        {
        }

        foreach (var cap in found)
        {
            AddCaption(job, cap);
        }
    }

    internal static void AddSelectedCaption(DownloadJob job)
    {
        if (string.IsNullOrWhiteSpace(job.CaptionUrl) && string.IsNullOrWhiteSpace(job.SubLang))
        {
            return;
        }

        if (MediaLanguage.IsOff(job.SubLang) && string.IsNullOrWhiteSpace(job.CaptionUrl))
        {
            return;
        }

        var url = job.CaptionUrl;
        if (string.IsNullOrWhiteSpace(url) && YouTubeCatalog.TryReadVideoId(job.SourceUrl, out var id))
        {
            url = YouTubeCatalog.CaptionVttUrl(id, job.SubLang ?? "");
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var lang = StreamCaptionLoader.EffectiveLanguage(job.SubLang, url);
        if (lang.Length == 0)
        {
            lang = YouTubeCatalog.CaptionLanguageFromUrl(url) ?? "";
        }

        var name = lang;
        if (YouTubeCatalog.CaptionUrlIsTranslate(url))
        {
            name = string.IsNullOrWhiteSpace(lang) ? "Translated" : lang;
        }

        AddCaption(job, new ExternalCaption(lang, url, name));
    }

    internal static void AddCaption(DownloadJob job, ExternalCaption cap)
    {
        if (string.IsNullOrWhiteSpace(cap.Url))
        {
            return;
        }

        var lang = MediaLanguage.ShortCode(cap.Language);
        if (job.Captions.Any(item =>
                string.Equals(item.Url, cap.Url, StringComparison.OrdinalIgnoreCase) ||
                (lang.Length > 0 &&
                 string.Equals(MediaLanguage.ShortCode(item.Language), lang, StringComparison.OrdinalIgnoreCase) &&
                 YouTubeCatalog.CaptionUrlIsTranslate(item.Url) == YouTubeCatalog.CaptionUrlIsTranslate(cap.Url))))
        {
            return;
        }

        job.Captions.Add(cap);
    }

    internal static string PageReferer(string? referer, string? sourceUrl)
    {
        if (!string.IsNullOrWhiteSpace(sourceUrl) &&
            UrlSanitizer.IsUrl(sourceUrl) &&
            !StreamCatalog.IsDirectMedia(sourceUrl) &&
            (string.IsNullOrWhiteSpace(referer) || IsBareOrigin(referer)))
        {
            return sourceUrl;
        }

        return string.IsNullOrWhiteSpace(referer)
            ? StreamCatalog.SiteReferer(sourceUrl, sourceUrl)
            : referer;
    }

    private static bool IsBareOrigin(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.AbsolutePath is "/" or "" ) &&
               string.IsNullOrEmpty(uri.Query);
    }

    internal static bool LooksLikePlaylistFile(string path)
    {
        var head = ReadHeadText(path, 16);
        return head.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool LooksLikeStubFile(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return true;
        }

        var head = ReadHeadText(path, 64);
        return head.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ||
               head.StartsWith("<!", StringComparison.OrdinalIgnoreCase) ||
               head.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               head.StartsWith("{", StringComparison.Ordinal);
    }

    private static string ReadHeadText(string path, int count)
    {
        if (!File.Exists(path))
        {
            return "";
        }

        using var stream = File.OpenRead(path);
        var buffer = new byte[count];
        var read = stream.Read(buffer, 0, buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(dir, name + " (" + i + ")" + ext);
            if (!File.Exists(next))
            {
                return next;
            }
        }

        return path;
    }

    private void CollectHlsTracks(DownloadJob job, string master, string url, string? userAgent)
    {
        job.AudioTracks.Clear();
        job.AudioTracks.AddRange(HlsPlaylist.AudioTracks(master, url));
        var folder = Path.Combine(Path.GetTempPath(), "GrokPlayer", "captions");
        Directory.CreateDirectory(folder);
        foreach (var sub in HlsPlaylist.Subtitles(master, url))
        {
            if (sub.Forced)
            {
                continue;
            }

            try
            {
                var body = HlsCaptions.ReadDocument(sub.Url, userAgent);
                if (string.IsNullOrWhiteSpace(body) || !StreamCaptionLoader.LooksLikeSidecar(body))
                {
                    continue;
                }

                var parsed = SrtDocument.Parse(body, compact: false).ForDisplay();
                if (parsed.Cues.Count == 0)
                {
                    continue;
                }

                var lang = MediaLanguage.ShortCode(
                    string.IsNullOrWhiteSpace(sub.Language) ? sub.Name : sub.Language);
                var name = string.IsNullOrWhiteSpace(sub.Name) ? lang : sub.Name.Trim();
                var tag = string.IsNullOrWhiteSpace(lang) ? "auto" : SafeLangTag(lang);
                var vtt = Path.Combine(folder, "dl-" + Math.Abs(url.GetHashCode(StringComparison.Ordinal)).ToString("x") + "." + tag + ".vtt");
                File.WriteAllText(vtt, body);
                AddCaption(job, new ExternalCaption(lang, vtt, name));
            }
            catch (Exception)
            {
            }
        }
    }

    private List<(string Path, string Language, string Name)> DownloadExtraAudio(
        DownloadJob job,
        string? primaryUrl,
        string? userAgent,
        CancellationToken token)
    {
        var extra = new List<(string Path, string Language, string Name)>();
        var index = 0;
        foreach (var track in job.AudioTracks)
        {
            if (string.IsNullOrWhiteSpace(track.Url) ||
                string.Equals(track.Url, primaryUrl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tag = SafeLangTag(string.IsNullOrWhiteSpace(track.Language) ? index.ToString() : track.Language);
            var path = Path.ChangeExtension(job.OutputPath, ".audio-" + tag + ".bin");
            index++;
            if (!(File.Exists(path) && new FileInfo(path).Length > 1024))
            {
                try
                {
                    var list = GetString(track.Url, userAgent, job.Referer, token);
                    WriteMediaPlaylist(job, list, track.Url, path, userAgent, token, updateProgress: false);
                }
                catch (Exception)
                {
                    continue;
                }
            }

            if (File.Exists(path) && new FileInfo(path).Length > 1024)
            {
                extra.Add((path, track.Language, track.Name));
            }
        }

        return extra;
    }

    private static List<(string Path, string Language, string Name)> LabeledAudio(
        DownloadJob job,
        string? primaryPath,
        string? primaryUrl,
        IReadOnlyList<(string Path, string Language, string Name)> extra)
    {
        var list = new List<(string Path, string Language, string Name)>();
        if (!string.IsNullOrWhiteSpace(primaryPath) && File.Exists(primaryPath))
        {
            var primary = job.AudioTracks.FirstOrDefault(item =>
                string.Equals(item.Url, primaryUrl, StringComparison.OrdinalIgnoreCase));
            list.Add((primaryPath, primary.Language ?? "", primary.Name ?? ""));
        }

        list.AddRange(extra);
        return list;
    }

    internal static void AttachCaptions(DownloadJob job)
    {
        if (MediaLanguage.IsOff(job.SubLang) &&
            string.IsNullOrWhiteSpace(job.CaptionUrl) &&
            job.Captions.Count == 0)
        {
            return;
        }

        YouTubeCatalog.TryReadVideoId(job.SourceUrl, out var videoId);
        AddSelectedCaption(job);
        if (YouTubeCatalog.IsWatchUrl(job.SourceUrl))
        {
            try
            {
                foreach (var cap in YouTubeCatalog.ListCaptions(job.SourceUrl))
                {
                    AddCaption(job, cap);
                }
            }
            catch (Exception)
            {
            }
        }

        var caps = job.Captions.ToList();
        if (caps.Count == 0 &&
            (!string.IsNullOrWhiteSpace(videoId) || !string.IsNullOrWhiteSpace(job.CaptionUrl)))
        {
            var loaded = StreamCaptionLoader.Load(videoId, job.SubLang, job.CaptionUrl);
            if (!string.IsNullOrWhiteSpace(loaded))
            {
                caps.Add(new ExternalCaption(job.SubLang ?? "", loaded, job.SubLang ?? "Subtitle"));
            }
        }

        var tagged = 0;
        foreach (var cap in caps)
        {
            try
            {
                var file = File.Exists(cap.Url)
                    ? cap.Url
                    : StreamCaptionLoader.LoadSidecar(cap.Url, cap.Language, cap.Name);
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                {
                    file = StreamCaptionLoader.Load(videoId, cap.Language, cap.Url);
                }

                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                {
                    continue;
                }

                var lang = MediaLanguage.ShortCode(cap.Language);
                if (lang.Length == 0)
                {
                    lang = MediaLanguage.ShortCode(MediaLanguage.FromName(cap.Name));
                }

                if (YouTubeCatalog.CaptionUrlIsTranslate(cap.Url) &&
                    !string.IsNullOrWhiteSpace(YouTubeCatalog.CaptionLanguageFromUrl(cap.Url)))
                {
                    lang = MediaLanguage.ShortCode(YouTubeCatalog.CaptionLanguageFromUrl(cap.Url));
                }

                var dest = LanguageSidecarPath(job.OutputPath, lang);
                WriteCaptionSidecar(file, dest);
                if (File.Exists(dest) && !string.IsNullOrWhiteSpace(SafeLangTag(lang)))
                {
                    tagged++;
                }
            }
            catch (Exception)
            {
            }
        }

        if (tagged == 0)
        {
            var destSrt = Path.ChangeExtension(job.OutputPath, ".srt");
            foreach (var cap in caps)
            {
                try
                {
                    var file = File.Exists(cap.Url)
                        ? cap.Url
                        : StreamCaptionLoader.LoadSidecar(cap.Url, cap.Language, cap.Name);
                    if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
                    {
                        WriteCaptionSidecar(file, destSrt);
                        break;
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }

    internal static string LanguageSidecarPath(string output, string language)
    {
        var tag = SafeLangTag(language);
        var ext = Path.ChangeExtension(output, null);
        return string.IsNullOrWhiteSpace(tag)
            ? Path.ChangeExtension(output, ".srt")
            : ext + "." + tag + ".srt";
    }

    internal static string SafeLangTag(string? language)
    {
        var text = MediaLanguage.ShortCode(language);
        if (text.Length == 0)
        {
            text = (language ?? "").Trim();
        }

        if (text.Length == 0)
        {
            return "";
        }

        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            text = text.Replace(ch, '-');
        }

        return text.Trim('.', ' ', '-');
    }

    private static void WriteCaptionSidecar(string source, string dest)
    {
        var document = StreamCaptionLoader.DocumentPath(source);
        if (!File.Exists(document) && File.Exists(source))
        {
            document = source;
        }

        if (!File.Exists(document))
        {
            return;
        }

        if (document.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(document, dest, overwrite: true);
            return;
        }

        var parsed = SrtDocument.Parse(File.ReadAllText(document), compact: false).Compacted();
        if (parsed.Cues.Count > 0)
        {
            parsed.Save(dest);
        }
    }

    private static void TryDeleteOutputs(string path)
    {
        TryDelete(path);
        foreach (var ext in new[] { ".mkv", ".mp4", ".ts", ".video.bin", ".audio.bin", ".srt", ".vtt" })
        {
            TryDelete(Path.ChangeExtension(path, ext));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }
}
