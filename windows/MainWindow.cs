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
    private readonly SolidColorBrush onPace = new();
    private readonly SolidColorBrush caution = new();
    private readonly SolidColorBrush risk = new();
    private readonly ThemeSettings appearance;
    private readonly UISettings systemColors = new();
    private readonly Button refresh = new() { Content = "Refresh" };
    private readonly ToggleButton used = new() { Content = "Used" };
    private readonly ToggleButton left = new() { Content = "Left" };
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
    private readonly TextBlock planCaption;
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
    private bool dialogOpen;
    private bool refreshing;

    public event Action? RefreshRequested;
    public event Action<Preferences>? PreferencesChanged;
    public event Action? TestNotificationRequested;

    public MainWindow(Preferences preferences)
    {
        this.preferences = preferences;
        Title = "Codex Pace";
        AppWindow.Resize(new SizeInt32(1000, 820));
        appearance = ThemeSettings.CreateForWindowId(AppWindow.Id);
        root.Background = surface;

        timestamp = Text(secondary: true);
        heroTitle = Text(size: 20, strong: true);
        heroSubtitle = Text(secondary: true);
        error = Text(secondary: true);
        activity = Text(secondary: true);
        source = Text(secondary: true);
        planCaption = Text(secondary: true);
        planEmpty = Text(secondary: true);
        suggestionLabel = Text(size: 12, secondary: true);
        suggestionText = Text(strong: true);
        shortQuota = Text(secondary: true);
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
        suggestion = Stack(7, Rule(), Horizontal(8, suggestionLabel, suggestionText));

        used.Click += (_, _) => SetQuotaMode(true);
        left.Click += (_, _) => SetQuotaMode(false);
        sessionMode.Click += (_, _) => SetHeroMode(false);
        weeklyMode.Click += (_, _) => SetHeroMode(true);
        refresh.Click += (_, _) => RefreshRequested?.Invoke();

        var settings = new Button { Content = "Schedule" };
        var menu = new MenuFlyout();
        AddMenuItem(menu, "Edit sessions…", EditSessionsAsync);
        AddMenuItem(menu, "Work days…", EditWorkdaysAsync);
        menu.Items.Add(new MenuFlyoutSeparator());
        var test = new MenuFlyoutItem { Text = "Send test notification" };
        test.Click += (_, _) => TestNotificationRequested?.Invoke();
        menu.Items.Add(test);
        settings.Flyout = menu;

        var title = Text("Codex Pace", 29, strong: true);
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.HeadingLevel1);
        AutomationProperties.SetHeadingLevel(heroTitle, AutomationHeadingLevel.HeadingLevel2);
        ToolTipService.SetToolTip(refresh, "Check for the latest Codex usage");

        var controls = Horizontal(8, ModeSelector("Quota view", used, left), settings, refresh);
        var header = Pair(Stack(3, title, timestamp), controls, 568);
        var heroHeader = Pair(Stack(3, heroTitle, heroSubtitle), ModeSelector("Focus", sessionMode, weeklyMode), 392);
        var mainCard = Card(Stack(15, heroHeader, heroContent, shortQuota, suggestion), hero, 22);
        var planTitle = Text("Work sessions", 16, strong: true);
        AutomationProperties.SetHeadingLevel(planTitle, AutomationHeadingLevel.HeadingLevel2);
        var planCard = Card(Stack(10, Pair(planTitle, planCaption, 330), planEmpty, planScroller), panel, 17);
        var content = new Border
        {
            Child = Stack(14, header, mainCard, error, planCard, activity, source),
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
        used.IsChecked = preferences.ShowUsed;
        left.IsChecked = !preferences.ShowUsed;
        Set(timestamp, data.Quota is { } quota ? $"Updated {Stamp(quota.ObservedAt)}" : "Weekly usage tracker");
        Set(error, data.Error);
        Set(activity, string.IsNullOrWhiteSpace(data.RecentActivity) ? null : $"Recent Codex activity · {data.RecentActivity}");
        Set(source, data.Source);
        RenderHero();
        RenderPlan(data, now);
    }

    private void SetQuotaMode(bool showUsed)
    {
        used.IsChecked = showUsed;
        left.IsChecked = !showUsed;
        if (preferences.ShowUsed != showUsed)
            PreferencesChanged?.Invoke(preferences with { ShowUsed = showUsed });
    }

    private void SetHeroMode(bool weekly)
    {
        bool changed = showWeekly != weekly;
        showWeekly = weekly;
        sessionMode.IsChecked = !weekly;
        weeklyMode.IsChecked = weekly;
        if (changed) RenderHero();
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
        bool showAdvice = state.Warning is not null || state.Quota?.UsedPercent >= 100;
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
        Set(heroSubtitle, Range(session));
        var readout = Stack(7);
        string kind = preferences.ShowUsed ? "used" : "left";
        if (QuotaDisplay.SessionUsage(pace) is { } usage)
        {
            double? target = pace.SessionTarget / pace.SessionBudget * 100;
            string prefix = pace.SessionStartInferred ? "Estimated " : "";
            readout.Children.Add(Readings($"{prefix}Session budget {kind}", Oriented(usage.Fraction * 100), target));
            readout.Children.Add(Progress($"Session budget {kind}", usage.Fraction * 100, target));
        }
        else
        {
            readout.Children.Add(Text(pace.SessionBudget == 0 ? "No session allocation" : "Usage unavailable", 24, strong: true));
        }
        if (pace.SessionRoomToTarget is { } room)
        {
            var label = room < 0 ? $"{Percent(-room)} of weekly quota over the session target"
                : $"{Percent(room)} of weekly quota available this session";
            if (state.Quota is { } quota && IsStale(quota, now)) label += $" · Last read {Stamp(quota.ObservedAt)}";
            readout.Children.Add(Text(label, secondary: true));
        }

        var action = state.Guidance?.Action ?? SessionAction.AwaitingReadings;
        var details = Stack(7, Pair(Metric("Session guidance", Planner.ActionLabel(action)),
            Metric("Time left", Duration(session.End - now)), 208));
        string? guidance = action switch
        {
            SessionAction.CheckUsage => "Refresh to check your current room to target.",
            SessionAction.AwaitingReadings => "Another quota reading will help estimate your pace.",
            SessionAction.Pause when state.Quota?.UsedPercent >= 100 => "Your weekly quota is used up.",
            SessionAction.Pause => "The cumulative session target has been reached.",
            _ => null,
        };
        if (guidance is not null) details.Children.Add(Text(guidance, secondary: true));
        if (state.Guidance?.SafeSessionPercentPerHour is { } rate)
            details.Children.Add(Text($"Aim for about {Percent(rate)} of the session allowance per hour or less.", secondary: true));
        if (action != SessionAction.Pause && state.Guidance?.WeeklyRisk == true)
            details.Children.Add(Text("At this pace, your weekly quota could run out before reset.", secondary: true));
        if (pace.SessionBudget is null && action != SessionAction.CheckUsage)
            details.Children.Add(Text("Session percentages need a reading around the start of the shift.", secondary: true));
        heroContent.Children.Add(Pair(readout, details, 552, firstWeight: 1.55));
    }

    private void RenderWeekly(DashboardData state)
    {
        Set(heroTitle, "Weekly quota");
        Set(heroSubtitle, null);
        if (state.Quota is not { } quota)
        {
            heroContent.Children.Add(Text(refreshing ? "Checking quota…" : "Weekly quota unavailable", 22, strong: true));
            return;
        }

        var pace = state.Pace;
        double? target = pace is { Sessions.Count: > 0 } ? pace.TargetPercent : null;
        string kind = preferences.ShowUsed ? "used" : "left";
        var readout = Stack(7,
            Readings($"Quota {kind}", Oriented(quota.UsedPercent), target),
            Progress($"Weekly quota {kind}", quota.UsedPercent, target));
        string caption = preferences.ShowUsed ? $"{Percent(100 - quota.UsedPercent)} left" : $"{Percent(quota.UsedPercent)} used";
        if (pace?.ExhaustionDate is { } exhaustion) caption += $" · Projected to run out {Stamp(exhaustion)}";
        readout.Children.Add(Text(caption, secondary: true));

        var detail = Stack(8);
        if (pace is not null)
        {
            detail.Children.Add(Pair(Metric("Recent pace", Rate(pace.RecentRate)), Metric("Safe pace", Rate(pace.SafeRate)), 204));
            detail.Children.Add(Pair(Metric(pace.PlanEndsAt != quota.ResetsAt ? "Projected use by target" : "Projected weekly use",
                Percent(pace.ProjectedPercent)), Metric("Work time left", $"{pace.RemainingWorkHours:0.#} h"), 204));
            detail.Children.Add(Rule());
        }
        detail.Children.Add(Metric("Scheduled reset", Stamp(quota.ResetsAt)));
        var existing = preferences.ResetTarget is { } manual && manual.WindowResetsAt == quota.ResetsAt ? manual : null;
        if (existing is not null)
        {
            var note = existing.TargetAt <= renderedNow ? " · Target passed" : pace?.PlanEndsAt == existing.TargetAt ? " · Pacing uses this target" : "";
            detail.Children.Add(Text($"Manual reset target · {Stamp(existing.TargetAt)}{note}", secondary: true));
        }
        var reset = new Button
        {
            Content = existing is null ? "Set reset target…" : "Edit reset target…",
            IsEnabled = NextMinute(CurrentTime()) < quota.ResetsAt,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        reset.Click += async (_, _) => await RunDialogAsync(EditResetTargetAsync);
        detail.Children.Add(reset);
        heroContent.Children.Add(Pair(readout, detail, 552, firstWeight: 1.55));
    }

    private FrameworkElement Readings(string usageLabel, string usage, double? target)
    {
        var main = Stack(2, Text(usageLabel, 12, secondary: true), Text(usage, 39, strong: true));
        if (target is not { } value || !double.IsFinite(value)) return main;
        var planned = Stack(2,
            Text($"Target {(preferences.ShowUsed ? "used" : "left")} by now", 12, secondary: true),
            Text(Oriented(value), 27, strong: true));
        return Pair(main, planned, 224);
    }

    private FrameworkElement Progress(string label, double usedAmount, double? target)
    {
        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(preferences.ShowUsed ? usedAmount : 100 - usedAmount, 0, 100),
            Foreground = target is not { } planned ? muted : usedAmount <= planned ? onPace : usedAmount <= planned * 1.10 ? caution : risk,
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

    private void RenderPlan(DashboardData state, DateTimeOffset now)
    {
        var schedule = preferences.Schedule.IsValid ? preferences.Schedule : WorkSchedule.Standard;
        var templates = schedule.Entries.OrderBy(template => template.StartMinute).ToArray();
        var days = (state.Pace?.Sessions ?? []).GroupBy(session => WorkClock.Day(session.Start)).ToArray();
        Set(planCaption, days.Length > 0 ? $"{(preferences.ShowUsed ? "Used" : "Left")} at session end" : null);
        Set(planEmpty, days.Length > 0 ? null
            : preferences.Workdays.Length == 0 ? "No work days selected."
            : templates.Length == 0 ? "No sessions configured."
            : state.Pace is null ? "Work sessions appear with the weekly quota."
            : "The quota window contains no scheduled work hours.");
        planScroller.Visibility = days.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        planGrid.Children.Clear();
        planGrid.ColumnDefinitions.Clear();
        planGrid.RowDefinitions.Clear();
        if (days.Length == 0) return;
        planGrid.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var template in templates) planGrid.ColumnDefinitions.Add(new ColumnDefinition());
        SizePlanColumns();

        planGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddPlanCell(0, 0, "Workday", heading: true);
        for (int index = 0; index < templates.Length; index++)
        {
            var template = templates[index];
            AddPlanCell(0, index + 1, $"{template.Name}\n{Time(template.StartMinute)}–{Time(template.EndMinute)}", heading: true);
        }
        for (int index = 0; index < days.Length; index++)
        {
            var day = days[index];
            var sessions = day.ToDictionary(session => session.Template.Id);
            int row = index + 1;
            planGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddPlanCell(row, 0, Day(day.Key), heading: true);
            for (int column = 0; column < templates.Length; column++)
            {
                if (!sessions.TryGetValue(templates[column].Id, out var session))
                {
                    AddPlanCell(row, column + 1, "—");
                    continue;
                }
                bool active = state.Pace?.CurrentSession == session && session.Start <= now && now < session.End;
                AddPlanCell(row, column + 1, PlanCell(session, active, state.Quota, now), active: active);
            }
        }
    }

    private string PlanCell(WorkSession session, bool active, QuotaSample? quota, DateTimeOffset now)
    {
        var target = Oriented(session.CumulativeTargetPercent);
        var lines = new List<string> { Range(session) };
        if (active && quota is not null && quota.ObservedAt <= now && now < quota.ResetsAt)
        {
            var kind = preferences.ShowUsed ? "used" : "left";
            lines.Add($"Target {kind} {target} · Actual {kind} {Oriented(quota.UsedPercent)}");
            var room = Math.Round(session.CumulativeTargetPercent - quota.UsedPercent, 1);
            lines.Add(IsStale(quota, now) ? $"Last reading {Stamp(quota.ObservedAt)}"
                : room > 0 ? $"{Percent(room)} room to target"
                : room < 0 ? $"{Percent(-room)} over target" : "At target");
        }
        else lines.Add(target);
        lines.Add($"{Percent(session.TargetPercent)} session share{(active ? " · Now" : "")}");
        return string.Join(Environment.NewLine, lines);
    }

    private void AddPlanCell(int row, int column, string value, bool heading = false, bool active = false)
    {
        var text = Text(value, 13, strong: heading);
        var cell = new Border
        {
            Child = text,
            Background = active ? selection : panel,
            Padding = new Thickness(10, 8, 10, 8),
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
        const double firstWidth = 122;
        double otherWidth = Math.Max(155, (planScroller.ActualWidth - firstWidth - sessionCount) / sessionCount);
        planGrid.ColumnDefinitions[0].Width = new GridLength(firstWidth);
        for (int index = 1; index < planGrid.ColumnDefinitions.Count; index++)
            planGrid.ColumnDefinitions[index].Width = new GridLength(otherWidth);
    }

    private async Task EditSessionsAsync(XamlRoot xamlRoot)
    {
        var schedule = await SettingsDialogs.EditSessionsAsync(xamlRoot, preferences.Schedule);
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
            surface.Color = panel.Color = hero.Color = background;
            ink.Color = muted.Color = line.Color = foreground;
            selection.Color = background;
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
        SetColor(onPace, 0x287653, 0x8ac99e, dark);
        SetColor(caution, 0x946217, 0xe2ba73, dark);
        SetColor(risk, 0xa93846, 0xefa5a9, dark);
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

    private StackPanel Metric(string label, string value) => Stack(2, Text(label, 12, secondary: true), Text(value, strong: true));

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
    private static string Rate(double? value) => value.HasValue ? $"{Percent(value)}/h" : "—";
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
