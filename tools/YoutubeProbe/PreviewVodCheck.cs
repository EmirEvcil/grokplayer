using Grok.Player.Core.Media;
using Grok.Player.Core.Preview;
using SkiaSharp;

static class PreviewVodCheck
{
    public static int Run(string[] args)
    {
        var page = args.Length > 1
            ? args[1]
            : "https://www.hdfilmcehennemi.now/bolum/breaking-bad-1-sezon-1-bolum-1-izle-16/";
        Console.WriteLine("=== preview-vod ===");
        var playable = StreamCatalog.Resolve(page);
        if (playable is null)
        {
            Console.WriteLine("RESOLVE_NULL");
            return 2;
        }

        Console.WriteLine("media=" + playable.MediaUrl);
        Console.WriteLine("referer=" + playable.Referer);
        using var engine = SeekPreviewEngine.Create();
        var started = DateTime.UtcNow;
        engine.Prepare(playable.MediaUrl, playable.Referer);
        Console.WriteLine("prepareMs=" + (int)(DateTime.UtcNow - started).TotalMilliseconds);
        var times = new[] { 2, 10, 26, 36 };
        var good = 0;
        foreach (var minute in times)
        {
            started = DateTime.UtcNow;
            var shot = engine.CaptureFast(TimeSpan.FromMinutes(minute));
            Console.WriteLine("lq@" + minute + "m ms=" +
                              (int)(DateTime.UtcNow - started).TotalMilliseconds);
            Console.WriteLine("shot=" + (shot ?? "NULL"));
            if (DescribeStill(shot) >= 8)
            {
                good++;
            }
        }

        Console.WriteLine("good=" + good + "/" + times.Length);
        return good >= 2 ? 0 : 3;
    }

    private static double DescribeStill(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Console.WriteLine("still=missing");
            return 0;
        }

        var bytes = File.ReadAllBytes(path);
        Console.WriteLine("bytes=" + bytes.Length);
        using var decoded = SKBitmap.Decode(bytes);
        if (decoded is null)
        {
            Console.WriteLine("decode=fail");
            return 0;
        }

        long luma = 0;
        var n = decoded.Width * decoded.Height;
        for (var y = 0; y < decoded.Height; y++)
        {
            for (var x = 0; x < decoded.Width; x++)
            {
                var c = decoded.GetPixel(x, y);
                luma += (c.Red * 3) + (c.Green * 6) + c.Blue;
            }
        }

        var avg = n == 0 ? 0 : luma / (n * 10.0);
        Console.WriteLine("size=" + decoded.Width + "x" + decoded.Height);
        Console.WriteLine("avgLuma=" + avg.ToString("0.0"));
        return avg;
    }
}
