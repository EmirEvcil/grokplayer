using System.Diagnostics;

namespace Grok.Player.Core.IntegrationTests.Support;

internal static class GeneratedMedia
{
    public static string? TryCreateSample(int durationSeconds = 3)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            LastError = "ffmpeg not found";
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"grok-player-sample-{durationSeconds}s.mp4");
        if (File.Exists(path) && new FileInfo(path).Length > 1000)
        {
            return path;
        }

        var args =
            $"-y -f lavfi -i testsrc=duration={durationSeconds}:size=320x240:rate=24 " +
            $"-f lavfi -i sine=frequency=440:duration={durationSeconds} " +
            "-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest " +
            $"\"{path}\"";

        if (RunFfmpeg(ffmpeg, args) && File.Exists(path))
        {
            return path;
        }

        LastError = "ffmpeg encode failed: " + ffmpeg;
        return null;
    }

    public static string? LastError { get; private set; }

    private static bool RunFfmpeg(string ffmpeg, string args)
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
            LastError = "could not start " + ffmpeg;
            return false;
        }

        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception)
            {
            }

            LastError = "ffmpeg timed out";
            return false;
        }

        if (process.ExitCode != 0)
        {
            LastError = "ffmpeg exit " + process.ExitCode + " " + stderr[^Math.Min(400, stderr.Length)..];
        }

        return process.ExitCode == 0;
    }

    public static string? TryCreateVideoOnly(string source)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null || !File.Exists(source))
        {
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), "grok-player-video-only.mp4");
        return RunFfmpeg(ffmpeg, $"-y -i \"{source}\" -an -c:v copy \"{path}\"") ? path : null;
    }

    public static string? TryCreateAudioOnly(string source)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null || !File.Exists(source))
        {
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), "grok-player-audio-only.m4a");
        return RunFfmpeg(ffmpeg, $"-y -i \"{source}\" -vn -c:a copy \"{path}\"") ? path : null;
    }

    public static string? TryCreateUnicodeCopy(string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "grok-player-İstanbul");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "测试-sample.mp4");
        File.Copy(source, dest, overwrite: true);
        return dest;
    }

    private static string? FindFfmpeg()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var ordered = new List<string>();
        var winget = Path.Combine(local,
            @"Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.0.1-full_build\bin\ffmpeg.exe");
        if (File.Exists(winget))
        {
            ordered.Add(winget);
        }

        var fromPath = FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg");
        if (!string.IsNullOrWhiteSpace(fromPath) &&
            !fromPath.Contains(@"Python", StringComparison.OrdinalIgnoreCase))
        {
            ordered.Add(fromPath);
        }

        return ordered.FirstOrDefault(File.Exists);
    }

    private static string? FindOnPath(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths.Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
    }
}
