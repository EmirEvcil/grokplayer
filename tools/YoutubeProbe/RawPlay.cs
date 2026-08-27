using Grok.Player.Core.Native;

static class RawPlay
{
    public static void Run(string url, string? userAgent)
    {
        var log = Path.Combine(Path.GetTempPath(), "grok-mpv.log");
        if (File.Exists(log))
        {
            File.Delete(log);
        }

        using var mpv = new MpvNative();
        mpv.SetOption("config", "no");
        mpv.SetOption("vo", "null");
        mpv.SetOption("ao", "null");
        mpv.SetOption("idle", "yes");
        mpv.SetOption("ytdl", "no");
        mpv.SetOption("osc", "no");
        mpv.SetOption("log-file", log);
        mpv.SetOption("msg-level", "all=v");
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            mpv.SetOption("user-agent", userAgent);
        }

        mpv.SetOption("referrer", "https://www.youtube.com");
        mpv.Initialize();
        mpv.Command("loadfile", url, "replace");
        var until = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < until)
        {
            var ev = mpv.WaitEvent(0.2);
            if (ev.Id is MpvEventId.None)
            {
                continue;
            }

            Console.WriteLine("ev " + ev.Id + " err=" + ev.Error + " end=" + ev.EndFileReason + " " + ev.EndFileError);
            if (ev.Id is MpvEventId.FileLoaded or MpvEventId.EndFile)
            {
                break;
            }
        }

        mpv.Dispose();
        if (File.Exists(log))
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            foreach (var line in lines.TakeLast(50))
            {
                Console.WriteLine("log " + line);
            }
        }
    }
}
