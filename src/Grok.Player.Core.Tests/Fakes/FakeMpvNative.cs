using Grok.Player.Core.Native;

namespace Grok.Player.Core.Tests.Fakes;

public sealed class FakeMpvNative : IMpvNative
{
    private readonly Queue<MpvEvent> _events = new();
    private readonly Dictionary<string, object?> _properties = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observed = new(StringComparer.Ordinal);

    public List<(string Name, string Value)> Options { get; } = [];
    public List<string[]> Commands { get; } = [];
    public List<string> Lifecycle { get; } = [];
    public bool Initialized { get; private set; }
    public bool IsTerminated { get; private set; }
    public bool ThrowOnCommand { get; set; }
    public bool AutoLoad { get; set; } = true;
    public double AutoDurationSeconds { get; set; } = 120;
    public string AutoHwdec { get; set; } = "d3d11va";
    public object AutoVid { get; set; } = 1L;
    public MpvEndFileReason? LoadFailureReason { get; set; }
    public List<string> Screenshots { get; } = [];

    public void SetOption(string name, string value)
    {
        EnsureAlive();
        Options.Add((name, value));
        Lifecycle.Add($"option:{name}={value}");
    }

    public void SetOptionLong(string name, long value)
    {
        SetOption(name, value.ToString());
    }

    public void Initialize()
    {
        EnsureAlive();
        if (Initialized)
        {
            throw new MpvException("initialize called twice");
        }

        Initialized = true;
        Lifecycle.Add("initialize");
        _properties["pause"] = true;
        _properties["volume"] = 100d;
        _properties["time-pos"] = 0d;
        _properties["hwdec-current"] = "no";
    }

    public void Command(params string[] args)
    {
        EnsureAlive();
        if (!Initialized)
        {
            throw new MpvException(-3, "command");
        }

        if (ThrowOnCommand)
        {
            throw new MpvException(-12, args.ElementAtOrDefault(0) ?? "command");
        }

        Commands.Add(args);
        Lifecycle.Add("command:" + string.Join(' ', args));

        if (args.Length >= 2 && args[0] == "loadfile")
        {
            HandleLoad(args[1]);
        }
        else if (args[0] == "stop")
        {
            HandleStop();
        }
        else if (args.Length >= 2 && args[0] == "seek")
        {
            HandleSeek(args[1]);
        }
        else if (args.Length >= 2 && args[0] == "screenshot-to-file")
        {
            WriteScreenshot(args[1]);
        }
    }

    public void SetPropertyString(string name, string value)
    {
        SetProperty(name, value, MpvFormat.String);
    }

    public void SetPropertyFlag(string name, bool value)
    {
        SetProperty(name, value, MpvFormat.Flag);
    }

    public void SetPropertyDouble(string name, double value)
    {
        SetProperty(name, value, MpvFormat.Double);
    }

    public void SetPropertyLong(string name, long value)
    {
        SetProperty(name, value, MpvFormat.Int64);
        Lifecycle.Add($"property:{name}={value}");
    }

    public string? GetPropertyString(string name) =>
        _properties.TryGetValue(name, out var value) ? value?.ToString() : null;

    public bool? GetPropertyFlag(string name) =>
        _properties.TryGetValue(name, out var value) && value is bool flag ? flag : null;

    public double? GetPropertyDouble(string name) =>
        _properties.TryGetValue(name, out var value) && value is double number ? number : null;

    public long? GetPropertyLong(string name) =>
        _properties.TryGetValue(name, out var value) && value is long number ? number : null;

    public void ObserveProperty(string name, MpvFormat format)
    {
        EnsureAlive();
        _observed.Add(name);
        Lifecycle.Add($"observe:{name}");
    }

    public MpvEvent WaitEvent(double timeoutSeconds)
    {
        if (_events.Count > 0)
        {
            return _events.Dequeue();
        }

        return MpvEvent.None;
    }

    public void Wakeup() => Lifecycle.Add("wakeup");

    public void TerminateDestroy()
    {
        IsTerminated = true;
        Lifecycle.Add("terminate");
    }

    public void Dispose()
    {
        if (!IsTerminated)
        {
            TerminateDestroy();
        }
    }

    public void Seed(string name, object value) => _properties[name] = value;

    public void Enqueue(MpvEvent ev) => _events.Enqueue(ev);

    public bool HasOption(string name, string value) =>
        Options.Any(item => item.Name == name && item.Value == value);

    public string[]? LastCommand() => Commands.Count == 0 ? null : Commands[^1];

    public int OptionIndex(string name) =>
        Options.FindIndex(item => item.Name == name);

    private void HandleLoad(string path)
    {
        _properties["path"] = path;
        _properties["media-title"] = Path.GetFileName(path);
        _properties["time-pos"] = 0d;

        if (LoadFailureReason is { } reason)
        {
            Enqueue(MpvEvent.EndFile(reason, reason == MpvEndFileReason.Error ? -13 : 0));
            return;
        }

        if (!AutoLoad)
        {
            return;
        }

        _properties["duration"] = AutoDurationSeconds;
        _properties["pause"] = false;
        _properties["hwdec-current"] = AutoHwdec;
        _properties["vid"] = AutoVid;
        Enqueue(MpvEvent.FileLoaded());
        Enqueue(MpvEvent.Property("duration", AutoDurationSeconds, MpvFormat.Double));
        Enqueue(MpvEvent.Property("pause", false, MpvFormat.Flag));
        Enqueue(MpvEvent.Property("hwdec-current", AutoHwdec, MpvFormat.String));
        Enqueue(MpvEvent.Property("time-pos", 0d, MpvFormat.Double));
    }

    private void WriteScreenshot(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var payload = new byte[Math.Max(4096, png.Length)];
        Buffer.BlockCopy(png, 0, payload, 0, png.Length);
        File.WriteAllBytes(path, payload);
        Screenshots.Add(path);
    }

    private void HandleStop()
    {
        _properties["path"] = null;
        _properties["media-title"] = null;
        _properties["duration"] = null;
        _properties["time-pos"] = 0d;
        _properties["pause"] = true;
        _properties["vid"] = "no";
        _properties["file-format"] = null;
    }

    private void HandleSeek(string secondsText)
    {
        if (!double.TryParse(secondsText, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return;
        }

        if (_properties.TryGetValue("duration", out var durationObj) && durationObj is double duration)
        {
            seconds = Math.Clamp(seconds, 0, duration);
        }

        seconds = Math.Max(0, seconds);
        _properties["time-pos"] = seconds;
        Enqueue(MpvEvent.Property("time-pos", seconds, MpvFormat.Double));
        Enqueue(new MpvEvent { Id = MpvEventId.PlaybackRestart });
    }

    private void SetProperty(string name, object value, MpvFormat format)
    {
        EnsureAlive();
        _properties[name] = value;
        Lifecycle.Add($"property:{name}={value}");
        if (_observed.Contains(name))
        {
            Enqueue(MpvEvent.Property(name, value, format));
        }
    }

    private void EnsureAlive()
    {
        if (IsTerminated)
        {
            throw new ObjectDisposedException(nameof(FakeMpvNative));
        }
    }
}
