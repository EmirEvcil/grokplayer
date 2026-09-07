using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Grok.Player.Core.Media;
using Grok.Player.Core.Playlist;
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
    private readonly ConcurrentDictionary<string, string> _inboxKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LinkJobDto> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _incomingPins = new();
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private string? _pendingTvId;
    private string? _pendingTvName;
    private string? _pendingPin;
    private string? _acceptedPin;
    private string? _tvOpened;
    private bool _inboxRestored;
    public event Action? Changed;
    public event Action<string, string>? PairOffered;

    public LinkServer(DispatcherQueue ui, Func<PlaybackViewModel> view)
    {
        _ui = ui;
        _view = view;
        LoadTokens();
        LoadInboxKeys();
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
        _udp = BindDiscover();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
        if (_udp is not null)
        {
            _ = Task.Run(() => UdpLoop(_cts.Token));
            _ = Task.Run(() => HelloLoop(_cts.Token));
        }

        TryOpenFirewall();
    }

    private static UdpClient? BindDiscover()
    {
        try
        {
            var udp = new UdpClient();
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, LinkProtocol.DiscoverPort));
            udp.EnableBroadcast = true;
            return udp;
        }
        catch
        {
            return null;
        }
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
        _acceptedPin = trimmed;
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
        _pendingPin = null;
        Changed?.Invoke();
        return true;
    }

    public bool PairPending => _pendingTvId is not null;

    private void RememberOffer(string tvId, string? name, string? pin)
    {
        if (!string.IsNullOrWhiteSpace(pin) && pin == _acceptedPin)
        {
            return;
        }

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
        EndSession();
        Changed?.Invoke();
    }

    private void EndSession()
    {
    }

    public void AnnounceBye()
    {
        SendUdp(new UdpNote
        {
            T = "bye",
            Pc = _id,
            Name = _name,
            Host = Host,
            Port = Port,
        });
    }

    public void Dispose()
    {
        try { AnnounceBye(); } catch { }
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
            SendHello();
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

            if (note.T == "who")
            {
                SendHello(packet.RemoteEndPoint);
                continue;
            }

            if (note.T == "offer" && !string.IsNullOrWhiteSpace(note.Tv))
            {
                RememberOffer(note.Tv, note.Name, note.Pin);
                SendHello(packet.RemoteEndPoint);
            }
        }
    }

    private void SendHello(IPEndPoint? dest = null)
    {
        SendUdp(new UdpNote
        {
            T = "hello",
            Pc = _id,
            Name = _name,
            Host = Host,
            Port = Port,
        }, dest);
    }

    private void SendUdp(UdpNote note, IPEndPoint? dest = null)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(note, LinkProtocol.Json);
            if (dest is not null && _udp is not null)
            {
                _udp.Send(bytes, bytes.Length, dest);
                return;
            }

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
                    if (cmd.Op == "unpair")
                    {
                        var sent = Header(headers, LinkProtocol.TokenHeader);
                        var tv = _tokens.FirstOrDefault(pair => pair.Value == sent);
                        if (!string.IsNullOrWhiteSpace(tv.Key))
                        {
                            Forget(tv.Key);
                        }
                        EndSession();
                        RunOnUi(() => _view().SuppressResumePrompt());
                        await WriteJson(stream, 200, Snapshot());
                        return;
                    }

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
                    var key = Header(headers, "X-Key") ?? InboxKey(name, length);
                    var startOver = Header(headers, "X-Resume") != "continue";
                    var existing = ResolveInbox(key, title, name, length);
                    string dest;
                    if (existing is not null)
                    {
                        await Drain(stream, length, token);
                        dest = existing;
                    }
                    else
                    {
                        dest = UniqueInboxPath(name);
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
                    }

                    RememberInbox(key, dest, title);
                    RunOnUi(() =>
                    {
                        var view = _view();
                        if (!string.IsNullOrWhiteSpace(sidecar))
                        {
                            view.Open(dest);
                        }
                        else
                        {
                            view.EnqueueOrPlay(dest, play, title, startOver);
                        }
                    });
                    await WriteJson(stream, 200, new { ok = true, path = dest, job = key });
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
                try
                {
                    EnsureInboxRestored();
                    dto = Capture();
                }
                finally { done.Set(); }
            }))
        {
            return dto;
        }

        done.Wait(2000);
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
                Key = InboxKeyOf(list.Items[i].Path, list.Items[i].Title),
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
            Have = CaptureHave(list),
            Resume = view.TvResumeOffer is { } offer
                ? new LinkResumeDto
                {
                    Title = offer.Label,
                    Seconds = offer.Seconds,
                    Duration = offer.Duration,
                }
                : null,
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
                view.PlayLocalIndex(view.Playlist.CurrentIndex + 1);
                break;
            case "prev":
                view.PlayLocalIndex(Math.Max(0, view.Playlist.CurrentIndex - 1));
                break;
            case "playIndex" when cmd.Index is { } idx:
                view.PlayLocalIndex(idx);
                break;
            case "resumeContinue":
                view.ContinueResume();
                break;
            case "resumeStart":
                view.DeclineResume();
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
                _tvOpened = cmd.Url;
                view.EnqueueOrPlay(cmd.Url!, cmd.Play != false, cmd.Title, cmd.StartOver);
                if (!string.IsNullOrWhiteSpace(cmd.SubUrl))
                {
                    _ = FetchSidecar(cmd.SubUrl!, view);
                }
                break;
            case "openKnown":
                var known = ResolveInbox(cmd.Key, cmd.Title, null, 0);
                if (!string.IsNullOrWhiteSpace(known))
                {
                    view.EnqueueOrPlay(known, cmd.Play != false, cmd.Title, cmd.StartOver ?? true);
                }
                else if (!string.IsNullOrWhiteSpace(cmd.Title))
                {
                    var match = view.Playlist.Items.FirstOrDefault(item =>
                        string.Equals(item.Title, cmd.Title, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        view.EnqueueOrPlay(match.Path, cmd.Play != false, cmd.Title, cmd.StartOver ?? true);
                    }
                }
                break;
            case "disconnect":
                EndSession();
                view.SuppressResumePrompt();
                break;
            case "stopStream":
                if (string.IsNullOrWhiteSpace(cmd.Url) ||
                    string.Equals(view.Playlist.CurrentPath, cmd.Url, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_tvOpened, cmd.Url, StringComparison.OrdinalIgnoreCase))
                {
                    view.Stop();
                    _tvOpened = null;
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
        await using var file = new FileStream(
            dest,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.SequentialScan);
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

    private void EnsureInboxRestored()
    {
        if (_inboxRestored)
        {
            return;
        }

        _inboxRestored = true;
        var view = _view();
        foreach (var path in TvInboxPaths())
        {
            view.EnqueueOrPlay(path, play: false, Path.GetFileNameWithoutExtension(path));
        }
    }

    private IEnumerable<string> TvInboxPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stored in _inboxKeys.Values)
        {
            if (File.Exists(stored) && MediaFiles.IsSupported(stored) && !MediaFiles.IsSubtitle(stored) &&
                seen.Add(Path.GetFullPath(stored)))
            {
                yield return stored;
            }
        }

        var dir = LinkProtocol.InboxDir();
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var file in Directory.GetFiles(dir))
        {
            if (MediaFiles.IsSupported(file) && !MediaFiles.IsSubtitle(file) &&
                seen.Add(Path.GetFullPath(file)))
            {
                yield return file;
            }
        }
    }

    private List<LinkHaveDto> CaptureHave(MediaPlaylist list)
    {
        var have = new List<LinkHaveDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _inboxKeys)
        {
            if (seen.Add(pair.Key))
            {
                have.Add(new LinkHaveDto { Key = pair.Key, Title = Path.GetFileNameWithoutExtension(pair.Value) });
            }
        }

        foreach (var item in list.Items)
        {
            var key = InboxKeyOf(item.Path, item.Title);
            if (seen.Add(key))
            {
                have.Add(new LinkHaveDto { Key = key, Title = item.Title });
            }

            var titleKey = "title|" + item.Title.Trim().ToLowerInvariant();
            if (seen.Add(titleKey))
            {
                have.Add(new LinkHaveDto { Key = titleKey, Title = item.Title });
            }
        }

        return have;
    }

    private string? ResolveInbox(string? key, string? title, string? name, long length)
    {
        if (!string.IsNullOrWhiteSpace(key) && _inboxKeys.TryGetValue(key, out var mapped) && File.Exists(mapped))
        {
            if (length <= 0 || new FileInfo(mapped).Length == length)
            {
                return mapped;
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            foreach (var pair in _inboxKeys)
            {
                if (!File.Exists(pair.Value)) continue;
                var stem = Path.GetFileNameWithoutExtension(pair.Value);
                if (stem.Equals(title, StringComparison.OrdinalIgnoreCase) &&
                    (length <= 0 || new FileInfo(pair.Value).Length == length))
                {
                    return pair.Value;
                }
            }

            try
            {
                var view = _view();
                var hit = view.Playlist.Items.FirstOrDefault(item =>
                    item.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
                if (hit is not null && (UrlSanitizer.IsUrl(hit.Path) || File.Exists(hit.Path)))
                {
                    return hit.Path;
                }
            }
            catch
            {
            }
        }

        var source = string.IsNullOrWhiteSpace(name) ? null : SafeName(name);
        if (!string.IsNullOrWhiteSpace(source))
        {
            var dest = Path.Combine(LinkProtocol.InboxDir(), source);
            if (File.Exists(dest) && (length <= 0 || new FileInfo(dest).Length == length))
            {
                return dest;
            }

            foreach (var file in Directory.Exists(LinkProtocol.InboxDir())
                         ? Directory.GetFiles(LinkProtocol.InboxDir())
                         : [])
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith(Path.GetFileNameWithoutExtension(source), StringComparison.OrdinalIgnoreCase) &&
                    (length <= 0 || new FileInfo(file).Length == length))
                {
                    return file;
                }
            }
        }

        return null;
    }

    private void RememberInbox(string key, string path, string title)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(path)) return;
        _inboxKeys[key] = path;
        _inboxKeys["title|" + title.Trim().ToLowerInvariant()] = path;
        SaveInboxKeys();
    }

    private static string InboxKey(string name, long length) =>
        Path.GetFileName(name).Trim().ToLowerInvariant() + "|" + length;

    private static string InboxKeyOf(string path, string title)
    {
        try
        {
            if (!UrlSanitizer.IsUrl(path) && File.Exists(path))
            {
                return Path.GetFileName(path).Trim().ToLowerInvariant() + "|" + new FileInfo(path).Length;
            }
        }
        catch
        {
        }

        return "title|" + title.Trim().ToLowerInvariant();
    }

    private static async Task Drain(NetworkStream stream, long length, CancellationToken token)
    {
        if (length <= 0) return;
        var buf = new byte[64 * 1024];
        long got = 0;
        while (got < length)
        {
            var take = (int)Math.Min(buf.Length, length - got);
            var n = await stream.ReadAsync(buf.AsMemory(0, take), token);
            if (n <= 0) break;
            got += n;
        }
    }

    private string InboxKeysPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GrokPlayer",
        "tv-inbox.json");

    private void LoadInboxKeys()
    {
        try
        {
            var path = InboxKeysPath();
            if (!File.Exists(path)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (map is null) return;
            foreach (var pair in map)
            {
                if (File.Exists(pair.Value) || UrlSanitizer.IsUrl(pair.Value))
                {
                    _inboxKeys[pair.Key] = pair.Value;
                }
            }
        }
        catch
        {
        }
    }

    private void SaveInboxKeys()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InboxKeysPath())!);
            File.WriteAllText(InboxKeysPath(), JsonSerializer.Serialize(_inboxKeys));
        }
        catch
        {
        }
    }

    private static string UniqueInboxPath(string name)
    {
        var safe = SafeName(name);
        var dir = LinkProtocol.InboxDir();
        var dest = Path.Combine(dir, safe);
        if (!File.Exists(dest))
        {
            return dest;
        }

        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        return Path.Combine(dir, $"{stem}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{ext}");
    }

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
