using Grok.Player.Core.Playlist;

namespace Grok.Player.Core.Subtitles;

public sealed class SubtitleModel
{
    private readonly List<SubtitleTrack> _tracks = [];
    private readonly HashSet<string> _offForMedia = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _choice = new(StringComparer.OrdinalIgnoreCase);
    private int _activeIndex = -1;
    private int _appliedIndex = -1;
    private double _delaySeconds;
    private string? _media;

    public event Action<SubtitleNotify>? Changed;

    public IReadOnlyList<SubtitleTrack> Tracks => _tracks;

    public int ActiveIndex => _activeIndex;

    public int AppliedIndex => _appliedIndex;

    public bool Enabled => _appliedIndex >= 0 && _appliedIndex < _tracks.Count;

    public double DelaySeconds => _delaySeconds;

    public string? CurrentMedia => _media;

    public SubtitleTrack? Active => InRange(_activeIndex) ? _tracks[_activeIndex] : null;

    public SubtitleTrack? Applied => InRange(_appliedIndex) ? _tracks[_appliedIndex] : null;

    public SubtitleTrack AddFile(string path, bool apply, string? attachTo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var documentPath = StreamCaptionLoader.DocumentPath(path);
        var document = SrtDocument.Load(documentPath);
        var media = Normalize(attachTo) ?? _media;
        var existing = IndexOfSame(documentPath, media);
        if (existing >= 0)
        {
            var track = _tracks[existing];
            track.Document = document;
            track.SourcePath = documentPath;
            track.PlayPath = PlayFileFor(track);
            track.AttachedMedia = media ?? track.AttachedMedia;
            _activeIndex = existing;
            if (apply)
            {
                RememberChoice(track);
                _appliedIndex = existing;
                Changed?.Invoke(SubtitleNotify.Track);
            }
            else
            {
                Changed?.Invoke(SubtitleNotify.List);
            }

            return track;
        }

        var created = new SubtitleTrack(
            Guid.NewGuid().ToString("N"),
            Path.GetFileName(documentPath),
            documentPath,
            document,
            media);
        created.PlayPath = PlayFileFor(created);
        _tracks.Add(created);
        _activeIndex = _tracks.Count - 1;
        if (apply)
        {
            RememberChoice(created);
            _appliedIndex = _activeIndex;
            Changed?.Invoke(SubtitleNotify.Track);
        }
        else
        {
            Changed?.Invoke(SubtitleNotify.List);
        }

        return created;
    }

    public SubtitleTrack? IngestDropped(string path, IEnumerable<string> playlist)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var owner = FindOwner(path, playlist) ?? _media;
        var apply = owner is not null && SamePath(owner, _media);
        return AddFile(path, apply, owner);
    }

    public void DiscoverSidecar(string mediaPath)
    {
        var media = Normalize(mediaPath);
        if (media is null || media.Contains("://", StringComparison.Ordinal))
        {
            return;
        }

        var sidecar = Path.ChangeExtension(media, ".srt");
        if (!File.Exists(sidecar) || HasSource(sidecar))
        {
            return;
        }

        AddFile(sidecar, apply: false, attachTo: media);
    }

    public void BindForMedia(string? mediaPath)
    {
        var previous = _media;
        _media = Normalize(mediaPath);
        if (_media is null)
        {
            if (_appliedIndex >= 0)
            {
                _appliedIndex = -1;
                Changed?.Invoke(SubtitleNotify.Track);
            }

            return;
        }

        if (_offForMedia.Contains(_media))
        {
            if (_appliedIndex >= 0)
            {
                _appliedIndex = -1;
                Changed?.Invoke(SubtitleNotify.Track);
            }

            return;
        }

        var index = -1;
        if (_choice.TryGetValue(_media, out var id))
        {
            index = IndexOfId(id);
        }

        if (index < 0)
        {
            index = FirstAttachedTo(_media);
        }

        if (index < 0)
        {
            var listOnly = false;
            if (InRange(_activeIndex) && !BelongsTo(_tracks[_activeIndex], _media))
            {
                _activeIndex = -1;
                listOnly = true;
            }

            if (_appliedIndex >= 0)
            {
                _appliedIndex = -1;
                Changed?.Invoke(SubtitleNotify.Track);
                return;
            }

            if (listOnly)
            {
                Changed?.Invoke(SubtitleNotify.List);
            }

            return;
        }

        if (_appliedIndex == index &&
            _activeIndex == index &&
            string.Equals(previous, _media, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _activeIndex = index;
        _appliedIndex = index;
        Changed?.Invoke(SubtitleNotify.Track);
    }

    public bool IsVisible(SubtitleTrack track) => BelongsTo(track, _media);

    public static bool BelongsTo(SubtitleTrack track, string? media)
    {
        ArgumentNullException.ThrowIfNull(track);
        var current = Normalize(media);
        if (current is null)
        {
            return true;
        }

        if (SamePath(track.AttachedMedia, current))
        {
            return true;
        }

        if (current.StartsWith("youtube|", StringComparison.OrdinalIgnoreCase))
        {
            var id = current["youtube|".Length..];
            return id.Length > 0 &&
                   (track.SourcePath?.Contains(id, StringComparison.OrdinalIgnoreCase) == true ||
                    track.AttachedMedia?.Contains(id, StringComparison.OrdinalIgnoreCase) == true);
        }

        return !current.Contains("://", StringComparison.Ordinal) &&
               SameStem(track.SourcePath, current);
    }

    public bool MergeFile(string path)
    {
        if (Active is null)
        {
            return false;
        }

        var incoming = SrtDocument.Load(path);
        Active.Document = Active.Document.Merge(incoming);
        Active.IsMerged = true;
        Active.PlayPath = WritePlayFile(Active);
        if (_appliedIndex == _activeIndex)
        {
            Changed?.Invoke(SubtitleNotify.Track);
        }
        else
        {
            Changed?.Invoke(SubtitleNotify.List);
        }

        return true;
    }

    public static string? FindOwner(string subtitlePath, IEnumerable<string> playlist)
    {
        var stem = Path.GetFileNameWithoutExtension(subtitlePath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        foreach (var item in playlist)
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(item), stem, StringComparison.OrdinalIgnoreCase))
            {
                return Normalize(item);
            }
        }

        var dir = Path.GetDirectoryName(subtitlePath);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        foreach (var ext in MediaFiles.Extensions)
        {
            var candidate = Path.Combine(dir, stem + ext);
            if (File.Exists(candidate))
            {
                return Normalize(candidate);
            }
        }

        return null;
    }

    public static string? SidecarPath(string mediaPath)
    {
        var media = Normalize(mediaPath);
        if (media is null)
        {
            return null;
        }

        var sidecar = Path.ChangeExtension(media, ".srt");
        return File.Exists(sidecar) ? sidecar : null;
    }

    public void SelectTab(int index)
    {
        if (!InRange(index) || _activeIndex == index)
        {
            return;
        }

        _activeIndex = index;
        Changed?.Invoke(SubtitleNotify.List);
    }

    public void Apply(int index)
    {
        if (!InRange(index))
        {
            return;
        }

        _activeIndex = index;
        _appliedIndex = index;
        RememberChoice(_tracks[index]);
        Changed?.Invoke(SubtitleNotify.Track);
    }

    public void Disable()
    {
        if (_media is not null)
        {
            _offForMedia.Add(_media);
            _choice.Remove(_media);
        }

        if (_appliedIndex < 0)
        {
            return;
        }

        _appliedIndex = -1;
        Changed?.Invoke(SubtitleNotify.Track);
    }

    public void NudgeDelay(double deltaSeconds) => SetDelay(_delaySeconds + deltaSeconds);

    public void SetDelay(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return;
        }

        var clamped = Math.Clamp(seconds, -3600, 3600);
        if (Math.Abs(_delaySeconds - clamped) < 0.0005)
        {
            return;
        }

        _delaySeconds = clamped;
        Changed?.Invoke(SubtitleNotify.Delay);
    }

    public void ResetDelay() => SetDelay(0);

    public void SyncSelectedToPosition(SrtCue cue, TimeSpan position)
    {
        SetDelay((position - cue.Start).TotalSeconds);
    }

    public SrtCue? CueAtPlayback(TimeSpan position)
    {
        if (Active is null)
        {
            return null;
        }

        var shifted = position - TimeSpan.FromSeconds(_delaySeconds);
        return Active.Document.CueAt(shifted);
    }

    public SrtCue? InsertCue(int index)
    {
        if (Active is null)
        {
            return null;
        }

        var after = index >= 0 && index < Active.Document.Cues.Count
            ? Active.Document.Cues[index]
            : null;
        var start = after?.End ?? TimeSpan.Zero;
        var end = start + TimeSpan.FromSeconds(2);
        var insertAt = after is null ? 0 : index + 1;
        var cue = Active.Document.InsertAt(insertAt, start, end, "");
        PersistActive();
        return cue;
    }

    public bool DeleteCue(SrtCue cue)
    {
        if (Active is null || !Active.Document.Remove(cue))
        {
            return false;
        }

        PersistActive();
        return true;
    }

    public bool SaveActive()
    {
        if (Active is null)
        {
            return false;
        }

        var path = Active.SourcePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Active.PlayPath;
        }

        Active.Document.Save(path);
        Active.SourcePath = path;
        Active.PlayPath = WritePlayFile(Active);
        Active.IsMerged = false;
        if (_appliedIndex == _activeIndex || BelongsTo(Active, _media))
        {
            _appliedIndex = _activeIndex;
            RememberChoice(Active);
            Changed?.Invoke(SubtitleNotify.Track);
        }

        return true;
    }

    public void PersistActive()
    {
        if (Active is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Active.SourcePath))
        {
            try
            {
                Active.Document.Save(Active.SourcePath);
            }
            catch (Exception)
            {
            }
        }

        Active.PlayPath = WritePlayFile(Active);
        Active.IsMerged = true;
        if (_appliedIndex == _activeIndex || BelongsTo(Active, _media))
        {
            _appliedIndex = _activeIndex;
            RememberChoice(Active);
            Changed?.Invoke(SubtitleNotify.Track);
            return;
        }

        Changed?.Invoke(SubtitleNotify.List);
    }

    private void RememberChoice(SubtitleTrack track)
    {
        if (_media is null)
        {
            return;
        }

        track.AttachedMedia ??= _media;
        _choice[_media] = track.Id;
        _offForMedia.Remove(_media);
    }

    private bool HasSource(string path)
    {
        var full = Normalize(path);
        return full is not null && _tracks.Any(track => SamePath(track.SourcePath, full));
    }

    private int FirstAttachedTo(string media)
    {
        for (var i = 0; i < _tracks.Count; i++)
        {
            if (SamePath(_tracks[i].AttachedMedia, media) || SameStem(_tracks[i].SourcePath, media))
            {
                return i;
            }
        }

        return -1;
    }

    private int IndexOfSame(string documentPath, string? media)
    {
        var name = Path.GetFileName(documentPath);
        for (var i = 0; i < _tracks.Count; i++)
        {
            var track = _tracks[i];
            if (SamePath(track.SourcePath, documentPath))
            {
                return i;
            }

            if (media is not null &&
                SamePath(track.AttachedMedia, media) &&
                string.Equals(Path.GetFileName(track.SourcePath), name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private int IndexOfId(string id)
    {
        for (var i = 0; i < _tracks.Count; i++)
        {
            if (_tracks[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private bool InRange(int index) => index >= 0 && index < _tracks.Count;

    private static bool SameStem(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.GetFileNameWithoutExtension(left),
            Path.GetFileNameWithoutExtension(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        return a is not null && b is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = MediaPlaylist.Normalize(path);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string PlayFileFor(SubtitleTrack track)
    {
        if (track.Document.HasStyle)
        {
            return WritePlayFile(track);
        }

        var sibling = StreamCaptionLoader.PlayPath(track.SourcePath);
        return string.IsNullOrWhiteSpace(sibling) ? track.SourcePath : sibling;
    }

    private static string WritePlayFile(SubtitleTrack track)
    {
        var dir = Path.Combine(Path.GetTempPath(), "GrokPlayer", "subs");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (track.Document.HasStyle)
        {
            var ass = Path.Combine(dir, track.Id + "-" + stamp + ".ass");
            File.WriteAllText(ass, track.Document.ToAss());
            return ass;
        }

        var path = Path.Combine(dir, track.Id + "-" + stamp + ".srt");
        track.Document.Save(path);
        return path;
    }
}

public enum SubtitleNotify
{
    List,
    Track,
    Delay
}
