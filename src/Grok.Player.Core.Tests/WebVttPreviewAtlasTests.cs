using System.Net;
using System.Text;
using Grok.Player.Core.Preview;
using SkiaSharp;

namespace Grok.Player.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WebVttPreviewAtlasTests
{
    [Fact]
    public void Parses_relative_sprite_urls_and_crops_the_requested_tile()
    {
        const string manifest = "https://cdn.example/previews/thumbnails.vtt?proof=1";
        const string vtt = """
            WEBVTT

            00:00:00.000 --> 00:00:10.000
            thumbnails.jpg?v=2#xywh=0,0,320,180

            00:00:10.000 --> 00:00:20.000
            thumbnails.jpg?v=2#xywh=320,0,320,180
            """;
        using var bitmap = new SKBitmap(640, 180);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
            using var paint = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(320, 0, 320, 180, paint);
        }
        using var encodedImage = SKImage.FromBitmap(bitmap);
        using var encoded = encodedImage.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var http = new HttpClient(new AtlasHandler(vtt, encoded.ToArray()));
        using var atlas = new WebVttPreviewAtlas(manifest, "https://player.example/watch", http);

        Assert.Equal(10, atlas.IntervalSeconds);
        Assert.True(atlas.TryGetOrFetch(TimeSpan.FromSeconds(15), out var path));
        using var frame = SKBitmap.Decode(path);
        Assert.Equal((320, 180), (frame.Width, frame.Height));
        Assert.True(frame.GetPixel(160, 90).Blue > 200);
        Assert.True(atlas.RepresentsSameFrame(TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(19)));
        Assert.False(atlas.RepresentsSameFrame(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(11)));
    }

    private sealed class AtlasHandler(string vtt, byte[] sprite) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var manifest = request.RequestUri!.AbsolutePath.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = manifest
                    ? new StringContent(vtt, Encoding.UTF8, "text/vtt")
                    : new ByteArrayContent(sprite)
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
