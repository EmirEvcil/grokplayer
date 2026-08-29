using System.Net;
using Grok.Player.Core.Preview;
using SkiaSharp;

namespace Grok.Player.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class StoryboardImageTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Webp)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Png)]
    public void Native_resolution_and_correct_cell_are_preserved_regardless_of_url_suffix(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(640, 180);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
            using var paint = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(320, 0, 320, 180, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 100);
        using var http = new HttpClient(new ImageHandler(data.ToArray()));
        var spec = new StoryboardSpec([new StoryboardLevel(0, "https://test.example/sheet.jpg", 320, 180, 2, 2, 1, 10000)]);
        using var atlas = new StoryboardAtlas(spec, http: http);
        Assert.True(atlas.TryGetOrFetch(TimeSpan.FromSeconds(15), out var path));
        using var actual = SKBitmap.Decode(path);
        Assert.Equal(320, actual.Width);
        Assert.Equal(180, actual.Height);
        Assert.True(actual.GetPixel(160, 90).Blue > 240);
        Assert.True(actual.GetPixel(160, 90).Red < 10);
    }

    [Fact]
    public void Fast_160p_tier_is_available_before_best_320p_upgrade()
    {
        var low = Solid(160, 90, SKColors.Orange);
        var high = Solid(320, 180, SKColors.Blue);
        using var http = new HttpClient(new TierHandler(low, high));
        var spec = new StoryboardSpec([
            new StoryboardLevel(0, "https://test.example/low.jpg", 160, 90, 1, 1, 1, 10000),
            new StoryboardLevel(1, "https://test.example/high.jpg", 320, 180, 1, 1, 1, 10000)
        ]);
        using var atlas = new StoryboardAtlas(spec, http: http);
        Assert.True(atlas.TryGetOrFetch(TimeSpan.Zero, out var fast));
        using (var image = SKBitmap.Decode(fast)) Assert.Equal((160, 90), (image.Width, image.Height));
        Assert.True(atlas.TryGetOrFetchBest(TimeSpan.Zero, out var best));
        using (var image = SKBitmap.Decode(best)) Assert.Equal((320, 180), (image.Width, image.Height));
        Assert.NotEqual(fast, best);
    }

    [Fact]
    public async Task Rapid_hover_does_not_cancel_an_inflight_fast_tier()
    {
        var low = Solid(160, 90, SKColors.Orange);
        using var handler = new BlockingLowHandler(low);
        using var http = new HttpClient(handler);
        var spec = new StoryboardSpec([
            new StoryboardLevel(0, "https://test.example/low.jpg", 160, 90, 1, 1, 1, 10000),
            new StoryboardLevel(1, "https://test.example/high.jpg", 320, 180, 1, 1, 1, 10000)
        ]);
        using var atlas = new StoryboardAtlas(spec, http: http);

        var fetch = Task.Run(() => atlas.TryGetOrFetch(TimeSpan.Zero, out var path) ? path : null);
        Assert.True(handler.Started.Wait(TimeSpan.FromSeconds(2)));
        atlas.Prioritize(TimeSpan.FromSeconds(20));
        handler.Release.Set();

        var path = await fetch;
        Assert.NotNull(path);
        using var image = SKBitmap.Decode(path);
        Assert.Equal((160, 90), (image.Width, image.Height));
    }

    private static byte[] Solid(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 85);
        return data.ToArray();
    }

    private sealed class ImageHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class TierHandler(byte[] low, byte[] high) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.Contains("low") ? low : high) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class BlockingLowHandler(byte[] low) : HttpMessageHandler
    {
        public ManualResetEventSlim Started { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.Set();
            var signaled = WaitHandle.WaitAny([Release.WaitHandle, cancellationToken.WaitHandle], TimeSpan.FromSeconds(2));
            cancellationToken.ThrowIfCancellationRequested();
            if (signaled == WaitHandle.WaitTimeout) throw new TimeoutException();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(low) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Started.Dispose();
                Release.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
