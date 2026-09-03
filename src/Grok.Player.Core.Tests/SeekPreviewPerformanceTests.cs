using Grok.Player.Core.Media;
using Grok.Player.Core.Preview;
using Grok.Player.Core.Tests.Fakes;

namespace Grok.Player.Core.Tests;

public sealed class SeekPreviewPerformanceTests
{
    [Fact]
    public void Warm_does_not_delay_atlas_hover()
    {
        var hover = new SlowPrepareRenderer(TimeSpan.FromMilliseconds(800));
        using var atlas = new InstantAtlas();
        using var scheduler = new SeekPreviewScheduler(hover);
        var ready = new ManualResetEventSlim(false);
        string? image = null;
        scheduler.FrameReady += (_, path) =>
        {
            image = path;
            ready.Set();
        };

        scheduler.SetMedia("https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8", TimeSpan.FromMinutes(20), prefetch: false);
        scheduler.SetAtlas(atlas);
        scheduler.Warm("https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8");
        Thread.Sleep(30);
        var started = DateTime.UtcNow;
        scheduler.Request(TimeSpan.FromSeconds(80));
        Assert.True(ready.Wait(TimeSpan.FromMilliseconds(350)), "atlas hover waited for Warm/Prepare");
        Assert.True((DateTime.UtcNow - started).TotalMilliseconds < 350);
        Assert.Equal(atlas.PathFor(TimeSpan.FromSeconds(80)), image);
        Assert.Empty(hover.Times);
    }

    [Fact]
    public void Coverage_can_be_attached_later_without_blocking_hover()
    {
        var hover = new InstantRenderer();
        var coverage = new SlowRenderer(TimeSpan.FromMilliseconds(1200));
        using var scheduler = new SeekPreviewScheduler(hover);
        var ready = new ManualResetEventSlim(false);
        scheduler.FrameReady += (time, _) =>
        {
            if (time == TimeSpan.FromMinutes(12))
            {
                ready.Set();
            }
        };

        scheduler.SetMedia("https://cdn.example/vod.m3u8", TimeSpan.FromHours(1), prefetch: true);
        scheduler.AttachCoverage(coverage);
        scheduler.Request(TimeSpan.FromMinutes(12));
        Assert.True(ready.Wait(TimeSpan.FromMilliseconds(400)), "late coverage attach blocked hover");
    }

    [Fact]
    public void Dual_engine_hover_does_not_wait_for_a_slow_coverage_capture()
    {
        var hover = new InstantRenderer();
        var coverage = new SlowRenderer(TimeSpan.FromMilliseconds(1500));
        using var scheduler = new SeekPreviewScheduler(hover, coverageRenderer: coverage);
        var ready = new ManualResetEventSlim(false);
        TimeSpan published = TimeSpan.Zero;
        scheduler.FrameReady += (time, _) =>
        {
            if (time == TimeSpan.FromMinutes(26))
            {
                published = time;
                ready.Set();
            }
        };

        scheduler.SetMedia("https://cdn.example/vod.m3u8", TimeSpan.FromHours(1), prefetch: true);
        Assert.True(coverage.Started.Wait(TimeSpan.FromSeconds(2)), "coverage never started");
        var started = DateTime.UtcNow;
        scheduler.Request(TimeSpan.FromMinutes(26));
        Assert.True(ready.Wait(TimeSpan.FromMilliseconds(400)), "hover still waited on coverage");
        Assert.True((DateTime.UtcNow - started).TotalMilliseconds < 400, "hover took too long");
        Assert.Equal(TimeSpan.FromMinutes(26), published);
        Assert.Contains(TimeSpan.FromMinutes(26), hover.Times);
    }

    [Fact]
    public void Youtube_storyboard_never_starts_a_decoder_even_when_upgrade_is_flagged()
    {
        var hover = new InstantRenderer();
        var coverage = new InstantRenderer();
        using var atlas = new InstantAtlas { Upgrade = true };
        using var scheduler = new SeekPreviewScheduler(hover, coverageRenderer: coverage);
        var ready = new ManualResetEventSlim(false);
        scheduler.FrameReady += (_, _) => ready.Set();
        scheduler.SetMedia(
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            TimeSpan.FromMinutes(20),
            prefetch: false);
        scheduler.SetAtlas(atlas);
        scheduler.Request(TimeSpan.FromSeconds(80));
        Assert.True(ready.Wait(TimeSpan.FromMilliseconds(300)));
        Thread.Sleep(120);
        Assert.Empty(hover.Times);
        Assert.Empty(coverage.Times);
    }

    [Fact]
    public void Atlas_hover_is_served_without_starting_a_decoder()
    {
        var hover = new InstantRenderer();
        var coverage = new InstantRenderer();
        using var atlas = new InstantAtlas();
        using var scheduler = new SeekPreviewScheduler(hover, coverageRenderer: coverage);
        var ready = new ManualResetEventSlim(false);
        string? image = null;
        scheduler.FrameReady += (_, path) =>
        {
            image = path;
            ready.Set();
        };

        scheduler.SetMedia("https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8", TimeSpan.FromMinutes(20), prefetch: true);
        scheduler.SetAtlas(atlas);
        var started = DateTime.UtcNow;
        scheduler.Request(TimeSpan.FromSeconds(80));
        Assert.True(ready.Wait(TimeSpan.FromMilliseconds(300)), "YouTube atlas hover was late");
        Assert.True((DateTime.UtcNow - started).TotalMilliseconds < 300);
        Assert.Equal(atlas.PathFor(TimeSpan.FromSeconds(80)), image);
        Thread.Sleep(80);
        Assert.Empty(hover.Times);
        Assert.Empty(coverage.Times);
    }

    [Fact]
    public void Setting_an_atlas_cancels_decoder_coverage()
    {
        var coverage = new InstantRenderer();
        using var atlas = new InstantAtlas();
        using var scheduler = new SeekPreviewScheduler(new InstantRenderer(), coverageRenderer: coverage);
        scheduler.SetMedia("https://cdn.example/vod.m3u8", TimeSpan.FromMinutes(20), prefetch: true);
        Assert.True(WaitFor(() =>
        {
            lock (coverage.Times)
            {
                return coverage.Times.Count > 0;
            }
        }, 500), "coverage never started before the atlas bound");
        scheduler.SetAtlas(atlas);
        var afterAtlas = coverage.Times.Count;
        Thread.Sleep(150);
        lock (coverage.Times)
        {
            Assert.True(coverage.Times.Count - afterAtlas <= 1, "coverage kept decoding after the storyboard bound");
        }
    }

    [Fact]
    public void Latest_hover_replaces_an_in_flight_hover_without_serving_the_old_time()
    {
        var hover = new SlowRenderer(TimeSpan.FromMilliseconds(200));
        using var scheduler = new SeekPreviewScheduler(hover, coverageRenderer: new InstantRenderer());
        var published = new List<TimeSpan>();
        scheduler.FrameReady += (time, _) =>
        {
            lock (published)
            {
                published.Add(time);
            }
        };

        scheduler.SetMedia("https://cdn.example/vod.m3u8", TimeSpan.FromHours(1), prefetch: false);
        scheduler.Request(TimeSpan.FromMinutes(10));
        Assert.True(hover.Started.Wait(TimeSpan.FromSeconds(2)));
        scheduler.Request(TimeSpan.FromMinutes(36));
        Assert.True(WaitFor(() =>
        {
            lock (published)
            {
                return published.Contains(TimeSpan.FromMinutes(36));
            }
        }));
        lock (published)
        {
            Assert.DoesNotContain(TimeSpan.FromMinutes(10), published);
            Assert.Contains(TimeSpan.FromMinutes(36), published);
        }
    }

    [Fact]
    public void Youtube_media_does_not_start_decoder_coverage()
    {
        var coverage = new InstantRenderer();
        using var scheduler = new SeekPreviewScheduler(new InstantRenderer(), coverageRenderer: coverage);
        scheduler.SetMedia(
            "https://manifest.googlevideo.com/api/manifest/hls_variant/vod.m3u8",
            TimeSpan.FromMinutes(20),
            prefetch: true);
        Thread.Sleep(150);
        lock (coverage.Times)
        {
            Assert.Empty(coverage.Times);
        }
    }

    [Fact]
    public void Proxy_preview_keeps_the_protected_url_instead_of_unwrapping()
    {
        var fake = new FakeMpvNative { AutoDurationSeconds = 3600 };
        using var engine = new SeekPreviewEngine(fake);
        var real = "https://fastplay.mom/manifests/secret/master.txt?verify=1";
        var proxy = ProtectedStreamProxy.Register(real, "https://fastplay.mom/video/x", "secret", 0);
        engine.Prepare(proxy, "https://www.hdfilmcehennemi.now/bolum/x/");
        Assert.Contains(fake.Commands, command =>
            command.Length >= 2 &&
            command[0] == "loadfile" &&
            command[1] == proxy);
        Assert.DoesNotContain(fake.Commands, command =>
            command.Length >= 2 &&
            command[0] == "loadfile" &&
            command[1].Contains("fastplay.mom/manifests", StringComparison.Ordinal));
    }

    [Fact]
    public void Twenty_hover_moves_keep_publishing_the_current_time_only()
    {
        var hover = new InstantRenderer();
        using var scheduler = new SeekPreviewScheduler(hover, coverageRenderer: new SlowRenderer(TimeSpan.FromMilliseconds(80)));
        var last = TimeSpan.Zero;
        var count = 0;
        scheduler.FrameReady += (time, _) =>
        {
            last = time;
            Interlocked.Increment(ref count);
        };

        scheduler.SetMedia("https://cdn.example/vod.m3u8", TimeSpan.FromHours(1), prefetch: true);
        for (var i = 1; i <= 20; i++)
        {
            scheduler.Request(TimeSpan.FromMinutes(i));
            Thread.Sleep(15);
        }

        Assert.True(WaitFor(() => last == TimeSpan.FromMinutes(20)));
        Assert.True(count >= 1);
        Assert.Equal(TimeSpan.FromMinutes(20), last);
    }

    private static bool WaitFor(Func<bool> ready, int milliseconds = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (ready())
            {
                return true;
            }

            Thread.Sleep(15);
        }

        return ready();
    }

    private sealed class InstantRenderer : ISeekPreviewRenderer, IFastSeekPreviewRenderer
    {
        public List<TimeSpan> Times { get; } = [];

        public void Prepare(string path)
        {
        }

        public string? CaptureFast(TimeSpan time) => Capture(time);

        public string? Capture(TimeSpan time)
        {
            lock (Times)
            {
                Times.Add(time);
            }

            return WriteStill("hover");
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class SlowPrepareRenderer : ISeekPreviewRenderer, IFastSeekPreviewRenderer
    {
        private readonly TimeSpan _delay;

        public SlowPrepareRenderer(TimeSpan delay) => _delay = delay;

        public List<TimeSpan> Times { get; } = [];

        public void Prepare(string path) => Thread.Sleep(_delay);

        public string? CaptureFast(TimeSpan time) => Capture(time);

        public string? Capture(TimeSpan time)
        {
            lock (Times)
            {
                Times.Add(time);
            }

            return WriteStill("prep");
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class SlowRenderer : ISeekPreviewRenderer, IFastSeekPreviewRenderer
    {
        private readonly TimeSpan _delay;
        private int _started;

        public SlowRenderer(TimeSpan delay) => _delay = delay;

        public ManualResetEventSlim Started { get; } = new(false);
        public List<TimeSpan> Times { get; } = [];

        public void Prepare(string path)
        {
        }

        public string? CaptureFast(TimeSpan time) => Capture(time);

        public string? Capture(TimeSpan time)
        {
            if (Interlocked.Increment(ref _started) == 1)
            {
                Started.Set();
            }

            lock (Times)
            {
                Times.Add(time);
            }

            Thread.Sleep(_delay);
            return WriteStill("slow");
        }

        public void Reset()
        {
        }

        public void Dispose() => Started.Set();
    }

    private sealed class InstantAtlas : IPreviewAtlas
    {
        public bool Upgrade { get; init; }
        public bool NeedsDecodedUpgrade => Upgrade;
        public double IntervalSeconds => 10;

        public string PathFor(TimeSpan time)
        {
            var path = Path.Combine(Path.GetTempPath(), "perf-atlas-" + (int)(time.TotalSeconds / 10) + ".jpg");
            if (!File.Exists(path))
            {
                var bytes = new byte[900];
                bytes[0] = 0xFF;
                bytes[1] = 0xD8;
                File.WriteAllBytes(path, bytes);
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

    private static string WriteStill(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"perf-{tag}-{Guid.NewGuid():N}.jpg");
        var bytes = new byte[4096];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
