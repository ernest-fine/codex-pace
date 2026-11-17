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

        var demo = Environment.GetCommandLineArgs().Skip(1).Contains("--demo", StringComparer.Ordinal);
        host = new PaceApplication(demo, ExitHost);
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        activationWait = ThreadPool.RegisterWaitForSingleObject(
            activation,
            (_, _) => dispatcher.TryEnqueue(() => host?.OpenDashboard()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
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
