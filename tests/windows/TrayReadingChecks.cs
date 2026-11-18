using CodexPace;

internal static class TrayReadingChecks
{
    private static readonly DateTimeOffset Now = WorkClock.At(new DateOnly(2026, 11, 18), 15 * 60);
    private static readonly DateTimeOffset Reset = Now.AddDays(5);

    public static void WeeklyOrientations()
    {
        var data = Example();
        var used = TrayQuotaReading.Make(data, Now, showWeekly: true, showUsed: true);
        Equal("Weekly quota", used.Title);
        Equal("39% / 31% used", used.Caption);
        Equal("39%", used.Actual);
        Equal("31%", used.Target);
        Near(0.39, used.Amount);
        Near(0.31, used.Marker);
        Equal(TrayQuotaTone.OverTarget, used.Tone);
        Contains(used.Tooltip, "Weekly quota");
        Contains(used.Tooltip, "39% used");
        Contains(used.Tooltip, "Target 31% used by now");

        var left = TrayQuotaReading.Make(data, Now, showWeekly: true, showUsed: false);
        Equal("61% / 69% left", left.Caption);
        Near(0.61, left.Amount);
        Near(0.69, left.Marker);
        Equal(used.Tone, left.Tone);
    }

    public static void SessionOrientations()
    {
        var data = Example();
        var used = TrayQuotaReading.Make(data, Now, showWeekly: false, showUsed: true);
        Equal("Afternoon", used.Title);
        Equal("40% / 30% used", used.Caption);
        Near(0.4, used.Amount);
        Near(0.3, used.Marker);
        Contains(used.Tooltip, "Afternoon");
        Contains(used.Tooltip, "Target 30% used by now");

        var left = TrayQuotaReading.Make(data, Now, showWeekly: false, showUsed: false);
        Equal("60% / 70% left", left.Caption);
        Near(0.6, left.Amount);
        Near(0.7, left.Marker);
        Equal(used.Tone, left.Tone);
    }

    public static void StaleReadings()
    {
        var data = Example() with { Quota = new QuotaSample(Now.AddMinutes(-46), Reset, 10080, 39) };
        var weekly = TrayQuotaReading.Make(data, Now, true, true);
        Equal("39% / 31% used", weekly.Caption);
        Equal(TrayQuotaTone.Unknown, weekly.Tone);
        Contains(weekly.Note, "Last reading");
        Contains(weekly.Tooltip, "Last reading");

        var session = TrayQuotaReading.Make(data, Now, false, true);
        Equal("Afternoon", session.Title);
        NoNumbers(session);
        Contains(session.Note, "Refresh");

        var expired = data with { Quota = data.Quota! with { ResetsAt = Now } };
        Equal(TrayQuotaTone.Unknown, TrayQuotaReading.Make(expired, Now, true, true).Tone);
    }

    public static void UnavailableSessions()
    {
        var data = Example();
        NoNumbers(TrayQuotaReading.Make(data, Now.AddHours(4), false, true));
        Equal("Off hours", TrayQuotaReading.Make(data, Now.AddHours(4), false, true).Title);
        NoNumbers(TrayQuotaReading.Make(data with { Pace = null }, Now, false, true));
        NoNumbers(TrayQuotaReading.Make(data with { Quota = null }, Now, false, true));
        NoNumbers(TrayQuotaReading.Make(data with { Pace = data.Pace! with { SessionUsed = null } }, Now, false, true));
        NoNumbers(TrayQuotaReading.Make(data with { Pace = data.Pace! with { SessionBudget = 0 } }, Now, false, true));
        NoNumbers(TrayQuotaReading.Make(data with
        {
            Guidance = new SessionGuidance(SessionAction.CheckUsage, null, false)
        }, Now, false, true));
    }

    public static void MissingTargets()
    {
        var data = Example();
        foreach (var pace in new Pace?[] { null, data.Pace! with { Sessions = [] }, data.Pace! with { TargetPercent = double.NaN } })
        {
            var reading = TrayQuotaReading.Make(data with { Pace = pace }, Now, true, true);
            Equal("39% used", reading.Caption);
            Equal<string?>(null, reading.Target);
            Equal<double?>(null, reading.Marker);
            Equal(TrayQuotaTone.Unknown, reading.Tone);
        }

        var session = TrayQuotaReading.Make(data with { Pace = data.Pace! with { SessionTarget = null } }, Now, false, true);
        Equal("40% used", session.Caption);
        Equal<string?>(null, session.Target);
        Equal<double?>(null, session.Marker);
        Equal(TrayQuotaTone.Unknown, session.Tone);
        NoNumbers(TrayQuotaReading.Make(data with { Quota = data.Quota! with { UsedPercent = double.NaN } }, Now, true, true));
        NoNumbers(TrayQuotaReading.Make(data with { Quota = data.Quota! with { ObservedAt = Now.AddMinutes(1) } }, Now, true, true));
    }

    public static void ClampedGeometry()
    {
        var data = Example();
        var over = data with { Pace = data.Pace! with { SessionUsed = 15, SessionTarget = 20 } };
        var used = TrayQuotaReading.Make(over, Now, false, true);
        Equal("150% / 200% used", used.Caption);
        Near(1, used.Amount);
        Near(1, used.Marker);
        var left = TrayQuotaReading.Make(over, Now, false, false);
        Equal("0% / 0% left", left.Caption);
        Near(0, left.Amount);
        Near(0, left.Marker);

        var negativeTarget = data with { Pace = data.Pace! with { SessionTarget = -1 } };
        Near(0, TrayQuotaReading.Make(negativeTarget, Now, false, true).Marker);
        Near(1, TrayQuotaReading.Make(negativeTarget, Now, false, false).Marker);
    }

    public static void PaceTones()
    {
        foreach (bool showUsed in new[] { true, false })
        {
            var data = Example() with { Quota = Example().Quota! with { UsedPercent = 20 } };
            Equal(TrayQuotaTone.WithinTarget, TrayQuotaReading.Make(data, Now, true, showUsed).Tone);
            data = data with { Quota = data.Quota! with { UsedPercent = 31 } };
            Equal(TrayQuotaTone.WithinTarget, TrayQuotaReading.Make(data, Now, true, showUsed).Tone);
            data = data with { Quota = data.Quota! with { UsedPercent = 34 } };
            Equal(TrayQuotaTone.SlightlyOverTarget, TrayQuotaReading.Make(data, Now, true, showUsed).Tone);
            data = data with { Quota = data.Quota! with { UsedPercent = 35 } };
            Equal(TrayQuotaTone.OverTarget, TrayQuotaReading.Make(data, Now, true, showUsed).Tone);
        }
    }

    private static DashboardData Example()
    {
        var session = new WorkSession(Now.AddHours(-1), Now.AddHours(3), 15, 50,
            new SessionTemplate(Guid.NewGuid(), "Afternoon", 840, 1080));
        var pace = new Pace(
            PlanEndsAt: Reset, Sessions: [session], CurrentSession: session, TargetPercent: 31,
            RemainingWorkHours: 20, SafeRate: null, RecentRate: null, ProjectedPercent: null,
            ExhaustionDate: null, SessionRoomToTarget: 6, SessionProjectedRoomToTarget: null,
            SessionBudget: 10, SessionTarget: 3, SessionUsed: 4, SessionStartInferred: false,
            PartialSince: null, SessionStatus: SessionPaceStatus.OverPace);
        return new DashboardData(
            new QuotaSample(Now, Reset, 10080, 39), null, pace,
            new SessionGuidance(SessionAction.SlowDown, null, false),
            "", "", "", "", "", null, null);
    }

    private static void NoNumbers(TrayQuotaReading reading)
    {
        Equal("Codex Pace", reading.Caption);
        Equal<string?>(null, reading.Actual);
        Equal<string?>(null, reading.Target);
        Equal<double?>(null, reading.Amount);
        Equal<double?>(null, reading.Marker);
        Equal(TrayQuotaTone.Unknown, reading.Tone);
    }

    private static void Contains(string? value, string expected)
    {
        if (value is null || !value.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected '{expected}' in '{value}'.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static void Near(double expected, double? actual)
    {
        if (actual is not double amount || !double.IsFinite(amount) || Math.Abs(expected - amount) > 0.000001)
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
