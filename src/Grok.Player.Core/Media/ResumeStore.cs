using System.Text.Json;

namespace Grok.Player.Core.Media;

public sealed record ResumeRecord(string Fingerprint, string Label, double Seconds, double Duration, long UpdatedUtc);

public sealed class ResumeStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, ResumeRecord> _items = new(StringComparer.Ordinal);

    public ResumeStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrokPlayer",
            "resume.json");
        Load();
    }

    public bool TryGet(string fingerprint, out ResumeRecord record)
    {
        lock (_gate)
        {
            return _items.TryGetValue(fingerprint, out record!);
        }
    }

    public void Save(string fingerprint, string label, double seconds, double duration)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || seconds < 5 || duration < 10)
        {
            return;
        }

        if (seconds > duration * 0.95)
        {
            Forget(fingerprint);
            return;
        }

        lock (_gate)
        {
            _items[fingerprint] = new ResumeRecord(
                fingerprint,
                label,
                seconds,
                duration,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Persist();
        }
    }

    public void Forget(string fingerprint)
    {
        lock (_gate)
        {
            if (_items.Remove(fingerprint))
            {
                Persist();
            }
        }
    }

    public static bool ShouldResume(ResumeRecord record) =>
        record.Seconds >= 5 && record.Duration >= 10 && record.Seconds <= record.Duration * 0.95;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<ResumeRecord>>(json);
            if (list is null)
            {
                return;
            }

            _items = list.ToDictionary(item => item.Fingerprint, StringComparer.Ordinal);
        }
        catch (Exception)
        {
            _items = new Dictionary<string, ResumeRecord>(StringComparer.Ordinal);
        }
    }

    private void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(_items.Values.ToList()));
        }
        catch (Exception)
        {
        }
    }
}
