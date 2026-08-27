using Grok.Player.Core.Launch;
using Grok.Player.Core.Playlist;

namespace Grok.Player.Core.Tests;

public sealed class InstanceLaunchArgsTests
{
    [Fact]
    public void Parse_reads_file_and_settings()
    {
        var parsed = InstanceLaunchArgs.Parse(
        [
            "--volume", "42.5",
            "--mute",
            "--loop", "one",
            "--ontop",
            "--cinema",
            @"C:\media\clip.mp4"
        ]);

        Assert.Equal(@"C:\media\clip.mp4", parsed.Path);
        Assert.Equal(42.5, parsed.Volume);
        Assert.True(parsed.Mute);
        Assert.Equal(LoopMode.One, parsed.Loop);
        Assert.True(parsed.AlwaysOnTop);
        Assert.True(parsed.Cinema);
    }

    [Fact]
    public void Roundtrip_command_line_keeps_settings()
    {
        var original = new InstanceLaunchArgs
        {
            Path = @"C:\videos\my file.mkv",
            Volume = 80,
            Mute = true,
            Loop = LoopMode.Playlist,
            AlwaysOnTop = true,
            Cinema = false
        };

        var parsed = InstanceLaunchArgs.Parse(original.ToArgumentList());
        Assert.Equal(original.Path, parsed.Path);
        Assert.Equal(original.Volume, parsed.Volume);
        Assert.Equal(original.Mute, parsed.Mute);
        Assert.Equal(original.Loop, parsed.Loop);
        Assert.Equal(original.AlwaysOnTop, parsed.AlwaysOnTop);
        Assert.False(parsed.Cinema);
    }

    [Fact]
    public void New_instance_flag_roundtrips()
    {
        var original = new InstanceLaunchArgs
        {
            Path = @"C:\videos\clip.mp4",
            NewInstance = true
        };

        var parsed = InstanceLaunchArgs.Parse(original.ToArgumentList());
        Assert.True(parsed.NewInstance);
        Assert.Equal(@"C:\videos\clip.mp4", parsed.Path);
        Assert.Contains("--new-instance", original.ToArgumentList());
    }

    [Fact]
    public void Existing_instance_drop_is_enough_without_a_new_process()
    {
        var dir = InstanceIpc.DropDirectory();
        Directory.CreateDirectory(dir);
        foreach (var leftover in Directory.GetFiles(dir, "*.open"))
        {
            File.Delete(leftover);
        }

        var payload = "grokplayer://open?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=dQw4w9wgBcQ");
        Assert.True(InstanceIpc.TryEnqueueDrop(payload));
        Assert.Contains(payload, InstanceIpc.DrainDrops());
    }

    private static string[] Split(string commandLine)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (ch == ' ' && !quoted)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return [.. parts];
    }
}
