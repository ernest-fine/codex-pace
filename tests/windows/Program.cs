using System.Text.Json;
using CodexPace;

internal static class ScheduleChecks
{
    private static readonly DateOnly Monday = new(2026, 11, 16);
    private static readonly DateTimeOffset WindowStart = WorkClock.At(Monday, 10 * 60);
    private static readonly DateTimeOffset Reset = WorkClock.At(Monday.AddDays(7), 10 * 60);

    private static int Main()
    {
        (string Name, Action Check)[] checks =
        [
            ("Legacy schedules retain their hours on every day", ReadsLegacySchedule),
            ("Named shared sessions remain the default for every day", ReadsNamedSchedule),
            ("Per-day and empty overrides survive local JSON storage", RoundTripsDayOverrides),
            ("Applying one shared schedule replaces day overrides", AppliesEntireWeek),
            ("Day and week drafts stay separate while switching scope", KeepsScopeDrafts),
            ("Quota targets use enabled days and each day's durations", PlansPerDayDurations),
            ("Quota windows clip sessions before calculating shares", ClipsQuotaWindow),
            ("Weekly tray readings follow quota orientation and plan targets", TrayReadingChecks.WeeklyOrientations),
            ("Session tray readings use the current session allowance", TrayReadingChecks.SessionOrientations),
            ("Stale weekly tray readings retain values with an unknown tone", TrayReadingChecks.StaleReadings),
            ("Off-hours and unavailable session tray readings omit numbers", TrayReadingChecks.UnavailableSessions),
            ("Missing tray targets remain absent", TrayReadingChecks.MissingTargets),
            ("Tray display geometry clamps while values retain overspend", TrayReadingChecks.ClampedGeometry),
            ("Tray colors compare used quota independently of orientation", TrayReadingChecks.PaceTones),
            ("Session change uses the latest distinct in-session reading", SessionDeltaChecks.UsesLatestDistinctReading),
            ("Session remaining change stops at zero", SessionDeltaChecks.CapsRemainingChangeAtZero),
            ("Session change rejects stale, missing and reset comparisons", SessionDeltaChecks.RejectsUnavailableComparisons),
            ("The normal planner provides the Session change", SessionDeltaChecks.PlannerExposesTheChange),
            ("Fast support requires explicit model capabilities", FastModeChecks.ReadsExplicitModelCapabilities),
            ("Fast mode is suggested with weekly and session headroom", FastModeChecks.SuggestsFastWithQuotaHeadroom),
            ("Standard mode is suggested for quota pressure and heavy work", FastModeChecks.SuggestsStandardUnderPressure),
            ("Unsupported models and stale readings omit speed advice", FastModeChecks.OmitsUnverifiedOrStaleAdvice),
        ];

        foreach (var (name, check) in checks)
        {
            try
            {
                check();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL {name}\n{error}");
                return 1;
            }
        }

        Console.WriteLine($"{checks.Length} Windows schedule checks passed.");
        return 0;
    }

    private static void ReadsLegacySchedule()
    {
        const string json = """
            {"MorningStart":540,"MorningEnd":720,"AfternoonStart":780,"AfternoonEnd":1020}
            """;
        var schedule = Deserialize(json);
        Assert(schedule.IsValid, "Legacy schedule should remain valid.");
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var entries = schedule.EntriesOn(day);
            Equal(2, entries.Count, $"{day} entry count");
            Equal(540, entries[0].StartMinute, $"{day} morning start");
            Equal(720, entries[0].EndMinute, $"{day} morning end");
            Equal(WorkSchedule.MorningId, entries[0].Id, $"{day} stable morning ID");
            Equal(780, entries[1].StartMinute, $"{day} afternoon start");
            Equal(1020, entries[1].EndMinute, $"{day} afternoon end");
        }
    }

    private static void ReadsNamedSchedule()
    {
        const string json = """
            {
              "MorningStart":570,"MorningEnd":750,"AfternoonStart":840,"AfternoonEnd":1095,
              "Sessions":[
                {"Id":"00000000-0000-4000-8000-000000000003","Name":"Custom work","StartMinute":660,"EndMinute":810}
              ]
            }
            """;
        var schedule = Deserialize(json);
        Assert(schedule.IsValid, "Named schedule should remain valid.");
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var entry = schedule.EntriesOn(day).Single();
            Equal("Custom work", entry.Name, $"{day} custom name");
            Equal(660, entry.StartMinute, $"{day} custom start");
            Equal(810, entry.EndMinute, $"{day} custom end");
        }
    }

    private static void RoundTripsDayOverrides()
    {
        var shared = Session(1, "Shared", 540, 660);
        var tuesday = Session(2, "Tuesday shift", 780, 990);
        var original = WorkSchedule.FromSessions([shared]);
        var updated = original.WithDaySessions(DayOfWeek.Tuesday, [tuesday])
            .WithDaySessions(DayOfWeek.Friday, []);
        var restored = Deserialize(JsonSerializer.Serialize(updated));

        Assert(restored.IsValid, "Day overrides should round-trip as a valid schedule.");
        Equal(shared, restored.EntriesOn(DayOfWeek.Monday).Single(), "Unedited day retains shared hours");
        Equal(tuesday, restored.EntriesOn(DayOfWeek.Tuesday).Single(), "Tuesday keeps its hours");
        Equal(0, restored.EntriesOn(DayOfWeek.Friday).Count, "An empty day stays empty");
        Equal(shared, restored.EntriesOn(DayOfWeek.Saturday).Single(), "Weekend still inherits shared hours");
        Equal(shared, original.EntriesOn(DayOfWeek.Tuesday).Single(), "Editing creates an independent schedule");

        var invalid = restored.WithDaySessions(DayOfWeek.Sunday,
            [Session(3, "First", 540, 660), Session(4, "Overlap", 600, 720)]);
        Assert(!invalid.IsValid, "An invalid override should make the schedule invalid.");
        var otherDay = restored.WithDaySessions(DayOfWeek.Wednesday, [shared]);
        Assert(otherDay.IsValid, "The same stable template ID can appear on different days.");
    }

    private static void AppliesEntireWeek()
    {
        var selected = Session(2, "Late shift", 660, 900);
        var perDay = WorkSchedule.Standard.WithDaySessions(DayOfWeek.Tuesday, [selected])
            .WithDaySessions(DayOfWeek.Sunday, []);
        var shared = Deserialize(JsonSerializer.Serialize(
            WorkSchedule.FromSessions(perDay.EntriesOn(DayOfWeek.Tuesday))));
        foreach (var day in Enum.GetValues<DayOfWeek>())
            Equal(selected, shared.EntriesOn(day).Single(), $"{day} uses the shared selection");

        var emptyWeek = Deserialize(JsonSerializer.Serialize(
            WorkSchedule.FromSessions(perDay.EntriesOn(DayOfWeek.Sunday))));
        Assert(emptyWeek.IsValid, "An empty entire week is valid.");
        foreach (var day in Enum.GetValues<DayOfWeek>())
            Equal(0, emptyWeek.EntriesOn(day).Count, $"{day} is cleared");
    }

    private static void PlansPerDayDurations()
    {
        var monday = Session(1, "Monday work", 540, 660);
        var tuesday = Session(2, "Tuesday work", 600, 780);
        var disabled = Session(3, "Disabled day", 540, 720);
        var schedule = WorkSchedule.FromSessions([])
            .WithDaySessions(DayOfWeek.Monday, [monday])
            .WithDaySessions(DayOfWeek.Tuesday, [tuesday])
            .WithDaySessions(DayOfWeek.Wednesday, [])
            .WithDaySessions(DayOfWeek.Thursday, [disabled]);
        var pace = Plan(schedule, [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday], WindowStart.AddMinutes(30));

        // The window opens halfway through Monday's two-hour template.
        Equal(3, pace.Sessions.Count, "First Monday, Tuesday and next Monday are planned");
        Equal(monday, pace.Sessions[0].Template, "First day's selected template");
        Equal(tuesday, pace.Sessions[1].Template, "Tuesday's selected template");
        Equal(monday, pace.Sessions[2].Template, "Next Monday inherits its weekday override");
        Near(20, pace.Sessions[0].TargetPercent, "One clipped hour out of five");
        Near(60, pace.Sessions[1].TargetPercent, "Three Tuesday hours out of five");
        Near(20, pace.Sessions[2].TargetPercent, "One next-Monday hour out of five");
        Near(20, pace.Sessions[0].CumulativeTargetPercent, "First session target");
        Near(80, pace.Sessions[1].CumulativeTargetPercent, "Tuesday session target");
        Near(100, pace.Sessions[2].CumulativeTargetPercent, "Final session target");
        Near(10, pace.TargetPercent, "Elapsed scheduled half hour");
        Near(4.5, pace.RemainingWorkHours, "Future work uses per-day durations");
        Assert(pace.Sessions.All(session => WorkClock.Day(session.Start).DayOfWeek is DayOfWeek.Monday or DayOfWeek.Tuesday),
            "The enabled empty day and disabled non-empty day must not add hours.");
    }

    private static void KeepsScopeDrafts()
    {
        var original = WorkSchedule.FromSessions([Session(1, "Original", 540, 660)]);
        var draft = new ScheduleDraft(original, [DayOfWeek.Monday, DayOfWeek.Tuesday], WindowStart);
        draft.SelectDay(DayOfWeek.Tuesday);
        var tuesdayList = draft.ActiveList;
        var id = draft.Sessions.Single().Id;
        draft.UpdateSession(id, session => session with { Name = "Tuesday draft" }, tuesdayList);

        draft.SelectScope(ScheduleScope.EntireWeek);
        Equal("Tuesday draft", draft.Sessions.Single().Name, "Whole-week draft starts from selected day");
        var weekList = draft.ActiveList;
        draft.UpdateSession(id, session => session with { Name = "Weekly draft" }, weekList);
        draft.SelectScope(ScheduleScope.EditedDays);
        Equal("Tuesday draft", draft.Sessions.Single().Name, "Day draft survives switching to weekly mode");
        draft.SelectDay(DayOfWeek.Monday);
        Equal("Original", draft.Sessions.Single().Name, "Other day's draft remains independent");

        // Native time controls can finish editing after their day or scope is hidden.
        draft.UpdateSession(id, session => session with { StartMinute = 570 }, tuesdayList);
        Equal(540, draft.Sessions.Single().StartMinute, "Late Tuesday update must not change Monday");
        draft.SelectScope(ScheduleScope.EntireWeek);
        Equal("Weekly draft", draft.Sessions.Single().Name, "Weekly edits survive returning from day mode");
        Equal(540, draft.Sessions.Single().StartMinute, "Late day edit must not change weekly draft");
        var weekly = draft.ScheduleToSave;
        Assert(Enum.GetValues<DayOfWeek>().All(day => weekly.EntriesOn(day).Single().Name == "Weekly draft"),
            "Whole-week save should apply the shared draft to every day.");

        draft.SelectScope(ScheduleScope.EditedDays);
        var individual = draft.ScheduleToSave;
        Equal(570, individual.EntriesOn(DayOfWeek.Tuesday).Single().StartMinute, "Late field update keeps its original day");
        Equal("Tuesday draft", individual.EntriesOn(DayOfWeek.Tuesday).Single().Name, "Tuesday name remains");
        Equal("Original", original.EntriesOn(DayOfWeek.Tuesday).Single().Name, "Draft edits do not mutate saved schedule");
    }

    private static void ClipsQuotaWindow()
    {
        var quotaWindow = Session(1, "Monday shift", 540, 660);
        var schedule = WorkSchedule.FromSessions([])
            .WithDaySessions(DayOfWeek.Monday, [quotaWindow]);
        var pace = Plan(schedule, [DayOfWeek.Monday], WindowStart.AddMinutes(30));
        Equal(2, pace.Sessions.Count, "Two clipped Monday intervals");
        Equal(WindowStart, pace.Sessions[0].Start, "Opening clipped to the quota window");
        Equal(WorkClock.At(Monday, 660), pace.Sessions[0].End, "First Monday end");
        Equal(WorkClock.At(Monday.AddDays(7), 540), pace.Sessions[1].Start, "Final Monday start");
        Equal(Reset, pace.Sessions[1].End, "Closing clipped to the quota reset");
        Near(50, pace.Sessions[0].TargetPercent, "First clipped hour share");
        Near(50, pace.Sessions[1].TargetPercent, "Final clipped hour share");
        Near(25, pace.TargetPercent, "Elapsed half-hour share");
        Near(1.5, pace.RemainingWorkHours, "Remaining clipped hours");
        Near(100, pace.Sessions.Sum(session => session.TargetPercent), "All clipped slices sum to one quota");
    }

    private static Pace Plan(WorkSchedule schedule, DayOfWeek[] workdays, DateTimeOffset now)
    {
        Equal(WindowStart, Reset.AddMinutes(-10_080), "Fixture has an exact weekly quota window");
        var quota = new QuotaSample(now, Reset, 10_080, 10);
        var telemetry = new Telemetry(quota, null, [quota], null, [], "Codex account", null);
        var preferences = new Preferences { Schedule = schedule, Workdays = workdays };
        return Planner.Calculate(telemetry, [quota], preferences, now).Pace
            ?? throw new InvalidOperationException("The current weekly reading should create a plan.");
    }

    private static WorkSchedule Deserialize(string json) =>
        JsonSerializer.Deserialize<WorkSchedule>(json)
        ?? throw new InvalidOperationException("Schedule JSON returned null.");

    private static SessionTemplate Session(int suffix, string name, int start, int end) =>
        new(new Guid($"00000000-0000-4000-8000-{suffix:D12}"), name, start, end);

    private static void Assert(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    private static void Equal<T>(T expected, T actual, string detail)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{detail}. Expected {expected}, got {actual}.");
    }

    private static void Near(double expected, double? actual, string detail)
    {
        if (actual is not double value || !double.IsFinite(value) || Math.Abs(value - expected) > 0.000_001)
            throw new InvalidOperationException($"{detail}. Expected {expected}, got {actual}.");
    }
}
