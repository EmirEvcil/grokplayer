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
    private readonly object _gate = new();
    private readonly string _pipeName;
    private readonly Task _workers;
    private Action<string>? _received;
    private bool _disposed;

    private InstanceIpc(Mutex mutex, bool owns, string pipeName, bool pollDrops)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _workers = Task.CompletedTask;
        if (owns)
        {
            _workers = Task.WhenAll(Task.Run(Listen), pollDrops ? Task.Run(PollDrops) : Task.CompletedTask);
        }
    }

    public event Action<string>? Received
    {
        add
        {
            lock (_gate)
            {
                _received += value;
                while (_queued.TryDequeue(out var payload)) value?.Invoke(payload);
            }
        }
        remove { lock (_gate) _received -= value; }
    }

    public static bool TryOwn(out InstanceIpc ipc) => TryOwn(out ipc, MutexName, PipeName, true);

    internal static bool TryOwn(out InstanceIpc ipc, string mutexName, string pipeName, bool pollDrops = false)
    {
        // Ownership is the lifetime of the named handle, not a particular UI thread.
        // A thread-owned mutex may become abandoned during WinUI activation.
        var mutex = new Mutex(false, mutexName, out var created);
        ipc = new InstanceIpc(mutex, created, pipeName, pollDrops);
        return created;
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
        _mutex.Dispose();
        _ = _workers.ContinueWith(_ => _cts.Dispose(), TaskScheduler.Default);
    }

    private void Deliver(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        lock (_gate)
        {
            if (_received is null) _queued.Enqueue(payload);
            else _received(payload);
        }
    }

    private async Task Listen()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                Deliver(await reader.ReadToEndAsync(_cts.Token));
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
