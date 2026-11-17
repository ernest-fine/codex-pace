using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace CodexPace;

internal sealed class PaceApplication
{
    private readonly MainWindow window;
    private readonly TrayIcon tray;
    private readonly DispatcherQueueTimer timer;
    private readonly Action exit;
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

    public PaceApplication(bool demo, Action exit)
    {
        this.demo = demo;
        this.exit = exit;
        state = demo ? new LocalState() : store.Load();
        window = new MainWindow(state.Preferences);
        var dispatcher = window.DispatcherQueue;
        tray = new TrayIcon(WindowNative.GetWindowHandle(window), dispatcher);
        tray.OpenRequested += OpenDashboard;
        tray.RefreshRequested += () => _ = RefreshAsync(force: true);
        tray.TestNotificationRequested += TestNotification;
        tray.QuitRequested += () => Quit();
        window.RefreshRequested += () => _ = RefreshAsync(force: true);
        window.PreferencesChanged += ChangePreferences;
        window.TestNotificationRequested += TestNotification;
        window.AppWindow.Closing += (_, args) =>
        {
            if (!closing && tray.IsRegistered)
            {
                args.Cancel = true;
                window.AppWindow.Hide();
            }
        };
        window.Closed += (_, _) => Quit(windowAlreadyClosed: true);
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMinutes(2);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => _ = RefreshAsync(force: false);
        Render();
        window.Activate();
        timer.Start();
        _ = RefreshAsync(force: false);
    }

    public void OpenDashboard()
    {
        if (closing) return;
        if (!window.AppWindow.IsVisible) window.AppWindow.Show();
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
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
        latestObserved = current.Source == "Codex account" ? current.Quota : null;
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
        tray.SetStatus(Planner.TrayCaption(data, now, state.Preferences.ShowUsed), refreshing);
    }

    private void ConsiderWarning(DateTimeOffset now)
    {
        if (demo || latestObserved is null) return;
        var warning = Planner.ImmediateWarnings(latestObserved, CurrentData(now).Pace, now)
            .FirstOrDefault(candidate => candidate.ExpiresAt > now
                && !state.SentWarnings.ContainsKey(candidate.Key));
        if (warning is null) return;

        if (!ShowWarning(warning, test: false)) return;
        var active = state.SentWarnings.Where(entry => entry.Value > now)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        active[warning.Key] = now.AddDays(8);
        state = state with { SentWarnings = active };
    }

    private void TestNotification()
    {
        var example = Example();
        var result = Planner.Calculate(example, example.History, new Preferences(), DemoTime);
        var warning = Planner.ImmediateWarnings(example.Quota, result.Pace, DemoTime).FirstOrDefault();
        if (warning is not null) ShowWarning(warning, test: true);
    }

    private bool ShowWarning(BudgetWarning warning, bool test)
    {
        var title = test ? "Test · " + warning.Title : warning.Title;
        return tray.ShowWarning(title, warning.Recommendation + "\n" + warning.Detail);
    }

    private void Persist()
    {
        if (!demo) storageError = store.Save(state) ? null : "Couldn’t save local preferences or quota history.";
    }

    private static Telemetry Example()
    {
        var reset = WorkClock.At(new DateOnly(2026, 11, 23), 1275);
        var offsets = new[] { -4.5, -3.5, -2.5, -1, 0 };
        var amounts = new double[] { 35, 36, 38, 38, 39.5 };
        var history = offsets.Zip(amounts, (hours, used) =>
            new QuotaSample(DemoTime.AddHours(hours), reset, 10_080, used)).ToArray();
        ModelOption[] models =
        [
            new("gpt-6.1-sol", "GPT-6.1 Sol", new[] { "low", "medium", "high", "xhigh", "max", "ultra" }, "medium", true),
            new("gpt-6-luna", "GPT-6 Luna", new[] { "low", "medium", "high", "max" }, "high", false)
        ];
        return new Telemetry(history[^1], null, history,
            new Activity(DemoTime, "gpt-6.1-sol", "high", 4, 180_000), models, "Example data", null);
    }

    private void Quit(bool windowAlreadyClosed = false)
    {
        if (closing) return;
        closing = true;
        timer.Stop();
        shutdown.Cancel();
        tray.Dispose();
        if (!windowAlreadyClosed) window.Close();
        exit();
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
