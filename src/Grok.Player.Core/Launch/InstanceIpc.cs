using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace Grok.Player.Core.Launch;

public sealed class InstanceIpc : IDisposable
{
    public const string MutexName = @"Local\GrokPlayer.SingleInstance";
    public const string PipeName = "GrokPlayer.Open";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<string> _queued = new();
    private Action<string>? _received;
    private bool _owns;
    private bool _disposed;

    private InstanceIpc(Mutex mutex, bool owns)
    {
        _mutex = mutex;
        _owns = owns;
        if (owns)
        {
            Task.Run(Listen);
            Task.Run(PollDrops);
        }
    }

    public event Action<string>? Received
    {
        add
        {
            _received += value;
            while (_queued.TryDequeue(out var payload))
            {
                value?.Invoke(payload);
            }
        }
        remove => _received -= value;
    }

    public static bool TryOwn(out InstanceIpc ipc)
    {
        var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            try
            {
                created = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                created = true;
            }
        }

        if (!created)
        {
            mutex.Dispose();
            ipc = new InstanceIpc(new Mutex(false, MutexName), false);
            return false;
        }

        ipc = new InstanceIpc(mutex, true);
        return true;
    }

    public static bool TrySend(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(1200);
                var bytes = Encoding.UTF8.GetBytes(payload);
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(80);
            }
        }

        return TryEnqueueDrop(payload);
    }

    public static bool TryEnqueueDrop(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            var dir = DropDirectory();
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Guid.NewGuid().ToString("N") + ".open"), payload);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string DropDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrokPlayer", "open");

    internal static IReadOnlyList<string> DrainDrops()
    {
        var found = new List<string>();
        var dir = DropDirectory();
        if (!Directory.Exists(dir))
        {
            return found;
        }

        foreach (var file in Directory.GetFiles(dir, "*.open"))
        {
            try
            {
                var text = File.ReadAllText(file);
                File.Delete(file);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    found.Add(text.Trim());
                }
            }
            catch (Exception)
            {
            }
        }

        return found;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        if (_owns)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
        _cts.Dispose();
    }

    private void Deliver(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        var handler = _received;
        if (handler is null)
        {
            _queued.Enqueue(payload);
            return;
        }

        handler(payload);
    }

    private void Listen()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                Deliver(reader.ReadToEnd());
            }
            catch (Exception)
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private void PollDrops()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                foreach (var payload in DrainDrops())
                {
                    Deliver(payload);
                }
            }
            catch (Exception)
            {
            }

            try
            {
                _cts.Token.WaitHandle.WaitOne(200);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }
}
