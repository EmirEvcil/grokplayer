using System.Text.Json;

namespace Grok.Player.Core.Download;

public sealed class DownloadSettings
{
    public string Folder { get; set; } = DefaultFolder();
    public int MaxHeight { get; set; } = 1080;
    public int MaxParallel { get; set; } = 1;
    public string Container { get; set; } = "mp4";

    public static readonly string[] Containers = ["mp4", "mkv", "ts"];

    public string ContainerExtension => "." + NormalizeContainer(Container);

    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "GrokPlayer");

    public static string StorePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrokPlayer", "downloads.json");

    public static DownloadSettings Load(string? path = null)
    {
        var file = path ?? StorePath();
        try
        {
            if (File.Exists(file))
            {
                var loaded = JsonSerializer.Deserialize<DownloadSettings>(File.ReadAllText(file));
                if (loaded is not null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.Folder))
                    {
                        loaded.Folder = DefaultFolder();
                    }

                    loaded.MaxHeight = loaded.MaxHeight is 360 or 480 or 720 or 1080 or 1440 or 2160 or 0
                        ? loaded.MaxHeight
                        : 1080;
                    loaded.MaxParallel = Math.Clamp(loaded.MaxParallel, 1, 4);
                    loaded.Container = NormalizeContainer(loaded.Container);
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
        }

        return new DownloadSettings();
    }

    public void Save(string? path = null)
    {
        var file = path ?? StorePath();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this));
    }

    public string HeightLabel => MaxHeight == 0 ? "Best" : MaxHeight + "p";

    public static string NormalizeContainer(string? value)
    {
        var ext = (value ?? "").Trim().TrimStart('.').ToLowerInvariant();
        return ext is "mkv" or "ts" or "webm" ? ext : "mp4";
    }
}
