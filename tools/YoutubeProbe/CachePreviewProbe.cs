using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Grok.Player.Core.Native;
using Grok.Player.Core.Media;
using Grok.Player.Core.Player;
using Grok.Player.Core.Preview;

internal static class CachePreviewProbe
{
    public static async Task<int> Run(string target)
    {
        using var native = new MpvNative();
        using var host = new PlayerHost(native, new PlayerHostOptions { Headless = true, HardwareDecode = false,
            VideoOutput = "null", AudioOutput = "null", UseBackgroundEventLoop = true });
        host.Open(target, StreamKind.Live);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < 25 && host.Position.TotalSeconds <= 0) await Task.Delay(100);
        await Task.Delay(14000);
        var json = native.GetPropertyString("demuxer-cache-state");
        Console.WriteLine("cache=" + json);
        using var doc = JsonDocument.Parse(json!);
        var range = doc.RootElement.GetProperty("seekable-ranges")[0];
        var start = range.GetProperty("start").GetDouble();
        var end = range.GetProperty("end").GetDouble();
        var file = Path.Combine(Path.GetTempPath(), "grok-cache-probe-" + Guid.NewGuid().ToString("N") + ".mkv");
        var before = host.Position;
        timer.Restart();
        native.Command("dump-cache", start.ToString("R", CultureInfo.InvariantCulture), end.ToString("R", CultureInfo.InvariantCulture), file);
        Console.WriteLine($"dumpMs={timer.Elapsed.TotalMilliseconds:F1} bytes={new FileInfo(file).Length} liveBefore={before} liveAfter={host.Position} file={file}");
        using var decoder = SeekPreviewEngine.Create();
        decoder.Prepare(file);
        var image = decoder.Capture(TimeSpan.FromSeconds(3));
        Console.WriteLine($"image={image} originalTime={start + 3:F3}");
        await Task.Delay(1000);
        Console.WriteLine($"liveAfter1s={host.Position}");
        var remoteTarget = host.Position - TimeSpan.FromSeconds(120);
        var sourceLiveEdge = host.PreviewLiveEdgeSeconds() ?? host.Position.TotalSeconds;
        var behindLive = Math.Max(0, sourceLiveEdge - remoteTarget.TotalSeconds);
        using var liveRenderer = SeekPreviewEngine.Create();
        timer.Restart();
        var remoteImage = liveRenderer.CaptureBehindLive(target, behindLive, DateTime.UtcNow);
        var remoteCopy = Path.Combine(Path.GetTempPath(), "grok-live-segment-preview.jpg");
        if (remoteImage is not null) File.Copy(remoteImage, remoteCopy, true);
        var mainCopy = Path.Combine(Path.GetTempPath(), "grok-live-segment-main.jpg");
        var mainAt = host.Position;
        var mainCaptured = host.TryCaptureVideo(mainCopy, includeWindow: false);
        Console.WriteLine($"segment behind={behindLive:F3} ms={timer.ElapsedMilliseconds} ok={remoteImage is not null} mainAt={mainAt} target={remoteTarget} mainCaptured={mainCaptured} mainImage={mainCopy} previewImage={remoteCopy}");
        File.Delete(file);
        return image is null ? 2 : 0;
    }
}
