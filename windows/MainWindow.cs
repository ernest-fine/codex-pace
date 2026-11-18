using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace CodexPace;

internal sealed class MainWindow : Window
{
    private readonly Grid root = new();
    private readonly SolidColorBrush surface = new();
    private readonly SolidColorBrush panel = new();
    private readonly SolidColorBrush hero = new();
    private readonly SolidColorBrush ink = new();
    private readonly SolidColorBrush muted = new();
    private readonly SolidColorBrush line = new();
    private readonly SolidColorBrush selection = new();
    private readonly SolidColorBrush inset = new();
    private readonly SolidColorBrush onPace = new();
    private readonly SolidColorBrush caution = new();
    private readonly SolidColorBrush risk = new();
    private readonly SolidColorBrush healthyBadge = new();
    private readonly SolidColorBrush cautionBadge = new();
    private readonly SolidColorBrush riskBadge = new();
    private readonly ThemeSettings appearance;
    private readonly UISettings systemColors = new();
    private readonly Button refresh = new() { Content = "Refresh" };
    private readonly ToggleButton used = new() { Content = "Used" };
    private readonly ToggleButton left = new() { Content = "Left" };
    private readonly ToggleButton planUsed = new() { Content = "Used" };
    private readonly ToggleButton planLeft = new() { Content = "Left" };
    private readonly ToggleButton sessionMode = new() { Content = "Session" };
    private readonly ToggleButton weeklyMode = new() { Content = "Weekly" };
    private readonly Grid planGrid = new() { ColumnSpacing = 1, RowSpacing = 1 };
    private readonly ScrollViewer planScroller;
    private readonly StackPanel heroContent = new() { Spacing = 12 };
    private readonly TextBlock timestamp;
    private readonly TextBlock heroTitle;
    private readonly TextBlock heroSubtitle;
    private readonly TextBlock error;
    private readonly TextBlock activity;
    private readonly TextBlock source;
    private readonly TextBlock planEmpty;
    private readonly TextBlock suggestionLabel;
    private readonly TextBlock suggestionText;
    private readonly StackPanel suggestion;
    private readonly TextBlock shortQuota;

    private Preferences preferences;
    private DashboardData? data;
    private DateTimeOffset renderedNow = DateTimeOffset.UtcNow;
    private long renderedAt = Stopwatch.GetTimestamp();
    private bool showWeekly;
    private bool weeklyDetailsExpanded;
    private bool dialogOpen;
    private bool refreshing;

    public event Action? RefreshRequested;
    public event Action<Preferences>? PreferencesChanged;
    public event Action? TestNotificationRequested;
    public event Action? QuotaFocusChanged;

    public bool ShowsWeeklyQuota => showWeekly;

    public MainWindow(Preferences preferences)
    {
        this.preferences = preferences;
        Title = "Codex Pace";
        AppWindow.Resize(new SizeInt32(1000, 820));
        appearance = ThemeSettings.CreateForWindowId(AppWindow.Id);
        root.Background = surface;

        timestamp = Text(size: 15, secondary: true);
        heroTitle = Text(size: 24, strong: true);
        heroSubtitle = Text(size: 16, secondary: true);
        error = Text(secondary: true);
        activity = Text(size: 15, secondary: true);
        source = Text(size: 15, secondary: true);
        var footer = Pair(Stack(3, Text("Recent Codex activity", strong: true), activity),
            Stack(3, Text("Quota source", strong: true), source), 450, firstWeight: 1.65);
        planEmpty = Text(secondary: true);
        suggestionLabel = Text(size: 16, secondary: true);
        suggestionText = Text(size: 18, strong: true);
        shortQuota = Text(size: 16, secondary: true);
        planScroller = new ScrollViewer
        {
            Content = planGrid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        planScroller.SizeChanged += (_, _) => SizePlanColumns();
        AutomationProperties.SetName(planScroller, "Work sessions table");
        var nextTurn = new Grid { ColumnSpacing = 12 };
        nextTurn.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nextTurn.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nextTurn.Children.Add(suggestionLabel);
        Grid.SetColumn(suggestionText, 1);
        nextTurn.Children.Add(suggestionText);
        suggestion = Stack(10, Rule(), nextTurn);

        used.Click += (_, _) => SetQuotaMode(true);
        left.Click += (_, _) => SetQuotaMode(false);
        planUsed.Click += (_, _) => SetQuotaMode(true);
        planLeft.Click += (_, _) => SetQuotaMode(false);
        sessionMode.Click += (_, _) => SetHeroMode(false);
        weeklyMode.Click += (_, _) => SetHeroMode(true);
        refresh.Click += (_, _) => RefreshRequested?.Invoke();

        var settings = new Button { Content = "Schedule" };
        var workdays = new Button { Content = "Work days" };
        workdays.Click += async (_, _) => await RunDialogAsync(EditWorkdaysAsync);
        var menu = new MenuFlyout();
        AddMenuItem(menu, "Edit sessions…", EditSessionsAsync);
        menu.Items.Add(new MenuFlyoutSeparator());
        var test = new MenuFlyoutItem { Text = "Send test notification" };
        test.Click += (_, _) => TestNotificationRequested?.Invoke();
        menu.Items.Add(test);
        settings.Flyout = menu;

        var title = Text("Codex Pace", 27, strong: true);
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        AutomationProperties.SetHeadingLevel(heroTitle, AutomationHeadingLevel.Level2);
        ToolTipService.SetToolTip(refresh, "Check for the latest Codex usage");

        var controls = Horizontal(8, settings, workdays, refresh);
        var header = Pair(Stack(3, title, timestamp), controls, 490);
        var heroControls = Horizontal(8, ModeSelector("Focus", sessionMode, weeklyMode), ModeSelector("Quota view", used, left));
        var heroHeader = Pair(Stack(3, heroTitle, heroSubtitle), heroControls, 500);
        var mainCard = Card(Stack(15, heroHeader, heroContent, shortQuota, suggestion), hero, 22);
        var planTitle = Text("Work sessions", 19, strong: true);
        AutomationProperties.SetHeadingLevel(planTitle, AutomationHeadingLevel.Level2);
        var planCard = Card(Stack(12, Pair(planTitle, ModeSelector("Work sessions quota view", planUsed, planLeft), 320),
            planEmpty, planScroller), panel, 17);
        var content = new Border
        {
            Child = Stack(14, header, mainCard, error, planCard, Card(footer, panel, 16)),
            Padding = new Thickness(20),
            MaxWidth = 1064,
        };
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        root.ActualThemeChanged += (_, _) => ApplyAppearance();
        appearance.Changed += ContrastChanged;
        Closed += (_, _) => appearance.Changed -= ContrastChanged;
        ApplyAppearance();
        Render(new DashboardData(null, null, null, null, "", "", "", "", "", null, null),
            preferences, renderedNow, false);
    }

    public void Render(DashboardData data, Preferences preferences, DateTimeOffset now, bool refreshing)
    {
        this.data = data;
        this.preferences = preferences;
        this.refreshing = refreshing;
        renderedNow = now;
        renderedAt = Stopwatch.GetTimestamp();
        refresh.Content = refreshing ? "Refreshing…" : "Refresh";
        refresh.IsEnabled = !refreshing;
        SyncQuotaMode(preferences.ShowUsed);
        Set(timestamp, data.Quota is { } quota
            ? $"Updated {(WorkClock.Day(quota.ObservedAt) == WorkClock.Day(now) ? "today" : Day(WorkClock.Day(quota.ObservedAt)))} at {Time(quota.ObservedAt)}"
            : "Weekly usage tracker");
        Set(error, data.Error);
        Set(activity, data.RecentActivity);
        Set(source, data.Source);
        RenderHero();
        RenderPlan(data, now);
    }

    private void SetQuotaMode(bool showUsed)
    {
        SyncQuotaMode(showUsed);
        if (preferences.ShowUsed != showUsed)
            PreferencesChanged?.Invoke(preferences with { ShowUsed = showUsed });
    }

    private void SyncQuotaMode(bool showUsed)
    {
        used.IsChecked = planUsed.IsChecked = showUsed;
        left.IsChecked = planLeft.IsChecked = !showUsed;
    }

    private void SetHeroMode(bool weekly)
    {
        bool changed = showWeekly != weekly;
        showWeekly = weekly;
        sessionMode.IsChecked = !weekly;
        weeklyMode.IsChecked = weekly;
        if (changed)
        {
            RenderHero();
            QuotaFocusChanged?.Invoke();
        }
    }

    private void RenderHero()
    {
        if (data is not { } state) return;
        sessionMode.IsChecked = !showWeekly;
        weeklyMode.IsChecked = showWeekly;
        heroContent.Children.Clear();
        if (showWeekly) RenderWeekly(state);
        else RenderSession(state);

        Set(shortQuota, state.ShortQuota is { } shortWindow
            ? $"{Duration(TimeSpan.FromMinutes(shortWindow.WindowMinutes))} limit · {Percent(shortWindow.UsedPercent)} used · Resets {Stamp(shortWindow.ResetsAt)}"
            : null);
        bool showAdvice = state.Quota?.UsedPercent >= 100 || state.Warning is { } warning &&
            (!showWeekly || warning.Key.StartsWith("weekly.", StringComparison.Ordinal));
        suggestion.Visibility = showAdvice ? Visibility.Visible : Visibility.Collapsed;
        Set(suggestionLabel, state.Warning is null ? state.Status : "Next turn");
        Set(suggestionText, state.Recommendation);
        AutomationProperties.SetHelpText(suggestionText, state.RecommendationDetail);
        ToolTipService.SetToolTip(suggestionText, string.IsNullOrWhiteSpace(state.RecommendationDetail) ? null : state.RecommendationDetail);
    }

    private void RenderSession(DashboardData state)
    {
        var now = renderedNow;
        if (state.Pace is not { } pace || pace.CurrentSession is not { } session || now < session.Start || now >= session.End)
        {
            Set(heroTitle, "Current session");
            Set(heroSubtitle, null);
            if (state.Pace is null)
            {
                heroContent.Children.Add(Text(refreshing ? "Checking quota…" : "Checking quota", 22, strong: true));
                return;
            }

            var next = state.Pace.Sessions.FirstOrDefault(item => item.Start > now);
            bool between = next is not null && WorkClock.Day(next.Start) == WorkClock.Day(now) &&
                state.Pace.Sessions.Any(item => item.End <= now && WorkClock.Day(item.End) == WorkClock.Day(now));
            heroContent.Children.Add(Text(between ? "Between sessions" : "Outside work hours", 22, strong: true));
            if (next is not null) heroContent.Children.Add(Text($"Next {next.Template.Name} · {Day(WorkClock.Day(next.Start))} · {Range(next)}", secondary: true));
            return;
        }

        Set(heroTitle, session.Template.Name);
        Set(heroSubtitle, $"Ends at {Time(session.End)} · {Duration(session.End - now)} left");
        var readout = Stack(12);
        string kind = preferences.ShowUsed ? "used" : "left";
        if (QuotaDisplay.SessionUsage(pace) is { } usage)
        {
            double? target = pace.SessionTarget / pace.SessionBudget * 100;
            string prefix = pace.SessionStartInferred ? "Estimated " : "";
            readout.Children.Add(Readings($"{prefix}Session quota {kind}", Oriented(usage.Fraction * 100), target,
                state.SessionDelta?.Caption(preferences.ShowUsed)));
            readout.Children.Add(Progress($"Session quota {kind}", usage.Fraction * 100, target));
        }
        else
        {
            readout.Children.Add(Text(pace.SessionBudget == 0 ? "No session allocation" : "Usage unavailable", 24, strong: true));
        }
        if (pace.SessionRoomToTarget is { } room)
        {
            var detail = room < 0 ? "over the session target" : "available this session";
            if (state.Quota is { } quota && IsStale(quota, now)) detail += $" · Last read {Stamp(quota.ObservedAt)}";
            readout.Children.Add(QuotaNote($"{Percent(Math.Abs(room))} of weekly quota", detail));
        }

        var action = state.Guidance?.Action ?? SessionAction.AwaitingReadings;
        var details = Stack(7);
        string? guidance = action switch
        {
            SessionAction.CheckUsage => "Refresh to see how much quota is available.",
            SessionAction.AwaitingReadings => "Another quota reading will help estimate your pace.",
            SessionAction.Pause when state.Quota?.UsedPercent >= 100 => "Your weekly quota is used up.",
            SessionAction.Pause => "You’ve reached your session target.",
            _ => null,
        };
        if (guidance is not null) details.Children.Add(Text(guidance, 16));
        if (state.Guidance?.SafeSessionPercentPerHour is { } rate)
            details.Children.Add(Text($"Aim for {Percent(rate)} of your session quota per hour or less.", 16));
        if (action != SessionAction.Pause && state.Guidance?.WeeklyRisk == true)
            details.Children.Add(Text("At this pace, your weekly quota could run out before reset.", 16));
        if (pace.SessionBudget is null && action != SessionAction.CheckUsage)
            details.Children.Add(Text("Session percentages need a reading near the start of the session.", 16));
        heroContent.Children.Add(Pair(readout, GuidancePanel(Planner.ActionLabel(action), details,
            action switch
            {
                SessionAction.SlowDown => cautionBadge,
                SessionAction.Pause => riskBadge,
                SessionAction.KeepPace or SessionAction.RoomToSpeedUp => healthyBadge,
                _ => selection,
            }), 600, firstWeight: 1.32));
    }

    private void RenderWeekly(DashboardData state)
    {
        Set(heroTitle, "Weekly quota");
        if (state.Quota is not { } quota)
        {
            Set(heroSubtitle, null);
            heroContent.Children.Add(Text(refreshing ? "Checking quota…" : "Weekly quota unavailable", 22, strong: true));
            return;
        }

        var pace = state.Pace;
        var existing = preferences.ResetTarget is { } manual && manual.WindowResetsAt == quota.ResetsAt ? manual : null;
        Set(heroSubtitle, existing is not null && pace?.PlanEndsAt == existing.TargetAt
            ? $"Reset target {Stamp(existing.TargetAt)}"
            : $"Resets {Day(WorkClock.Day(quota.ResetsAt))} at {Time(quota.ResetsAt)}");
        double? target = pace is { Sessions.Count: > 0 } ? pace.TargetPercent : null;
        string kind = preferences.ShowUsed ? "used" : "left";
        var (badge, message, badgeColor) = WeeklyAdvice(quota, pace);
        var readout = Stack(12,
            Readings($"Weekly quota {kind}", Oriented(quota.UsedPercent), target),
            Progress($"Weekly quota {kind}", quota.UsedPercent, target, badgeColor == healthyBadge ? onPace : null),
            QuotaNote($"{Percent(preferences.ShowUsed ? 100 - quota.UsedPercent : quota.UsedPercent)} of weekly quota",
                preferences.ShowUsed ? "remaining" : "used so far"));
        heroContent.Children.Add(Pair(readout, GuidancePanel(badge, Text(message, 16), badgeColor),
            600, firstWeight: 1.32));

        var detail = Stack(10);
        if (pace is not null)
        {
            detail.Children.Add(Pair(Metric("Recent pace", Rate(pace.RecentRate)), Metric("Safe pace", Rate(pace.SafeRate)), 204));
            detail.Children.Add(Pair(Metric(pace.PlanEndsAt != quota.ResetsAt ? "Projected use by target" : "Projected weekly use",
                Percent(pace.ProjectedPercent)), Metric("Work time left", $"{pace.RemainingWorkHours:0.#} h"), 204));
            detail.Children.Add(Rule());
        }
        var reset = new Button
        {
            Content = existing is null ? "Set reset target…" : "Edit reset target…",
            IsEnabled = NextMinute(CurrentTime()) < quota.ResetsAt,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        reset.Click += async (_, _) => await RunDialogAsync(EditResetTargetAsync);
        detail.Children.Add(Pair(Metric("Scheduled reset", Stamp(quota.ResetsAt)), reset, 310));
        if (existing is not null)
        {
            var note = existing.TargetAt <= renderedNow ? " · Target passed" : pace?.PlanEndsAt == existing.TargetAt ? " · Pacing uses this target" : "";
            detail.Children.Add(Text($"Manual reset target · {Stamp(existing.TargetAt)}{note}", 16, secondary: true));
        }

        var forecast = new Expander
        {
            Header = Text("Weekly pacing details", 16, strong: true),
            Content = detail,
            IsExpanded = weeklyDetailsExpanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(forecast, "Weekly pacing details");
        forecast.Expanding += (_, _) => weeklyDetailsExpanded = true;
        forecast.Collapsed += (_, _) => weeklyDetailsExpanded = false;
        heroContent.Children.Add(forecast);
    }

    private (string Label, string Message, Brush Badge) WeeklyAdvice(QuotaSample quota, Pace? pace)
    {
        if (quota.ResetsAt <= renderedNow || quota.ObservedAt > renderedNow || IsStale(quota, renderedNow))
            return ("Check usage", "Refresh to see your current weekly pace.", selection);
        if (quota.UsedPercent >= 100)
            return ("Pause", "You’ve reached your weekly quota limit.", riskBadge);
        if (pace is null || pace.RemainingWorkHours == 0)
            return ("Off hours", "No work hours are scheduled before your next reset.", selection);
        if (pace.ProjectedPercent is not { } projection)
            return ("Checking", "Waiting for more usage readings during work hours.", selection);
        if (projection >= 100)
            return ("Slow down", "At this pace, you could reach your weekly limit before reset.", cautionBadge);
        return ("Keep this pace", "Your recent pace fits your remaining weekly quota.", healthyBadge);
    }

    private Border GuidancePanel(string title, FrameworkElement detail, Brush badgeColor)
    {
        var badge = new Border
        {
            Child = Text(title, 16, strong: true),
            Background = badgeColor,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        return new Border
        {
            Child = Stack(8, badge, detail),
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(12),
            Background = inset,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    private TextBlock QuotaNote(string amount, string detail)
    {
        var note = Text(size: 16, secondary: true);
        note.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = amount, FontWeight = FontWeights.SemiBold, Foreground = ink });
        note.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " " + detail });
        return note;
    }

    private FrameworkElement Readings(string usageLabel, string usage, double? target, string? change = null)
    {
        var main = Stack(2, Text(usageLabel, 16, secondary: true), Text(usage, 54, strong: true));
        if (change is not null) main.Children.Add(Text(change, 14, secondary: true));
        if (target is not { } value || !double.IsFinite(value)) return main;
        var label = Text($"Target {(preferences.ShowUsed ? "used" : "left")} by now", 16, secondary: true);
        var number = Text(Oriented(value), 36, strong: true);
        label.TextAlignment = number.TextAlignment = TextAlignment.Right;
        var planned = Stack(2, label, number);
        planned.VerticalAlignment = VerticalAlignment.Bottom;
        return Pair(main, planned, 224);
    }

    private FrameworkElement Progress(string label, double usedAmount, double? target, Brush? fill = null)
    {
        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(preferences.ShowUsed ? usedAmount : 100 - usedAmount, 0, 100),
            Foreground = fill ?? (target is not { } planned ? muted : usedAmount <= planned ? onPace : usedAmount <= planned * 1.10 ? caution : risk),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(progress, label);
        if (target is not { } value || !double.IsFinite(value)) return progress;
        AutomationProperties.SetHelpText(progress, $"Target by now is {Oriented(value)}.");
        var track = new Grid { Height = 13 };
        track.Children.Add(progress);
        double fraction = Math.Clamp((preferences.ShowUsed ? value : 100 - value) / 100, 0, 1);
        var marker = new Border { Background = ink, Width = 2, Height = 12, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        track.Children.Add(marker);
        track.SizeChanged += (_, args) => marker.Margin = new Thickness(Math.Max(0, (args.NewSize.Width - 2) * fraction), 0, 0, 0);
        return track;
    }

    private sealed record PlanDay(DateOnly Date, SessionTemplate[] Templates, Dictionary<Guid, WorkSession> Sessions);

    private void RenderPlan(DashboardData state, DateTimeOffset now)
    {
        var schedule = preferences.Schedule.IsValid ? preferences.Schedule : WorkSchedule.Standard;
        var days = (state.Pace?.Sessions ?? []).GroupBy(session => WorkClock.Day(session.Start))
            .Select(group => new PlanDay(group.Key,
                schedule.EntriesOn(group.Key.DayOfWeek).OrderBy(template => template.StartMinute).ToArray(),
                group.ToDictionary(session => session.Template.Id)))
            .ToArray();
        Set(planEmpty, days.Length > 0 ? null
            : preferences.Workdays.Length == 0 ? "No work days selected."
            : !preferences.Workdays.Any(day => schedule.EntriesOn(day).Count > 0) ? "No sessions configured."
            : state.Pace is null ? "Work sessions appear with the weekly quota."
            : "The quota window contains no scheduled work hours.");
        planScroller.Visibility = days.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        planGrid.Children.Clear();
        planGrid.ColumnDefinitions.Clear();
        planGrid.RowDefinitions.Clear();
        if (days.Length == 0) return;
        var columns = days.Max(day => day.Templates.Length);
        var templates = days[0].Templates;
        bool sharedTemplates = days.All(day => day.Templates.SequenceEqual(templates));
        planGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int index = 0; index < columns; index++) planGrid.ColumnDefinitions.Add(new ColumnDefinition());
        SizePlanColumns();

        planGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddPlanCell(0, 0, Text("Workday", 16, strong: true));
        for (int index = 0; index < columns; index++)
        {
            if (sharedTemplates)
            {
                var template = templates[index];
                AddPlanCell(0, index + 1, Stack(2, Text(template.Name, 16, strong: true),
                    Text($"{Time(template.StartMinute)}–{Time(template.EndMinute)}", 14, secondary: true)));
            }
            else AddPlanCell(0, index + 1, Text($"Session {index + 1}", 16, strong: true));
        }
        for (int index = 0; index < days.Length; index++)
        {
            var day = days[index];
            int row = index + 1;
            planGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddPlanCell(row, 0, Stack(2, Text(day.Date.ToString("ddd", CultureInfo.CurrentCulture), 16, strong: true),
                Text(day.Date.ToString("d MMM", CultureInfo.CurrentCulture), 14, secondary: true)));
            for (int column = 0; column < columns; column++)
            {
                bool hasTemplate = column < day.Templates.Length;
                if (!hasTemplate || !day.Sessions.TryGetValue(day.Templates[column].Id, out var session))
                {
                    var missing = Text("—", 16, secondary: true);
                    AutomationProperties.SetName(missing, hasTemplate ? "Outside this quota window" : "No session scheduled");
                    AddPlanCell(row, column + 1, missing);
                    continue;
                }
                bool active = state.Pace?.CurrentSession == session && session.Start <= now && now < session.End;
                AddPlanCell(row, column + 1, PlanCell(session, !sharedTemplates, active, state.Quota, now), active: active);
            }
        }
    }

    private FrameworkElement PlanCell(WorkSession session, bool showSessionName, bool active, QuotaSample? quota, DateTimeOffset now)
    {
        var target = Oriented(session.CumulativeTargetPercent);
        var kind = preferences.ShowUsed ? "used" : "left";
        var targetValue = PlanReading($"Target {kind}", target);
        var details = Stack(5);
        if (showSessionName)
        {
            details.Children.Add(Text(session.Template.Name, 16, strong: true));
            details.Children.Add(Text(Range(session), 14, secondary: true));
        }
        if (active && quota is not null && quota.ObservedAt <= now && now < quota.ResetsAt)
        {
            details.Children.Add(Pair(targetValue, PlanReading($"Actual {kind}", Oriented(quota.UsedPercent)), 174));
            var room = Math.Round(session.CumulativeTargetPercent - quota.UsedPercent, 1);
            var stale = IsStale(quota, now);
            var note = Text(stale ? $"Last reading {Stamp(quota.ObservedAt)} · Now"
                : room > 0 ? $"{Percent(room)} available · Now"
                : room < 0 ? $"{Percent(-room)} over target · Now" : "At target · Now", 14);
            note.Foreground = stale ? muted : room < 0 ? risk : onPace;
            details.Children.Add(note);
        }
        else
        {
            details.Children.Add(targetValue);
            if (active) details.Children.Add(Text("Now", 14, secondary: true));
        }
        if (!showSessionName && Range(session) != $"{Time(session.Template.StartMinute)}–{Time(session.Template.EndMinute)}")
            details.Children.Add(Text(Range(session), 14, secondary: true));

        var share = $"{Percent(session.TargetPercent)} session share";
        AutomationProperties.SetHelpText(details, share);
        ToolTipService.SetToolTip(details, share);
        return details;
    }

    private StackPanel PlanReading(string label, string value) =>
        Stack(2, Text(label, 14, secondary: true), Text(value, 23, strong: true));

    private void AddPlanCell(int row, int column, FrameworkElement content, bool active = false)
    {
        var cell = new Border
        {
            Child = active ? new Border
            {
                Child = content,
                Background = selection,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
            } : content,
            Padding = active ? new Thickness(0, 4, 0, 4) : new Thickness(10, 14, 10, 14),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = line,
        };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        planGrid.Children.Add(cell);
    }

    private void SizePlanColumns()
    {
        if (planGrid.ColumnDefinitions.Count < 2) return;
        int sessionCount = planGrid.ColumnDefinitions.Count - 1;
        const double firstWidth = 112;
        double otherWidth = Math.Max(165, (planScroller.ActualWidth - firstWidth - sessionCount) / sessionCount);
        planGrid.ColumnDefinitions[0].Width = new GridLength(firstWidth);
        for (int index = 1; index < planGrid.ColumnDefinitions.Count; index++)
            planGrid.ColumnDefinitions[index].Width = new GridLength(otherWidth);
    }

    private async Task EditSessionsAsync(XamlRoot xamlRoot)
    {
        var schedule = await SettingsDialogs.EditSessionsAsync(xamlRoot, preferences.Schedule, preferences.Workdays, CurrentTime());
        if (schedule is not null) PreferencesChanged?.Invoke(preferences with { Schedule = schedule });
    }

    private async Task EditWorkdaysAsync(XamlRoot xamlRoot)
    {
        var workdays = await SettingsDialogs.EditWorkdaysAsync(xamlRoot, preferences.Workdays);
        if (workdays is not null) PreferencesChanged?.Invoke(preferences with { Workdays = workdays });
    }

    private async Task EditResetTargetAsync(XamlRoot xamlRoot)
    {
        if (data?.Quota is not { } quota || NextMinute(CurrentTime()) >= quota.ResetsAt) return;
        var existing = preferences.ResetTarget is { } target && target.WindowResetsAt == quota.ResetsAt
            ? target.TargetAt : (DateTimeOffset?)null;
        var result = await SettingsDialogs.EditResetTargetAsync(xamlRoot, quota.ResetsAt, existing, CurrentTime);
        if (!result.Applied) return;
        var currentTime = CurrentTime();
        string? validation = data?.Quota?.ResetsAt != quota.ResetsAt || currentTime >= quota.ResetsAt
            ? "The quota window changed. Open the reset target for the current window."
            : result.Target is { } saved && saved <= currentTime ? "Choose a reset target in the future." : null;
        if (validation is not null)
        {
            await new ContentDialog { XamlRoot = xamlRoot, Title = "Reset target", Content = validation, CloseButtonText = "OK" }.ShowAsync();
            return;
        }
        PreferencesChanged?.Invoke(preferences with
        {
            ResetTarget = result.Target is { } deadline ? new ManualResetTarget(deadline, quota.ResetsAt) : null,
        });
    }

    private void AddMenuItem(MenuFlyout menu, string label, Func<XamlRoot, Task> show)
    {
        var item = new MenuFlyoutItem { Text = label };
        item.Click += async (_, _) => await RunDialogAsync(show);
        menu.Items.Add(item);
    }

    private async Task RunDialogAsync(Func<XamlRoot, Task> show)
    {
        if (dialogOpen || root.XamlRoot is not { } xamlRoot) return;
        dialogOpen = true;
        try { await show(xamlRoot); }
        finally { dialogOpen = false; }
    }

    private void ContrastChanged(ThemeSettings sender, object args) => DispatcherQueue.TryEnqueue(ApplyAppearance);

    private void ApplyAppearance()
    {
        if (appearance.HighContrast)
        {
            Color background = systemColors.GetColorValue(UIColorType.Background);
            Color foreground = systemColors.GetColorValue(UIColorType.Foreground);
            surface.Color = panel.Color = hero.Color = inset.Color = background;
            ink.Color = muted.Color = line.Color = foreground;
            selection.Color = healthyBadge.Color = cautionBadge.Color = riskBadge.Color = background;
            onPace.Color = caution.Color = risk.Color = foreground;
            return;
        }
        bool dark = root.ActualTheme == ElementTheme.Dark;
        SetColor(surface, 0xeeefed, 0x151716, dark);
        SetColor(panel, 0xfafbf9, 0x1d201e, dark);
        SetColor(hero, 0xfdfefc, 0x222723, dark);
        SetColor(ink, 0x252826, 0xf3f4f1, dark);
        SetColor(muted, 0x5d645f, 0xbac2bd, dark);
        SetColor(line, 0xdce1dc, 0x3b443e, dark);
        SetColor(selection, 0xe6ebe6, 0x353e37, dark);
        SetColor(inset, 0xeaf0ea, 0x2d372f, dark);
        SetColor(onPace, 0x287653, 0x8ac99e, dark);
        SetColor(caution, 0x946217, 0xe2ba73, dark);
        SetColor(risk, 0xa93846, 0xefa5a9, dark);
        SetColor(healthyBadge, 0xdceee1, 0x294734, dark);
        SetColor(cautionBadge, 0xffedc9, 0x594320, dark);
        SetColor(riskBadge, 0xfbdce0, 0x642d37, dark);
    }

    private static void SetColor(SolidColorBrush brush, int light, int dark, bool darkMode)
    {
        int color = darkMode ? dark : light;
        brush.Color = Color.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color);
    }

    private TextBlock Text(string? value = null, double size = 14, bool strong = false, bool secondary = false) => new()
    {
        Text = value ?? string.Empty,
        FontSize = size,
        FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = secondary ? muted : ink,
        TextWrapping = TextWrapping.Wrap,
    };

    private static void Set(TextBlock text, string? value)
    {
        text.Text = value ?? string.Empty;
        text.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    private Border Card(UIElement body, Brush fill, double padding) => new()
    {
        Child = body,
        Padding = new Thickness(padding),
        Background = fill,
        BorderBrush = line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
    };

    private Border Rule() => new() { Height = 1, Background = line };

    private StackPanel Metric(string label, string value) => Stack(2, Text(label, 16, secondary: true), Text(value, 18, strong: true));

    private static StackPanel Stack(double spacing, params UIElement[] children)
    {
        var result = new StackPanel { Spacing = spacing };
        foreach (var child in children) result.Children.Add(child);
        return result;
    }

    private static StackPanel Horizontal(double spacing, params UIElement[] children)
    {
        var row = Stack(spacing, children);
        row.Orientation = Orientation.Horizontal;
        row.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }

    private static StackPanel ModeSelector(string label, params ToggleButton[] buttons)
    {
        var group = Horizontal(2, buttons);
        AutomationProperties.SetName(group, label);
        foreach (var button in buttons) AutomationProperties.SetName(button, $"{label}: {button.Content}");
        return group;
    }

    private static Grid Pair(FrameworkElement first, FrameworkElement second, double breakpoint, double firstWeight = 0)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        var firstColumn = new ColumnDefinition();
        var secondColumn = new ColumnDefinition();
        var firstRow = new RowDefinition { Height = GridLength.Auto };
        var secondRow = new RowDefinition { Height = GridLength.Auto };
        grid.ColumnDefinitions.Add(firstColumn);
        grid.ColumnDefinitions.Add(secondColumn);
        grid.RowDefinitions.Add(firstRow);
        grid.RowDefinitions.Add(secondRow);
        grid.Children.Add(first);
        grid.Children.Add(second);
        grid.SizeChanged += (_, args) => Layout(args.NewSize.Width);
        Layout(breakpoint);
        return grid;

        void Layout(double width)
        {
            bool wide = width >= breakpoint;
            grid.ColumnSpacing = wide ? 12 : 0;
            grid.RowSpacing = wide ? 0 : 8;
            firstColumn.Width = new GridLength(wide && firstWeight > 0 ? firstWeight : 1, GridUnitType.Star);
            secondColumn.Width = !wide ? new GridLength(0) : firstWeight > 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            Grid.SetColumn(second, wide ? 1 : 0);
            Grid.SetRow(second, wide ? 0 : 1);
        }
    }

    private DateTimeOffset CurrentTime() => renderedNow + Stopwatch.GetElapsedTime(renderedAt);
    private string Oriented(double usedAmount) => QuotaDisplay.OrientedPercent(usedAmount, preferences.ShowUsed);
    private static string Percent(double? value) => QuotaDisplay.Percent(value);
    private static string Rate(double? value) => value.HasValue ? $"{Percent(value)} per work hour" : "—";
    private static bool IsStale(QuotaSample quota, DateTimeOffset now) => now - quota.ObservedAt > TimeSpan.FromMinutes(45);
    private static string Day(DateOnly date) => date.ToString("ddd, d MMM", CultureInfo.CurrentCulture);
    private static string Stamp(DateTimeOffset instant) => WorkClock.Local(instant).ToString("ddd, d MMM HH:mm", CultureInfo.CurrentCulture);
    private static string Time(DateTimeOffset instant) => WorkClock.Local(instant).ToString("HH:mm", CultureInfo.CurrentCulture);
    private static string Time(int minute) => $"{minute / 60:00}:{minute % 60:00}";
    private static string Range(WorkSession session) => $"{Time(session.Start)}–{Time(session.End)}";
    private static DateTimeOffset NextMinute(DateTimeOffset instant) =>
        new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero).AddMinutes(1);

    private static string Duration(TimeSpan duration)
    {
        int minutes = (int)Math.Ceiling(Math.Max(0, duration.TotalMinutes));
        int hours = minutes / 60;
        int remainder = minutes % 60;
        if (hours == 0) return $"{remainder} min";
        return remainder == 0 ? $"{hours} h" : $"{hours} h {remainder} min";
    }
}
