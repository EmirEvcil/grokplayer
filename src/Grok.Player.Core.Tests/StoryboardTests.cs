using Grok.Player.Core.Media;
using Grok.Player.Core.Preview;

namespace Grok.Player.Core.Tests;

public sealed class StoryboardTests
{
    private const string Spec =
        "https://i.ytimg.com/sb/9uOMectkCCs/storyboard3_L$L/$N.jpg?sqp=sig" +
        "|48#27#100#10#10#0#default#rs$low" +
        "|80#45#102#10#10#10000#M$M#rs$mid" +
        "|160#90#102#5#5#10000#M$M#rs$high";

    [Fact]
    public void Parses_youtube_spec_and_picks_the_high_board()
    {
        var board = StoryboardSpec.Parse(Spec);
        Assert.NotNull(board);
        Assert.Equal(3, board!.Levels.Count);
        Assert.Equal(160, board.BestLevel!.Width);
        Assert.Equal(90, board.BestLevel.Height);
        Assert.Equal(10, board.BestLevel.Interval(null).TotalSeconds);
    }

    [Fact]
    public void Cell_at_25s_is_the_third_tile_on_sheet_zero()
    {
        var cell = StoryboardSpec.Parse(Spec)!.CellAt(TimeSpan.FromSeconds(25));
        Assert.NotNull(cell);
        Assert.Equal(0, cell!.Value.Sheet);
        Assert.Equal(2, cell.Value.Column);
        Assert.Equal(0, cell.Value.Row);
        Assert.Equal(160, cell.Value.CellWidth);
        Assert.Contains("storyboard3_L2/M0.jpg", cell.Value.Url, StringComparison.Ordinal);
        Assert.Contains("sigh=rs%24high", cell.Value.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Cell_crosses_onto_the_next_sheet()
    {
        var cell = StoryboardSpec.Parse(Spec)!.CellAt(TimeSpan.FromSeconds(260));
        Assert.NotNull(cell);
        Assert.Equal(1, cell!.Value.Sheet);
        Assert.Contains("/M1.jpg", cell.Value.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_spec_from_player_json()
    {
        var json =
            """
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"abcdefghijk","title":"Clip"},"streamingData":{"hlsManifestUrl":"https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8"},"storyboards":{"playerStoryboardSpecRenderer":{"spec":"https://i.ytimg.com/sb/abcdefghijk/storyboard3_L$L/$N.jpg|80#45#50#10#10#5000#M$M#rs$abc"}}}
            """;
        var playable = YouTubeCatalog.ParsePlayerResponse(json);
        Assert.NotNull(playable);
        Assert.Contains("storyboard3_L$L", playable!.StoryboardSpec, StringComparison.Ordinal);
        var board = StoryboardSpec.Parse(playable.StoryboardSpec);
        Assert.NotNull(board);
        Assert.Equal(5, board!.BestLevel!.Interval(null).TotalSeconds);
    }

    [Fact]
    public void Scheduler_serves_atlas_cells_without_capturing_video()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        using var atlas = new FakeAtlas();
        var ready = new ManualResetEventSlim(false);
        string? image = null;
        scheduler.FrameReady += (_, path) =>
        {
            image = path;
            ready.Set();
        };
        scheduler.SetMedia("https://cdn.example/vod.m3u8", TimeSpan.FromSeconds(600), prefetch: true);
        scheduler.SetAtlas(atlas);
        Assert.Equal(atlas.PathFor(TimeSpan.FromSeconds(80)), scheduler.GetCached(TimeSpan.FromSeconds(80)));
        scheduler.Request(TimeSpan.FromSeconds(80));
        Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(atlas.PathFor(TimeSpan.FromSeconds(80)), image);
        lock (renderer.Times)
        {
            Assert.Empty(renderer.Times);
        }
    }

    [Fact]
    public void Scheduler_keeps_the_atlas_when_the_hls_url_is_rebound()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        using var atlas = new FakeAtlas();
        scheduler.SetMedia("https://cdn.example/one.m3u8", TimeSpan.FromSeconds(600), prefetch: false);
        scheduler.SetAtlas(atlas);
        Assert.NotNull(scheduler.GetCached(TimeSpan.FromSeconds(20)));
        scheduler.SetMedia("https://cdn.example/two.m3u8", TimeSpan.FromSeconds(600), prefetch: false);
        scheduler.Request("https://cdn.example/two.m3u8", TimeSpan.FromSeconds(20));
        Assert.NotNull(scheduler.GetCached(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Network_prefetch_does_not_fill_the_start_of_the_video()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        scheduler.SetMedia("https://cdn.example/vod.m3u8", TimeSpan.FromSeconds(600), prefetch: true);
        Thread.Sleep(250);
        lock (renderer.Times)
        {
            Assert.Empty(renderer.Times);
        }
    }

    private sealed class RecordingRenderer : ISeekPreviewRenderer
    {
        public List<TimeSpan> Times { get; } = [];

        public void Prepare(string path)
        {
        }

        public string? Capture(TimeSpan time)
        {
            lock (Times)
            {
                Times.Add(time);
                return $"/tmp/preview-{Times.Count}.png";
            }
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeAtlas : IPreviewAtlas
    {
        public double IntervalSeconds => 10;

        public string PathFor(TimeSpan time)
        {
            var path = Path.Combine(Path.GetTempPath(), "atlas-" + (int)time.TotalSeconds + ".jpg");
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9, 0, 1, 2, 3]);
                var pad = new byte[900];
                pad[0] = 0xFF;
                pad[1] = 0xD8;
                File.WriteAllBytes(path, pad);
            }

            return path;
        }

        public bool TryGetFrame(TimeSpan time, out string path)
        {
            path = PathFor(time);
            return true;
        }

        public void Prefetch(TimeSpan time)
        {
        }

        public bool TryGetOrFetch(TimeSpan time, out string path) => TryGetFrame(time, out path);

        public void Dispose()
        {
        }
    }
}
