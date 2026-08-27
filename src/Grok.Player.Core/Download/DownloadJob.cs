namespace Grok.Player.Core.Download;

public enum DownloadState
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Canceled
}

public sealed class DownloadJob
{
    public DownloadJob(string sourceUrl, string title, string outputPath)
    {
        Id = Guid.NewGuid().ToString("N");
        SourceUrl = sourceUrl;
        Title = string.IsNullOrWhiteSpace(title) ? "Stream" : title.Trim();
        OutputPath = outputPath;
    }

    public string Id { get; }
    public string SourceUrl { get; }
    public string Title { get; }
    public string OutputPath { get; set; }
    public DownloadState State { get; set; } = DownloadState.Queued;
    public long Bytes { get; set; }
    public long TotalBytes { get; set; }
    public int SegmentsDone { get; set; }
    public int SegmentsTotal { get; set; }
    public string? Error { get; set; }
    public bool ManualStart { get; set; }
    public bool DeleteRequested { get; set; }
    public int Height { get; set; }
    public int MaxHeight { get; set; }
    public string? AudioLang { get; set; }

    public double Progress
    {
        get
        {
            if (TotalBytes > 0)
            {
                return Math.Clamp(Bytes / (double)TotalBytes, 0, 1);
            }

            if (SegmentsTotal > 0)
            {
                return Math.Clamp(SegmentsDone / (double)SegmentsTotal, 0, 1);
            }

            return State == DownloadState.Completed ? 1 : 0;
        }
    }

    public string StatusText => State switch
    {
        DownloadState.Queued => "Waiting",
        DownloadState.Running => Height > 0 ? "Downloading " + Height + "p" : "Downloading",
        DownloadState.Paused => "Paused",
        DownloadState.Completed => "Done",
        DownloadState.Failed => string.IsNullOrWhiteSpace(Error) ? "Failed" : Error,
        DownloadState.Canceled => "Canceled",
        _ => State.ToString()
    };

    public string SizeText
    {
        get
        {
            if (TotalBytes > 0)
            {
                return FormatBytes(Bytes) + " / " + FormatBytes(TotalBytes);
            }

            if (SegmentsTotal > 0)
            {
                return SegmentsDone + " / " + SegmentsTotal + " parts";
            }

            return Bytes > 0 ? FormatBytes(Bytes) : "";
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " B";
        }

        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024d).ToString("0.#") + " KB";
        }

        return (bytes / (1024d * 1024d)).ToString("0.#") + " MB";
    }

    public static string SafeFileName(string title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "download" : title.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(ch, '_');
        }

        return name.Length > 80 ? name[..80].Trim() : name;
    }
}
