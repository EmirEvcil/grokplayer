using System.Diagnostics;
using Grok.Player.App.Native;
using Grok.Player.Core.Download;
using Grok.Player.Core.Launch;
using Microsoft.UI.Xaml;

namespace Grok.Player.App;

public partial class App : Application
{
    private Window? _window;
    private InstanceIpc? _ipc;

    public App()
    {
        UnhandledException += (_, e) =>
        {
            try
            {
                var text = e.Exception.ToString();
                if (e.Exception.InnerException is { } inner)
                {
                    text += Environment.NewLine + inner;
                }

                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), text);
            }
            catch
            {
            }

            if (e.Exception is Grok.Player.Core.Native.MpvException)
            {
                e.Handled = true;
            }
        };

        InitializeComponent();
    }

    public static MainWindow? Main { get; private set; }

    public static DownloadManager Downloads { get; } = new();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var launch = InstanceLaunchArgs.Parse(argv);
        var recovered = InstanceLaunchArgs.RecoverProtocol(Environment.CommandLine);
        if (recovered is not null &&
            (string.IsNullOrWhiteSpace(launch.Path) || recovered.Length > launch.Path.Length))
        {
            launch = launch.WithPath(recovered);
        }
        if (InstanceIpc.TryOwn(out var ipc))
        {
            _ipc = ipc;
        }
        else
        {
            ipc.Dispose();
            if (!launch.NewInstance)
            {
                var payload = launch.Path ?? "--activate";
                if (!InstanceIpc.TrySend(payload))
                    InstanceIpc.TryEnqueueDrop(payload);
                // IPC failure must not silently turn a normal open into a new instance.
                Environment.Exit(0);
                return;
            }
        }

        ProtocolRegistration.EnsureCurrentUser(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GrokPlayer.exe"));
        _window = Main = new MainWindow();
        if (_ipc is not null)
        {
            _ipc.Received += payload =>
            {
                var window = Main;
                if (window is null)
                {
                    return;
                }

                if (!window.DispatcherQueue.TryEnqueue(() => window.OpenFromExternal(payload)))
                {
                    InstanceIpc.TryEnqueueDrop(payload);
                }
            };
        }

        _window.Activate();
    }
}
