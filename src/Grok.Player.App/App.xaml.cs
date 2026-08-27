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
        if (!launch.NewInstance)
        {
            if (!InstanceIpc.TryOwn(out var ipc))
            {
                var payload = launch.Path ?? string.Join('\n', argv);
                if (InstanceIpc.TrySend(payload) || InstanceIpc.TryEnqueueDrop(payload))
                {
                    Environment.Exit(0);
                    return;
                }

                var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GrokPlayer.exe");
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        ArgumentList = { "--new-instance", "--stream", payload },
                        UseShellExecute = false
                    });
                }
                catch
                {
                }

                Environment.Exit(0);
                return;
            }

            _ipc = ipc;
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
