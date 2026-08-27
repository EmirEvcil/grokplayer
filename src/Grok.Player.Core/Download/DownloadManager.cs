using Grok.Player.Core.Media;
using Grok.Player.Core.Playlist;

namespace Grok.Player.Core.Download;

public sealed class DownloadManager : IDisposable
{
    private readonly object _gate = new();
    private readonly List<DownloadJob> _jobs = [];
    private readonly Dictionary<string, CancellationTokenSource> _tokens = [];
    private readonly HttpClient _http;
    private bool _disposed;

    public DownloadManager(DownloadSettings? settings = null, HttpMessageHandler? handler = null)
    {
        Settings = settings ?? DownloadSettings.Load();
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromMinutes(5) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15");
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

    public DownloadJob Enqueue(string sourceUrl, string title, bool start, string? audioLang = null, int maxHeight = 0)
    {
        return EnqueueCore(sourceUrl, title, start, audioLang, maxHeight);
    }

    public DownloadJob EnqueueCore(string sourceUrl, string title, bool start, string? audioLang, int maxHeight = 0)
    {
        Directory.CreateDirectory(Settings.Folder);
        var name = DownloadJob.SafeFileName(title);
        var path = UniquePath(Path.Combine(Settings.Folder, name + Settings.ContainerExtension));
        var job = new DownloadJob(sourceUrl, title, path)
        {
            AudioLang = audioLang,
            MaxHeight = maxHeight > 0 ? maxHeight : Settings.MaxHeight
        };
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
        ThreadPool.QueueUserWorkItem(_ => Run(job, cts.Token));
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
            if (YouTubeCatalog.IsWatchUrl(source))
            {
                var playable = YouTubeCatalog.Resolve(source, null, job.AudioLang, null);
                if (playable is null || playable.Kind == StreamKind.Live)
                {
                    Fail(job, playable is null ? "YouTube stream unavailable" : "Live streams cannot be downloaded");
                    return;
                }

                source = playable.MediaUrl;
                userAgent = playable.UserAgent;
                job.AudioLang ??= playable.AudioLang;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (LooksLikeHls(source))
            {
                DownloadHls(job, source, userAgent, token);
            }
            else
            {
                DownloadFile(job, source, userAgent, token);
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
        var master = GetString(url, userAgent, token);
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

            Raise();
            mediaUrl = pick.Url;
            master = GetString(mediaUrl, userAgent, token);
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
            var audioList = GetString(audioUrl, userAgent, token);
            WriteMediaPlaylist(job, audioList, audioUrl, audioPath, userAgent, token, updateProgress: false);
        }

        if (FfmpegMux.TryRemux(videoPath, audioPath, dest) ||
            StreamDump.TryRemux(videoPath, audioPath, dest, token))
        {
            job.OutputPath = dest;
            TryDelete(videoPath);
            if (audioPath is not null)
            {
                TryDelete(audioPath);
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
            Write(output, GetBytes(map, userAgent, token));
            if (updateProgress)
            {
                job.SegmentsDone++;
                Raise();
            }
        }

        foreach (var segment in segments)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfPaused(job);
            var bytes = GetBytes(segment.Url, userAgent, token, segment.RangeStart, segment.RangeLength);
            Write(output, bytes);
            if (updateProgress)
            {
                job.Bytes += bytes.Length;
                job.SegmentsDone++;
                Raise();
            }
        }
    }

    private void DownloadFile(DownloadJob job, string url, string? userAgent, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, userAgent);
        using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        job.TotalBytes = response.Content.Headers.ContentLength ?? 0;
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
                Raise();
            }
        }
    }

    private string GetString(string url, string? userAgent, CancellationToken token) =>
        System.Text.Encoding.UTF8.GetString(GetBytes(url, userAgent, token));

    private byte[] GetBytes(string url, string? userAgent, CancellationToken token, long? rangeStart = null, int? rangeLength = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, userAgent);
        if (rangeStart is { } start && rangeLength is { } length)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, start + length - 1);
        }

        using var response = _http.Send(request, token);
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsByteArrayAsync(token).GetAwaiter().GetResult();
    }

    private static void ApplyHeaders(HttpRequestMessage request, string? userAgent)
    {
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
    }

    private static void Write(Stream output, byte[] bytes) => output.Write(bytes, 0, bytes.Length);

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

    private static bool LooksLikeHls(string url) =>
        url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("hls_variant", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);

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

    private static void TryDeleteOutputs(string path)
    {
        TryDelete(path);
        foreach (var ext in new[] { ".mkv", ".mp4", ".ts", ".video.bin", ".audio.bin" })
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
