using Grok.Player.Core.Preview;
using SkiaSharp;

namespace Grok.Player.Core.Tests;

public sealed class LivePreviewBufferTests
{
    [Fact]
    public async Task Keeps_entire_three_minute_dvr_window_and_expires_older_frames()
    {
        using var buffer = new LivePreviewBuffer();
        for (var second = 0; second <= 240; second++)
        {
            var time = TimeSpan.FromSeconds(second);
            Assert.True(await buffer.CaptureAsync(Capture, () => time));
        }
        Assert.NotNull(buffer.GetFrame(TimeSpan.FromSeconds(60), 0));
        Assert.NotNull(buffer.GetFrame(TimeSpan.FromSeconds(240), 0));
        Assert.Null(buffer.GetFrame(TimeSpan.FromSeconds(10)));
        Assert.InRange(buffer.Count, 181, 186);
    }

    [Fact]
    public async Task Slow_capture_does_not_block_caller_or_queue_more_captures()
    {
        using var buffer = new LivePreviewBuffer();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = buffer.CaptureAsync(path =>
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(3))) return false;
            return Capture(path);
        }, () => TimeSpan.FromSeconds(1));
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(first.IsCompleted);
            Assert.False(await buffer.CaptureAsync(_ => throw new InvalidOperationException("Must not queue"), () => TimeSpan.Zero));
        }
        finally { release.Set(); }
        Assert.True(await first);
    }

    [Fact]
    public async Task Media_reset_rejects_in_flight_old_frame_and_keeps_new_media_empty()
    {
        using var buffer = new LivePreviewBuffer();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        string? raw = null;
        var pending = buffer.CaptureAsync(path =>
        {
            raw = path;
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(3))) return false;
            return Capture(path);
        }, () => TimeSpan.FromSeconds(20));
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
            buffer.Reset();
        }
        finally { release.Set(); }
        Assert.False(await pending);
        Assert.Equal(0, buffer.Count);
        Assert.False(File.Exists(raw));
        Assert.False(File.Exists(Path.ChangeExtension(raw, ".thumb.jpg")));
        Assert.True(await buffer.CaptureAsync(Capture, () => TimeSpan.FromSeconds(1)));
        Assert.Null(buffer.GetFrame(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Seek_during_capture_is_not_cached_under_wrong_timestamp()
    {
        using var buffer = new LivePreviewBuffer();
        var position = TimeSpan.FromSeconds(10);
        Assert.False(await buffer.CaptureAsync(path =>
        {
            position = TimeSpan.FromSeconds(120);
            return Capture(path);
        }, () => position));
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public async Task Stores_small_thumbnail_once_per_second_and_never_returns_unrelated_frame()
    {
        using var buffer = new LivePreviewBuffer();
        Assert.True(await buffer.CaptureAsync(path => Capture(path, 1920, 1080), () => TimeSpan.FromSeconds(50)));
        var path = buffer.GetFrame(TimeSpan.FromSeconds(51));
        Assert.NotNull(path);
        using var bitmap = SKBitmap.Decode(path);
        Assert.Equal(512, bitmap.Width);
        Assert.Equal(288, bitmap.Height);
        Assert.False(await buffer.CaptureAsync(_ => throw new InvalidOperationException("Duplicate capture"), () => TimeSpan.FromSeconds(50.5)));
        Assert.Null(buffer.GetFrame(TimeSpan.FromSeconds(10)));
        buffer.Reset();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Stores_a_cache_decoded_frame_at_its_absolute_live_time()
    {
        using var buffer = new LivePreviewBuffer();
        var raw = Path.Combine(Path.GetTempPath(), $"grok-cache-frame-{Guid.NewGuid():N}.jpg");
        Assert.True(Capture(raw, 896, 504));
        Assert.True(buffer.Store(TimeSpan.FromSeconds(125), raw, deleteSource: true));
        Assert.NotNull(buffer.GetFrame(TimeSpan.FromSeconds(125), 0));
        Assert.Null(buffer.GetFrame(TimeSpan.FromSeconds(121), 2));
        Assert.False(File.Exists(raw));
    }

    private static bool Capture(string path) => Capture(path, 64, 36);

    private static bool Capture(string path, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var file = File.Create(path);
        encoded.SaveTo(file);
        return true;
    }
}
