using Grok.Player.Core.Preview;
using Grok.Player.Core.Tests.Fakes;
using Grok.Player.Core.Tests.Support;

namespace Grok.Player.Core.Tests;

public sealed class SeekPreviewTests
{
    [Fact]
    public void Frozen_hls_window_selects_the_segment_relative_to_the_live_edge()
    {
        var playlist = """
            #EXTM3U
            #EXT-X-MEDIA-SEQUENCE:100
            #EXT-X-MAP:URI="init.mp4"
            #EXTINF:4,
            1.m4s
            #EXTINF:4,
            2.m4s
            #EXTINF:4,
            3.m4s
            #EXTINF:4,
            4.m4s
            #EXTINF:4,
            5.m4s
            """;
        var window = HlsLivePreviewExtractor.BuildWindow(
            new Uri("https://cdn.example/live/media.m3u8"), playlist, behindLiveSeconds: 5);
        Assert.NotNull(window);
        Assert.Equal(7, window.Value.SeekSeconds, precision: 3);
        Assert.DoesNotContain("/1.m4s", window.Value.Manifest);
        Assert.Contains("https://cdn.example/live/3.m4s", window.Value.Manifest);
        Assert.Contains("https://cdn.example/live/5.m4s", window.Value.Manifest);
        Assert.Contains("https://cdn.example/live/init.mp4", window.Value.Manifest);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:102", window.Value.Manifest);
    }

    [Fact]
    public void Hover_without_media_is_hidden()
    {
        using var controller = new SeekPreviewController(new RecordingRenderer());
        var state = controller.Move(10, 100);
        Assert.False(state.IsVisible);
        Assert.Null(state.ImagePath);
    }

    [Fact]
    public void Hover_shows_timestamp_immediately()
    {
        using var renderer = new RecordingRenderer();
        using var controller = new SeekPreviewController(renderer, TimeSpan.Zero, 0);
        controller.SetMedia(@"C:\a.mp4", TimeSpan.FromSeconds(100));
        var state = controller.Move(50, 200, DateTime.UtcNow);
        Assert.True(state.IsVisible);
        Assert.Equal("00:25", state.TimeText);
        Assert.Equal(0.25, state.NormalizedPosition);
        Assert.Empty(renderer.Times);
    }

    [Fact]
    public void RememberImage_attaches_to_current_preview()
    {
        using var controller = new SeekPreviewController(new RecordingRenderer(), TimeSpan.Zero, 0);
        controller.SetMedia(@"C:\a.mp4", TimeSpan.FromSeconds(100));
        controller.Move(50, 200);
        controller.RememberImage(@"C:\thumb.png");
        Assert.Equal(@"C:\thumb.png", controller.Current.ImagePath);
    }

    [Fact]
    public void Hover_shows_loading_instead_of_an_unrelated_previous_frame()
    {
        using var controller = new SeekPreviewController(new RecordingRenderer(), TimeSpan.Zero, 0);
        controller.SetMedia(@"C:\a.mp4", TimeSpan.FromSeconds(100));
        controller.Move(50, 200);
        controller.RememberImage(@"C:\thumb.png");
        var later = controller.Move(180, 200);
        Assert.Null(later.ImagePath);
        Assert.Equal("01:30", later.TimeText);
    }

    [Fact]
    public void Scheduler_coalesces_and_returns_cached_frame()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        var ready = new ManualResetEventSlim(false);
        string? image = null;
        scheduler.FrameReady += (_, path) =>
        {
            image = path;
            ready.Set();
        };

        scheduler.SetMedia(@"C:\a.mp4");
        scheduler.Request(TimeSpan.FromSeconds(10));
        scheduler.Request(TimeSpan.FromSeconds(10.2));
        Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
        Assert.NotNull(image);
        Assert.True(renderer.Times.Count >= 1);
        Assert.Equal(image, scheduler.GetCached(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Scheduler_still_captures_the_latest_hover_after_an_in_flight_frame()
    {
        var renderer = new BlockingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        var ready = new ManualResetEventSlim(false);
        scheduler.FrameReady += (time, _) =>
        {
            if (time == TimeSpan.FromSeconds(9))
            {
                ready.Set();
            }
        };

        scheduler.SetMedia(@"C:\a.mp4");
        scheduler.Request(TimeSpan.FromSeconds(1));
        Assert.True(renderer.FirstCaptureStarted.Wait(TimeSpan.FromSeconds(2)));
        scheduler.Request(TimeSpan.FromSeconds(9));
        renderer.ReleaseFirstCapture.Set();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
        Assert.NotNull(scheduler.GetCached(TimeSpan.FromSeconds(1)));
        Assert.NotNull(scheduler.GetCached(TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void Network_hover_discards_old_prefetch_and_prioritizes_the_new_neighborhood()
    {
        var renderer = new BlockingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        var published = new System.Collections.Concurrent.ConcurrentQueue<TimeSpan>();
        scheduler.FrameReady += (time, _) => published.Enqueue(time);
        scheduler.SetMedia("https://cdn.example/vod.m3u8", null, prefetch: false);
        scheduler.Request(TimeSpan.FromSeconds(10));
        Assert.True(renderer.FirstCaptureStarted.Wait(TimeSpan.FromSeconds(2)));

        scheduler.Request(TimeSpan.FromSeconds(100));
        renderer.ReleaseFirstCapture.Set();

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (renderer.Times)
            {
                if (renderer.Times.Count >= 3)
                {
                    Assert.Equal(TimeSpan.FromSeconds(10), renderer.Times[0]);
                    Assert.Equal(TimeSpan.FromSeconds(100), renderer.Times[1]);
                    Assert.Equal(TimeSpan.FromSeconds(98), renderer.Times[2]);
                    Assert.Equal(TimeSpan.FromSeconds(100), Assert.Single(published));
                    return;
                }
            }

            Thread.Sleep(20);
        }

        Assert.Fail("The prioritized hover neighborhood was not rendered in time.");
    }

    [Fact]
    public void Exact_network_hover_does_not_queue_unrequested_neighbors()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        var ready = new ManualResetEventSlim(false);
        scheduler.FrameReady += (_, _) => ready.Set();
        scheduler.SetMedia("https://cdn.example/live.m3u8", null, prefetch: false);
        scheduler.RequestExact("https://cdn.example/live.m3u8", TimeSpan.FromSeconds(42));
        Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
        Thread.Sleep(100);
        lock (renderer.Times)
            Assert.Equal([TimeSpan.FromSeconds(42)], renderer.Times);
    }

    [Fact]
    public void Live_exact_hover_uses_edge_distance_without_preparing_a_sliding_decoder()
    {
        var renderer = new LiveRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        var ready = new ManualResetEventSlim(false);
        scheduler.FrameReady += (_, _) => ready.Set();
        scheduler.SetMedia("https://cdn.example/live.m3u8", null, prefetch: false);
        scheduler.RequestLiveExact(
            "https://cdn.example/live.m3u8",
            TimeSpan.FromSeconds(42),
            behindLiveSeconds: 120);
        Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(120, renderer.BehindLiveSeconds);
        Assert.Equal(0, renderer.PrepareCalls);
    }

    [Fact]
    public void Scheduler_returns_nearest_frame_even_when_far()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        var ready = new ManualResetEventSlim(false);
        scheduler.FrameReady += (_, _) => ready.Set();
        scheduler.SetMedia(@"C:\a.mp4");
        scheduler.Request(TimeSpan.FromSeconds(2));
        Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
        Assert.NotNull(scheduler.GetCached(TimeSpan.FromSeconds(2)));
        Assert.Null(scheduler.GetCached(TimeSpan.FromSeconds(40)));
        Assert.Equal(
            scheduler.GetCached(TimeSpan.FromSeconds(2)),
            scheduler.GetCached(TimeSpan.FromSeconds(40), maxDeltaSeconds: -1));
    }

    [Fact]
    public void Scheduler_prefetches_a_storyboard_and_serves_hover_first()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        var hover = new ManualResetEventSlim(false);
        scheduler.FrameReady += (time, _) =>
        {
            if (time == TimeSpan.FromSeconds(12))
            {
                hover.Set();
            }
        };

        scheduler.SetMedia(@"C:\a.mp4");
        scheduler.Request(TimeSpan.FromSeconds(12));
        Assert.True(hover.Wait(TimeSpan.FromSeconds(2)));
        lock (renderer.Times)
        {
            Assert.Equal(TimeSpan.FromSeconds(12), renderer.Times[0]);
        }

        scheduler.SetMedia(@"C:\a.mp4", TimeSpan.FromSeconds(20));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            lock (renderer.Times)
            {
                if (renderer.Times.Count >= 4)
                {
                    break;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            Thread.Sleep(20);
        }

        lock (renderer.Times)
        {
            Assert.True(renderer.Times.Count >= 4);
        }
    }

    [Fact]
    public void Scheduler_hides_a_frame_that_is_too_far_from_the_hover()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        var ready = new ManualResetEventSlim(false);
        scheduler.FrameReady += (_, _) => ready.Set();
        scheduler.SetMedia(@"C:\a.mp4");
        scheduler.Request(TimeSpan.FromSeconds(2));
        Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
        Assert.NotNull(scheduler.GetCached(TimeSpan.FromSeconds(2), maxDeltaSeconds: 8));
        Assert.Null(scheduler.GetCached(TimeSpan.FromSeconds(40), maxDeltaSeconds: 8));
        Assert.Null(scheduler.GetCached(TimeSpan.FromSeconds(40)));
    }

    [Fact]
    public void Scheduler_returns_nearest_cached_frame()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        var ready = new ManualResetEventSlim(false);
        scheduler.FrameReady += (_, _) => ready.Set();
        scheduler.SetMedia(@"C:\a.mp4");
        scheduler.Request(TimeSpan.FromSeconds(10));
        Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(scheduler.GetCached(TimeSpan.FromSeconds(10)), scheduler.GetCached(TimeSpan.FromSeconds(10.4)));
    }

    [Fact]
    public void Live_hover_uses_the_available_window()
    {
        using var controller = new SeekPreviewController(new RecordingRenderer(), TimeSpan.Zero, 0);
        controller.SetMedia("https://cdn.example/live.m3u8", TimeSpan.FromSeconds(900));
        var state = controller.Move(100, 200, DateTime.UtcNow);
        Assert.True(state.IsVisible);
        Assert.Equal("07:30", state.TimeText);
        Assert.Equal(0.5, state.NormalizedPosition);
    }

    [Fact]
    public void Scheduler_remembers_harvested_frames_and_ignores_far_hits()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        scheduler.SetMedia("https://cdn.example/live.m3u8", TimeSpan.FromSeconds(900), prefetch: false);
        var file = Path.Combine(Path.GetTempPath(), $"harvest-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        try
        {
            scheduler.Remember(TimeSpan.FromSeconds(80), file);
            Assert.Equal(file, scheduler.GetCached(TimeSpan.FromSeconds(80)));
            Assert.Equal(file, scheduler.GetCached(TimeSpan.FromSeconds(81), maxDeltaSeconds: 2.5));
            Assert.Null(scheduler.GetCached(TimeSpan.FromSeconds(400), maxDeltaSeconds: 2.5));
            lock (renderer.Times)
            {
                Assert.Empty(renderer.Times);
            }
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Scheduler_endurance_keeps_a_bounded_cache()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        scheduler.SetMedia(@"C:\a.mp4", TimeSpan.FromSeconds(600), prefetch: false);
        var files = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            var file = Path.Combine(Path.GetTempPath(), $"endurance-{Guid.NewGuid():N}.png");
            var payload = new byte[4096];
            payload[0] = 0x89;
            payload[1] = 0x50;
            payload[2] = 0x4E;
            File.WriteAllBytes(file, payload);
            files.Add(file);
            scheduler.Remember(TimeSpan.FromSeconds(i), file);
        }

        Assert.NotNull(scheduler.GetCached(TimeSpan.FromSeconds(199)));
        Assert.InRange(files.Count(File.Exists), 1, 64);
        foreach (var leftover in files.Where(File.Exists))
        {
            try
            {
                File.Delete(leftover);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Scheduler_prefetches_a_range_and_skips_cached_times()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        var ready = new ManualResetEventSlim(false);
        scheduler.FrameReady += (_, _) => ready.Set();
        scheduler.SetMedia("https://cdn.example/live.m3u8", null, prefetch: false);
        var watched = Path.Combine(Path.GetTempPath(), $"watched-{Guid.NewGuid():N}.png");
        var payload = new byte[4096];
        payload[0] = 0x89;
        payload[1] = 0x50;
        payload[2] = 0x4E;
        File.WriteAllBytes(watched, payload);
        try
        {
            scheduler.Remember(TimeSpan.FromSeconds(90), watched);
            scheduler.PrefetchRange(TimeSpan.FromSeconds(80), TimeSpan.FromSeconds(200));
            var until = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < until)
            {
                lock (renderer.Times)
                {
                    if (renderer.Times.Count >= 4)
                    {
                        break;
                    }
                }

                Thread.Sleep(20);
            }

            lock (renderer.Times)
            {
                Assert.DoesNotContain(TimeSpan.FromSeconds(90), renderer.Times);
                Assert.True(renderer.Times.Count >= 4);
                Assert.All(renderer.Times, time =>
                {
                    Assert.InRange(time.TotalSeconds, 80, 200);
                });
            }
            Assert.False(ready.IsSet); // Background neighbors must not replace a hovered frame.
        }
        finally
        {
            try
            {
                File.Delete(watched);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Scheduler_skips_storyboard_prefetch_for_live()
    {
        var renderer = new RecordingRenderer();
        using var scheduler = new SeekPreviewScheduler(renderer, bucketSeconds: 1);
        scheduler.SetMedia("https://cdn.example/live.m3u8", TimeSpan.FromSeconds(900), prefetch: false);
        Thread.Sleep(250);
        lock (renderer.Times)
        {
            Assert.Empty(renderer.Times);
        }
    }

    [Fact]
    public void Hide_clears_visibility()
    {
        using var controller = new SeekPreviewController(new RecordingRenderer(), TimeSpan.Zero, 0);
        controller.SetMedia(@"C:\a.mp4", TimeSpan.FromSeconds(10));
        controller.Move(5, 10, DateTime.UtcNow);
        Assert.False(controller.Hide().IsVisible);
    }

    [Fact]
    public void Engine_seeks_and_writes_screenshot()
    {
        var fake = new FakeMpvNative();
        using var engine = new SeekPreviewEngine(fake);
        var path = TestMedia.CreateTempFile();
        try
        {
            engine.Prepare(path);
            fake.WaitEvent(0);
            var shot = engine.Capture(TimeSpan.FromSeconds(12.5));
            Assert.NotNull(shot);
            Assert.True(File.Exists(shot));
            Assert.Contains(fake.Commands, c => c[0] == "seek" && c[1] == "12.5" && c[2] == "absolute");
            engine.Prepare("https://cdn.example/vod.mp4");
            engine.Capture(TimeSpan.FromSeconds(8));
            Assert.Contains(fake.Commands, c => c[0] == "seek" && c[1] == "8" && (c[2] == "absolute+keyframes" || c[2] == "absolute"));
            Assert.Contains(fake.Commands, c => c[0] == "screenshot-to-file");
            Assert.True(fake.HasOption("screenshot-sw", "yes"));
            Assert.True(fake.HasOption("vo", "null"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Vod_hls_preview_does_not_use_live_demuxer()
    {
        var fake = new FakeMpvNative();
        using var engine = new SeekPreviewEngine(fake);
        engine.Prepare("https://stream.kick.com/vod/master.m3u8");
        Assert.DoesNotContain(fake.Lifecycle, item => item.Contains("live_start_index=-1", StringComparison.Ordinal));
        Assert.Contains(fake.Lifecycle, item => item.Contains("referrer=https://kick.com/", StringComparison.Ordinal));
    }

    [Fact]
    public void Network_preview_sends_site_referer()
    {
        var fake = new FakeMpvNative();
        using var engine = new SeekPreviewEngine(fake);
        engine.Prepare("https://v16-webapp.tiktokcdn.com/video/tos/foo");
        Assert.Contains(fake.Lifecycle, item => item.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase));
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

    private sealed class BlockingRenderer : ISeekPreviewRenderer
    {
        private int _captures;

        public List<TimeSpan> Times { get; } = [];
        public ManualResetEventSlim FirstCaptureStarted { get; } = new(false);
        public ManualResetEventSlim ReleaseFirstCapture { get; } = new(false);

        public void Prepare(string path)
        {
        }

        public string Capture(TimeSpan time)
        {
            lock (Times)
            {
                Times.Add(time);
            }

            if (Interlocked.Increment(ref _captures) == 1)
            {
                FirstCaptureStarted.Set();
                ReleaseFirstCapture.Wait(TimeSpan.FromSeconds(2));
            }

            return $"/tmp/preview-{time.TotalSeconds:0}.png";
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
            ReleaseFirstCapture.Set();
        }
    }

    private sealed class LiveRenderer : ISeekPreviewRenderer, ILiveSeekPreviewRenderer
    {
        public int PrepareCalls { get; private set; }
        public double BehindLiveSeconds { get; private set; }

        public void Prepare(string path) => PrepareCalls++;
        public string? Capture(TimeSpan time) => null;
        public string CaptureBehindLive(string path, double behindLiveSeconds, DateTime requestedUtc)
        {
            BehindLiveSeconds = behindLiveSeconds;
            return "/tmp/live-preview.jpg";
        }
        public void Reset() { }
        public void Dispose() { }
    }
}
