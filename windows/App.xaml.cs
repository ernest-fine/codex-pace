using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CodexPace;

public partial class App : Application
{
    private const string InstanceName = @"Local\CodexPace";
    private const string ActivationName = @"Local\CodexPace.Activate";
    private Mutex? instance;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? activationWait;
    private PaceApplication? host;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (host is not null)
        {
            host.OpenDashboard();
            return;
        }

        activation = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationName);
        instance = new Mutex(true, InstanceName, out var firstInstance);
        if (!firstInstance)
        {
            activation.Set();
            activation.Dispose();
            instance.Dispose();
            Exit();
            return;
        }

        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        bool demo = arguments.Contains("--demo", StringComparer.Ordinal);
        bool checkTray = demo && arguments.Contains("--check-tray", StringComparer.Ordinal);
        if (checkTray && Environment.GetEnvironmentVariable("CODEX_PACE_TRAY_CHECK_LOG") is { } log)
        {
            UnhandledException += (_, error) => File.AppendAllText(log, $"{error.Message}\n{error.Exception}\n");
        }
        host = new PaceApplication(demo, ExitHost);
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        activationWait = ThreadPool.RegisterWaitForSingleObject(
            activation,
            (_, _) => dispatcher.TryEnqueue(() => host?.OpenDashboard()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        if (checkTray) _ = host.CheckTrayAsync();
    }

    private void ExitHost()
    {
        activationWait?.Unregister(null);
        activation?.Dispose();
        instance?.ReleaseMutex();
        instance?.Dispose();
        host = null;
        Exit();
    }
}
