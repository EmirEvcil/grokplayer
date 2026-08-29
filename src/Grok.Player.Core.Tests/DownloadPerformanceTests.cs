using System.Net;
using Grok.Player.Core.Download;

namespace Grok.Player.Core.Tests;

public sealed class DownloadPerformanceTests
{
    [Fact]
    public void Large_download_completes_without_flooding_ui_progress_events()
    {
        var directory = Path.Combine(Path.GetTempPath(), "grok-download-test-" + Guid.NewGuid());
        using var finished = new ManualResetEventSlim();
        using var handler = new StreamingHandler();
        using var manager = new DownloadManager(new DownloadSettings { Folder = directory }, handler);
        var notifications = 0;
        manager.Changed += () =>
        {
            Interlocked.Increment(ref notifications);
            if (manager.Jobs.Any(job => job.State is DownloadState.Completed or DownloadState.Failed)) finished.Set();
        };
        try
        {
            var job = manager.Enqueue("https://example.test/video.mp4", "test", true);
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(DownloadState.Completed, job.State);
            Assert.Equal(32 * 1024 * 1024, new FileInfo(job.OutputPath).Length);
            Assert.InRange(notifications, 3, 24); // Not one redraw per 512 KiB.
        }
        finally
        {
            manager.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class StreamingHandler : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token) =>
            new(HttpStatusCode.OK) { Content = new StreamContent(new ZeroStream()) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(Send(request, token));
    }

    private sealed class ZeroStream : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 32 * 1024 * 1024;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, Length - _position);
            Array.Clear(buffer, offset, read);
            _position += read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
