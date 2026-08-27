using System.Diagnostics;

namespace Grok.Player.Core.Download;

internal static class FfmpegMux
{
    internal static string? LastError { get; private set; }

    public static bool TryRemux(string video, string? audio, string output)
    {
        LastError = null;
        var ffmpeg = Find();
        if (ffmpeg is null)
        {
            LastError = "ffmpeg not found";
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string[] attempts = string.IsNullOrWhiteSpace(audio)
            ? [$"-hide_banner -y -i \"{video}\" -c copy \"{output}\""]
            :
            [
                $"-hide_banner -y -i \"{video}\" -i \"{audio}\" -map 0:v:0 -map 1:a:0 -c copy -shortest \"{output}\"",
                $"-hide_banner -y -i \"{video}\" -i \"{audio}\" -c copy -shortest \"{output}\""
            ];

        foreach (var args in attempts)
        {
            if (Run(ffmpeg, args) && File.Exists(output) && new FileInfo(output).Length > 1024)
            {
                return true;
            }
        }

        LastError ??= "ffmpeg remux failed";
        return false;
    }

    internal static string? Find()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var winget = Path.Combine(
            local,
            @"Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.0.1-full_build\bin\ffmpeg.exe");
        if (File.Exists(winget))
        {
            return winget;
        }

        foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
        {
            var found = FindOnPath(name);
            if (found is not null &&
                !found.Contains(@"\Python", StringComparison.OrdinalIgnoreCase) &&
                !found.Contains("/Python", StringComparison.OrdinalIgnoreCase))
            {
                return found;
            }
        }

        var python = Path.Combine(local, @"Programs\Python\Python311\Scripts\ffmpeg.exe");
        return File.Exists(python) ? python : null;
    }

    private static bool Run(string ffmpeg, string args)
    {
        try
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
                LastError = "could not start ffmpeg";
                return false;
            }

            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(120_000))
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
                LastError = "ffmpeg exit " + process.ExitCode + " " +
                            stderr[^Math.Min(240, stderr.Length)..].Trim();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    private static string? FindOnPath(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths.Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
    }
}
