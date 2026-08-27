using System.Diagnostics;
using System.Net;
using System.Text;

namespace Grok.Player.Core.IntegrationTests.Support;

internal sealed class LiveHlsFixture : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _root;
    private bool _disposed;

    private LiveHlsFixture(HttpListener listener, string root, string playlistUrl)
    {
        _listener = listener;
        _root = root;
        PlaylistUrl = playlistUrl;
    }

    public string PlaylistUrl { get; }

    public static LiveHlsFixture? TryCreate()
    {
        var sample = GeneratedMedia.TryCreateSample(4);
        if (sample is null)
        {
            return null;
        }

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            return null;
        }

        var root = Path.Combine(Path.GetTempPath(), "grok-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var playlist = Path.Combine(root, "live.m3u8");
        var args =
            $"-y -i \"{sample}\" -c:v libx264 -pix_fmt yuv420p -c:a aac " +
            $"-f hls -hls_time 1 -hls_list_size 0 -hls_segment_filename \"{Path.Combine(root, "seg%d.ts")}\" " +
            $"\"{playlist}\"";

        if (!Run(ffmpeg, args) || !File.Exists(playlist))
        {
            TryDelete(root);
            return null;
        }

        var body = File.ReadAllText(playlist).Replace("#EXT-X-ENDLIST", "", StringComparison.Ordinal);
        File.WriteAllText(playlist, body, Encoding.UTF8);

        var listener = new HttpListener();
        var port = 0;
        for (var i = 0; i < 8; i++)
        {
            port = 18000 + Random.Shared.Next(1000);
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                break;
            }
            catch (HttpListenerException)
            {
                if (i == 7)
                {
                    TryDelete(root);
                    return null;
                }
            }
        }

        var fixture = new LiveHlsFixture(listener, root, $"http://127.0.0.1:{port}/live.m3u8");
        fixture.Serve();
        return fixture;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        TryDelete(_root);
    }

    private void Serve()
    {
        _listener.BeginGetContext(OnRequest, null);
    }

    private void OnRequest(IAsyncResult result)
    {
        if (_disposed)
        {
            return;
        }

        HttpListenerContext context;
        try
        {
            context = _listener.EndGetContext(result);
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            Serve();
            var name = context.Request.Url?.AbsolutePath.Trim('/') ?? "live.m3u8";
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "live.m3u8";
            }

            var path = Path.GetFullPath(Path.Combine(_root, name));
            if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var bytes = File.ReadAllBytes(path);
            context.Response.StatusCode = 200;
            context.Response.ContentType = name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? "application/vnd.apple.mpegurl"
                : "video/mp2t";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }
        catch (Exception)
        {
            try
            {
                context.Response.Abort();
            }
            catch (Exception)
            {
            }
        }
    }

    private static bool Run(string ffmpeg, string args)
    {
        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        using var process = Process.Start(start);
        if (process is null)
        {
            return false;
        }

        process.WaitForExit(40_000);
        return process.ExitCode == 0;
    }

    private static string? FindFfmpeg()
    {
        foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
        {
            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            var hit = paths.Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
        catch (IOException)
        {
        }
    }
}
