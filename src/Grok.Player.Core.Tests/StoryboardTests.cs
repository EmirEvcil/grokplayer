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
    public void Times_inside_the_same_storyboard_cell_represent_the_same_frame()
    {
        using var atlas = new StoryboardAtlas(StoryboardSpec.Parse(Spec)!);
        Assert.True(atlas.RepresentsSameFrame(TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(29)));
        Assert.False(atlas.RepresentsSameFrame(TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(31)));
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
            image ??= path;
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
    public void Scheduler_upgrades_low_preview_when_hover_moves_inside_the_same_cell()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 0.2);
        using var atlas = new ProgressiveFakeAtlas();
        var images = new List<string>();
        var highReady = new ManualResetEventSlim(false);
        scheduler.FrameReady += (_, path) =>
        {
            lock (images) images.Add(path);
            if (path == atlas.HighPath) highReady.Set();
        };
        scheduler.SetMedia("https://cdn.example/vod.m3u8", TimeSpan.FromSeconds(600), prefetch: false);
        scheduler.SetAtlas(atlas);
        scheduler.Request(TimeSpan.FromSeconds(21));
        Assert.True(atlas.LowServed.Wait(TimeSpan.FromSeconds(2)));
        scheduler.Request(TimeSpan.FromSeconds(29));
        atlas.AllowHigh.Set();
        Assert.True(highReady.Wait(TimeSpan.FromSeconds(2)));
        lock (images)
        {
            Assert.Contains(atlas.LowPath, images);
            Assert.Contains(atlas.HighPath, images);
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

    [Fact]
    public void Low_storyboard_upgrades_from_the_decoder_only_after_hover_settles()
    {
        using var atlas = new LowOnlyAtlas();
        using var renderer = new UpgradeRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 0.2, atlasUpgradeDelayMs: 30);
        var upgraded = new ManualResetEventSlim(false);
        scheduler.FrameReady += (_, path) =>
        {
            if (path == renderer.OutputPath) upgraded.Set();
        };
        scheduler.SetMedia("https://cdn.example/vod.m3u8", TimeSpan.FromMinutes(10), prefetch: false);
        scheduler.SetAtlas(atlas);

        scheduler.Request(TimeSpan.FromSeconds(10));
        Thread.Sleep(5);
        scheduler.Request(TimeSpan.FromSeconds(30));

        Assert.True(upgraded.Wait(TimeSpan.FromSeconds(2)));
        lock (renderer.Times)
        {
            Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(renderer.Times));
        }
    }

    [Fact]
    public void Decoder_upgrade_uses_the_storyboard_cell_time_without_changing_the_hover_target()
    {
        using var atlas = new LowOnlyAtlas();
        using var renderer = new UpgradeRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 0.2, atlasUpgradeDelayMs: 0);
        var upgraded = new ManualResetEventSlim(false);
        TimeSpan published = TimeSpan.Zero;
        scheduler.FrameReady += (time, path) =>
        {
            if (path != renderer.OutputPath) return;
            published = time;
            upgraded.Set();
        };
        scheduler.SetMedia("https://cdn.example/vod.m3u8", TimeSpan.FromMinutes(10), prefetch: false);
        scheduler.SetAtlas(atlas);

        scheduler.Request(TimeSpan.FromSeconds(37));

        Assert.True(upgraded.Wait(TimeSpan.FromSeconds(2)));
        lock (renderer.Times)
        {
            Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(renderer.Times));
        }
        Assert.Equal(TimeSpan.FromSeconds(37), published);
    }

    [Fact]
    public void Late_decoder_upgrade_from_previous_hover_is_not_published_inside_the_same_storyboard_cell()
    {
        using var atlas = new LowOnlyAtlas();
        using var renderer = new BlockingUpgradeRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 0.2, atlasUpgradeDelayMs: 0);
        var decoded = new List<string>();
        using var currentReady = new ManualResetEventSlim(false);
        scheduler.FrameReady += (_, path) =>
        {
            if (!renderer.IsOutput(path)) return;
            lock (decoded) decoded.Add(path);
            if (path == renderer.SecondOutput) currentReady.Set();
        };
        scheduler.SetMedia("https://cdn.example/vod.m3u8", TimeSpan.FromMinutes(10), prefetch: false);
        scheduler.SetAtlas(atlas);

        scheduler.Request(TimeSpan.FromSeconds(21));
        Assert.True(renderer.FirstCaptureStarted.Wait(TimeSpan.FromSeconds(2)));
        scheduler.Request(TimeSpan.FromSeconds(29));
        renderer.ReleaseFirstCapture.Set();

        Assert.True(currentReady.Wait(TimeSpan.FromSeconds(2)));
        lock (decoded)
        {
            Assert.DoesNotContain(renderer.FirstOutput, decoded);
            Assert.Contains(renderer.SecondOutput, decoded);
        }
    }

    [Fact]
    public void Storyboard_hover_never_borrows_a_nearby_decoded_frame_while_its_own_frame_loads()
    {
        var old = Path.Combine(Path.GetTempPath(), "grok-stale-decoder-" + Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(old, [1]);
        try
        {
            using var atlas = new MissingAtlas();
            using var scheduler = new SeekPreviewScheduler(new RecordingRenderer(), bucketSeconds: 0.2);
            scheduler.SetMedia("https://cdn.example/vod.m3u8", TimeSpan.FromMinutes(10), prefetch: false);
            scheduler.Remember(TimeSpan.FromSeconds(20), old);
            scheduler.SetAtlas(atlas);

            Assert.Equal(old, scheduler.GetCached(TimeSpan.FromSeconds(20), maxDeltaSeconds: 11));
            Assert.Null(scheduler.GetCached(TimeSpan.FromSeconds(29), maxDeltaSeconds: 11));
        }
        finally
        {
            File.Delete(old);
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

    private sealed class MissingAtlas : IPreviewAtlas
    {
        public double IntervalSeconds => 10;
        public bool TryGetFrame(TimeSpan time, out string path) { path = ""; return false; }
        public void Prefetch(TimeSpan time) { }
        public bool TryGetOrFetch(TimeSpan time, out string path) { path = ""; return false; }
        public void Dispose() { }
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

    private sealed class ProgressiveFakeAtlas : IPreviewAtlas
    {
        public ProgressiveFakeAtlas()
        {
            LowPath = Path.Combine(Path.GetTempPath(), "grok-preview-low-" + Guid.NewGuid().ToString("N") + ".png");
            HighPath = Path.Combine(Path.GetTempPath(), "grok-preview-high-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(LowPath, [1]);
            File.WriteAllBytes(HighPath, [2]);
        }

        public string LowPath { get; }
        public string HighPath { get; }
        public ManualResetEventSlim LowServed { get; } = new(false);
        public ManualResetEventSlim AllowHigh { get; } = new(false);
        public double IntervalSeconds => 10;
        public bool TryGetFrame(TimeSpan time, out string path) { path = ""; return false; }
        public void Prefetch(TimeSpan time) { }
        public bool TryGetOrFetch(TimeSpan time, out string path)
        {
            path = LowPath;
            LowServed.Set();
            return true;
        }
        public bool TryGetOrFetchBest(TimeSpan time, out string path)
        {
            AllowHigh.Wait(TimeSpan.FromSeconds(2));
            path = HighPath;
            return true;
        }
        public bool RepresentsSameFrame(TimeSpan left, TimeSpan right) =>
            (int)(left.TotalSeconds / 10) == (int)(right.TotalSeconds / 10);
        public TimeSpan FrameTime(TimeSpan time) =>
            TimeSpan.FromSeconds(Math.Floor(time.TotalSeconds / 10) * 10);
        public void Dispose()
        {
            LowServed.Dispose();
            AllowHigh.Dispose();
            File.Delete(LowPath);
            File.Delete(HighPath);
        }
    }

    private sealed class LowOnlyAtlas : IPreviewAtlas
    {
        private readonly List<string> _paths = [];
        public double IntervalSeconds => 10;
        public bool NeedsDecodedUpgrade => true;
        public bool TryGetFrame(TimeSpan time, out string path) => TryGetOrFetch(time, out path);
        public void Prefetch(TimeSpan time) { }
        public bool TryGetOrFetch(TimeSpan time, out string path)
        {
            path = Path.Combine(Path.GetTempPath(), "grok-low-only-" + (int)time.TotalSeconds + ".jpg");
            if (!File.Exists(path)) File.WriteAllBytes(path, [1]);
            if (!_paths.Contains(path)) _paths.Add(path);
            return true;
        }
        public bool RepresentsSameFrame(TimeSpan left, TimeSpan right) =>
            (int)(left.TotalSeconds / 10) == (int)(right.TotalSeconds / 10);
        public TimeSpan FrameTime(TimeSpan time) =>
            TimeSpan.FromSeconds(Math.Floor(time.TotalSeconds / 10) * 10);
        public void Dispose()
        {
            foreach (var path in _paths) File.Delete(path);
        }
    }

    private sealed class UpgradeRenderer : ISeekPreviewRenderer, IExactSeekPreviewRenderer
    {
        public UpgradeRenderer()
        {
            OutputPath = Path.Combine(Path.GetTempPath(), "grok-decoded-upgrade-" + Guid.NewGuid().ToString("N") + ".jpg");
        }
        public string OutputPath { get; }
        public List<TimeSpan> Times { get; } = [];
        public void Prepare(string path) { }
        public string Capture(TimeSpan time)
        {
            lock (Times) Times.Add(time);
            File.WriteAllBytes(OutputPath, [2]);
            return OutputPath;
        }
        public string CaptureExact(TimeSpan time) => Capture(time);
        public void Reset() { }
        public void Dispose() => File.Delete(OutputPath);
    }

    private sealed class BlockingUpgradeRenderer : ISeekPreviewRenderer, IExactSeekPreviewRenderer
    {
        private int _captures;

        public BlockingUpgradeRenderer()
        {
            FirstOutput = Path.Combine(Path.GetTempPath(), "grok-old-hover-" + Guid.NewGuid().ToString("N") + ".jpg");
            SecondOutput = Path.Combine(Path.GetTempPath(), "grok-current-hover-" + Guid.NewGuid().ToString("N") + ".jpg");
        }

        public string FirstOutput { get; }
        public string SecondOutput { get; }
        public ManualResetEventSlim FirstCaptureStarted { get; } = new(false);
        public ManualResetEventSlim ReleaseFirstCapture { get; } = new(false);
        public bool IsOutput(string path) => path == FirstOutput || path == SecondOutput;
        public void Prepare(string path) { }
        public string Capture(TimeSpan time) => CaptureExact(time);
        public string CaptureExact(TimeSpan time)
        {
            var capture = Interlocked.Increment(ref _captures);
            if (capture == 1)
            {
                FirstCaptureStarted.Set();
                ReleaseFirstCapture.Wait(TimeSpan.FromSeconds(2));
            }
            var output = capture == 1 ? FirstOutput : SecondOutput;
            File.WriteAllBytes(output, [capture == 1 ? (byte)1 : (byte)2]);
            return output;
        }
        public void Reset() { }
        public void Dispose()
        {
            ReleaseFirstCapture.Set();
            FirstCaptureStarted.Dispose();
            ReleaseFirstCapture.Dispose();
            File.Delete(FirstOutput);
            File.Delete(SecondOutput);
        }
    }
}
