namespace Grok.Player.Core.Subtitles;

public sealed class SubtitleTrack
{
    public SubtitleTrack(string id, string name, string sourcePath, SrtDocument document, string? attachedMedia = null)
    {
        Id = id;
        Name = name;
        SourcePath = sourcePath;
        Document = document;
        PlayPath = sourcePath;
        AttachedMedia = attachedMedia;
    }

    public string? AttachedMedia { get; set; }

    public string Id { get; }

    public string Name { get; set; }

    public string SourcePath { get; set; }

    public SrtDocument Document { get; set; }

    public string PlayPath { get; set; }

    public bool IsMerged { get; set; }
}
