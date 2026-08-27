using System.Net.NetworkInformation;

namespace Grok.Player.Core.Media;

public interface INetworkMonitor
{
    bool IsAvailable { get; }

    event Action<bool>? Changed;
}

public sealed class NetworkMonitor : INetworkMonitor, IDisposable
{
    private readonly object _gate = new();
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(8));
    private readonly CancellationTokenSource _cts = new();
    private bool _nicUp = NetworkInterface.GetIsNetworkAvailable();
    private bool _reachable = true;

    public NetworkMonitor()
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailability;
        _ = ProbeLoop();
    }

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _nicUp && _reachable;
            }
        }
    }

    public event Action<bool>? Changed;

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailability;
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();
    }

    public static bool ProbeReachable()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = http.GetAsync("http://connectivitycheck.gstatic.com/generate_204")
                .GetAwaiter()
                .GetResult();
            if ((int)response.StatusCode is 204 or >= 200 and < 400)
            {
                return true;
            }
        }
        catch (Exception)
        {
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = http.GetAsync("http://www.msftconnecttest.com/connecttest.txt")
                .GetAwaiter()
                .GetResult();
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task ProbeLoop()
    {
        await RefreshReachable();
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
                await RefreshReachable();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task RefreshReachable()
    {
        var reachable = _nicUp && ProbeReachable();
        SetReachable(reachable);
        return Task.CompletedTask;
    }

    private void OnAvailability(object? sender, NetworkAvailabilityEventArgs e)
    {
        lock (_gate)
        {
            _nicUp = e.IsAvailable;
        }

        if (!e.IsAvailable)
        {
            SetReachable(false);
            return;
        }

        _ = RefreshReachable();
    }

    private void SetReachable(bool reachable)
    {
        bool changed;
        lock (_gate)
        {
            changed = _reachable != reachable;
            _reachable = reachable;
        }

        if (changed)
        {
            Changed?.Invoke(IsAvailable);
        }
    }
}

public sealed class StaticNetworkMonitor : INetworkMonitor
{
    private bool _available;

    public StaticNetworkMonitor(bool available = true) => _available = available;

    public bool IsAvailable => _available;

    public event Action<bool>? Changed;

    public void SetAvailable(bool available)
    {
        if (_available == available)
        {
            return;
        }

        _available = available;
        Changed?.Invoke(available);
    }
}
