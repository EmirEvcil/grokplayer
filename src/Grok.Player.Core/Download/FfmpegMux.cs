using System.Diagnostics;
using Grok.Player.Core.Media;

namespace Grok.Player.Core.Download;

internal static class FfmpegMux
{
    internal static string? LastError { get; private set; }

    public static bool TryRemux(string video, string? audio, string output, IReadOnlyList<string>? extraAudio = null)
    {
        var extras = extraAudio ?? [];
        var labeled = new List<(string Path, string Language, string Name)>();
        if (!string.IsNullOrWhiteSpace(audio) && File.Exists(audio))
        {
            labeled.Add((audio, "", ""));
        }

        foreach (var extra in extras)
        {
            if (File.Exists(extra))
            {
                labeled.Add((extra, "", ""));
            }
        }

        return TryRemux(video, labeled, output);
    }

    public static bool TryRemux(
        string video,
        IReadOnlyList<(string Path, string Language, string Name)> audio,
        string output)
    {
        LastError = null;
        var ffmpeg = Find();
        if (ffmpeg is null)
        {
            LastError = "ffmpeg not found";
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var tracks = (audio ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Path) && File.Exists(item.Path))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var inputs = new List<string> { "-hide_banner -y -i \"" + video + "\"" };
        foreach (var track in tracks)
        {
            inputs.Add("-i \"" + track.Path + "\"");
        }

        var maps = new List<string> { "-map 0:v:0" };
        for (var i = 1; i < inputs.Count; i++)
        {
            maps.Add("-map " + i + ":a:0?");
        }

        var meta = AudioMetadataArgs(tracks);
        var labeled = string.Join(' ', inputs) + " " + string.Join(' ', maps) + meta +
                      " -c copy -shortest \"" + output + "\"";
        var first = tracks.Count > 0 ? tracks[0].Path : null;
        string[] attempts = tracks.Count == 0
            ? [$"-hide_banner -y -i \"{video}\" -c copy \"{output}\""]
            :
            [
                labeled,
                $"-hide_banner -y -i \"{video}\" -i \"{first}\" -map 0:v:0 -map 1:a:0 -c copy -shortest \"{output}\""
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

    internal static string AudioMetadataArgs(IReadOnlyList<(string Path, string Language, string Name)> tracks)
    {
        var parts = new List<string>();
        for (var i = 0; i < tracks.Count; i++)
        {
            var lang = MediaLanguage.Normalize(tracks[i].Language);
            if (lang.Length == 0)
            {
                lang = MediaLanguage.FromName(tracks[i].Name);
            }

            if (lang.Length > 0)
            {
                parts.Add("-metadata:s:a:" + i + " language=" + lang);
            }

            var title = string.IsNullOrWhiteSpace(tracks[i].Name) ? lang : tracks[i].Name.Trim();
            if (!string.IsNullOrWhiteSpace(title))
            {
                parts.Add("-metadata:s:a:" + i + " title=\"" + title.Replace("\"", "'", StringComparison.Ordinal) + "\"");
            }
        }

        return parts.Count == 0 ? "" : " " + string.Join(' ', parts);
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

            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }

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
