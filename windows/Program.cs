using System.Text.Json;

namespace CodexPace;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Graphite.FollowSystemAppearance();
        using var instance = new Mutex(true, @"Local\CodexPace", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Codex Pace is already running. Open it from the Windows notification area.",
                "Codex Pace", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var app = new PaceApplication(args.Contains("--demo", StringComparer.Ordinal));
        Application.Run(app);
    }
}

internal sealed class PaceApplication : ApplicationContext
{
    private readonly MainForm window;
    private readonly NotifyIcon tray;
    private readonly Icon? appIcon;
    private readonly ToolStripMenuItem caption = new("Codex Pace") { Enabled = false };
    private readonly ToolStripMenuItem refreshItem = new("Refresh usage");
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 120_000 };
    private readonly CancellationTokenSource shutdown = new();
    private readonly TelemetryReader reader = new();
    private readonly LocalStateStore store = new();
    private readonly bool demo;
    private LocalState state;
    private Telemetry? telemetry;
    private QuotaSample? latestObserved;
    private bool refreshing;
    private bool closing;
    private string? storageError;

    private static readonly DateTimeOffset DemoTime = WorkClock.At(new DateOnly(2026, 11, 18), 900);
    private DateTimeOffset Now => demo ? DemoTime : DateTimeOffset.UtcNow;

    public PaceApplication(bool demo)
    {
        this.demo = demo;
        state = demo ? new LocalState() : store.Load();
        window = new MainForm(state.Preferences);
        appIcon = Environment.ProcessPath is { } executable ? Icon.ExtractAssociatedIcon(executable) : null;
        window.Icon = appIcon ?? SystemIcons.Application;
        tray = new NotifyIcon
        {
            Icon = window.Icon,
            Text = "Codex Pace",
            ContextMenuStrip = menu,
            Visible = true
        };

        menu.Items.AddRange(
        [
            caption,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open dashboard", null, (_, _) => OpenDashboard()),
            refreshItem,
            new ToolStripMenuItem("Send test notification", null, (_, _) => TestNotification()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit", null, (_, _) => ExitThread()),
        ]);
        tray.DoubleClick += (_, _) => OpenDashboard();
        tray.BalloonTipClicked += (_, _) => OpenDashboard();
        refreshItem.Click += (_, _) => _ = RefreshAsync(force: true);
        window.RefreshRequested += () => _ = RefreshAsync(force: true);
        window.PreferencesChanged += ChangePreferences;
        window.TestNotificationRequested += TestNotification;
        window.FormClosing += (_, e) =>
        {
            if (!closing && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                window.Hide();
            }
        };
        window.FormClosed += (_, _) =>
        {
            if (!closing) ExitThread();
        };
        timer.Tick += (_, _) => _ = RefreshAsync(force: false);
        window.Shown += (_, _) =>
        {
            timer.Start();
            _ = RefreshAsync(force: false);
        };
        Render();
        window.Show();
    }

    private void OpenDashboard()
    {
        if (closing) return;
        window.Show();
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.Activate();
    }

    private async Task RefreshAsync(bool force)
    {
        if (refreshing || closing) return;
        refreshing = true;
        TimeZoneInfo.ClearCachedData();
        Render();
        try
        {
            var current = demo ? Example() : await reader.ReadAsync(Now, force, shutdown.Token);
            if (closing) return;
            Accept(current, Now);
        }
        catch (OperationCanceledException) when (closing)
        {
            // The window and notification area are being disposed.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!closing)
            {
                latestObserved = null;
                if (telemetry is not null)
                    telemetry = telemetry with { Error = "Couldn’t refresh Codex usage. Showing the last reading." };
            }
        }
        finally
        {
            refreshing = false;
            if (!closing) Render();
        }
    }

    private void Accept(Telemetry current, DateTimeOffset now)
    {
        latestObserved = current.Quota;
        if (current.Quota is { } observed && state.Preferences.ResetTarget is { } target
            && observed.ResetsAt != target.WindowResetsAt)
        {
            state = state with { Preferences = state.Preferences with { ResetTarget = null } };
        }

        var saved = state.History.LastOrDefault(sample => sample.ObservedAt <= now);
        var quota = current.Quota ?? saved;
        telemetry = current with
        {
            Quota = quota,
            Source = current.Quota is null && saved is not null ? "Last saved account reading" : current.Source
        };
        if (quota is not null)
        {
            var history = state.History.Concat(current.History).Append(quota)
                .Where(sample => sample.ResetsAt == quota.ResetsAt && sample.WindowMinutes == quota.WindowMinutes
                    && sample.ObservedAt <= now && sample.UsedPercent is >= 0 and <= 100)
                .GroupBy(sample => sample.ObservedAt).Select(group => group.Last())
                .OrderBy(sample => sample.ObservedAt).TakeLast(2400).ToArray();
            state = state with { History = history };
        }
        ConsiderWarning(now);
        Persist();
    }

    private void ChangePreferences(Preferences preferences)
    {
        state = state with { Preferences = preferences };
        ConsiderWarning(Now);
        Persist();
        Render();
    }

    private DashboardData CurrentData(DateTimeOffset now) =>
        Planner.Calculate(telemetry, state.History, state.Preferences, now);

    private void Render()
    {
        var now = Now;
        var data = CurrentData(now);
        if (storageError != null)
            data = data with { Error = string.Join(" ", new[] { data.Error, storageError }.Where(message => message != null)) };
        window.Render(data, state.Preferences, now, refreshing);
        caption.Text = Planner.TrayCaption(data, now, state.Preferences.ShowUsed);
        tray.Text = caption.Text;
        refreshItem.Enabled = !refreshing;
    }

    private void ConsiderWarning(DateTimeOffset now)
    {
        if (demo || latestObserved is not { } observed || observed.ResetsAt <= now
            || (now - observed.ObservedAt).TotalMinutes is < 0 or > 5) return;
        var warning = CurrentData(now).Warning;
        if (warning is null || warning.ExpiresAt <= now || state.SentWarnings.ContainsKey(warning.Key)) return;

        ShowWarning(warning, test: false);
        var active = state.SentWarnings.Where(entry => entry.Value > now)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        active[warning.Key] = now.AddDays(8);
        state = state with { SentWarnings = active };
    }

    private void TestNotification()
    {
        var example = Example();
        var result = Planner.Calculate(example, example.History, new Preferences(), DemoTime);
        if (result.Warning is { } warning) ShowWarning(warning, test: true);
    }

    private void ShowWarning(BudgetWarning warning, bool test)
    {
        var title = test ? "Test · " + warning.Title : warning.Title;
        tray.ShowBalloonTip(8000, title, warning.Recommendation + "\n" + warning.Detail, ToolTipIcon.Warning);
    }

    private void Persist()
    {
        if (!demo) storageError = store.Save(state) ? null : "Couldn’t save local preferences or quota history.";
    }

    private static Telemetry Example()
    {
        var reset = WorkClock.At(new DateOnly(2026, 11, 23), 1275);
        var offsets = new[] { -4.5, -3.5, -2.5, -1, 0 };
        var amounts = new double[] { 49, 53, 57, 57, 60 };
        var history = offsets.Zip(amounts, (hours, used) =>
            new QuotaSample(DemoTime.AddHours(hours), reset, 10_080, used)).ToArray();
        ModelOption[] models =
        [
            new("gpt-6.1-sol", "GPT-6.1 Sol", ["low", "medium", "high", "xhigh", "max", "ultra"], "medium", true),
            new("gpt-6-luna", "GPT-6 Luna", ["low", "medium", "high", "max"], "high", false)
        ];
        return new Telemetry(history[^1], null, history,
            new Activity(DemoTime, "gpt-6.1-sol", "high", 4, 180_000), models, "Example data", null);
    }

    protected override void ExitThreadCore()
    {
        if (closing) return;
        closing = true;
        timer.Stop();
        shutdown.Cancel();
        tray.Visible = false;
        if (!window.IsDisposed) window.Close();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            tray.Dispose();
            menu.Dispose();
            window.Dispose();
            appIcon?.Dispose();
            shutdown.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed record LocalState
{
    public Preferences Preferences { get; init; } = new();
    public QuotaSample[] History { get; init; } = [];
    public Dictionary<string, DateTimeOffset> SentWarnings { get; init; } = new();
}

internal sealed class LocalStateStore
{
    private static readonly string PathToState = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Codex Pace", "settings.json");

    public LocalState Load()
    {
        try
        {
            if (File.Exists(PathToState))
            {
                using var file = File.OpenRead(PathToState);
                if (file.Length <= 8 * 1024 * 1024 && JsonSerializer.Deserialize<LocalState>(file) is { } state)
                {
                    var preferences = state.Preferences ?? new Preferences();
                    preferences = preferences with
                    {
                        Schedule = preferences.Schedule is { IsValid: true } ? preferences.Schedule : WorkSchedule.Standard,
                        Workdays = preferences.Workdays?.Where(day => Enum.IsDefined(day)).Distinct().ToArray()
                            ?? Preferences.StandardWorkdays.ToArray(),
                    };
                    return state with
                    {
                        Preferences = preferences,
                        History = (state.History ?? []).Where(sample => sample is not null)
                            .OrderBy(sample => sample.ObservedAt).TakeLast(2400).ToArray(),
                        SentWarnings = state.SentWarnings ?? new()
                    };
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Prefer an operable dashboard when preferences are missing or unreadable.
        }
        return new LocalState();
    }

    public bool Save(LocalState state)
    {
        var temporary = PathToState + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathToState)!);
            using (var file = File.Create(temporary)) JsonSerializer.Serialize(file, state);
            File.Move(temporary, PathToState, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
