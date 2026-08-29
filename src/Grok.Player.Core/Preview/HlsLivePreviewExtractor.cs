using System.Diagnostics;
using System.Globalization;
using Grok.Player.Core.Download;

namespace Grok.Player.Core.Preview;

internal static class HlsLivePreviewExtractor
{
    private static readonly HttpClient Http = CreateClient();
    private const double CoverageIntervalSeconds = 1;

    public static string? Capture(string source, double behindLiveSeconds, DateTime requestedUtc)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var masterUri) ||
            masterUri.Scheme is not ("http" or "https") ||
            !masterUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            return null;

        var ffmpeg = FfmpegMux.Find();
        if (ffmpeg is null) return null;

        string? manifestPath = null;
        var outputPath = Path.Combine(Path.GetTempPath(), $"grok-live-preview-{Guid.NewGuid():N}.jpg");
        var keepOutput = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var master = Http.GetStringAsync(masterUri, timeout.Token).GetAwaiter().GetResult();
            var mediaUri = ResolveBestVideoPlaylist(masterUri, master);
            var media = mediaUri == masterUri
                ? master
                : Http.GetStringAsync(mediaUri, timeout.Token).GetAwaiter().GetResult();

            // The live edge advances while the request waits in the worker and
            // while the manifest is fetched. Preserve the originally hovered
            // wall-clock point by adding that elapsed time to its edge distance.
            var elapsed = Math.Max(0, (DateTime.UtcNow - requestedUtc).TotalSeconds);
            var window = BuildWindow(mediaUri, media, Math.Max(0, behindLiveSeconds) + elapsed);
            if (window is null) return null;

            manifestPath = Path.Combine(Path.GetTempPath(), $"grok-live-preview-{Guid.NewGuid():N}.m3u8");
            File.WriteAllText(manifestPath, window.Value.Manifest);

            var start = new ProcessStartInfo
            {
                FileName = ffmpeg,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = false,
                RedirectStandardOutput = false
            };
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-protocol_whitelist", "file,http,https,tcp,tls,crypto",
                "-ss", window.Value.SeekSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", manifestPath,
                "-an", "-sn", "-dn", "-frames:v", "1",
                "-vf", "scale=512:-2:force_original_aspect_ratio=decrease",
                "-q:v", "2", outputPath
            }) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null) return null;
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(true); } catch (Exception) { }
                return null;
            }

            keepOutput = process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024;
            return keepOutput ? outputPath : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (manifestPath is not null) TryDelete(manifestPath);
            if (!keepOutput) TryDelete(outputPath);
        }
    }

    public static IReadOnlyList<CoverageFrame> CaptureCoverage(
        string source,
        double sourceLiveEdgeSeconds,
        double keepSeconds,
        DateTime requestedUtc,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var masterUri) ||
            masterUri.Scheme is not ("http" or "https") ||
            !masterUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            return [];
        var ffmpeg = FfmpegMux.Find();
        if (ffmpeg is null) return [];

        string? manifestPath = null;
        string? outputFolder = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var master = Http.GetStringAsync(masterUri, timeout.Token).GetAwaiter().GetResult();
            var mediaUri = ResolveVideoPlaylist(masterUri, master, preferHighest: false);
            var media = mediaUri == masterUri
                ? master
                : Http.GetStringAsync(mediaUri, timeout.Token).GetAwaiter().GetResult();
            var manifestElapsed = Math.Max(0, (DateTime.UtcNow - requestedUtc).TotalSeconds);
            var estimatedLiveEdge = sourceLiveEdgeSeconds + manifestElapsed;
            var coverage = BuildCoverageWindow(mediaUri, media, keepSeconds);
            if (coverage is null) return [];

            manifestPath = Path.Combine(Path.GetTempPath(), $"grok-live-coverage-{Guid.NewGuid():N}.m3u8");
            File.WriteAllText(manifestPath, coverage.Value.Manifest);
            outputFolder = Path.Combine(Path.GetTempPath(), $"grok-live-coverage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(outputFolder);
            var outputPattern = Path.Combine(outputFolder, "frame-%04d.jpg");
            var start = new ProcessStartInfo
            {
                FileName = ffmpeg,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = false,
                RedirectStandardOutput = false
            };
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-protocol_whitelist", "file,http,https,tcp,tls,crypto",
                "-i", manifestPath,
                "-an", "-sn", "-dn",
                "-vf", $"fps=1/{CoverageIntervalSeconds.ToString("0.###", CultureInfo.InvariantCulture)},scale=256:-2:force_original_aspect_ratio=decrease",
                "-q:v", "5", outputPattern
            }) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null) return [];
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch (Exception) { }
            });
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(true); } catch (Exception) { }
                return [];
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) return [];

            var firstPosition = estimatedLiveEdge - coverage.Value.DurationSeconds;
            var files = Directory.GetFiles(outputFolder, "frame-*.jpg")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var result = new List<CoverageFrame>(files.Length);
            for (var i = 0; i < files.Length; i++)
            {
                result.Add(new CoverageFrame(
                    TimeSpan.FromSeconds(Math.Max(0, firstPosition + i * CoverageIntervalSeconds)),
                    files[i]));
            }
            outputFolder = null; // The caller owns and removes the returned files.
            return result;
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
        {
            return [];
        }
        finally
        {
            if (manifestPath is not null) TryDelete(manifestPath);
            if (outputFolder is not null) TryDeleteDirectory(outputFolder);
        }
    }

    internal static FrozenWindow? BuildWindow(Uri mediaUri, string playlist, double behindLiveSeconds)
    {
        var lines = playlist.Replace("\r", "").Split('\n');
        var segments = new List<Segment>();
        string? map = null;
        string? key = null;
        string? byteRange = null;
        var discontinuity = false;
        long mediaSequence = 0;
        double? pendingDuration = null;
        var maxDuration = 1d;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                long.TryParse(line.AsSpan(22), NumberStyles.Integer, CultureInfo.InvariantCulture, out mediaSequence);
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                map = AbsolutizeUriAttribute(mediaUri, line);
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                key = AbsolutizeUriAttribute(mediaUri, line);
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                byteRange = line;
            }
            else if (line == "#EXT-X-DISCONTINUITY")
            {
                discontinuity = true;
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var comma = line.IndexOf(',');
                var value = comma >= 0 ? line.AsSpan(8, comma - 8) : line.AsSpan(8);
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) && duration > 0)
                {
                    pendingDuration = duration;
                    maxDuration = Math.Max(maxDuration, duration);
                }
            }
            else if (line.Length > 0 && line[0] != '#' && pendingDuration is { } duration)
            {
                segments.Add(new Segment(
                    duration,
                    new Uri(mediaUri, line).AbsoluteUri,
                    map,
                    key,
                    byteRange,
                    discontinuity));
                pendingDuration = null;
                byteRange = null;
                discontinuity = false;
            }
        }

        if (segments.Count == 0) return null;
        var total = segments.Sum(segment => segment.Duration);
        var target = Math.Clamp(total - behindLiveSeconds, 0, Math.Max(0, total - 0.04));
        var index = 0;
        var cursor = 0d;
        while (index < segments.Count - 1 && cursor + segments[index].Duration <= target)
        {
            cursor += segments[index].Duration;
            index++;
        }

        var first = Math.Max(0, index - 1);
        var firstTime = segments.Take(first).Sum(segment => segment.Duration);
        var last = Math.Min(segments.Count - 1, index + 1);
        var output = new List<string>
        {
            "#EXTM3U",
            "#EXT-X-VERSION:7",
            "#EXT-X-PLAYLIST-TYPE:VOD",
            $"#EXT-X-TARGETDURATION:{Math.Ceiling(maxDuration):0}",
            $"#EXT-X-MEDIA-SEQUENCE:{mediaSequence + first}"
        };
        string? emittedMap = null;
        string? emittedKey = null;
        for (var i = first; i <= last; i++)
        {
            var segment = segments[i];
            if (segment.Map is not null && segment.Map != emittedMap)
            {
                output.Add(segment.Map);
                emittedMap = segment.Map;
            }
            if (segment.Key is not null && segment.Key != emittedKey)
            {
                output.Add(segment.Key);
                emittedKey = segment.Key;
            }
            if (segment.Discontinuity) output.Add("#EXT-X-DISCONTINUITY");
            output.Add($"#EXTINF:{segment.Duration.ToString("0.###", CultureInfo.InvariantCulture)},");
            if (segment.ByteRange is not null) output.Add(segment.ByteRange);
            output.Add(segment.Uri);
        }
        output.Add("#EXT-X-ENDLIST");
        return new FrozenWindow(string.Join(Environment.NewLine, output), target - firstTime);
    }

    internal static CoverageWindow? BuildCoverageWindow(Uri mediaUri, string playlist, double keepSeconds)
    {
        var parsed = ParseSegments(mediaUri, playlist);
        if (parsed.Segments.Count == 0) return null;
        var total = parsed.Segments.Sum(segment => segment.Duration);
        var wanted = Math.Clamp(keepSeconds, 1, total);
        var first = parsed.Segments.Count - 1;
        var duration = parsed.Segments[first].Duration;
        while (first > 0 && duration < wanted)
        {
            first--;
            duration += parsed.Segments[first].Duration;
        }

        var output = RenderManifest(
            parsed.Segments,
            first,
            parsed.Segments.Count - 1,
            parsed.MediaSequence,
            parsed.MaxDuration);
        return new CoverageWindow(output, duration);
    }

    private static ParsedPlaylist ParseSegments(Uri mediaUri, string playlist)
    {
        var segments = new List<Segment>();
        string? map = null;
        string? key = null;
        string? byteRange = null;
        var discontinuity = false;
        long mediaSequence = 0;
        double? pendingDuration = null;
        var maxDuration = 1d;
        foreach (var raw in playlist.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                long.TryParse(line.AsSpan(22), NumberStyles.Integer, CultureInfo.InvariantCulture, out mediaSequence);
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                map = AbsolutizeUriAttribute(mediaUri, line);
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
                key = AbsolutizeUriAttribute(mediaUri, line);
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
                byteRange = line;
            else if (line == "#EXT-X-DISCONTINUITY")
                discontinuity = true;
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var comma = line.IndexOf(',');
                var value = comma >= 0 ? line.AsSpan(8, comma - 8) : line.AsSpan(8);
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    pendingDuration = seconds;
                    maxDuration = Math.Max(maxDuration, seconds);
                }
            }
            else if (line.Length > 0 && line[0] != '#' && pendingDuration is { } duration)
            {
                segments.Add(new Segment(duration, new Uri(mediaUri, line).AbsoluteUri, map, key, byteRange, discontinuity));
                pendingDuration = null;
                byteRange = null;
                discontinuity = false;
            }
        }
        return new ParsedPlaylist(segments, mediaSequence, maxDuration);
    }

    private static string RenderManifest(
        IReadOnlyList<Segment> segments,
        int first,
        int last,
        long mediaSequence,
        double maxDuration)
    {
        var output = new List<string>
        {
            "#EXTM3U", "#EXT-X-VERSION:7", "#EXT-X-PLAYLIST-TYPE:VOD",
            $"#EXT-X-TARGETDURATION:{Math.Ceiling(maxDuration):0}",
            $"#EXT-X-MEDIA-SEQUENCE:{mediaSequence + first}"
        };
        string? emittedMap = null;
        string? emittedKey = null;
        for (var i = first; i <= last; i++)
        {
            var segment = segments[i];
            if (segment.Map is not null && segment.Map != emittedMap) { output.Add(segment.Map); emittedMap = segment.Map; }
            if (segment.Key is not null && segment.Key != emittedKey) { output.Add(segment.Key); emittedKey = segment.Key; }
            if (segment.Discontinuity) output.Add("#EXT-X-DISCONTINUITY");
            output.Add($"#EXTINF:{segment.Duration.ToString("0.###", CultureInfo.InvariantCulture)},");
            if (segment.ByteRange is not null) output.Add(segment.ByteRange);
            output.Add(segment.Uri);
        }
        output.Add("#EXT-X-ENDLIST");
        return string.Join(Environment.NewLine, output);
    }

    private static Uri ResolveBestVideoPlaylist(Uri masterUri, string manifest) =>
        ResolveVideoPlaylist(masterUri, manifest, preferHighest: true);

    private static Uri ResolveVideoPlaylist(Uri masterUri, string manifest, bool preferHighest)
    {
        var lines = manifest.Replace("\r", "").Split('\n');
        Uri? best = null;
        long bestBandwidth = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;
            var bandwidth = AttributeLong(line, "BANDWIDTH") ?? 0;
            var next = i + 1;
            while (next < lines.Length && (string.IsNullOrWhiteSpace(lines[next]) || lines[next].TrimStart().StartsWith('#'))) next++;
            if (next >= lines.Length || best is not null && (preferHighest ? bandwidth <= bestBandwidth : bandwidth >= bestBandwidth)) continue;
            best = new Uri(masterUri, lines[next].Trim());
            bestBandwidth = bandwidth;
        }
        return best ?? masterUri;
    }

    private static long? AttributeLong(string line, string name)
    {
        var marker = name + "=";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += marker.Length;
        var end = line.IndexOf(',', start);
        if (end < 0) end = line.Length;
        return long.TryParse(line.AsSpan(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string AbsolutizeUriAttribute(Uri baseUri, string tag)
    {
        const string marker = "URI=\"";
        var start = tag.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return tag;
        start += marker.Length;
        var end = tag.IndexOf('"', start);
        if (end < 0) return tag;
        return tag[..start] + new Uri(baseUri, tag[start..end]).AbsoluteUri + tag[end..];
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 GrokPlayer/1.0");
        return client;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal readonly record struct FrozenWindow(string Manifest, double SeekSeconds);
    internal readonly record struct CoverageWindow(string Manifest, double DurationSeconds);
    public readonly record struct CoverageFrame(TimeSpan Time, string Path);
    private sealed record ParsedPlaylist(List<Segment> Segments, long MediaSequence, double MaxDuration);
    private sealed record Segment(
        double Duration,
        string Uri,
        string? Map,
        string? Key,
        string? ByteRange,
        bool Discontinuity);
}
