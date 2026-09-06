using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Grok.Player.Core.Presentation;
using Microsoft.UI.Dispatching;

namespace Grok.Player.App.Link;

public sealed class LinkServer : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly Func<PlaybackViewModel> _view;
    private readonly string _id = LinkProtocol.DeviceId();
    private readonly string _name = LinkProtocol.DeviceName();
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LinkJobDto> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _incomingPins = new();
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private string? _pendingTvId;
    private string? _pendingTvName;
    private string? _pendingPin;
    public event Action? Changed;
    public event Action<string, string>? PairOffered;

    public LinkServer(DispatcherQueue ui, Func<PlaybackViewModel> view)
    {
        _ui = ui;
        _view = view;
        LoadTokens();
    }

    public string Id => _id;
    public string Name => _name;
    public int Port { get; private set; } = LinkProtocol.HttpPort;
    public string Host => LinkProtocol.LanAddress();
    public IReadOnlyDictionary<string, string> Tokens => _tokens;
    public IEnumerable<LinkJobDto> Jobs => _jobs.Values;

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        Port = BindHttp();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, LinkProtocol.DiscoverPort))
        {
            EnableBroadcast = true,
        };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _ = Task.Run(() => AcceptLoop(_cts.Token));
        _ = Task.Run(() => UdpLoop(_cts.Token));
        _ = Task.Run(() => HelloLoop(_cts.Token));
        TryOpenFirewall();
    }

    public bool TryAcceptPin(string pin)
    {
        var trimmed = new string((pin ?? "").Where(char.IsDigit).ToArray());
        if (trimmed.Length != 6 || _pendingTvId is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_pendingPin) && _pendingPin != trimmed)
        {
            return false;
        }

        var token = Guid.NewGuid().ToString("N");
        _tokens[_pendingTvId] = token;
        SaveTokens();
        SendUdp(new UdpNote
        {
            T = "ok",
            Tv = _pendingTvId,
            Pc = _id,
            Name = _name,
            Host = Host,
            Port = Port,
            Token = token,
        });
        _pendingTvId = null;
        _pendingTvName = null;
        Changed?.Invoke();
        return true;
    }

    private void RememberOffer(string tvId, string? name, string? pin)
    {
        var fresh = _pendingTvId != tvId ||
            (!string.IsNullOrWhiteSpace(pin) && _pendingPin != pin);
        _pendingTvId = tvId;
        _pendingTvName = name;
        if (!string.IsNullOrWhiteSpace(pin))
        {
            _pendingPin = pin;
        }

        if (!fresh)
        {
            return;
        }

        PairOffered?.Invoke(tvId, name ?? "TV");
        Changed?.Invoke();
    }

    public void Forget(string tvId)
    {
        _tokens.TryRemove(tvId, out _);
        SaveTokens();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _tcp?.Stop(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _cts?.Dispose();
    }

    private int BindHttp()
    {
        var port = LinkProtocol.HttpPort;
        for (var i = 0; i < 8; i++)
        {
            try
            {
                _tcp = new TcpListener(IPAddress.Any, port);
                _tcp.Start();
                return port;
            }
            catch
            {
                port++;
            }
        }

        _tcp = new TcpListener(IPAddress.Any, 0);
        _tcp.Start();
        return ((IPEndPoint)_tcp.LocalEndpoint).Port;
    }

    private async Task HelloLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            SendUdp(new UdpNote
            {
                T = "hello",
                Pc = _id,
                Name = _name,
                Host = Host,
                Port = Port,
            });
            try { await Task.Delay(2000, token); } catch { return; }
        }
    }

    private async Task UdpLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _udp is not null)
        {
            UdpReceiveResult packet;
            try { packet = await _udp.ReceiveAsync(token); }
            catch { return; }

            UdpNote? note;
            try
            {
                note = JsonSerializer.Deserialize<UdpNote>(packet.Buffer, LinkProtocol.Json);
            }
            catch { continue; }

            if (note is null)
            {
                continue;
            }

            if (note.T == "offer" && !string.IsNullOrWhiteSpace(note.Tv))
            {
                RememberOffer(note.Tv, note.Name, note.Pin);
            }
        }
    }

    private void SendUdp(UdpNote note)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(note, LinkProtocol.Json);
            using var send = new UdpClient();
            send.EnableBroadcast = true;
            send.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, LinkProtocol.DiscoverPort));
        }
        catch
        {
        }
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _tcp is not null)
        {
            TcpClient client;
            try { client = await _tcp.AcceptTcpClientAsync(token); }
            catch { return; }

            _ = Task.Run(() => Serve(client, token), token);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            stream.ReadTimeout = 120_000;
            stream.WriteTimeout = 30_000;
            var header = await ReadHeaders(stream, token);
            if (header is null)
            {
                return;
            }

            var (method, path, headers, length) = header.Value;
            var tokenOk = Authorized(headers);
            try
            {
                if (method == "GET" && path == "/v1/hello")
                {
                    await WriteJson(stream, 200, new { ok = true, id = _id, name = _name, port = Port });
                    return;
                }

                if (method == "POST" && path == "/v1/expect")
                {
                    var raw = await ReadBody(stream, length, token);
                    var note = JsonSerializer.Deserialize<UdpNote>(raw, LinkProtocol.Json);
                    if (note?.Tv is { Length: > 0 } && note.Pin is { Length: 6 })
                    {
                        RememberOffer(note.Tv, note.Name, note.Pin);
                    }

                    await WriteJson(stream, 200, new { ok = true });
                    return;
                }

                if (method == "POST" && path == "/v1/claim")
                {
                    var raw = await ReadBody(stream, length, token);
                    var note = JsonSerializer.Deserialize<UdpNote>(raw, LinkProtocol.Json);
                    if (note?.Tv is { } tvId &&
                        _tokens.TryGetValue(tvId, out var ready) &&
                        !string.IsNullOrWhiteSpace(ready))
                    {
                        await WriteJson(stream, 200, new
                        {
                            ok = true,
                            token = ready,
                            pc = _id,
                            name = _name,
                            host = Host,
                            port = Port,
                        });
                        return;
                    }

                    await WriteJson(stream, 204, new { ok = false });
                    return;
                }

                if (!tokenOk)
                {
                    await WriteText(stream, 401, "auth");
                    return;
                }

                if (method == "GET" && path == "/v1/state")
                {
                    await WriteJson(stream, 200, Snapshot());
                    return;
                }

                if (method == "POST" && path == "/v1/cmd")
                {
                    var body = await ReadBody(stream, length, token);
                    var cmd = JsonSerializer.Deserialize<LinkCmd>(body, LinkProtocol.Json) ?? new LinkCmd();
                    RunOnUi(() => Apply(cmd));
                    await WriteJson(stream, 200, Snapshot());
                    return;
                }

                if (method == "PUT" && path.StartsWith("/v1/inbox/", StringComparison.Ordinal))
                {
                    var name = Uri.UnescapeDataString(path["/v1/inbox/".Length..]);
                    var play = Header(headers, "X-Play") != "queue";
                    var title = Header(headers, "X-Title") ?? Path.GetFileNameWithoutExtension(name);
                    var sidecar = Header(headers, "X-Sidecar");
                    var dest = Path.Combine(LinkProtocol.InboxDir(), SafeName(name));
                    var job = new LinkJobDto
                    {
                        Id = Guid.NewGuid().ToString("N")[..8],
                        Title = title,
                        Kind = "copy",
                        Status = "receiving",
                        Total = length,
                    };
                    _jobs[job.Id] = job;
                    Changed?.Invoke();
                    await SaveFile(stream, dest, length, job, token);
                    job.Status = "ready";
                    job.Done = job.Total;
                    Changed?.Invoke();
                    RunOnUi(() =>
                    {
                        var view = _view();
                        if (!string.IsNullOrWhiteSpace(sidecar))
                        {
                            view.Open(dest);
                        }
                        else
                        {
                            view.EnqueueOrPlay(dest, play, title);
                        }
                    });
                    await WriteJson(stream, 200, new { ok = true, path = dest, job = job.Id });
                    return;
                }

                await WriteText(stream, 404, "no");
            }
            catch
            {
                try { await WriteText(stream, 500, "err"); } catch { }
            }
        }
    }

    private bool Authorized(Dictionary<string, string> headers)
    {
        var sent = Header(headers, LinkProtocol.TokenHeader);
        return !string.IsNullOrWhiteSpace(sent) && _tokens.Values.Contains(sent);
    }

    private LinkStateDto Snapshot()
    {
        LinkStateDto dto = new();
        var done = new ManualResetEventSlim(false);
        if (!_ui.TryEnqueue(() =>
            {
                try { dto = Capture(); }
                finally { done.Set(); }
            }))
        {
            return dto;
        }

        done.Wait(400);
        return dto;
    }

    private LinkStateDto Capture()
    {
        var view = _view();
        var list = view.Playlist;
        var items = new List<LinkItemDto>();
        for (var i = 0; i < list.Items.Count; i++)
        {
            items.Add(new LinkItemDto
            {
                Index = i,
                Title = list.Items[i].Title,
                Current = i == list.CurrentIndex,
            });
        }

        var audio = view.PlayingAudioChoices()
            .Select(t => new LinkTrackDto { Index = t.Index, Label = t.Label, Selected = t.Selected })
            .ToList();
        var subs = view.PlayingSubtitleChoices()
            .Select(t => new LinkTrackDto { Index = t.Index, Label = t.Label, Selected = t.Selected })
            .ToList();
        var selectedAudio = audio.FirstOrDefault(a => a.Selected);
        var dub = selectedAudio is null ? null
            : LooksDub(selectedAudio.Label) ? "dub" : "original";
        var height = view.Player.HasMedia ? view.VideoHeightLabel() : null;
        return new LinkStateDto
        {
            Playing = view.IsPlaying,
            Paused = view.HasMedia && !view.IsPlaying,
            HasMedia = view.HasMedia,
            PositionMs = (long)view.Player.Position.TotalMilliseconds,
            DurationMs = (long)(view.Player.Duration?.TotalMilliseconds ?? 0),
            Volume = view.Volume,
            Title = view.HasMedia ? view.Title : null,
            Path = list.CurrentPath,
            PlaylistIndex = list.CurrentIndex,
            Playlist = items,
            Audio = audio,
            Subs = subs,
            Resolution = height,
            Dubbing = dub,
            Jobs = _jobs.Values.OrderByDescending(j => j.Id).Take(8).ToList(),
        };
    }

    private void Apply(LinkCmd cmd)
    {
        var view = _view();
        switch (cmd.Op)
        {
            case "play":
                if (!view.IsPlaying) view.TogglePlayPause();
                break;
            case "pause":
                if (view.IsPlaying) view.TogglePlayPause();
                break;
            case "toggle":
                view.TogglePlayPause();
                break;
            case "seek" when cmd.Ms is { } ms:
                view.ApplySeek(ms / 1000.0);
                break;
            case "seekBy" when cmd.Ms is { } delta:
                view.SeekBy(TimeSpan.FromMilliseconds(delta));
                break;
            case "volume" when cmd.Value is { } vol:
                view.Volume = Math.Clamp(vol, 0, 100);
                break;
            case "next":
                view.PlayIndex(view.Playlist.CurrentIndex + 1);
                break;
            case "prev":
                view.PlayIndex(Math.Max(0, view.Playlist.CurrentIndex - 1));
                break;
            case "playIndex" when cmd.Index is { } idx:
                view.PlayIndex(idx);
                break;
            case "sub" when cmd.Index is { } sub:
                view.SelectPlayingSubtitle(sub);
                break;
            case "audio" when cmd.Index is { } aud:
                view.SelectPlayingAudio(aud);
                break;
            case "dub" when cmd.Lang is { } lang:
                PickDub(view, lang);
                break;
            case "open" when !string.IsNullOrWhiteSpace(cmd.Url):
                view.EnqueueOrPlay(cmd.Url!, cmd.Play != false, cmd.Title);
                if (!string.IsNullOrWhiteSpace(cmd.SubUrl))
                {
                    _ = FetchSidecar(cmd.SubUrl!, view);
                }
                break;
        }
    }

    private static void PickDub(PlaybackViewModel view, string lang)
    {
        var tracks = view.PlayingAudioChoices();
        var wantDub = lang.Equals("dub", StringComparison.OrdinalIgnoreCase) ||
                      lang.Equals("tr", StringComparison.OrdinalIgnoreCase);
        var match = tracks.FirstOrDefault(t => wantDub ? LooksDub(t.Label) : !LooksDub(t.Label));
        if (tracks.Count > 0 && (wantDub ? LooksDub(match.Label) : !LooksDub(match.Label)))
        {
            view.SelectPlayingAudio(match.Index);
        }
    }

    private static bool LooksDub(string label)
    {
        var t = label.ToLowerInvariant();
        return t.Contains("tur") || t.Contains("tr") || t.Contains("dub") || t.Contains("dublaj") || t.Contains("türk");
    }

    private static async Task FetchSidecar(string url, PlaybackViewModel view)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var bytes = await http.GetByteArrayAsync(url);
            var dest = Path.Combine(LinkProtocol.InboxDir(), "sidecar-" + Guid.NewGuid().ToString("N")[..8] + ".srt");
            await File.WriteAllBytesAsync(dest, bytes);
            view.Open(dest);
        }
        catch
        {
        }
    }

    private void RunOnUi(Action action)
    {
        if (!_ui.TryEnqueue(() => action()))
        {
            action();
        }
    }

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, long Length)?> ReadHeaders(
        NetworkStream stream, CancellationToken token)
    {
        var buf = new MemoryStream();
        var one = new byte[1];
        while (buf.Length < 64_000)
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1), token);
            if (n <= 0) return null;
            buf.WriteByte(one[0]);
            if (buf.Length >= 4)
            {
                var a = buf.GetBuffer();
                var len = (int)buf.Length;
                if (a[len - 4] == '\r' && a[len - 3] == '\n' && a[len - 2] == '\r' && a[len - 1] == '\n')
                {
                    break;
                }
            }
        }

        var text = Encoding.ASCII.GetString(buf.GetBuffer(), 0, (int)buf.Length);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        headers.TryGetValue("Content-Length", out var cl);
        long.TryParse(cl, out var length);
        return (parts[0].ToUpperInvariant(), parts[1], headers, length);
    }

    private static async Task<byte[]> ReadBody(NetworkStream stream, long length, CancellationToken token)
    {
        if (length <= 0) return [];
        var data = new byte[length];
        var got = 0;
        while (got < data.Length)
        {
            var n = await stream.ReadAsync(data.AsMemory(got, data.Length - got), token);
            if (n <= 0) break;
            got += n;
        }

        return data;
    }

    private static async Task SaveFile(NetworkStream stream, string dest, long length, LinkJobDto job, CancellationToken token)
    {
        await using var file = File.Create(dest);
        var buf = new byte[64 * 1024];
        long got = 0;
        while (got < length)
        {
            var take = (int)Math.Min(buf.Length, length - got);
            var n = await stream.ReadAsync(buf.AsMemory(0, take), token);
            if (n <= 0) break;
            await file.WriteAsync(buf.AsMemory(0, n), token);
            got += n;
            job.Done = got;
        }
    }

    private static async Task WriteJson(NetworkStream stream, int code, object body)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body, LinkProtocol.Json);
        await Write(stream, code, "application/json", json);
    }

    private static async Task WriteText(NetworkStream stream, int code, string text)
    {
        await Write(stream, code, "text/plain", Encoding.UTF8.GetBytes(text));
    }

    private static async Task Write(NetworkStream stream, int code, string type, byte[] body)
    {
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} OK\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static string? Header(Dictionary<string, string> headers, string key) =>
        headers.TryGetValue(key, out var value) ? value : null;

    private static string SafeName(string name)
    {
        var file = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            file = file.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(file) ? "video.bin" : file;
    }

    private string TokenPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GrokPlayer",
        "link-tokens.json");

    private void LoadTokens()
    {
        try
        {
            var path = TokenPath();
            if (!File.Exists(path)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (map is null) return;
            foreach (var pair in map) _tokens[pair.Key] = pair.Value;
        }
        catch { }
    }

    private void SaveTokens()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokenPath())!);
            File.WriteAllText(TokenPath(), JsonSerializer.Serialize(_tokens));
        }
        catch { }
    }

    private static void TryOpenFirewall()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall add rule name=\"GrokPlayer Link\" dir=in action=allow protocol=TCP localport={LinkProtocol.HttpPort}",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch { }
    }
}

internal static class LinkViewExt
{
    public static string? VideoHeightLabel(this PlaybackViewModel view)
    {
        return view.HasMedia ? view.TitleFormat : null;
    }
}
