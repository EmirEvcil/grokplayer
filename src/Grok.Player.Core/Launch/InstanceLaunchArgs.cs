using Grok.Player.Core.Playlist;

namespace Grok.Player.Core.Launch;

public sealed class InstanceLaunchArgs
{
    public string? Path { get; init; }
    public double Volume { get; init; } = 100;
    public bool Mute { get; init; }
    public LoopMode Loop { get; init; } = LoopMode.Off;
    public bool AlwaysOnTop { get; init; }
    public bool Cinema { get; init; }
    public bool NewInstance { get; init; }

    public static string? RecoverProtocol(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var at = commandLine.IndexOf("grokplayer:", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : commandLine[at..].Trim().Trim('"');
    }

    public InstanceLaunchArgs WithPath(string? path) =>
        new()
        {
            Path = path,
            Volume = Volume,
            Mute = Mute,
            Loop = Loop,
            AlwaysOnTop = AlwaysOnTop,
            Cinema = Cinema,
            NewInstance = NewInstance
        };

    private static string JoinProtocolTail(string[] list, ref int i)
    {
        var path = list[i].Trim('"');
        if (!path.StartsWith("grokplayer:", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("://", StringComparison.Ordinal))
        {
            return path;
        }

        while (i + 1 < list.Length)
        {
            var next = list[i + 1];
            if (next.StartsWith("--", StringComparison.Ordinal))
            {
                break;
            }

            var eq = next.IndexOf('=');
            if (eq > 0 && next[..eq].All(char.IsLetter))
            {
                path += "&" + next;
                i++;
                continue;
            }

            break;
        }

        return path;
    }

    public static InstanceLaunchArgs Parse(IEnumerable<string> args)
    {
        string? path = null;
        var volume = 100d;
        var mute = false;
        var loop = LoopMode.Off;
        var onTop = false;
        var cinema = false;
        var newInstance = false;
        var list = args.ToArray();
        for (var i = 0; i < list.Length; i++)
        {
            var item = list[i];
            if (TryFlag(item, "--volume", list, ref i, out var volumeText) &&
                double.TryParse(volumeText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                volume = parsed;
            }
            else if (TryBool(item, "--mute", list, ref i, out var muteValue))
            {
                mute = muteValue;
            }
            else if (TryFlag(item, "--loop", list, ref i, out var loopText))
            {
                loop = ParseLoop(loopText);
            }
            else if (TryBool(item, "--ontop", list, ref i, out var onTopValue))
            {
                onTop = onTopValue;
            }
            else if (TryBool(item, "--cinema", list, ref i, out var cinemaValue))
            {
                cinema = cinemaValue;
            }
            else if (TryBool(item, "--new-instance", list, ref i, out var newValue))
            {
                newInstance = newValue;
            }
            else if (item.Equals("--stream", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < list.Length)
                {
                    i++;
                    path = JoinProtocolTail(list, ref i);
                }
            }
            else if (!item.StartsWith("--", StringComparison.Ordinal) && path is null)
            {
                path = JoinProtocolTail(list, ref i);
            }
        }

        return new InstanceLaunchArgs
        {
            Path = path,
            Volume = volume,
            Mute = mute,
            Loop = loop,
            AlwaysOnTop = onTop,
            Cinema = cinema,
            NewInstance = newInstance
        };
    }

    public IReadOnlyList<string> ToArgumentList()
    {
        var parts = new List<string>
        {
            "--volume",
            Volume.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "--loop",
            FormatLoop(Loop)
        };
        if (Mute)
        {
            parts.Add("--mute");
        }

        if (AlwaysOnTop)
        {
            parts.Add("--ontop");
        }

        if (Cinema)
        {
            parts.Add("--cinema");
        }

        if (NewInstance)
        {
            parts.Add("--new-instance");
        }

        if (!string.IsNullOrWhiteSpace(Path))
        {
            parts.Add(Path);
        }

        return parts;
    }

    public string ToCommandLine() =>
        string.Join(' ', ToArgumentList().Select(Quote));

    private static LoopMode ParseLoop(string text) =>
        text.Equals("one", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("file", StringComparison.OrdinalIgnoreCase)
            ? LoopMode.One
            : text.Equals("playlist", StringComparison.OrdinalIgnoreCase)
                ? LoopMode.Playlist
                : LoopMode.Off;

    private static string FormatLoop(LoopMode loop) => loop switch
    {
        LoopMode.One => "one",
        LoopMode.Playlist => "playlist",
        _ => "off"
    };

    private static bool TryFlag(string item, string name, string[] all, ref int index, out string value)
    {
        value = "";
        if (item.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= all.Length)
            {
                return false;
            }

            value = all[++index];
            return true;
        }

        var prefix = name + "=";
        if (item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = item[prefix.Length..];
            return true;
        }

        return false;
    }

    private static bool TryBool(string item, string name, string[] all, ref int index, out bool value)
    {
        value = false;
        if (item.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (TryFlag(item, name, all, ref index, out var text))
        {
            value = text is "1" or "true" or "yes";
            return true;
        }

        return false;
    }

    private static string Quote(string value)
    {
        if (!value.Contains(' ') && !value.Contains('"'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
