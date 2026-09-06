using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grok.Player.App.Link;

internal static class LinkProtocol
{
    public const int DiscoverPort = 17421;
    public const int HttpPort = 17422;
    public const string TokenHeader = "X-Grok-Token";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string LanAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                {
                    return addr.Address.ToString();
                }
            }
        }

        return "127.0.0.1";
    }

    public static string DeviceId()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrokPlayer",
            "link-id.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length > 8)
            {
                return existing;
            }
        }

        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, id);
        return id;
    }

    public static string DeviceName()
    {
        var name = Environment.MachineName?.Trim();
        return string.IsNullOrWhiteSpace(name) ? "GrokPlayer PC" : name + " PC";
    }

    public static string InboxDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "GrokPlayer",
            "From TV");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

internal sealed class UdpNote
{
    public string T { get; set; } = "";
    public string? Tv { get; set; }
    public string? Pc { get; set; }
    public string? Name { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Pin { get; set; }
    public string? Token { get; set; }
}

internal sealed class LinkStateDto
{
    public bool Connected { get; set; } = true;
    public bool Playing { get; set; }
    public bool Paused { get; set; }
    public bool HasMedia { get; set; }
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public double Volume { get; set; }
    public string? Title { get; set; }
    public string? Path { get; set; }
    public int PlaylistIndex { get; set; }
    public List<LinkItemDto> Playlist { get; set; } = [];
    public List<LinkTrackDto> Audio { get; set; } = [];
    public List<LinkTrackDto> Subs { get; set; } = [];
    public string? Resolution { get; set; }
    public string? Dubbing { get; set; }
    public List<LinkJobDto> Jobs { get; set; } = [];
}

internal sealed class LinkItemDto
{
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public bool Current { get; set; }
}

internal sealed class LinkTrackDto
{
    public int Index { get; set; }
    public string Label { get; set; } = "";
    public bool Selected { get; set; }
}

public sealed class LinkJobDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public long Done { get; set; }
    public long Total { get; set; }
}

internal sealed class LinkCmd
{
    public string Op { get; set; } = "";
    public long? Ms { get; set; }
    public double? Value { get; set; }
    public int? Index { get; set; }
    public string? Lang { get; set; }
    public string? Url { get; set; }
    public string? Title { get; set; }
    public bool? Play { get; set; }
    public string? SubUrl { get; set; }
}
