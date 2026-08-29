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
    public void Recovers_a_protocol_url_split_on_ampersands()
    {
        var parsed = InstanceLaunchArgs.Parse(
        [
            "--stream",
            "grokplayer://open?url=https://www.youtube.com/watch?v=Qtl8lJwbd4g",
            "sub=en",
            "caption=https://www.youtube.com/api/timedtext?v=Qtl8lJwbd4g"
        ]);
        Assert.Contains("sub=en", parsed.Path, StringComparison.Ordinal);
        Assert.Contains("caption=", parsed.Path, StringComparison.Ordinal);
        Assert.True(ExternalOpen.TryParse(parsed.Path, out var open));
        Assert.Equal("en", open.SubLang);
        Assert.Contains("timedtext", open.CaptionUrl, StringComparison.Ordinal);

        var recovered = InstanceLaunchArgs.RecoverProtocol(
            "\"C:\\\\app\\\\GrokPlayer.exe\" --stream \"grokplayer://open?url=https://youtu.be/x&sub=tr:asr&caption=https://example/vtt\"");
        Assert.Equal("grokplayer://open?url=https://youtu.be/x&sub=tr:asr&caption=https://example/vtt", recovered);
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
