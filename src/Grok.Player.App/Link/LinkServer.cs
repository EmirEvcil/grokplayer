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
    private readonly ConcurrentDictionary<string, string> _tvNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _tvSeen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _inboxKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LinkJobDto> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _kicked = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _incomingPins = new();
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private string? _pendingTvId;
    private string? _pendingTvName;
    private string? _pendingPin;
    private string? _acceptedPin;
    private string? _tvOpened;
    private string? _sessionTvId;
    private string? _tvHost;
    private int _tvPort = 17423;
    private readonly ConcurrentDictionary<string, LinkProgressDto> _progress = new(StringComparer.OrdinalIgnoreCase);
    private long _lastAuthAt;
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
    public string? SessionTvId => _sessionTvId;
    public string? SessionToken =>
        _sessionTvId is { } id && _tokens.TryGetValue(id, out var token) ? token : null;
    public IEnumerable<LinkJobDto> Jobs => _jobs.Values;

    public IReadOnlyList<TrustedTv> Televisions()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return _tokens.Select(pair => new TrustedTv
        {
            Id = pair.Key,
            Name = _tvNames.TryGetValue(pair.Key, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : "TV " + pair.Key[..Math.Min(8, pair.Key.Length)],
            Token = pair.Value,
            SeenAt = _tvSeen.TryGetValue(pair.Key, out var seen) ? seen : 0,
        }).OrderByDescending(tv => tv.Id == _sessionTvId)
            .ThenByDescending(tv => now - tv.SeenAt < 8_000)
            .ThenBy(tv => tv.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public bool IsTvOnline(string id) =>
        _tvSeen.TryGetValue(id, out var seen) &&
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - seen < 8_000;

    public void DisconnectSession()
    {
        var id = _sessionTvId;
        if (!string.IsNullOrWhiteSpace(id))
        {
            _kicked[id] = 0;
        }

        EndSession();
    }

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
        RememberTvName(_pendingTvId, _pendingTvName);
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
        RaiseChanged();
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
        RememberTvName(tvId, name);
        if (!string.IsNullOrWhiteSpace(pin))
        {
            _pendingPin = pin;
        }

        if (!fresh)
        {
            return;
        }

        RaisePairOffered(tvId, name ?? "TV");
        RaiseChanged();
    }

    public void Forget(string tvId)
    {
        if (string.IsNullOrWhiteSpace(tvId))
        {
            return;
        }

        if (_sessionTvId == tvId)
        {
            DisconnectSession();
        }

        _tokens.TryRemove(tvId, out _);
        _tvNames.TryRemove(tvId, out _);
        _tvSeen.TryRemove(tvId, out _);
        _kicked.TryRemove(tvId, out _);
        SaveTokens();
        RaiseChanged();
    }

    private void EndSession()
    {
        if (_sessionTvId is null)
        {
            return;
        }

        _sessionTvId = null;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (!_ui.TryEnqueue(() => Changed?.Invoke()))
        {
            Changed?.Invoke();
        }
    }

    private void RaisePairOffered(string tvId, string name)
    {
        if (!_ui.TryEnqueue(() => PairOffered?.Invoke(tvId, name)))
        {
            PairOffered?.Invoke(tvId, name);
        }
    }

    private void RememberTvName(string? id, string? name)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _tvNames[id] = name.Trim();
    }

    private void NoteTvSeen(string? id, string? name)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        RememberTvName(id, name);
        _tvSeen[id] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private string? TvIdForToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return _tokens.FirstOrDefault(pair => pair.Value == token).Key;
    }

    private void TouchSession(string? token)
    {
        var tvId = TvIdForToken(token);
        if (string.IsNullOrWhiteSpace(tvId) || _kicked.ContainsKey(tvId))
        {
            return;
        }

        var changed = _sessionTvId != tvId;
        _sessionTvId = tvId;
        _lastAuthAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        NoteTvSeen(tvId, null);
        if (changed)
        {
            RaiseChanged();
        }
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
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_sessionTvId is not null && now - _lastAuthAt > 20_000)
            {
                EndSession();
            }

            if (_kicked.Count > 0 && _sessionTvId is null && now - _lastAuthAt > 2_500)
            {
                _kicked.Clear();
            }

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
                if (!string.IsNullOrWhiteSpace(note.Tv))
                {
                    var fresh = !_tvSeen.ContainsKey(note.Tv);
                    NoteTvSeen(note.Tv, note.Name);
                    if (fresh)
                    {
                        RaiseChanged();
                    }
                }

                SendHello(packet.RemoteEndPoint);
                continue;
            }

            if (note.T == "offer" && !string.IsNullOrWhiteSpace(note.Tv))
            {
                RememberOffer(note.Tv, note.Name, note.Pin);
                NoteTvSeen(note.Tv, note.Name);
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
            try
            {
                client.NoDelay = true;
                client.LingerState = new LingerOption(true, 2);
            }
            catch
            {
            }

            stream.ReadTimeout = 120_000;
            stream.WriteTimeout = 30_000;
            var header = await ReadHeaders(stream, token);
            if (header is null)
            {
                return;
            }

            var (method, path, headers, length) = header.Value;
            if (client.Client.RemoteEndPoint is IPEndPoint peer)
            {
                _tvHost = peer.Address.ToString();
            }
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
                    if (note?.Tv is { } claimedTv &&
                        _tokens.TryGetValue(claimedTv, out var ready) &&
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

                if (!tokenOk && path.StartsWith("/v1/file", StringComparison.Ordinal))
                {
                    var queryToken = Query(path, "token");
                    tokenOk = !string.IsNullOrWhiteSpace(queryToken) && _tokens.Values.Contains(queryToken);
                }

                if (!tokenOk)
                {
                    await WriteText(stream, 401, "auth");
                    return;
                }

                var sent = Header(headers, LinkProtocol.TokenHeader);
                var tvId = TvIdForToken(sent);
                var kicked = !string.IsNullOrWhiteSpace(tvId) && _kicked.ContainsKey(tvId);
                if (kicked)
                {
                    _lastAuthAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                else
                {
                    TouchSession(sent);
                }

                if (method == "GET" && path == "/v1/state")
                {
                    await WriteJson(stream, 200, Snapshot());
                    return;
                }

                if (method == "GET" && path.StartsWith("/v1/browse", StringComparison.Ordinal))
                {
                    await WriteJson(stream, 200, Browse(Query(path, "path")));
                    return;
                }

                if (method == "GET" && path.StartsWith("/v1/file", StringComparison.Ordinal))
                {
                    var filePath = SharedFolders.Resolve(Query(path, "path"));
                    if (filePath is null || !File.Exists(filePath) || !MediaFiles.IsSupported(filePath))
                    {
                        await WriteText(stream, 404, "no");
                        return;
                    }

                    await WriteFile(stream, filePath, Header(headers, "Range"), method);
                    return;
                }

                if (method == "POST" && path == "/v1/progress")
                {
                    var raw = await ReadBody(stream, length, token);
                    var cmd = JsonSerializer.Deserialize<LinkCmd>(raw, LinkProtocol.Json);
                    RememberProgress(cmd?.Items);
                    await WriteJson(stream, 200, new { ok = true });
                    return;
                }

                if (method == "POST" && path == "/v1/cmd")
                {
                    var body = await ReadBody(stream, length, token);
                    var cmd = JsonSerializer.Deserialize<LinkCmd>(body, LinkProtocol.Json) ?? new LinkCmd();
                    if (cmd.Op == "unpair")
                    {
                        if (!string.IsNullOrWhiteSpace(tvId))
                        {
                            Forget(tvId);
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
                        RaiseChanged();
                        await SaveFile(stream, dest, length, job, token);
                        job.Status = "ready";
                        job.Done = job.Total;
                        RaiseChanged();
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
            finally
            {
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
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
            Connected = _sessionTvId is not null,
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
                ResumeIfNeeded(view, cmd.Key, cmd.Title, cmd.Url, cmd.StartOver);
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
                    ResumeIfNeeded(view, cmd.Key, cmd.Title, known, cmd.StartOver);
                }
                else if (!string.IsNullOrWhiteSpace(cmd.Title))
                {
                    var match = view.Playlist.Items.FirstOrDefault(item =>
                        string.Equals(item.Title, cmd.Title, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        view.EnqueueOrPlay(match.Path, cmd.Play != false, cmd.Title, cmd.StartOver ?? true);
                        ResumeIfNeeded(view, cmd.Key, cmd.Title, match.Path, cmd.StartOver);
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
        var payload = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, payload, 0, head.Length);
        Buffer.BlockCopy(body, 0, payload, head.Length, body.Length);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
        await Task.Delay(40);
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
                have.Add(HaveDto(pair.Key, Path.GetFileNameWithoutExtension(pair.Value), pair.Value));
            }
        }

        foreach (var item in list.Items)
        {
            var key = InboxKeyOf(item.Path, item.Title);
            if (seen.Add(key))
            {
                have.Add(HaveDto(key, item.Title, item.Path));
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

    public string? TvHost => _tvHost;
    public int TvPort => _tvPort;

    private void ResumeIfNeeded(PlaybackViewModel view, string? key, string? title, string? path, bool? startOver)
    {
        if (startOver == true)
        {
            return;
        }

        var ms = ProgressOf(key, title, path);
        if (ms >= 5_000)
        {
            view.ApplySeek(ms / 1000.0);
        }
    }

    private long ProgressOf(string? key, string? title, string? path)
    {
        if (!string.IsNullOrWhiteSpace(key) && _progress.TryGetValue(key, out var byKey))
        {
            return byKey.PositionMs;
        }

        if (!string.IsNullOrWhiteSpace(title) &&
            _progress.TryGetValue("title|" + title.Trim().ToLowerInvariant(), out var byTitle))
        {
            return byTitle.PositionMs;
        }

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var file = new FileInfo(path);
            var fileKey = file.Name.ToLowerInvariant() + "|" + file.Length;
            if (_progress.TryGetValue(fileKey, out var byFile))
            {
                return byFile.PositionMs;
            }
        }

        return 0;
    }

    private void RememberProgress(List<LinkProgressDto>? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.PositionMs < 1000)
            {
                continue;
            }

            _progress[item.Key] = item;
            if (!string.IsNullOrWhiteSpace(item.Title))
            {
                _progress["title|" + item.Title.Trim().ToLowerInvariant()] = item;
            }
        }
    }

    private LinkHaveDto HaveDto(string key, string title, string? path)
    {
        var ms = ProgressOf(key, title, path);
        return new LinkHaveDto
        {
            Key = key,
            Title = title,
            PositionMs = ms,
            DurationMs = 0,
        };
    }

    private static string Query(string path, string name)
    {
        var q = path.IndexOf('?');
        if (q < 0)
        {
            return "";
        }

        foreach (var part in path[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
            }
        }

        return "";
    }

    private static object Browse(string? path)
    {
        var grants = SharedFolders.List();
        if (string.IsNullOrWhiteSpace(path))
        {
            return new
            {
                path = "",
                parent = (string?)null,
                granted = grants,
                dirs = grants.Select(item => new { name = Path.GetFileName(item.TrimEnd('\\')), path = item }).ToList(),
                videos = Array.Empty<object>(),
            };
        }

        var resolved = SharedFolders.Resolve(path);
        if (resolved is null || !Directory.Exists(resolved))
        {
            return new
            {
                path,
                parent = ParentOf(path, grants),
                granted = grants,
                dirs = Array.Empty<object>(),
                videos = Array.Empty<object>(),
            };
        }

        var dirs = Directory.EnumerateDirectories(resolved)
            .Where(item => !Path.GetFileName(item).StartsWith('.'))
            .OrderBy(item => Path.GetFileName(item), StringComparer.OrdinalIgnoreCase)
            .Select(item => new { name = Path.GetFileName(item), path = item })
            .ToList();
        var videos = MediaOrder.SortByTitle(
                Directory.EnumerateFiles(resolved).Where(MediaFiles.IsSupported),
                Path.GetFileNameWithoutExtension)
            .Select(item =>
            {
                var info = new FileInfo(item);
                return new
                {
                    name = info.Name,
                    path = info.FullName,
                    size = info.Length,
                    title = Path.GetFileNameWithoutExtension(info.Name),
                };
            })
            .ToList();
        return new
        {
            path = resolved,
            parent = ParentOf(resolved, grants),
            granted = grants,
            dirs,
            videos,
        };
    }

    private static string? ParentOf(string path, List<string> grants)
    {
        if (grants.Any(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase)))
        {
            return "";
        }

        return Path.GetDirectoryName(path);
    }

    private static async Task WriteFile(NetworkStream stream, string path, string? rangeHeader, string method)
    {
        var info = new FileInfo(path);
        var total = info.Length;
        var start = 0L;
        var end = Math.Max(0, total - 1);
        if (!string.IsNullOrWhiteSpace(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = rangeHeader["bytes=".Length..];
            var dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                if (long.TryParse(spec[..dash], out var parsedStart)) start = parsedStart;
                if (long.TryParse(spec[(dash + 1)..], out var parsedEnd)) end = parsedEnd;
            }
        }

        start = Math.Clamp(start, 0, Math.Max(0, total - 1));
        end = Math.Clamp(end, start, Math.Max(0, total - 1));
        var length = end - start + 1;
        var status = start == 0 && end == total - 1 ? "200 OK" : "206 Partial Content";
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append("\r\n")
            .Append("Content-Type: application/octet-stream\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append("Content-Length: ").Append(length).Append("\r\n");
        if (status.StartsWith("206"))
        {
            head.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(total).Append("\r\n");
        }

        head.Append("Connection: close\r\n\r\n");
        var bytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(bytes);
        if (!method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await using var input = File.OpenRead(path);
            input.Seek(start, SeekOrigin.Begin);
            var left = length;
            var buffer = new byte[64 * 1024];
            while (left > 0)
            {
                var n = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
                if (n <= 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, n));
                left -= n;
            }
        }
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
            var raw = File.ReadAllText(path);
            if (raw.TrimStart().StartsWith('['))
            {
                var listed = JsonSerializer.Deserialize<List<TrustedTv>>(raw, LinkProtocol.Json) ?? [];
                foreach (var tv in listed)
                {
                    if (string.IsNullOrWhiteSpace(tv.Id) || string.IsNullOrWhiteSpace(tv.Token)) continue;
                    _tokens[tv.Id] = tv.Token;
                    RememberTvName(tv.Id, tv.Name);
                    if (tv.SeenAt > 0) _tvSeen[tv.Id] = tv.SeenAt;
                }
                return;
            }

            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
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
            File.WriteAllText(TokenPath(), JsonSerializer.Serialize(Televisions(), LinkProtocol.Json));
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
