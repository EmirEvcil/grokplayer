using System.Text.Json;
using System.Text.Json.Serialization;
using Grok.Player.Core.Media;

namespace Grok.Player.Core.Subtitles;

public enum StreamSubtitleMode
{
    Off = 0,
    On = 1,
    Browser = 2
}

public sealed class StreamSubtitleSettings
{
    public StreamSubtitleMode Mode { get; set; } = StreamSubtitleMode.On;

    public string? LastAudio { get; set; }

    public string? LastSub { get; set; }

    [JsonIgnore]
    public string? Store { get; set; }

    public static string StorePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrokPlayer", "stream-subtitles.json");

    public static StreamSubtitleSettings Load(string? path = null)
    {
        var file = path ?? StorePath();
        try
        {
            if (File.Exists(file))
            {
                var loaded = JsonSerializer.Deserialize<StreamSubtitleSettings>(File.ReadAllText(file));
                if (loaded is not null)
                {
                    if (!Enum.IsDefined(loaded.Mode))
                    {
                        loaded.Mode = StreamSubtitleMode.On;
                    }

                    if (!MediaLanguage.IsPlausible(MediaLanguage.Normalize(loaded.LastAudio)))
                    {
                        loaded.LastAudio = null;
                    }

                    if (!MediaLanguage.IsPlausible(MediaLanguage.Normalize(loaded.LastSub)))
                    {
                        loaded.LastSub = null;
                    }

                    loaded.Store = file;
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
        }

        return new StreamSubtitleSettings { Store = file };
    }

    public void Save(string? path = null)
    {
        var file = path ?? Store ?? StorePath();
        Store = file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this));
    }

    public bool Enabled => Mode != StreamSubtitleMode.Off;
}
