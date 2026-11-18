using System.Globalization;
using CodexPace;

internal static class SessionDeltaChecks
{
    private static readonly DateTimeOffset Now = WorkClock.At(new DateOnly(2026, 11, 18), 15 * 60);
    private static readonly DateTimeOffset Reset = WorkClock.At(new DateOnly(2026, 11, 23), 21 * 60);

    public static void UsesLatestDistinctReading()
    {
        var quota = Sample(Now, 30);
        var history = new[]
        {
            Sample(Now.AddMinutes(-10), 28),
            Sample(Now.AddMinutes(-2), 29),
            Sample(Now, 30),
            Sample(Now.AddMinutes(2), 31),
        };
        var delta = SessionQuotaDelta.Calculate(quota, Pace(), history, Now)
            ?? throw new InvalidOperationException("A distinct earlier reading should produce a change.");
        // The displayed budget is 20 points even though the original planned session share is 10.
        Near(5, delta.UsedPercentagePoints);
        Near(-5, delta.LeftPercentagePoints);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Equal("+5% of session quota since last reading", delta.Caption(showUsed: true));
            Equal("−5% of session quota since last reading", delta.Caption(showUsed: false));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    public static void CapsRemainingChangeAtZero()
    {
        var quota = Sample(Now, 41);
        var delta = SessionQuotaDelta.Calculate(quota, Pace(used: 21), [Sample(Now.AddMinutes(-2), 39)], Now)
            ?? throw new InvalidOperationException("An overspend should retain the change in used quota.");
        Near(10, delta.UsedPercentagePoints);
        Near(-5, delta.LeftPercentagePoints);

        var alreadyEmpty = SessionQuotaDelta.Calculate(quota, Pace(used: 23), [Sample(Now.AddMinutes(-2), 40)], Now)
            ?? throw new InvalidOperationException("Used quota can continue increasing after the allowance is empty.");
        Near(5, alreadyEmpty.UsedPercentagePoints);
        Near(0, alreadyEmpty.LeftPercentagePoints);
        Equal<string?>(null, alreadyEmpty.Caption(showUsed: false));
    }

    public static void RejectsUnavailableComparisons()
    {
        var quota = Sample(Now, 30);
        QuotaSample[][] rejectedHistory =
        [
            [],
            [Sample(Now, 30)],
            [Sample(Now.AddMinutes(-2), 29) with { ResetsAt = Reset.AddDays(-7) }],
            [Sample(Now.AddMinutes(-2), 29) with { WindowMinutes = 300 }],
            [Sample(Now.AddMinutes(-46), 29)],
            [Sample(Now.AddHours(-2), 29)],
            [Sample(Now.AddMinutes(1), 29)],
            [Sample(Now.AddMinutes(-2), 31)],
            [Sample(Now.AddMinutes(-2), double.NaN)],
        ];
        foreach (var history in rejectedHistory)
            Equal<SessionQuotaDelta?>(null, SessionQuotaDelta.Calculate(quota, Pace(), history, Now));

        Equal<SessionQuotaDelta?>(null, SessionQuotaDelta.Calculate(null, Pace(), [], Now));
        Equal<SessionQuotaDelta?>(null, SessionQuotaDelta.Calculate(quota, null, [], Now));
        Equal<SessionQuotaDelta?>(null, SessionQuotaDelta.Calculate(quota, Pace() with { SessionBudget = null },
            [Sample(Now.AddMinutes(-2), 29)], Now));
        Equal<SessionQuotaDelta?>(null, SessionQuotaDelta.Calculate(quota, Pace() with { SessionUsed = null },
            [Sample(Now.AddMinutes(-2), 29)], Now));
        Equal<SessionQuotaDelta?>(null, SessionQuotaDelta.Calculate(Sample(Now.AddMinutes(-46), 30), Pace(),
            [Sample(Now.AddMinutes(-48), 29)], Now));
        Equal<SessionQuotaDelta?>(null, SessionQuotaDelta.Calculate(quota with { ResetsAt = Now }, Pace(),
            [Sample(Now.AddMinutes(-2), 29)], Now));
    }

    public static void PlannerExposesTheChange()
    {
        var beforeStart = Sample(Now.AddHours(-1), 20);
        var previous = Sample(Now.AddMinutes(-2), 29);
        var current = Sample(Now, 30);
        var history = new[] { beforeStart, previous, current };
        var telemetry = new Telemetry(current, null, history, null, [], "Codex account", null);
        var result = Planner.Calculate(telemetry, history, new Preferences(), Now);
        var budget = result.Pace?.SessionBudget
            ?? throw new InvalidOperationException("The baseline fixture should yield a session budget.");
        var delta = result.SessionDelta
            ?? throw new InvalidOperationException("The normal planner entry point should expose the session change.");
        Near(100 / budget, delta.UsedPercentagePoints);
        Near(-100 / budget, delta.LeftPercentagePoints);
    }

    private static QuotaSample Sample(DateTimeOffset at, double used) => new(at, Reset, 10080, used);

    private static Pace Pace(double used = 10)
    {
        var session = new WorkSession(Now.AddHours(-1), Now.AddHours(3), 10, 40,
            new SessionTemplate(Guid.Parse("00000000-0000-4000-8000-000000000001"), "Afternoon", 840, 1080));
        return new Pace(Reset, [session], session, 30, 10, 2, 1, 40, null, 10, 8,
            SessionBudget: 20, SessionTarget: 10, SessionUsed: used, SessionStartInferred: false,
            PartialSince: null, SessionStatus: SessionPaceStatus.OnTrack);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static void Near(double expected, double? actual)
    {
        if (actual is not double value || !double.IsFinite(value) || Math.Abs(value - expected) > 0.000001)
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
