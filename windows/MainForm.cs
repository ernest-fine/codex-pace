using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexPace;

internal static class Graphite
{
    private static bool dark;
    private static bool highContrast;
    private static bool initialized;
    private static readonly ConditionalWeakTable<Control, Action<Control>> styles = new();

    public static bool FollowSystemAppearance()
    {
#pragma warning disable WFO5001
        var contrast = SystemInformation.HighContrast;
        var nextDark = !contrast && Application.SystemColorMode == SystemColorMode.Dark;
        if (initialized && nextDark == dark && contrast == highContrast) return false;
        initialized = true;
        dark = nextDark;
        highContrast = contrast;
        Application.SetColorMode(nextDark ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001
        return true;
    }

    public static T Style<T>(T control, Action<T> apply) where T : Control
    {
        styles.Add(control, item => apply((T)item));
        apply(control);
        return control;
    }

    public static void Apply(Control control)
    {
        if (styles.TryGetValue(control, out var apply)) apply(control);
        if (control is Form { IsHandleCreated: true } form)
        {
            int enabled = dark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int));
        }
        foreach (Control child in control.Controls) Apply(child);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);

    public static Color Surface => Choose(0xeeefed, 0x151716, SystemColors.Control);
    public static Color Panel => Choose(0xfafbf9, 0x1d201e, SystemColors.Window);
    public static Color Ink => Choose(0x252826, 0xf3f4f1, SystemColors.WindowText);
    public static Color Muted => Choose(0x5d645f, 0xbac2bd, SystemColors.WindowText);
    public static Color Line => Choose(0xdce1dc, 0x3b443e, SystemColors.WindowText);
    public static Color Selection => Choose(0xe6ebe6, 0x353e37, SystemColors.Control);
    public static Color Hero => Choose(0xfdfefc, 0x222723, SystemColors.Window);
    public static Color Inset => Choose(0xedf0eb, 0x2e3630, SystemColors.Control);
    public static Color Track => Choose(0xdde3dd, 0x3e4841, SystemColors.Window);

    public static Color PaceFill(double used, double? target)
    {
        if (SystemInformation.HighContrast) return SystemColors.Highlight;
        if (target is not { } elapsed) return Muted;
        if (used <= elapsed) return Choose(0x287653, 0x8ac99e, SystemColors.Highlight);
        if (used <= elapsed * 1.10) return Choose(0x946217, 0xe2ba73, SystemColors.Highlight);
        return Choose(0xa93846, 0xefa5a9, SystemColors.Highlight);
    }

    private static Color Choose(int light, int darkColor, Color highContrast) =>
        SystemInformation.HighContrast ? highContrast : Color.FromArgb(unchecked((int)0xff000000) | (dark ? darkColor : light));
}

internal sealed class GraphiteCard : TableLayoutPanel
{
    private readonly Func<Color> surface;
    private readonly int radius;
    private readonly bool hasBorder;

    public GraphiteCard(Func<Color> surface, int radius, bool hasBorder)
    {
        this.surface = surface;
        this.radius = radius;
        this.hasBorder = hasBorder;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        if (ClientSize.Width <= 1 || ClientSize.Height <= 1) return;

        var bounds = new RectangleF(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f);
        float corner = Math.Min(radius * DeviceDpi / 96f, Math.Min(bounds.Width, bounds.Height) / 2f);
        using var shape = new GraphicsPath();
        shape.AddRoundedRectangle(bounds, new SizeF(corner, corner));
        var graphicsState = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(surface());
        e.Graphics.FillPath(brush, shape);
        if (hasBorder)
        {
            using var border = new Pen(Graphite.Line);
            e.Graphics.DrawPath(border, shape);
        }
        e.Graphics.Restore(graphicsState);
    }
}

internal sealed class MainForm : Form
{
    private readonly Label updateStamp = Ui.Text(secondary: true);
    private readonly Button refresh = Ui.Button("Refresh");
    private readonly Label error = Ui.Text(secondary: true);
    private readonly Label sessionTitle = Ui.Text(size: 12, bold: true);
    private readonly Label sessionRange = Ui.Text(secondary: true);
    private readonly Label sessionTime = Ui.Text(secondary: true);
    private readonly Label sessionUsageLabel = Ui.Text(secondary: true);
    private readonly Label sessionUsage = Ui.Text(size: 36, bold: true);
    private readonly QuotaUsageBar sessionProgress = new("Session budget used");
    private readonly Label sessionComparison = Ui.Text(secondary: true);
    private readonly Label sessionAction = Ui.Text(size: 20, bold: true);
    private readonly Label sessionDetail = Ui.Text(secondary: true);
    private readonly Control sessionSuggestionRule = Ui.Rule();
    private readonly Label sessionSuggestionLabel = Ui.Text(secondary: true);
    private readonly Label sessionSuggestion = Ui.Text(bold: true);
    private readonly Label sessionForecast = Ui.Text(secondary: true);
    private readonly Label sessionTargetLabel = Ui.Text(secondary: true);
    private readonly Label sessionTarget = Ui.Text(size: 25, bold: true);
    private readonly TableLayoutPanel sessionTargetDisplay;
    private readonly Label sessionRemaining = Ui.Text(secondary: true);
    private readonly Label weeklyUsageLabel = Ui.Text(secondary: true);
    private readonly Label weeklyUsage = Ui.Text(size: 24, bold: true);
    private readonly QuotaUsageBar weeklyProgress = new("Weekly quota used");
    private readonly Label weeklyTarget = Ui.Text(size: 22, bold: true);
    private readonly Label weeklyTargetLabel = Ui.Text(secondary: true);
    private readonly TableLayoutPanel weeklyTargetDisplay;
    private readonly Label weeklyCaption = Ui.Text(secondary: true);
    private readonly Label weeklyRate = Ui.Text();
    private readonly Label weeklySafe = Ui.Text();
    private readonly Label weeklyProjection = Ui.Text();
    private readonly Label weeklyProjectionLabel = Ui.Text("Projected weekly use", secondary: true);
    private readonly Label weeklyWorkLeft = Ui.Text();
    private readonly TableLayoutPanel weeklyMetrics;
    private readonly Label resetTitle = Ui.Text("Scheduled reset", secondary: true);
    private readonly Label resetStatus = Ui.Text(bold: true);
    private readonly Label resetTarget = Ui.Text(secondary: true);
    private readonly Label shortQuota = Ui.Text(secondary: true);
    private readonly Button resetButton = Ui.Button("Set reset target…");
    private readonly Label recommendation = Ui.Text(size: 13, bold: true);
    private readonly Label recommendationDetail = Ui.Text(secondary: true);
    private readonly TableLayoutPanel adviceCard;
    private readonly Label activity = Ui.Text(secondary: true);
    private readonly Label source = Ui.Text(secondary: true);
    private readonly Label planCaption = Ui.Text(secondary: true);
    private readonly Label planEmpty = Ui.Text(secondary: true);
    private readonly RadioButton used = Ui.Mode("Used");
    private readonly RadioButton left = Ui.Mode("Left");
    private readonly DataGridView planGrid;
    private readonly ContextMenuStrip settingsMenu = new();

    private Preferences preferences;
    private DashboardData? data;
    private DateTimeOffset renderedNow = DateTimeOffset.UtcNow;
    private long renderedAt = Stopwatch.GetTimestamp();
    private IReadOnlyList<SessionTemplate> renderedTemplates = [];
    private IReadOnlyList<PlanRow> renderedRows = [];
    private bool rendering;
    private bool fittingGrid;
    private bool appearanceQueued;

    public event Action? RefreshRequested;
    public event Action<Preferences>? PreferencesChanged;
    public event Action? TestNotificationRequested;

    public MainForm(Preferences preferences)
    {
        this.preferences = preferences;
        Text = "Codex Pace";
        AccessibleName = Text;
        Font = Ui.BodyFont;
        Graphite.Style(this, form =>
        {
            form.BackColor = Graphite.Surface;
            form.ForeColor = Graphite.Ink;
        });
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        MinimumSize = new Size(620, 480);
        ClientSize = new Size(940, 810);
        StartPosition = FormStartPosition.CenterScreen;

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, TabStop = false };
        var content = Ui.Stack(padding: 22);
        scroll.Controls.Add(content);
        Controls.Add(scroll);

        var settings = Ui.Button("Settings");
        settingsMenu.Items.Add("Edit sessions…", null, (_, _) => EditSessions());
        settingsMenu.Items.Add("Work days…", null, (_, _) => EditWorkdays());
        settingsMenu.Items.Add(new ToolStripSeparator());
        settingsMenu.Items.Add("Send test notification", null, (_, _) => TestNotificationRequested?.Invoke());
        Graphite.Style(settingsMenu, menu =>
        {
            menu.BackColor = Graphite.Panel;
            menu.ForeColor = Graphite.Ink;
        });
        settings.Click += (_, _) => settingsMenu.Show(settings, new Point(0, settings.Height));
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        var title = Ui.Stack();
        Ui.Add(title, Ui.Text("Codex Pace", size: 23, bold: true));
        Ui.Add(title, updateStamp, 3);
        used.CheckedChanged += (_, _) => OnTargetModeChanged(used, showUsed: true);
        left.CheckedChanged += (_, _) => OnTargetModeChanged(left, showUsed: false);
        var modes = Ui.Actions(used, left);
        modes.AccessibleName = "Quota view";
        var quotaViewLabel = Ui.Text("Quota view", secondary: true);
        var quotaView = Ui.Actions(quotaViewLabel, modes);
        quotaViewLabel.Margin = new Padding(0, 5, 0, 0);
        Ui.Add(content, Ui.SideBySide(title, Ui.Actions(quotaView, settings, refresh)));
        Ui.Add(content, error, 10);

        var hero = Ui.Card(() => Graphite.Hero, padding: 22, radius: 16);
        var readings = Ui.Stack();
        var sessionHeading = Ui.Stack();
        Ui.Add(sessionHeading, sessionTitle);
        Ui.Add(sessionHeading, sessionRange, 3);
        Ui.Add(readings, Ui.SideBySide(sessionHeading, sessionTime));

        var sessionBudgetDisplay = Ui.Stack();
        Ui.Add(sessionBudgetDisplay, sessionUsageLabel);
        Ui.Add(sessionBudgetDisplay, sessionUsage, 3);
        sessionTargetDisplay = Ui.Stack();
        Ui.Add(sessionTargetDisplay, sessionTargetLabel);
        Ui.Add(sessionTargetDisplay, sessionTarget, 3);
        Ui.Add(readings, Ui.SideBySide(sessionBudgetDisplay, sessionTargetDisplay), 10);
        Ui.Add(readings, sessionProgress, 7);
        Ui.Add(readings, sessionRemaining, 4);
        Ui.Add(readings, sessionComparison, 4);
        Ui.Add(readings, sessionForecast, 4);

        var guidance = Ui.Card(() => Graphite.Inset, padding: 18, radius: 12, hasBorder: false);
        Ui.Add(guidance, sessionAction);
        Ui.Add(guidance, sessionDetail, 5);
        Ui.Add(guidance, sessionSuggestionRule, 9);
        Ui.Add(guidance, sessionSuggestionLabel, 8);
        Ui.Add(guidance, sessionSuggestion, 4);
        Ui.Add(hero, Ui.Columns(readings, guidance, firstPercent: 62, minimumWidth: 620, gap: 24));
        Ui.Add(content, hero, 14);

        var weekly = Ui.Card();
        Ui.Add(weekly, Ui.Text("Weekly quota", size: 11, bold: true));
        var weeklyUsageDisplay = Ui.Stack();
        Ui.Add(weeklyUsageDisplay, weeklyUsageLabel);
        Ui.Add(weeklyUsageDisplay, weeklyUsage, 3);
        weeklyTargetDisplay = Ui.Stack();
        Ui.Add(weeklyTargetDisplay, weeklyTargetLabel);
        Ui.Add(weeklyTargetDisplay, weeklyTarget, 3);
        Ui.Add(weekly, Ui.SideBySide(weeklyUsageDisplay, weeklyTargetDisplay), 10);
        Ui.Add(weekly, weeklyProgress, 6);
        Ui.Add(weekly, weeklyCaption, 4);
        weeklyMetrics = Ui.Metrics(
            (Ui.Text("Recent pace", secondary: true), weeklyRate),
            (Ui.Text("Safe pace", secondary: true), weeklySafe),
            (weeklyProjectionLabel, weeklyProjection),
            (Ui.Text("Work time left", secondary: true), weeklyWorkLeft));
        Ui.Add(weekly, weeklyMetrics, 9);
        resetButton.Click += (_, _) => EditResetTarget();
        var resetText = Ui.Stack();
        Ui.Add(resetText, resetTitle);
        Ui.Add(resetText, resetStatus, 3);
        Ui.Add(resetText, resetTarget, 4);
        Ui.Add(weekly, resetText, 11);
        resetButton.Anchor = AnchorStyles.Left;
        Ui.Add(weekly, resetButton, 5);
        Ui.Add(weekly, shortQuota, 6);

        var plan = Ui.Card();
        Ui.Add(plan, Ui.SideBySide(Ui.Text("Work sessions", size: 11, bold: true), planCaption));
        Ui.Add(plan, planEmpty, 6);
        planGrid = CreatePlanGrid();
        Ui.Add(plan, planGrid, 6);
        Ui.Add(content, Ui.Columns(weekly, plan, firstPercent: 36.5f, minimumWidth: 780, gap: 14), 14);

        adviceCard = Ui.Card();
        Ui.Add(adviceCard, recommendation);
        Ui.Add(adviceCard, recommendationDetail, 4);
        Ui.Add(content, adviceCard, 12);
        Ui.Add(content, activity, 10);
        Ui.Add(content, source, 9);

        Ui.WrapWithin(content, error, activity, source);
        Ui.WrapWithin(title, updateStamp);
        Ui.WrapWithin(readings, sessionComparison, sessionForecast);
        Ui.WrapWithin(sessionBudgetDisplay, sessionUsage);
        Ui.WrapWithin(guidance, sessionDetail, sessionSuggestion);
        Ui.WrapWithin(weekly, weeklyCaption, shortQuota);
        Ui.WrapWithin(resetText, resetStatus, resetTarget);
        Ui.WrapWithin(adviceCard, recommendation, recommendationDetail);

        Render(new DashboardData(null, null, null, null, "", "", "", "", "", null, null),
            preferences, renderedNow, refreshing: false);
    }

    public void Render(DashboardData data, Preferences preferences, DateTimeOffset now, bool refreshing)
    {
        this.data = data;
        this.preferences = preferences;
        renderedNow = now;
        renderedAt = Stopwatch.GetTimestamp();

        rendering = true;
        SuspendLayout();
        try
        {
            refresh.Text = refreshing ? "Refreshing…" : "Refresh";
            refresh.Enabled = !refreshing;
            Ui.Set(updateStamp, data.Quota is { } quota ? $"Updated {Ui.Stamp(quota.ObservedAt)}" : "Weekly usage tracker");
            Ui.Set(error, data.Error);
            var activeSession = RenderHero(data, preferences.ShowUsed, now);
            RenderWeekly(data, preferences, now);
            var showAdvice = data.Warning is not null || data.Quota?.UsedPercent >= 100;
            var showSessionSuggestion = activeSession && showAdvice;
            sessionSuggestionRule.Visible = showSessionSuggestion;
            Ui.Set(sessionSuggestionLabel, showSessionSuggestion && data.Warning is not null
                ? "Suggested for the next turn" : null);
            Ui.Set(sessionSuggestion, showSessionSuggestion ? data.Recommendation : null);
            var showStandaloneAdvice = showAdvice && !activeSession;
            adviceCard.Visible = showStandaloneAdvice;
            Ui.Set(recommendation, showStandaloneAdvice ? data.Recommendation : null);
            Ui.Set(recommendationDetail, showStandaloneAdvice ? data.RecommendationDetail : null);
            Ui.Set(activity, string.IsNullOrWhiteSpace(data.RecentActivity) ? null : $"Recent Codex activity · {data.RecentActivity}");
            Ui.Set(source, data.Source);
            used.Checked = preferences.ShowUsed;
            left.Checked = !preferences.ShowUsed;
            RenderPlan(data, preferences, now);
        }
        finally
        {
            ResumeLayout(performLayout: true);
            rendering = false;
        }
    }

    private bool RenderHero(DashboardData data, bool showUsed, DateTimeOffset now)
    {
        var pace = data.Pace;
        if (pace?.CurrentSession is not { } session || now < session.Start || now >= session.End)
        {
            Ui.Set(sessionTitle, "Current session");
            Ui.Set(sessionRange, null);
            Ui.Set(sessionTime, null);
            Ui.Set(sessionUsageLabel, null);
            Ui.Set(sessionUsage, null);
            sessionTargetDisplay.Visible = false;
            sessionProgress.Visible = false;
            Ui.Set(sessionComparison, null);
            Ui.Set(sessionRemaining, null);
            Ui.Set(sessionForecast, null);
            if (pace is null)
            {
                Ui.Set(sessionAction, "Checking quota");
                Ui.Set(sessionDetail, null);
            }
            else
            {
                var next = pace.Sessions.FirstOrDefault(item => item.Start > now);
                var between = next is not null && WorkClock.Day(next.Start) == WorkClock.Day(now)
                    && pace.Sessions.Any(item => item.End <= now && WorkClock.Day(item.End) == WorkClock.Day(now));
                Ui.Set(sessionAction, between ? "Between sessions" : "Outside work hours");
                Ui.Set(sessionDetail, next is null ? null
                    : $"Next {next.Template.Name} · {Ui.Day(next.Start)} · {Ui.Range(next)}");
            }
            return false;
        }

        var room = pace.SessionRoomToTarget;
        var guidance = data.Guidance;
        var action = guidance?.Action ?? SessionAction.AwaitingReadings;
        Ui.Set(sessionTitle, session.Template.Name);
        Ui.Set(sessionRange, Ui.Range(session));
        Ui.Set(sessionTime, $"{Ui.Duration(session.End - now)} left");
        RenderSessionUsage(pace, showUsed);
        if (data.Quota is { } quota)
        {
            var kind = showUsed ? "used" : "left";
            var comparison = $"{QuotaDisplay.OrientedPercent(quota.UsedPercent, showUsed)} weekly quota {kind} · "
                + $"{QuotaDisplay.OrientedPercent(session.CumulativeTargetPercent, showUsed)} target {kind} at {Ui.Time(session.End)}";
            if (IsStale(quota, now)) comparison += $" · Last read {Ui.Stamp(quota.ObservedAt)}";
            Ui.Set(sessionComparison, comparison);
        }
        else Ui.Set(sessionComparison, null);

        Ui.Set(sessionAction, Planner.ActionLabel(action));
        var details = new List<string>();
        switch (action)
        {
            case SessionAction.CheckUsage:
                details.Add("Refresh to check your current room to target.");
                break;
            case SessionAction.AwaitingReadings:
                details.Add("Another quota reading will help estimate your pace.");
                break;
            case SessionAction.Pause when data.Quota?.UsedPercent >= 100:
                details.Add("Your weekly quota is used up.");
                break;
            case SessionAction.Pause when room < 0:
                details.Add($"You’re {Ui.Percent(-room)} of weekly quota over the shift-end target.");
                break;
            case SessionAction.Pause:
                details.Add("You’ve reached the cumulative shift-end target.");
                break;
        }
        if (guidance?.SafeSessionPercentPerHour is { } rate)
            details.Add($"Aim for about {Ui.Percent(rate)} of the session allowance per hour or less.");
        if (action != SessionAction.Pause && guidance?.WeeklyRisk == true)
            details.Add("At this pace, your weekly quota could run out before reset.");
        if (pace.SessionBudget is null)
            details.Add("Session percentages need a quota reading around the start of the shift.");
        Ui.Set(sessionDetail, string.Join(Environment.NewLine, details));

        if (room is { } remaining && pace.SessionBudget is double budget && budget > 0)
        {
            var prefix = pace.SessionStartInferred ? "Estimated " : "";
            var amount = showUsed ? Math.Clamp(remaining, 0, budget) : pace.SessionUsed;
            Ui.Set(sessionRemaining, amount is null ? null
                : $"{prefix}{Ui.SessionPercent(amount, budget)} {(showUsed ? "left" : "used")} in this session");
        }
        else Ui.Set(sessionRemaining, null);

        var forecast = action != SessionAction.CheckUsage && pace.SessionProjectedRoomToTarget is { } projection
            ? Math.Round(projection, 1) switch
            {
                > 0 => $"Projected to finish {Ui.Percent(projection)} of weekly quota below target",
                < 0 => $"Projected to exceed the shift-end target by {Ui.Percent(-projection)} of weekly quota",
                _ => "Projected to finish at the shift-end target"
            }
            : null;
        Ui.Set(sessionForecast, forecast);
        return true;
    }

    private void RenderSessionUsage(Pace pace, bool showUsed)
    {
        var targetFraction = pace.SessionBudget is double budget && budget > 0 ? pace.SessionTarget / budget : null;
        var kind = showUsed ? "used" : "left";
        var target = targetFraction is double elapsed && double.IsFinite(elapsed)
            ? QuotaDisplay.OrientedPercent(elapsed * 100, showUsed) : null;
        Ui.Set(sessionTargetLabel, $"Target {kind} by now");
        Ui.Set(sessionTarget, target);
        sessionTargetDisplay.Visible = target is not null;

        if (QuotaDisplay.SessionUsage(pace) is { } usage)
        {
            var estimate = pace.SessionStartInferred ? "Estimated " : "";
            var amount = QuotaDisplay.OrientedPercent(usage.Fraction * 100, showUsed);
            Ui.Set(sessionUsageLabel, $"{estimate}Session budget {kind}");
            Ui.Set(sessionUsage, amount);
            var description = $"{estimate}{amount} of the opening session budget {kind}.";
            if (target is not null) description += $" Target by now is {target} {kind}.";
            sessionProgress.AccessibleName = $"Session budget {kind}";
            sessionProgress.SetUsage(usage.Fraction, target is null ? null : targetFraction, amount, description, showLeft: !showUsed);
            sessionProgress.Visible = true;
        }
        else
        {
            Ui.Set(sessionUsageLabel, null);
            Ui.Set(sessionUsage, pace.SessionBudget == 0 ? "No session allocation" : "Usage unavailable");
            sessionProgress.Visible = false;
        }
    }

    private void RenderWeekly(DashboardData data, Preferences preferences, DateTimeOffset now)
    {
        var quota = data.Quota;
        var pace = data.Pace;
        var showUsed = preferences.ShowUsed;
        var usageKind = showUsed ? "used" : "left";
        double DisplayAmount(double used) => showUsed ? used : 100 - used;
        double? usedTarget = quota is not null && pace is { Sessions.Count: > 0 } ? pace.TargetPercent : null;
        var targetByNow = usedTarget is { } target ? DisplayAmount(target) : (double?)null;
        Ui.Set(weeklyUsageLabel, quota is null ? null : $"Quota {usageKind}");
        Ui.Set(weeklyUsage, quota is null ? "Weekly quota unavailable" : Ui.Percent(DisplayAmount(quota.UsedPercent)));
        Ui.Set(weeklyTargetLabel, $"Target {usageKind} by now");
        Ui.Set(weeklyTarget, targetByNow.HasValue ? Ui.Percent(targetByNow) : null);
        weeklyTargetDisplay.Visible = targetByNow.HasValue;
        if (quota is { UsedPercent: >= 0 and <= 100 })
        {
            var amount = Ui.Percent(DisplayAmount(quota.UsedPercent));
            var description = $"{amount} of weekly quota {usageKind}.";
            if (targetByNow is { } planned) description += $" Target by now is {Ui.Percent(planned)} {usageKind}.";
            weeklyProgress.AccessibleName = $"Weekly quota {usageKind}";
            weeklyProgress.SetUsage(quota.UsedPercent / 100, usedTarget / 100, amount, description, showLeft: !showUsed);
            weeklyProgress.Visible = true;
        }
        else weeklyProgress.Visible = false;

        var caption = quota is null ? null
            : showUsed ? $"{Ui.Percent(100 - quota.UsedPercent)} left" : $"{Ui.Percent(quota.UsedPercent)} used";
        if (pace?.ExhaustionDate is { } exhaustion)
        {
            var forecast = $"Projected to run out {Ui.Stamp(exhaustion)}";
            caption = caption is null ? forecast : $"{caption} · {forecast}";
        }
        Ui.Set(weeklyCaption, caption);
        weeklyMetrics.Visible = pace is not null;
        if (pace is not null)
        {
            var hasTarget = pace.PlanEndsAt != quota?.ResetsAt;
            Ui.Set(weeklyRate, Ui.Rate(pace.RecentRate));
            Ui.Set(weeklySafe, Ui.Rate(pace.SafeRate));
            Ui.Set(weeklyProjection, Ui.Percent(pace.ProjectedPercent));
            Ui.Set(weeklyProjectionLabel, hasTarget ? "Projected use by target" : "Projected weekly use");
            Ui.Set(weeklyWorkLeft, pace.RemainingWorkHours.ToString("0.#", CultureInfo.CurrentCulture) + " h");
        }

        resetTitle.Visible = quota is not null;
        Ui.Set(resetStatus, quota is null ? null : Ui.Stamp(quota.ResetsAt));
        var manual = preferences.ResetTarget;
        var sameWindow = manual is not null && manual.WindowResetsAt == quota?.ResetsAt;
        resetButton.Visible = quota is not null;
        resetButton.Enabled = quota is not null && Ui.NextMinute(now) < quota.ResetsAt;
        resetButton.Text = sameWindow ? "Edit reset target…" : "Set reset target…";
        Ui.Set(resetTarget, sameWindow ? $"Manual reset target · {Ui.Stamp(manual!.TargetAt)}"
            + (manual.TargetAt <= now ? " · Target passed" : pace?.PlanEndsAt == manual.TargetAt ? " · Pacing uses this target" : "") : null);
        Ui.Set(shortQuota, data.ShortQuota is { } shortWindow
            ? $"{Ui.Duration(TimeSpan.FromMinutes(shortWindow.WindowMinutes))} limit · {Ui.Percent(shortWindow.UsedPercent)} used · Resets {Ui.Stamp(shortWindow.ResetsAt)}"
            : null);
    }

    private void RenderPlan(DashboardData data, Preferences preferences, DateTimeOffset now)
    {
        var pace = data.Pace;
        var schedule = preferences.Schedule.IsValid ? preferences.Schedule : WorkSchedule.Standard;
        var templates = schedule.Entries.OrderBy(template => template.StartMinute).ToArray();
        var rows = new List<PlanRow>();
        foreach (var day in (pace?.Sessions ?? []).GroupBy(session => WorkClock.Day(session.Start)))
        {
            var sessions = day.ToDictionary(session => session.Template.Id);
            var values = new string[templates.Length + 1];
            values[0] = Ui.Day(day.Key);
            int activeColumn = -1;
            for (int index = 0; index < templates.Length; index++)
            {
                int column = index + 1;
                if (!sessions.TryGetValue(templates[index].Id, out var session))
                {
                    values[column] = "—";
                    continue;
                }

                bool active = pace?.CurrentSession == session && session.Start <= now && now < session.End;
                values[column] = PlanCell(session, active, data.Quota, preferences.ShowUsed, now);
                if (active) activeColumn = column;
            }

            rows.Add(new PlanRow(values, activeColumn));
        }

        var hasSessions = rows.Count > 0;
        Ui.Set(planCaption, hasSessions ? $"{(preferences.ShowUsed ? "Used" : "Left")} at session end" : null);
        Ui.Set(planEmpty, rows.Count != 0 ? null
            : preferences.Workdays.Length == 0 ? "No work days selected."
            : templates.Length == 0 ? "No sessions configured."
            : pace is null ? "Work sessions appear with the weekly quota."
            : "The quota window contains no scheduled work hours.");
        planGrid.Visible = rows.Count > 0;
        bool columnsChanged = !renderedTemplates.SequenceEqual(templates);
        bool rowsChanged = renderedRows.Count != rows.Count || renderedRows.Zip(rows).Any(pair =>
            pair.First.ActiveColumn != pair.Second.ActiveColumn || !pair.First.Values.SequenceEqual(pair.Second.Values));
        if (!columnsChanged && !rowsChanged) return;

        var currentRow = planGrid.CurrentCell?.RowIndex ?? -1;
        var currentColumn = planGrid.CurrentCell?.ColumnIndex ?? -1;
        var horizontalOffset = planGrid.HorizontalScrollingOffset;
        planGrid.SuspendLayout();
        try
        {
            planGrid.Rows.Clear();
            if (columnsChanged)
            {
                while (planGrid.Columns.Count > 1) planGrid.Columns.RemoveAt(planGrid.Columns.Count - 1);
                foreach (var template in templates)
                {
                    planGrid.Columns.Add(new DataGridViewTextBoxColumn
                    {
                        HeaderText = $"{template.Name}\n{Ui.Time(template.StartMinute)}–{Ui.Time(template.EndMinute)}",
                        FillWeight = 1,
                        MinimumWidth = 168,
                        SortMode = DataGridViewColumnSortMode.NotSortable,
                    });
                }
                renderedTemplates = templates;
            }

            foreach (var row in rows)
            {
                var index = planGrid.Rows.Add(row.Values);
                if (row.ActiveColumn >= 0)
                {
                    var cell = planGrid.Rows[index].Cells[row.ActiveColumn];
                    cell.Tag = true;
                    cell.Style.BackColor = Graphite.Selection;
                }
            }
            if (!columnsChanged)
            {
                if (currentRow >= 0 && currentRow < rows.Count && currentColumn >= 0 && currentColumn < planGrid.Columns.Count)
                    planGrid.CurrentCell = planGrid.Rows[currentRow].Cells[currentColumn];
                planGrid.HorizontalScrollingOffset = horizontalOffset;
            }
            if (!planGrid.Focused) planGrid.ClearSelection();
            renderedRows = rows;
        }
        finally
        {
            planGrid.ResumeLayout();
        }
        FitPlanGrid();
    }

    private static string PlanCell(WorkSession session, bool active, QuotaSample? quota,
        bool showUsed, DateTimeOffset now)
    {
        var target = Ui.Percent(showUsed ? session.CumulativeTargetPercent : 100 - session.CumulativeTargetPercent);
        var rows = new List<string> { Ui.Range(session) };
        if (active && quota is not null && now >= session.Start && now < quota.ResetsAt && quota.ObservedAt <= now
            && quota.UsedPercent is >= 0 and <= 100)
        {
            var mode = showUsed ? "used" : "left";
            var actual = Ui.Percent(showUsed ? quota.UsedPercent : 100 - quota.UsedPercent);
            rows.Add($"Target {mode} {target} · Actual {mode} {actual}");
            var room = Math.Round(session.CumulativeTargetPercent - quota.UsedPercent, 1);
            rows.Add(IsStale(quota, now) ? $"Last reading {Ui.Stamp(quota.ObservedAt)}"
                : room > 0 ? $"{Ui.Percent(room)} room to target"
                : room < 0 ? $"{Ui.Percent(-room)} over target" : "At target");
        }
        else rows.Add(target);

        rows.Add($"{Ui.Percent(session.TargetPercent)} session share{(active ? " · Now" : "")}");
        return string.Join(Environment.NewLine, rows);
    }

    private DataGridView CreatePlanGrid()
    {
        var grid = new DataGridView
        {
            AccessibleName = "Work sessions by day",
            Dock = DockStyle.Top,
            Height = 75,
            BorderStyle = BorderStyle.None,
            BackgroundColor = Graphite.Panel,
            GridColor = Graphite.Line,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            RowHeadersVisible = false,
            MultiSelect = false,
            ScrollBars = ScrollBars.Horizontal,
            EnableHeadersVisualStyles = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect
        };
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        Graphite.Style(grid, table =>
        {
            table.BackgroundColor = Graphite.Panel;
            table.GridColor = Graphite.Line;
            table.DefaultCellStyle.BackColor = Graphite.Panel;
            table.DefaultCellStyle.ForeColor = Graphite.Ink;
            table.DefaultCellStyle.SelectionBackColor = Graphite.Selection;
            table.DefaultCellStyle.SelectionForeColor = Graphite.Ink;
            table.ColumnHeadersDefaultCellStyle.BackColor = Graphite.Panel;
            table.ColumnHeadersDefaultCellStyle.ForeColor = Graphite.Ink;
            table.ColumnHeadersDefaultCellStyle.SelectionBackColor = Graphite.Selection;
            table.ColumnHeadersDefaultCellStyle.SelectionForeColor = Graphite.Ink;
            foreach (DataGridViewRow row in table.Rows)
                foreach (DataGridViewCell cell in row.Cells)
                    if (cell.Tag is true) cell.Style.BackColor = Graphite.Selection;
        });
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Workday",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Width = 112,
            MinimumWidth = 88,
            Frozen = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        grid.ClientSizeChanged += (_, _) => FitPlanGrid();
        return grid;
    }

    private void FitPlanGrid()
    {
        if (fittingGrid || planGrid is null || planGrid.Rows.Count == 0) return;
        fittingGrid = true;
        try
        {
            planGrid.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCells);
            var height = planGrid.ColumnHeadersHeight + planGrid.Rows.GetRowsHeight(DataGridViewElementStates.Visible) + 2;
            if (planGrid.Columns.GetColumnsWidth(DataGridViewElementStates.Visible) > planGrid.ClientSize.Width)
                height += planGrid.Controls.OfType<HScrollBar>().FirstOrDefault()?.Height ?? SystemInformation.HorizontalScrollBarHeight;
            if (planGrid.Height != height) planGrid.Height = height;
        }
        finally { fittingGrid = false; }
    }

    private void OnTargetModeChanged(RadioButton button, bool showUsed)
    {
        if (!rendering && button.Checked && preferences.ShowUsed != showUsed)
            PreferencesChanged?.Invoke(preferences with { ShowUsed = showUsed });
    }

    private void EditSessions()
    {
        using var dialog = new SessionsDialog(preferences.Schedule);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            PreferencesChanged?.Invoke(preferences with { Schedule = dialog.Schedule });
    }

    private void EditWorkdays()
    {
        using var dialog = new WorkdaysDialog(preferences.Workdays);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            PreferencesChanged?.Invoke(preferences with { Workdays = dialog.Workdays });
    }

    private void EditResetTarget()
    {
        if (data?.Quota is not { } quota || Ui.NextMinute(CurrentTime()) >= quota.ResetsAt) return;
        var existing = preferences.ResetTarget is { } target && target.WindowResetsAt == quota.ResetsAt
            ? target.TargetAt : (DateTimeOffset?)null;
        using var dialog = new ResetDialog(quota.ResetsAt, existing, CurrentTime);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var currentTime = CurrentTime();
        if (data?.Quota?.ResetsAt != quota.ResetsAt || currentTime >= quota.ResetsAt)
        {
            MessageBox.Show(this, "The quota window changed. Open the reset target for the current window.",
                "Reset target", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (dialog.Target is { } savedTarget && savedTarget <= currentTime)
        {
            MessageBox.Show(this, "Choose a reset target in the future.", "Reset target", MessageBoxButtons.OK);
            return;
        }
        PreferencesChanged?.Invoke(preferences with
        {
            ResetTarget = dialog.Target is { } deadline ? new ManualResetTarget(deadline, quota.ResetsAt) : null
        });
    }

    private DateTimeOffset CurrentTime() => renderedNow + Stopwatch.GetElapsedTime(renderedAt);
    private static bool IsStale(QuotaSample quota, DateTimeOffset now) => now - quota.ObservedAt > TimeSpan.FromMinutes(45);
    private sealed record PlanRow(string[] Values, int ActiveColumn);

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        // WM_SETTINGCHANGE covers the app theme. WM_SYSCOLORCHANGE also covers contrast themes.
        if (message.Msg is not (0x001A or 0x0015) || appearanceQueued || !IsHandleCreated || IsDisposed) return;
        appearanceQueued = true;
        BeginInvoke(() =>
        {
            try
            {
                if (IsDisposed || !Graphite.FollowSystemAppearance()) return;
                Application.VisualStyleState = Application.VisualStyleState;
                foreach (var form in Application.OpenForms.Cast<Form>().Append(this).Distinct().ToArray())
                {
                    Graphite.Apply(form);
                    form.Invalidate(invalidateChildren: true);
                }
                Graphite.Apply(settingsMenu);
            }
            finally { appearanceQueued = false; }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) settingsMenu.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class QuotaUsageBar : Control
{
    private double usedFraction;
    private double? targetFraction;
    private string? displayPercent;
    private bool showLeft;

    public QuotaUsageBar(string accessibleName)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        Height = 18;
        TabStop = false;
        AccessibleName = accessibleName;
        AccessibleRole = AccessibleRole.ProgressBar;
    }

    public void SetUsage(double used, double? target, string percent, string description, bool showLeft = false)
    {
        usedFraction = used;
        targetFraction = target;
        displayPercent = percent;
        this.showLeft = showLeft;
        AccessibleDescription = description;
        Invalidate();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new UsageAccessibilityObject(this);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var bounds = ClientRectangle;
        if (bounds.Width == 0 || bounds.Height == 0) return;

        var scale = Math.Max(1, DeviceDpi / 96f);
        var thickness = Math.Min(bounds.Height, (int)Math.Round(scale * 8));
        var track = new Rectangle(0, (bounds.Height - thickness) / 2, bounds.Width, thickness);
        var highContrast = SystemInformation.HighContrast;
        using var shape = new GraphicsPath();
        float radius = Math.Min(track.Width, track.Height) / 2f;
        shape.AddRoundedRectangle(track, new SizeF(radius, radius));
        var graphicsState = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var trackBrush = new SolidBrush(Graphite.Track);
        e.Graphics.FillPath(trackBrush, shape);

        var fill = showLeft ? 1 - usedFraction : usedFraction;
        var filledWidth = (int)Math.Round(track.Width * Math.Clamp(fill, 0, 1));
        if (filledWidth > 0)
        {
            using var fillBrush = new SolidBrush(Graphite.PaceFill(usedFraction, targetFraction));
            var clip = e.Graphics.Save();
            e.Graphics.SetClip(shape, CombineMode.Intersect);
            e.Graphics.FillRectangle(fillBrush, track.X, track.Y, filledWidth, track.Height);
            e.Graphics.Restore(clip);
        }

        if (highContrast)
        {
            using var border = new Pen(SystemColors.WindowText, scale);
            e.Graphics.DrawRectangle(border, track.X, track.Y, track.Width - 1, track.Height - 1);
        }

        if (targetFraction is { } target)
        {
            var markerTarget = showLeft ? 1 - target : target;
            var x = (int)Math.Round((bounds.Width - 1) * Math.Clamp(markerTarget, 0, 1));
            using var marker = new Pen(Graphite.Ink, scale * 2);
            e.Graphics.DrawLine(marker, x, 0, x, bounds.Height - 1);
        }
        e.Graphics.Restore(graphicsState);
    }

    private sealed class UsageAccessibilityObject(QuotaUsageBar owner) : ControlAccessibleObject(owner)
    {
        public override string? Value
        {
            get => owner.displayPercent;
            set { }
        }
    }
}

internal abstract class SettingsDialog : Form
{
    private readonly int requestedWidth;
    private readonly TableLayoutPanel layout;
    private readonly Panel bodyHost;
    private FlowLayoutPanel? footer;
    protected TableLayoutPanel Body { get; }

    protected SettingsDialog(string title, int width)
    {
        requestedWidth = width;
        Text = title;
        AccessibleName = title;
        Font = Ui.BodyFont;
        Graphite.Style(this, form =>
        {
            form.BackColor = Graphite.Panel;
            form.ForeColor = Graphite.Ink;
        });
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(width, 410);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bodyHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, TabStop = false };
        Body = Ui.Stack(padding: 18);
        bodyHost.Controls.Add(Body);
        layout.Controls.Add(bodyHost, 0, 0);
        Controls.Add(layout);
    }

    protected void AddFooter(Button save, Button? alternate = null)
    {
        var cancel = Ui.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = save;
        CancelButton = cancel;
        footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 12, 12),
            Margin = Padding.Empty
        };
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);
        if (alternate is not null) footer.Controls.Add(alternate);
        layout.Controls.Add(footer, 0, 1);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromControl(Owner ?? this).WorkingArea;
        var frame = new Size(Width - ClientSize.Width, Height - ClientSize.Height);
        int ScalePixels(int size) => (int)Math.Round(size * DeviceDpi / 96.0);
        var margin = ScalePixels(20);
        var width = Math.Min(ScalePixels(requestedWidth), Math.Max(1, area.Width - frame.Width - margin));
        var maxHeight = Math.Max(1, area.Height - frame.Height - margin);
        ClientSize = new Size(width, Math.Min(ScalePixels(410), maxHeight));
        layout.PerformLayout();

        var bodyWidth = Math.Max(1, bodyHost.ClientSize.Width);
        var bodyHeight = Body.GetPreferredSize(new Size(bodyWidth, 0)).Height;
        var footerHeight = footer?.GetPreferredSize(new Size(width, 0)).Height ?? 0;
        ClientSize = new Size(width, Math.Min(maxHeight, Math.Max(ScalePixels(160), bodyHeight + footerHeight)));
        MinimumSize = new Size(Width, 0);

        var anchor = Owner?.Bounds ?? area;
        Location = new Point(
            Math.Clamp(anchor.Left + (anchor.Width - Width) / 2, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(anchor.Top + (anchor.Height - Height) / 2, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }
}

internal sealed class SessionsDialog : SettingsDialog
{
    public WorkSchedule Schedule { get; private set; }

    public SessionsDialog(WorkSchedule schedule) : base("Edit sessions", 500)
    {
        Schedule = schedule;
        var draft = schedule.Entries.ToList();
        Ui.Add(Body, Ui.Text("Edit sessions", size: 16, bold: true));
        var description = Ui.Text("Sessions repeat on your selected work days.", secondary: true);
        Ui.Add(Body, description, 3);

        var list = new ListBox
        {
            AccessibleName = "Scheduled sessions",
            Dock = DockStyle.Top,
            Height = 140,
            IntegralHeight = false,
            HorizontalScrollbar = true
        };
        Ui.Add(Body, list, 10);
        var add = Ui.Button("Add session");
        var remove = Ui.Button("Remove session");
        Ui.Add(Body, Ui.Actions(add, remove), 5);

        var name = new TextBox { AccessibleName = "Session name", Dock = DockStyle.Top };
        var start = Ui.HourPicker("Session start", 570);
        var end = Ui.HourPicker("Session end", 630);
        var fields = Ui.Stack();
        Ui.Add(fields, Ui.Text("Session name", secondary: true));
        Ui.Add(fields, name, 3);
        var startField = Ui.Stack();
        Ui.Add(startField, Ui.Text("Start", secondary: true));
        Ui.Add(startField, start, 3);
        var endField = Ui.Stack();
        Ui.Add(endField, Ui.Text("End", secondary: true));
        Ui.Add(endField, end, 3);
        Ui.Add(fields, Ui.Actions(startField, endField), 7);
        Ui.Add(Body, fields, 8);

        var validation = Ui.Text(secondary: true);
        Ui.Add(Body, validation, 6);
        Ui.WrapWithin(Body, description, validation);
        var save = Ui.Button("Save");
        var updating = false;

        WorkSchedule ProposedSchedule() => WorkSchedule.FromSessions(draft
            .Select(entry => entry with { Name = entry.Name.Trim() })
            .OrderBy(entry => entry.StartMinute));
        string Caption(SessionTemplate entry) => $"{(string.IsNullOrWhiteSpace(entry.Name) ? "Unnamed session" : entry.Name.Trim())}"
            + $" · {Ui.Time(entry.StartMinute)}–{Ui.Time(entry.EndMinute)}";
        void Validate()
        {
            save.Enabled = ProposedSchedule().IsValid;
            Ui.Set(validation, save.Enabled ? null
                : "Give each session a name, set its end after its start, and keep sessions from overlapping.");
        }

        void ShowSelection()
        {
            if (updating) return;
            var index = list.SelectedIndex;
            fields.Visible = index >= 0;
            remove.Enabled = index >= 0;
            if (index < 0) return;

            var entry = draft[index];
            updating = true;
            name.Text = entry.Name;
            start.Value = DateTime.Today.AddMinutes(entry.StartMinute);
            end.Value = DateTime.Today.AddMinutes(entry.EndMinute);
            updating = false;
        }

        void ReplaceList(int selection)
        {
            updating = true;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var entry in draft) list.Items.Add(Caption(entry));
                list.SelectedIndex = selection;
            }
            finally
            {
                list.EndUpdate();
                updating = false;
            }
            ShowSelection();
            Validate();
        }

        void SaveFields()
        {
            var index = list.SelectedIndex;
            if (updating || index < 0) return;

            var entry = draft[index] with
            {
                Name = name.Text,
                StartMinute = (int)start.Value.TimeOfDay.TotalMinutes,
                EndMinute = (int)end.Value.TimeOfDay.TotalMinutes
            };
            draft[index] = entry;
            updating = true;
            list.Items[index] = Caption(entry);
            list.SelectedIndex = index;
            updating = false;
            Validate();
        }

        list.SelectedIndexChanged += (_, _) => ShowSelection();
        name.TextChanged += (_, _) => SaveFields();
        start.ValueChanged += (_, _) => SaveFields();
        end.ValueChanged += (_, _) => SaveFields();
        add.Click += (_, _) =>
        {
            var minute = draft.Count == 0 ? 570 : Math.Min(1438, draft.Max(entry => entry.EndMinute));
            draft.Add(new SessionTemplate(Guid.NewGuid(), $"Session {draft.Count + 1}", minute, Math.Min(1439, minute + 60)));
            ReplaceList(draft.Count - 1);
            name.Focus();
            name.SelectAll();
        };
        remove.Click += (_, _) =>
        {
            var index = list.SelectedIndex;
            if (index < 0) return;
            draft.RemoveAt(index);
            ReplaceList(Math.Min(index, draft.Count - 1));
        };
        save.Click += (_, _) =>
        {
            SaveFields();
            var updated = ProposedSchedule();
            if (!updated.IsValid) return;
            Schedule = updated;
            DialogResult = DialogResult.OK;
        };
        AddFooter(save);
        ReplaceList(draft.Count > 0 ? 0 : -1);
    }
}

internal sealed class WorkdaysDialog : SettingsDialog
{
    public DayOfWeek[] Workdays { get; private set; }

    public WorkdaysDialog(DayOfWeek[] workdays) : base("Work days", 440)
    {
        Workdays = workdays;
        Ui.Add(Body, Ui.Text("Work days", size: 16, bold: true));
        var description = Ui.Text("Your sessions repeat on the selected days. Clear all days for no scheduled work.", secondary: true);
        Ui.Add(Body, description, 4);
        Ui.WrapWithin(Body, description);

        DayOfWeek[] days =
        [
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
        ];
        var list = new CheckedListBox
        {
            AccessibleName = "Recurring work days",
            CheckOnClick = true,
            Dock = DockStyle.Top,
            IntegralHeight = false
        };
        foreach (var day in days) list.Items.Add(day, workdays.Contains(day));
        list.Height = days.Length * list.ItemHeight + 8;
        Ui.Add(Body, list, 12);

        var save = Ui.Button("Save");
        save.Click += (_, _) =>
        {
            Workdays = list.CheckedItems.Cast<DayOfWeek>().ToArray();
            DialogResult = DialogResult.OK;
        };
        AddFooter(save);
    }
}

internal sealed class ResetDialog : SettingsDialog
{
    public DateTimeOffset? Target { get; private set; }

    public ResetDialog(DateTimeOffset scheduledReset, DateTimeOffset? target, Func<DateTimeOffset> now)
        : base("Manual reset target", 460)
    {
        Target = target;
        Ui.Add(Body, Ui.Text("Manual reset target", size: 16, bold: true));
        var instructions = Ui.Text("Use your manual reset in Codex by this time. The target applies to this quota window.", secondary: true);
        Ui.Add(Body, instructions, 4);
        Ui.WrapWithin(Body, instructions);
        Ui.Add(Body, Ui.Text($"Scheduled account reset · {Ui.Stamp(scheduledReset)}"), 10);

        var current = now();
        var nextMinute = Ui.NextMinute(current);
        var defaultDate = nextMinute.AddMinutes(30) < scheduledReset ? nextMinute.AddMinutes(30) : nextMinute;
        var selection = WorkClock.Local(target ?? defaultDate).DateTime;
        var date = Ui.DatePicker("Reset date", selection.Date);
        var time = Ui.HourPicker("Reset time", (int)selection.TimeOfDay.TotalMinutes);
        var fields = Ui.Actions(date, time);
        fields.AccessibleName = "Date and time";
        Ui.Add(Body, Ui.Text("Date and time", secondary: true), 10);
        Ui.Add(Body, fields, 4);

        var validation = Ui.Text(secondary: true);
        Ui.Add(Body, validation, 6);
        Ui.WrapWithin(Body, validation);
        var save = Ui.Button("Save");
        DateTimeOffset Draft() => WorkClock.FromLocal(date.Value.Date + time.Value.TimeOfDay);
        bool Validate()
        {
            var draft = Draft();
            var error = draft <= now() ? "Choose a date and time in the future."
                : draft >= scheduledReset ? "Choose a time before the scheduled account reset." : null;
            Ui.Set(validation, error);
            save.Enabled = error is null;
            return error is null;
        }
        date.ValueChanged += (_, _) => Validate();
        time.ValueChanged += (_, _) => Validate();
        save.Click += (_, _) =>
        {
            if (!Validate()) return;
            Target = Draft();
            DialogResult = DialogResult.OK;
        };

        Button? clear = null;
        if (target is not null)
        {
            clear = Ui.Button("Clear target");
            clear.Click += (_, _) =>
            {
                Target = null;
                DialogResult = DialogResult.OK;
            };
        }
        AddFooter(save, clear);
        Validate();
    }
}

internal static class Ui
{
    private static readonly Dictionary<(float, bool), Font> Fonts = new();
    public static Font BodyFont { get; } = SystemFonts.MessageBoxFont ?? Control.DefaultFont;

    public static Label Text(string text = "", float? size = null, bool bold = false, bool secondary = false)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Dock = DockStyle.Top,
            UseMnemonic = false,
            Margin = Padding.Empty
        };
        if (size.HasValue || bold)
        {
            var key = (size ?? BodyFont.Size, bold);
            if (!Fonts.TryGetValue(key, out var font))
            {
                font = new Font(BodyFont.FontFamily, key.Item1,
                    bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
                Fonts.Add(key, font);
            }
            label.Font = font;
        }
        return Graphite.Style(label, item => item.ForeColor = secondary ? Graphite.Muted : Graphite.Ink);
    }

    public static void Set(Label label, string? text)
    {
        label.Text = text ?? string.Empty;
        label.Visible = !string.IsNullOrWhiteSpace(text);
    }

    public static Button Button(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(72, 28),
        Padding = new Padding(5, 0, 5, 0),
        FlatStyle = FlatStyle.System,
        UseVisualStyleBackColor = true
    };

    public static RadioButton Mode(string text)
    {
        var button = new RadioButton
        {
            Text = text,
            Appearance = Appearance.Button,
            AutoSize = true,
            Padding = new Padding(7, 1, 7, 1),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            BackColor = Graphite.Selection,
            ForeColor = Graphite.Ink
        };
        return Graphite.Style(button, item =>
        {
            item.BackColor = Graphite.Selection;
            item.ForeColor = Graphite.Ink;
            item.FlatAppearance.CheckedBackColor = Graphite.Panel;
            item.FlatAppearance.BorderColor = Graphite.Line;
        });
    }

    public static TableLayoutPanel Stack(int padding = 0) => ConfigureStack(new TableLayoutPanel(), padding);

    private static TableLayoutPanel ConfigureStack(TableLayoutPanel stack, int padding)
    {
        stack.AutoSize = true;
        stack.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        stack.Dock = DockStyle.Top;
        stack.ColumnCount = 1;
        stack.Padding = new Padding(padding);
        stack.Margin = Padding.Empty;
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return stack;
    }

    public static TableLayoutPanel Card(Func<Color>? surface = null, int padding = 16, int radius = 14, bool hasBorder = true) =>
        ConfigureStack(new GraphiteCard(surface ?? (() => Graphite.Panel), radius, hasBorder), padding);

    public static Control Rule() => Graphite.Style(new Panel
    {
        Height = 1,
        Dock = DockStyle.Top,
        TabStop = false
    }, panel => panel.BackColor = Graphite.Line);

    public static void Add(TableLayoutPanel stack, Control control, int gap = 0)
    {
        var row = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, gap, 0, 0);
        stack.Controls.Add(control, 0, row);
    }

    public static TableLayoutPanel SideBySide(Control first, Control last)
    {
        var row = Stack();
        row.ColumnCount = 2;
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowCount = 1;
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        first.Dock = DockStyle.Fill;
        first.Margin = Padding.Empty;
        last.Dock = DockStyle.None;
        last.Anchor = AnchorStyles.Right;
        last.Margin = new Padding(8, 0, 0, 0);
        row.Controls.Add(first, 0, 0);
        row.Controls.Add(last, 1, 0);
        return row;
    }

    public static TableLayoutPanel Columns(Control first, Control second, float firstPercent, int minimumWidth, int gap)
    {
        var layout = Stack();
        Add(layout, first);
        Add(layout, second);
        bool? wide = null;

        void Arrange()
        {
            var scale = layout.DeviceDpi / 96f;
            var useColumns = layout.ClientSize.Width >= minimumWidth * scale;
            if (wide == useColumns) return;
            wide = useColumns;
            int spacing = (int)Math.Round(gap * scale);

            layout.SuspendLayout();
            if (useColumns)
            {
                layout.ColumnCount = 2;
                layout.SetCellPosition(second, new TableLayoutPanelCellPosition(1, 0));
                layout.RowCount = 1;
            }
            else
            {
                layout.RowCount = 2;
                layout.SetCellPosition(second, new TableLayoutPanelCellPosition(0, 1));
                layout.ColumnCount = 1;
            }
            layout.ColumnStyles.Clear();
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, useColumns ? firstPercent : 100));
            if (useColumns) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 - firstPercent));
            layout.RowStyles.Clear();
            for (var row = 0; row < layout.RowCount; row++)
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            first.Margin = useColumns ? new Padding(0, 0, spacing, 0) : Padding.Empty;
            second.Margin = useColumns ? Padding.Empty : new Padding(0, spacing, 0, 0);
            layout.ResumeLayout(performLayout: true);
        }

        layout.ClientSizeChanged += (_, _) => Arrange();
        Arrange();
        return layout;
    }

    public static FlowLayoutPanel Actions(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty
        };
        for (var index = 0; index < controls.Length; index++)
        {
            var control = controls[index];
            control.Margin = new Padding(index == 0 ? 0 : 6, 0, 0, 0);
            row.Controls.Add(control);
        }
        return row;
    }

    public static TableLayoutPanel Metrics(params (Label Title, Label Value)[] metrics)
    {
        var row = Stack();
        row.ColumnCount = 2;
        row.RowCount = (metrics.Length + 1) / 2;
        for (var index = 0; index < row.RowCount; index++)
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.ColumnStyles.Clear();
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var index = 0; index < metrics.Length; index++)
        {
            var cell = Stack();
            cell.Margin = new Padding(0, index < 2 ? 0 : 9, 8, 0);
            Add(cell, metrics[index].Title);
            Add(cell, metrics[index].Value, 3);
            WrapWithin(cell, metrics[index].Title, metrics[index].Value);
            row.Controls.Add(cell, index % 2, index / 2);
        }
        return row;
    }

    public static void WrapWithin(Control container, params Label[] labels)
    {
        void Update()
        {
            var width = container.ClientSize.Width - container.Padding.Horizontal;
            if (width <= 0) return;
            foreach (var label in labels)
            {
                var maximum = new Size(width, 0);
                if (label.MaximumSize != maximum) label.MaximumSize = maximum;
            }
        }
        container.ClientSizeChanged += (_, _) => Update();
        Update();
    }

    public static DateTimePicker HourPicker(string label, int minute) => new()
    {
        AccessibleName = label,
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "HH:mm",
        ShowUpDown = true,
        Width = 100,
        Value = DateTime.Today.AddMinutes(Math.Clamp(minute, 0, 1439)),
    };

    public static DateTimePicker DatePicker(string label, DateTime date) => new()
    {
        AccessibleName = label,
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "ddd, dd MMM yyyy",
        Width = 160,
        Value = date,
    };

    public static string Percent(double? amount) => QuotaDisplay.Percent(amount);

    public static string SessionPercent(double? amount, double budget) =>
        budget > 0 ? Percent(amount / budget * 100) : "—";

    public static string Rate(double? value) => value is null ? "—" : $"{Percent(value)} of weekly quota per hour";
    public static string Day(DateTimeOffset time) => Day(WorkClock.Day(time));
    public static string Day(DateOnly date) => date.ToString("ddd, d MMM", CultureInfo.CurrentCulture);
    public static string Stamp(DateTimeOffset time) => WorkClock.Local(time).ToString("ddd, d MMM HH:mm", CultureInfo.CurrentCulture);
    public static string Time(DateTimeOffset time) => WorkClock.Local(time).ToString("HH:mm", CultureInfo.CurrentCulture);
    public static string Time(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
    public static string Range(WorkSession session) => $"{Time(session.Start)}–{Time(session.End)}";

    public static DateTimeOffset NextMinute(DateTimeOffset instant)
    {
        var ticks = instant.UtcTicks;
        return new DateTimeOffset(ticks - ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero).AddMinutes(1);
    }

    public static string Duration(TimeSpan duration)
    {
        var minutes = (int)Math.Ceiling(Math.Max(0, duration.TotalMinutes));
        var hours = minutes / 60;
        var fraction = minutes % 60;
        if (hours == 0) return $"{fraction} min";
        if (fraction == 0) return $"{hours} h";
        return $"{hours} h {fraction} min";
    }
}
